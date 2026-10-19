using Tmds.DBus;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(Tmds.DBus.Connection.DynamicAssemblyName)]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Miracast.Receiver.Linux.Tests")]

namespace Miracast.Receiver.Linux;

internal sealed class WpaSupplicantP2pController : IAsyncDisposable
{
    private const string Service = "fi.w1.wpa_supplicant1";
    private static readonly ObjectPath RootPath = new("/fi/w1/wpa_supplicant1");

    // Device information subelement: primary sink, session available, service
    // discovery, preferred P2P, RTSP control port 7236, 50 Mbps throughput.
    internal static readonly byte[] SinkWfdInformationElements =
        [0x00, 0x00, 0x06, 0x01, 0x51, 0x1c, 0x44, 0x00, 0x32];

    private readonly Connection _connection = new(Address.System);
    private readonly string _interfaceName;
    private readonly string _friendlyName;
    private readonly Action<string> _log;
    private readonly Func<ObjectPath, Task> _authorizePeer;
    private readonly Func<P2pGroup, Task> _groupStarted;
    private readonly Func<Task> _groupFinished;
    private readonly List<IDisposable> _subscriptions = [];
    private IWpaSupplicant? _supplicant;
    private IWpaP2pDevice? _p2pDevice;
    private byte[]? _previousWfdIes;
    private IWpaWps? _wps;
    private string? _previousConfigMethods;
    private bool _extendedListenStarted;
    private bool _findStarted;

    public WpaSupplicantP2pController(
        string interfaceName,
        string friendlyName,
        Action<string> log,
        Func<ObjectPath, Task> authorizePeer,
        Func<P2pGroup, Task> groupStarted,
        Func<Task> groupFinished)
    {
        _interfaceName = interfaceName;
        _friendlyName = friendlyName;
        _log = log;
        _authorizePeer = authorizePeer;
        _groupStarted = groupStarted;
        _groupFinished = groupFinished;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _connection.ConnectAsync().ConfigureAwait(false);

        _supplicant = _connection.CreateProxy<IWpaSupplicant>(Service, RootPath);
        var capabilities = await _supplicant.GetAsync<string[]>("Capabilities").ConfigureAwait(false);
        if (!capabilities.Contains("p2p", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The system wpa_supplicant was built without P2P support.");

        ObjectPath interfacePath;
        try
        {
            interfacePath = await _supplicant.GetInterfaceAsync(_interfaceName).ConfigureAwait(false);
        }
        catch (DBusException exception)
        {
            throw new InvalidOperationException(
                $"The system wpa_supplicant does not control Wi-Fi interface '{_interfaceName}'.", exception);
        }

        _previousWfdIes = await _supplicant.GetAsync<byte[]>("WFDIEs").ConfigureAwait(false);
        await _supplicant.SetAsync("WFDIEs", SinkWfdInformationElements).ConfigureAwait(false);

        // wpa_supplicant's default config methods omit push-button, which makes
        // Android sources fall back to PIN entry.
        _wps = _connection.CreateProxy<IWpaWps>(Service, interfacePath);
        _previousConfigMethods = await _wps.GetAsync<string>("ConfigMethods").ConfigureAwait(false);
        await _wps.SetAsync("ConfigMethods", "virtual_push_button push_button display keypad")
            .ConfigureAwait(false);

        _p2pDevice = _connection.CreateProxy<IWpaP2pDevice>(Service, interfacePath);
        await _p2pDevice.SetAsync(
            "P2PDeviceConfig",
            new Dictionary<string, object>
            {
                ["DeviceName"] = _friendlyName,
                ["PrimaryDeviceType"] = new byte[] { 0x00, 0x07, 0x00, 0x50, 0xf2, 0x04, 0x00, 0x01 },
                ["GOIntent"] = 0u,
            }).ConfigureAwait(false);

        _subscriptions.Add(await _p2pDevice.WatchProvisionDiscoveryPBCRequestAsync(
            peer => RunSignalHandlerAsync(() => _authorizePeer(peer), "PBC authorization"),
            exception => _log($"wpa_supplicant PBC signal failed: {exception.Message}")).ConfigureAwait(false));
        _subscriptions.Add(await _p2pDevice.WatchGONegotiationRequestAsync(
            request => RunSignalHandlerAsync(
                () => _authorizePeer(request.peer),
                "GO negotiation authorization"),
            exception => _log($"wpa_supplicant GO negotiation signal failed: {exception.Message}")).ConfigureAwait(false));
        _subscriptions.Add(await _p2pDevice.WatchGroupStartedAsync(
            properties => RunSignalHandlerAsync(
                () => _groupStarted(P2pGroup.FromProperties(properties)),
                "group startup"),
            exception => _log($"wpa_supplicant group signal failed: {exception.Message}")).ConfigureAwait(false));
        _subscriptions.Add(await _p2pDevice.WatchGroupFinishedAsync(
            _ => RunSignalHandlerAsync(_groupFinished, "group shutdown"),
            exception => _log($"wpa_supplicant group-finished signal failed: {exception.Message}")).ConfigureAwait(false));

        await _p2pDevice.ExtendedListenAsync(
            new Dictionary<string, object>
            {
                ["period"] = 500,
                ["interval"] = 500,
            }).ConfigureAwait(false);
        _extendedListenStarted = true;
        await _p2pDevice.FindAsync(
            new Dictionary<string, object>
            {
                ["DiscoveryType"] = "social",
            }).ConfigureAwait(false);
        _findStarted = true;

        _log($"System wpa_supplicant is advertising '{_friendlyName}' on {_interfaceName}.");
    }

    public async Task<string> GetPeerAddressAsync(ObjectPath peerPath)
    {
        var peer = _connection.CreateProxy<IWpaPeer>(Service, peerPath);
        var address = await peer.GetAsync<byte[]>("DeviceAddress").ConfigureAwait(false);
        if (address.Length != 6)
            throw new InvalidOperationException($"wpa_supplicant returned an invalid P2P peer address for {peerPath}.");
        return string.Join(':', address.Select(value => value.ToString("X2")));
    }

    public Task<string> GetInterfaceNameAsync(ObjectPath interfacePath)
    {
        var @interface = _connection.CreateProxy<IWpaInterface>(Service, interfacePath);
        return @interface.GetAsync<string>("Ifname");
    }

    public Task ApplySinkWfdIesAsync()
    {
        var supplicant = _supplicant
                         ?? throw new InvalidOperationException("The P2P controller is not running.");
        return supplicant.SetAsync("WFDIEs", SinkWfdInformationElements);
    }

    private async void RunSignalHandlerAsync(Func<Task> action, string operation)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log($"P2P {operation} failed: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();

        if (_p2pDevice is not null)
        {
            if (_findStarted)
            {
                try
                {
                    await _p2pDevice.StopFindAsync().ConfigureAwait(false);
                }
                catch (DBusException exception)
                {
                    _log($"Could not stop P2P discovery: {exception.Message}");
                }
            }

            if (_extendedListenStarted)
            {
                try
                {
                    await _p2pDevice.ExtendedListenAsync(new Dictionary<string, object>()).ConfigureAwait(false);
                }
                catch (DBusException exception)
                {
                    _log($"Could not stop P2P extended listen: {exception.Message}");
                }
            }

            _p2pDevice = null;
        }

        if (_wps is not null && _previousConfigMethods is not null)
        {
            try
            {
                await _wps.SetAsync("ConfigMethods", _previousConfigMethods).ConfigureAwait(false);
            }
            catch (DBusException exception)
            {
                _log($"Could not restore wpa_supplicant WPS config methods: {exception.Message}");
            }
        }

        _wps = null;
        _previousConfigMethods = null;

        if (_supplicant is not null && _previousWfdIes is not null)
        {
            try
            {
                await _supplicant.SetAsync("WFDIEs", _previousWfdIes).ConfigureAwait(false);
            }
            catch (DBusException exception)
            {
                _log($"Could not restore wpa_supplicant WFD IEs: {exception.Message}");
            }
        }

        _supplicant = null;
        _previousWfdIes = null;
        _findStarted = false;
        _extendedListenStarted = false;
        _connection.Dispose();
    }
}

internal readonly record struct P2pGroup(ObjectPath InterfacePath, string Role)
{
    public static P2pGroup FromProperties(IDictionary<string, object> properties)
    {
        if (!properties.TryGetValue("interface_object", out var pathValue) || pathValue is not ObjectPath path)
            throw new InvalidOperationException("wpa_supplicant GroupStarted omitted interface_object.");
        if (!properties.TryGetValue("role", out var roleValue) || roleValue is not string role)
            throw new InvalidOperationException("wpa_supplicant GroupStarted omitted role.");
        return new(path, role);
    }
}

[DBusInterface("fi.w1.wpa_supplicant1")]
internal interface IWpaSupplicant : IDBusObject
{
    Task<ObjectPath> GetInterfaceAsync(string ifname);
    Task<T> GetAsync<T>(string property);
    Task SetAsync(string property, object value);
}

[DBusInterface("fi.w1.wpa_supplicant1.Interface.P2PDevice")]
internal interface IWpaP2pDevice : IDBusObject
{
    Task FindAsync(IDictionary<string, object> arguments);
    Task StopFindAsync();
    Task ExtendedListenAsync(IDictionary<string, object> arguments);
    Task<T> GetAsync<T>(string property);
    Task SetAsync(string property, object value);
    Task<IDisposable> WatchProvisionDiscoveryPBCRequestAsync(
        Action<ObjectPath> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchGONegotiationRequestAsync(
        Action<(ObjectPath peer, ushort devicePasswordId, byte deviceGoIntent)> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchGroupStartedAsync(
        Action<IDictionary<string, object>> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchGroupFinishedAsync(
        Action<IDictionary<string, object>> handler,
        Action<Exception>? onError = null);
}

[DBusInterface("fi.w1.wpa_supplicant1.Interface.WPS")]
internal interface IWpaWps : IDBusObject
{
    Task<T> GetAsync<T>(string property);
    Task SetAsync(string property, object value);
}

[DBusInterface("fi.w1.wpa_supplicant1.Peer")]
internal interface IWpaPeer : IDBusObject
{
    Task<T> GetAsync<T>(string property);
}

[DBusInterface("fi.w1.wpa_supplicant1.Interface")]
internal interface IWpaInterface : IDBusObject
{
    Task<T> GetAsync<T>(string property);
}
