using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class StoragePresenceStoreTests
{
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
}
