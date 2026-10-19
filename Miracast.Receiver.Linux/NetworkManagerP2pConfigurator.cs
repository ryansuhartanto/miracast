using System.Net;
using System.Text.RegularExpressions;

namespace Miracast.Receiver.Linux;

internal sealed partial class NetworkManagerP2pConfigurator(Action<string> log)
{
    private const string ConnectionName = "Miracast receiver";

    // NetworkManager removes every P2P group it did not start itself, so the group
    // has to come from a NetworkManager wifi-p2p connection rather than from
    // wpa_supplicant's Connect. The connection is kept in memory only.
    public async Task ConnectAsync(string peerAddress, byte[] wfdIes, CancellationToken cancellationToken)
    {
        await CommandRunner.RunAsync(
                "nmcli", ["connection", "delete", "id", ConnectionName], cancellationToken, ignoreFailure: true)
            .ConfigureAwait(false);
        await CommandRunner.RunAsync(
                "nmcli",
                [
                    "connection", "add", "save", "no",
                    "type", "wifi-p2p",
                    "con-name", ConnectionName,
                    "connection.autoconnect", "no",
                    "wifi-p2p.peer", peerAddress,
                    "wifi-p2p.wfd-ies", string.Join(':', wfdIes.Select(value => value.ToString("x2"))),
                ],
                cancellationToken)
            .ConfigureAwait(false);
        await CommandRunner.RunAsync("nmcli", ["connection", "up", "id", ConnectionName], cancellationToken)
            .ConfigureAwait(false);
    }

    public Task DeleteConnectionAsync() =>
        CommandRunner.RunAsync(
            "nmcli", ["connection", "delete", "id", ConnectionName], CancellationToken.None, ignoreFailure: true);

    // As client the source is the group owner and runs the DHCP server; as group
    // owner NetworkManager's dnsmasq leases the source an address and records it
    // in the neighbour table.
    public async Task<IPAddress> WaitForSourceAddressAsync(
        string p2pDevice,
        string groupInterface,
        string role,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                var address = role == "client"
                    ? DhcpServerIdentifier().Match(await CommandRunner.RunAsync(
                            "nmcli", ["-g", "DHCP4.OPTION", "device", "show", p2pDevice], timeout.Token,
                            ignoreFailure: true)
                        .ConfigureAwait(false))
                    : NeighbourAddress().Match(await CommandRunner.RunAsync(
                            "ip", ["-4", "-o", "neigh", "show", "dev", groupInterface], timeout.Token,
                            ignoreFailure: true)
                        .ConfigureAwait(false));
                if (address.Success)
                {
                    log($"Miracast source on {groupInterface} ({role}) is at {address.Groups[1].Value}.");
                    return IPAddress.Parse(address.Groups[1].Value);
                }

                await Task.Delay(250, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Could not find the Miracast source's IPv4 address on P2P group '{groupInterface}'.");
        }
    }

    [GeneratedRegex(@"dhcp_server_identifier = (\d+\.\d+\.\d+\.\d+)")]
    private static partial Regex DhcpServerIdentifier();

    [GeneratedRegex(@"^(\d+\.\d+\.\d+\.\d+) .*\blladdr ", RegexOptions.Multiline)]
    private static partial Regex NeighbourAddress();
}
