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
        Assert.All(volumes, volume => Assert.False(string.IsNullOrWhiteSpace(volume.StableId)));
    }
}
