using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Tests;

public sealed class BackupTopologyTests
{
    [Fact]
    public void FolderStorageIdentityIsStableAcrossPathCasingAndTrailingSeparators()
    {
        var root = Path.Combine(Path.GetTempPath(), "BackupMesh-Folder");
        Assert.Equal(FolderStorageIdentity.Create(root), FolderStorageIdentity.Create(root.ToLowerInvariant() + Path.DirectorySeparatorChar));
        Assert.True(FolderStorageIdentity.TryGetPath(FolderStorageIdentity.Create(root), out var parsed));
        Assert.Equal(Path.GetFullPath(root), parsed, ignoreCase: true);
    }

    [Fact]
    public void SupportsManyToManySourceAndDeviceMappings()
    {
        var source = Guid.NewGuid();
        var setA = new SourceBackupSet(Guid.NewGuid(), source, "Home server", "Photos", ["/srv/photos"]);
        var setB = new SourceBackupSet(Guid.NewGuid(), source, "Home server", "Documents", ["/srv/documents"]);
        var deviceA = Device("Archive A");
        var deviceB = Device("Archive B");
        var topology = new StorageAgentConfiguration(
            [deviceA, deviceB],
            [setA, setB],
            [
                new(Guid.NewGuid(), setA.Id, deviceA.Id, "sources/home/photos"),
                new(Guid.NewGuid(), setA.Id, deviceB.Id, "mirror/photos"),
                new(Guid.NewGuid(), setB.Id, deviceA.Id, "sources/home/documents")
            ]);

        Assert.Empty(BackupTopologyValidator.Validate(topology));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\absolute")]
    [InlineData("/absolute")]
    [InlineData("folder:name")]
    [InlineData("folder./child")]
    [InlineData("folder /child")]
    [InlineData(".")]
    [InlineData("")]
    public void RejectsUnsafeRepositoryPaths(string path) =>
        Assert.False(BackupTopologyValidator.IsSafeRelativeRepositoryPath(path));

    [Fact]
    public void RejectsTwoEnabledMappingsForTheSameDevicePath()
    {
        var device = Device("Archive");
        var first = Set("Photos");
        var second = Set("Documents");
        var topology = new StorageAgentConfiguration(
            [device], [first, second],
            [new(Guid.NewGuid(), first.Id, device.Id, "shared/repository"), new(Guid.NewGuid(), second.Id, device.Id, "SHARED\\REPOSITORY")]);

        Assert.Contains(BackupTopologyValidator.Validate(topology), error => error.Contains("same device path", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void RejectsInvalidPerDeviceArrivalDelay(int minutes)
    {
        var device = Device("Archive") with { ArrivalDelayMinutes = minutes };

        Assert.Contains(BackupTopologyValidator.Validate(new([device], [], [])),
            error => error.Contains("arrival delay", StringComparison.Ordinal));
    }

    private static RegisteredDevice Device(string name) => new(Guid.NewGuid(), Guid.NewGuid().ToString(), name, name, "E:\\", DateTimeOffset.UtcNow, null);
    private static SourceBackupSet Set(string name) => new(Guid.NewGuid(), Guid.NewGuid(), "Source", name, ["/data"]);
}
