using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Tests;

public sealed class WindowsStorageVolumeInventoryTests
{
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
