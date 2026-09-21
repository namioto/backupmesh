using System.Net;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class StorageNetworkAddressTests
{
    [Fact]
    public void AutomaticAddressSkipsLoopbackAndLinkLocal()
    {
        var addresses = new[] { "127.0.0.1", "169.254.10.20", "192.168.1.20", "10.0.0.5" }.Select(IPAddress.Parse);
        Assert.Equal("192.168.1.20", StorageNetworkAddress.SelectHost(["localhost", "127.0.0.1"], addresses));
    }

    [Fact]
    public void ExplicitDnsNameTakesPrecedence()
    {
        Assert.Equal("storage.example", StorageNetworkAddress.SelectHost([" storage.example "], [IPAddress.Parse("192.168.1.20")]));
    }

    [Fact]
    public void OfflineDoesNotInventAMachineName()
    {
        Assert.Null(StorageNetworkAddress.SelectHost([], [IPAddress.Loopback, IPAddress.Any]));
    }
}
