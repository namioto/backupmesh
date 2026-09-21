using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BackupMesh.Storage.Service;

internal static class StorageNetworkAddress
{
    internal static IPAddress[] LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
            && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
        .Select(adapter => adapter.GetIPProperties())
        .OrderByDescending(properties => properties.GatewayAddresses.Any(gateway => !gateway.Address.Equals(IPAddress.Any)))
        .SelectMany(properties => properties.UnicastAddresses)
        .Select(address => address.Address)
        .Where(IsUsable).Distinct().ToArray();

    internal static bool IsUsable(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork
        && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any)
        && address.GetAddressBytes()[0] < 224
        && !(address.GetAddressBytes()[0] == 169 && address.GetAddressBytes()[1] == 254);

    internal static string? SelectHost(IEnumerable<string> configuredNames, IEnumerable<IPAddress> addresses) =>
        configuredNames.Select(name => name.Trim()).FirstOrDefault(name => name.Length > 0
            && !name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && (!IPAddress.TryParse(name, out var address) || !IPAddress.IsLoopback(address)))
        ?? addresses.FirstOrDefault(IsUsable)?.ToString();

    internal static string? Resolve(IEnumerable<string> configuredNames) => SelectHost(configuredNames, LocalAddresses());
}
