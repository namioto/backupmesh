using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.App;

public interface IDeviceInventory
{
    IReadOnlyList<AvailableDriveViewModel> GetStorageDevices();
}

public sealed class WindowsDeviceInventory : IDeviceInventory
{
    private readonly IStorageVolumeInventory _inventory = new WindowsStorageVolumeInventory();

    public IReadOnlyList<AvailableDriveViewModel> GetStorageDevices() => _inventory.GetVolumes()
        .Select(volume => new AvailableDriveViewModel(volume.StableId, volume.Root, volume.VolumeLabel,
            volume.AvailableBytes, volume.TotalBytes, volume.HardwareName, volume.VolumeCount))
        .ToArray();
}
