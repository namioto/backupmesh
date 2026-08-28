using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class BackupTargetResolverTests
{
    [Fact]
    public void ResolvesOnlyReadyMappingOwnedBySourceBackupSet()
    {
        var root = Path.GetTempPath();
        var sourceId = Guid.NewGuid();
        var set = new SourceBackupSet(Guid.NewGuid(), sourceId, "Source", "Photos", ["/photos"]);
        var device = new RegisteredDevice(Guid.NewGuid(), "volume:test", "Archive", "TEST", root, DateTimeOffset.UtcNow, null, 0);
        var mapping = new BackupTargetMapping(Guid.NewGuid(), set.Id, device.Id, "backupmesh/photos");
        var topology = new StorageAgentConfiguration([device], [set], [mapping]);
        var configuration = new StorageConfigurationStore(new StorageConfigurationOptions { PersistencePath = string.Empty });
        configuration.Update(new(0, topology));
        var presence = new StoragePresenceStore();
        presence.Refresh(topology, [new("volume:test", root, "TEST", 100, 200, "Disk", 1)], DateTimeOffset.UtcNow);
        var resolver = new BackupTargetResolver(configuration, presence);

        var result = resolver.Resolve(new(Guid.NewGuid(), sourceId, set.Id, mapping.Id, DateTimeOffset.UtcNow, null));

        Assert.NotNull(result.Target);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "backupmesh/photos")), result.Target.DestinationFolder);
    }

    [Fact]
    public void RejectsMappingRequestedByDifferentSource()
    {
        var set = new SourceBackupSet(Guid.NewGuid(), Guid.NewGuid(), "Source", "Photos", ["/photos"]);
        var device = new RegisteredDevice(Guid.NewGuid(), "volume:test", "Archive", "TEST", "X:\\", DateTimeOffset.UtcNow, null, 0);
        var mapping = new BackupTargetMapping(Guid.NewGuid(), set.Id, device.Id, "BackupMesh");
        var configuration = new StorageConfigurationStore(new StorageConfigurationOptions { PersistencePath = string.Empty });
        configuration.Update(new(0, new([device], [set], [mapping])));
        var resolver = new BackupTargetResolver(configuration, new StoragePresenceStore());

        var result = resolver.Resolve(new(Guid.NewGuid(), Guid.NewGuid(), set.Id, mapping.Id, DateTimeOffset.UtcNow, null));

        Assert.Equal("TARGET_NOT_FOUND", result.ErrorCode);
    }
}
