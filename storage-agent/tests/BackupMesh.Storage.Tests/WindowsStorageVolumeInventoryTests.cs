using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Tests;

public sealed class WindowsStorageVolumeInventoryTests
{
    [Fact]
    public void StableIdentityUsesVolumeSerialInsteadOfMutableLabelAndCapacity()
    {
        var first = WindowsStorageVolumeInventory.BuildStableId("serial:disk-1", "A1B2-C3D4", "Old label", 1000);
        var renamed = WindowsStorageVolumeInventory.BuildStableId("serial:disk-1", "A1B2-C3D4", "New label", 2000);

        Assert.Equal(first, renamed);
        Assert.Equal("serial:disk-1|volume-serial:A1B2-C3D4", first);
    }

    [Fact]
    public void StableIdentityFallsBackWhenWindowsReportsNoVolumeSerial()
    {
        Assert.Equal("pnp:disk-2|volume:Data:4096", WindowsStorageVolumeInventory.BuildStableId("pnp:disk-2", null, "Data", 4096));
    }

    [Fact]
    public void IncludesTheRunningWindowsSystemVolume()
    {
        if (!OperatingSystem.IsWindows()) return;
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);

        var volumes = new WindowsStorageVolumeInventory().GetVolumes();

        Assert.NotEmpty(volumes);
        Assert.Contains(volumes, volume => string.Equals(volume.Root, systemRoot, StringComparison.OrdinalIgnoreCase));
        Assert.False(volumes.Single(volume => string.Equals(volume.Root, systemRoot, StringComparison.OrdinalIgnoreCase)).CanEject);
        Assert.All(volumes, volume => Assert.False(string.IsNullOrWhiteSpace(volume.StableId)));
    }

    [Fact]
    public void EjectorRejectsFixedStorageWithoutCallingWindowsRemoval()
    {
        var fixedVolume = new StorageVolumeInfo("fixed", "C:\\", "System", 1, 2, "Fixed", 1);
        var result = new WindowsStorageDeviceEjector().Eject(fixedVolume);
        Assert.False(result.Succeeded);
        Assert.Contains("does not support", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
