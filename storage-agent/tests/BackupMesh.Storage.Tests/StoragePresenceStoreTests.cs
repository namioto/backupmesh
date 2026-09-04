using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class StoragePresenceStoreTests
{
    [Fact]
    public void RegisteredFolderActsAsAnIndependentStorageDevice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"backupmesh-folder-device-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var stableId = FolderStorageIdentity.Create(root);
            var device = new RegisteredDevice(Guid.NewGuid(), stableId, "Folder A", "Folder", root, DateTimeOffset.UtcNow, null, 0);

            var presence = new StoragePresenceStore().Refresh(new([device], [], []), [], DateTimeOffset.UtcNow).Single();

            Assert.True(presence.Connected);
            Assert.True(presence.Ready);
            Assert.Equal(Path.GetFullPath(root), presence.CurrentRoot, ignoreCase: true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ConnectedDeviceBecomesReadyAfterItsOwnArrivalDelay()
    {
        var root = Path.GetTempPath();
        var device = new RegisteredDevice(Guid.NewGuid(), "volume:test", "Test", "TEST", root, DateTimeOffset.UtcNow, null, 5);
        var volume = new StorageVolumeInfo("volume:test", root, "TEST", 100, 200, "Disk", 1);
        var store = new StoragePresenceStore();
        var connectedAt = DateTimeOffset.UtcNow;

        var waiting = store.Refresh(new([device], [], []), [volume], connectedAt).Single();
        var ready = store.Refresh(new([device], [], []), [volume], connectedAt.AddMinutes(5)).Single();

        Assert.True(waiting.Connected);
        Assert.False(waiting.Ready);
        Assert.Equal(connectedAt.AddMinutes(5), waiting.EligibleAt);
        Assert.True(ready.Ready);
    }

    [Fact]
    public void ReconnectionRestartsTheDeviceDelay()
    {
        var root = Path.GetTempPath();
        var device = new RegisteredDevice(Guid.NewGuid(), "volume:test", "Test", "TEST", root, DateTimeOffset.UtcNow, null, 10);
        var volume = new StorageVolumeInfo("volume:test", root, "TEST", 100, 200, "Disk", 1);
        var store = new StoragePresenceStore();
        var first = DateTimeOffset.UtcNow;
        store.Refresh(new([device], [], []), [volume], first);
        store.Refresh(new([device], [], []), [], first.AddMinutes(2));

        var reconnected = store.Refresh(new([device], [], []), [volume], first.AddMinutes(3)).Single();

        Assert.Equal(first.AddMinutes(13), reconnected.EligibleAt);
        Assert.False(reconnected.Ready);
    }

    [Fact]
    public void SourceDeviceArrivalQueuesEveryReadyDestinationForThatBackupSet()
    {
        var sourceDeviceId = Guid.NewGuid();
        var firstTargetId = Guid.NewGuid();
        var secondTargetId = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var sourceRoot = Path.Combine(Path.GetTempPath(), "camera-card");
        var topology = new StorageAgentConfiguration(
            [Device(sourceDeviceId, sourceRoot), Device(firstTargetId, Path.Combine(Path.GetTempPath(), "target-a")), Device(secondTargetId, Path.Combine(Path.GetTempPath(), "target-b"))],
            [new(setId, sourceId, "This PC", "Camera", [Path.Combine(sourceRoot, "DCIM")])],
            [new(Guid.NewGuid(), setId, firstTargetId, "camera", true), new(Guid.NewGuid(), setId, secondTargetId, "camera", true)]);
        var presence = new[]
        {
            Presence(sourceDeviceId, sourceRoot, true), Presence(firstTargetId, Path.Combine(Path.GetTempPath(), "target-a"), true), Presence(secondTargetId, Path.Combine(Path.GetTempPath(), "target-b"), true)
        };

        var drafts = StorageMonitorService.BuildArrivalDrafts(topology, presence, presence[0]);

        Assert.Equal(2, drafts.Count);
        Assert.All(drafts, draft => Assert.Equal("source-arrival", draft.Reason));
        Assert.Equal([firstTargetId, secondTargetId], drafts.Select(draft => topology.Mappings.Single(mapping => mapping.Id == draft.TargetMappingId).DeviceId));
    }

    [Fact]
    public void SourceArrivalDoesNotQueueUnavailableDestination()
    {
        var sourceDeviceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var sourceRoot = Path.Combine(Path.GetTempPath(), "source-drive");
        var mapping = new BackupTargetMapping(Guid.NewGuid(), setId, targetId, "repository");
        var topology = new StorageAgentConfiguration(
            [Device(sourceDeviceId, sourceRoot), Device(targetId, Path.Combine(Path.GetTempPath(), "offline-target"))],
            [new(setId, Guid.NewGuid(), "This PC", "Import", [sourceRoot])], [mapping]);
        var presence = new[] { Presence(sourceDeviceId, sourceRoot, true), Presence(targetId, null, false) };

        Assert.Empty(StorageMonitorService.BuildArrivalDrafts(topology, presence, presence[0]));
    }

    [Fact]
    public void ArrivalDoesNotTreatARemotePosixSourcePathAsLocal()
    {
        // A Linux Source Agent's backup-set paths are POSIX absolute paths. Path.GetFullPath resolves
        // a leading '/' against the current drive's root on Windows (e.g. "/home/user/Documents" ->
        // "C:\home\user\Documents"), which used to coincidentally fall under any connected device's
        // root and make Storage believe a remote Source's data "arrived" locally.
        var driveRoot = Path.GetPathRoot(Directory.GetCurrentDirectory())!;
        var sourceDeviceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var mapping = new BackupTargetMapping(Guid.NewGuid(), setId, targetId, "repository");
        var topology = new StorageAgentConfiguration(
            [Device(sourceDeviceId, driveRoot), Device(targetId, Path.Combine(Path.GetTempPath(), "target"))],
            [new(setId, Guid.NewGuid(), "Remote Linux Source", "Documents", ["/home/user/Documents"])], [mapping]);
        var presence = new[] { Presence(sourceDeviceId, driveRoot, true), Presence(targetId, Path.Combine(Path.GetTempPath(), "target"), true) };

        Assert.Empty(StorageMonitorService.BuildArrivalDrafts(topology, presence, presence[0]));
    }

    private static RegisteredDevice Device(Guid id, string root) =>
        new(id, FolderStorageIdentity.Create(root), root, "Folder", root, DateTimeOffset.UtcNow, null, 0);

    private static RegisteredDevicePresence Presence(Guid id, string? root, bool ready) =>
        new(id, $"folder:{root}", root ?? "Offline", root is not null, ready, root, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
}
