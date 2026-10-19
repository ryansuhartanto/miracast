using System.Runtime.InteropServices;
using Miracast.Receiver.Entities.EventArgs;
using Tmds.DBus;

namespace Miracast.Receiver.Linux;

public sealed class MiracastReceiverService : IMiracastReceiverService, IAsyncDisposable
{
    public const int RtspPort = 7236;
    public const int RtpPort = 7236;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WpaSupplicantP2pController? _p2pController;
    private WfdRtspClient? _rtspClient;
    private NetworkManagerP2pConfigurator? _networkManager;
    private CancellationTokenSource? _running;
    private string? _wifiInterface;
    private int _connectionAnnounced;
    private int _videoAnnounced;
    private int _connecting;
    private bool _started;

    public event EventHandler<ConnectionCreatedEventArgs>? ConnectionCreated;
    public event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    public event EventHandler<VideoReceivedEventArgs>? VideoReceived;
    public event EventHandler<string>? LogReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The wpa_supplicant Miracast receiver can only run on Linux.");
        if (GetEffectiveUserId() == 0)
            throw new InvalidOperationException("Do not run the receiver as root; start it as the desktop user.");

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
                return;

            _wifiInterface = await FindWifiInterfaceAsync(cancellationToken).ConfigureAwait(false);
            var friendlyName = GetFriendlyName();
            _running = new CancellationTokenSource();

            _rtspClient = new WfdRtspClient(
                Log,
                AnnounceConnection,
                AnnounceDisconnection,
                AnnounceVideo);
            _networkManager = new NetworkManagerP2pConfigurator(Log);

            _p2pController = new WpaSupplicantP2pController(
                _wifiInterface,
                friendlyName,
                Log,
                AuthorizePeerAsync,
                ConfigureGroupAsync,
                GroupFinishedAsync);
            await _p2pController.StartAsync(cancellationToken).ConfigureAwait(false);

            _started = true;
            Log($"Miracast receiver '{friendlyName}' is listening on managed adapter {_wifiInterface}.");
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task AuthorizePeerAsync(ObjectPath peerPath)
    {
        var controller = _p2pController
                         ?? throw new InvalidOperationException("The P2P controller is not running.");
        var networkManager = _networkManager
                             ?? throw new InvalidOperationException("The receiver is not running.");
        var address = await controller.GetPeerAddressAsync(peerPath).ConfigureAwait(false);

        // Sources follow provision discovery with a GO negotiation request; both
        // arrive while the first connection attempt is still activating.
        if (Interlocked.Exchange(ref _connecting, 1) == 1)
            return;
        try
        {
            Log($"Connecting to Miracast source {address}.");
            await networkManager.ConnectAsync(
                    address,
                    WpaSupplicantP2pController.SinkWfdInformationElements,
                    _running?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    private async Task ConfigureGroupAsync(P2pGroup group)
    {
        var controller = _p2pController
                         ?? throw new InvalidOperationException("The P2P controller is not running.");
        var networkManager = _networkManager
                             ?? throw new InvalidOperationException("The receiver is not running.");
        var groupInterface = await controller.GetInterfaceNameAsync(group.InterfacePath).ConfigureAwait(false);
        Log($"P2P group started on {groupInterface}; role: {group.Role}.");

        var source = await networkManager.WaitForSourceAddressAsync(
                $"p2p-dev-{_wifiInterface}",
                groupInterface,
                group.Role,
                _running?.Token ?? CancellationToken.None)
            .ConfigureAwait(false);
        // ponytail: assumes the source's RTSP port is the default 7236; read it from
        // the source's WFD IEs if a source advertises another one.
        _rtspClient?.Connect(source, RtspPort);
    }

    private async Task GroupFinishedAsync()
    {
        Log("P2P group finished.");
        _rtspClient?.Close();
        AnnounceDisconnection();

        // NetworkManager clears the global WFD IEs when its P2P connection ends,
        // which hides this sink from sources.
        // ponytail: fixed delay to land after NetworkManager's reset; watch its
        // device state instead if the race shows up.
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (_p2pController is { } controller)
            await controller.ApplySinkWfdIesAsync().ConfigureAwait(false);
    }

    private void AnnounceConnection()
    {
        if (Interlocked.Exchange(ref _connectionAnnounced, 1) == 0)
            ConnectionCreated?.Invoke(this, new ConnectionCreatedEventArgs());
    }

    private void AnnounceVideo(int width, int height)
    {
        AnnounceConnection();
        if (Interlocked.Exchange(ref _videoAnnounced, 1) != 0)
            return;

        VideoReceived?.Invoke(this, new VideoReceivedEventArgs
        {
            Source = new VideoSource(new Uri($"rtp://0.0.0.0:{RtpPort}"), width, height),
        });
    }

    private void AnnounceDisconnection()
    {
        Interlocked.Exchange(ref _videoAnnounced, 0);
        if (Interlocked.Exchange(ref _connectionAnnounced, 0) == 1)
            ConnectionClosed?.Invoke(this, new ConnectionClosedEventArgs());
    }

    private async Task StopCoreAsync()
    {
        _started = false;
        _running?.Cancel();

        var controller = _p2pController;
        _p2pController = null;
        try
        {
            if (controller is not null)
                await controller.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            var rtspClient = _rtspClient;
            _rtspClient = null;
            var networkManager = _networkManager;
            _networkManager = null;
            try
            {
                if (rtspClient is not null)
                    await rtspClient.DisposeAsync().ConfigureAwait(false);
                if (networkManager is not null)
                    await networkManager.DeleteConnectionAsync().ConfigureAwait(false);
            }
            finally
            {
                _running?.Dispose();
                _running = null;
                _wifiInterface = null;
                AnnounceDisconnection();
            }
        }
    }

    private static async Task<string> FindWifiInterfaceAsync(CancellationToken cancellationToken)
    {
        var configuredInterface = Environment.GetEnvironmentVariable("MIRACAST_WIFI_INTERFACE");
        if (!string.IsNullOrWhiteSpace(configuredInterface))
            return configuredInterface.Trim();

        var output = await CommandRunner.RunAsync(
                "nmcli",
                ["-t", "-f", "DEVICE,TYPE,STATE", "device", "status"],
                cancellationToken)
            .ConfigureAwait(false);

        var adapters = output.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SplitNmcliLine)
            .Where(fields => fields.Length >= 3
                             && fields[1].Equals("wifi", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (adapters.Count == 0)
            throw new InvalidOperationException("nmcli did not report a Wi-Fi interface.");

        // Prefer an idle managed adapter. An active adapter remains a valid fallback
        // when its driver supports concurrent infrastructure and P2P operation.
        return adapters.FirstOrDefault(fields =>
                   !fields[2].Equals("connected", StringComparison.OrdinalIgnoreCase))?[0]
               ?? adapters[0][0];
    }

    internal static string[] SplitNmcliLine(string line)
    {
        var fields = new List<string>();
        var value = new System.Text.StringBuilder();
        var escaped = false;
        foreach (var character in line.TrimEnd('\r'))
        {
            if (escaped)
            {
                value.Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == ':')
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }
        if (escaped)
            value.Append('\\');
        fields.Add(value.ToString());
        return fields.ToArray();
    }

    private static string GetFriendlyName()
    {
        var configuredName = Environment.GetEnvironmentVariable("MIRACAST_FRIENDLY_NAME");
        return string.IsNullOrWhiteSpace(configuredName)
            ? $"{Environment.MachineName} Miracast"
            : configuredName.Trim();
    }

    private void Log(string message) => LogReceived?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
