using System.Management;
using System.IO;

namespace BackupMesh.Storage.App;

public interface IDeviceInventory
{
    IReadOnlyList<AvailableDriveViewModel> GetStorageDevices();
}

public sealed class WindowsDeviceInventory : IDeviceInventory
{
    public IReadOnlyList<AvailableDriveViewModel> GetStorageDevices()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            return QueryPhysicalDisks();
        }
        catch (ManagementException)
        {
            return FallbackVolumes();
        }
        catch (UnauthorizedAccessException)
        {
            return FallbackVolumes();
        }
    }

    private static IReadOnlyList<AvailableDriveViewModel> QueryPhysicalDisks()
    {
        var devices = new List<AvailableDriveViewModel>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Model, SerialNumber, PNPDeviceID, InterfaceType, MediaType FROM Win32_DiskDrive");
        using var disks = searcher.Get();
        foreach (ManagementObject disk in disks)
        {
            var interfaceType = Text(disk["InterfaceType"]);
            var mediaType = Text(disk["MediaType"]);
            if (!interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase)) continue;

            var volumes = GetVolumes(disk)
                .Where(volume => volume.TotalBytes > 64L * 1024 * 1024)
                .OrderByDescending(volume => volume.TotalBytes)
                .ToArray();
            if (volumes.Length == 0) continue;
            var dataVolume = volumes[0];
            var serial = Text(disk["SerialNumber"]).Trim();
            var pnpId = Text(disk["PNPDeviceID"]).Trim();
            var deviceId = Text(disk["DeviceID"]).Trim();
            var stableId = !string.IsNullOrWhiteSpace(serial) ? $"serial:{serial}" : !string.IsNullOrWhiteSpace(pnpId) ? $"pnp:{pnpId}" : $"device:{deviceId}";
            var model = Text(disk["Model"]).Trim();
            var name = string.IsNullOrWhiteSpace(model) ? dataVolume.VolumeLabel : model;
            devices.Add(new(stableId, dataVolume.Root, dataVolume.VolumeLabel, dataVolume.AvailableBytes, dataVolume.TotalBytes, name, volumes.Length));
        }
        return devices.OrderBy(device => device.HardwareName).ToArray();
    }

    private static IEnumerable<DetectedVolume> GetVolumes(ManagementObject disk)
    {
        foreach (ManagementObject partition in disk.GetRelated("Win32_DiskPartition"))
            using (partition)
            {
                foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                    using (logical)
                    {
                        var root = Text(logical["DeviceID"]);
                        if (string.IsNullOrWhiteSpace(root)) continue;
                        var info = new DriveInfo(root + Path.DirectorySeparatorChar);
                        if (!info.IsReady) continue;
                        yield return new(info.RootDirectory.FullName,
                            string.IsNullOrWhiteSpace(info.VolumeLabel) ? "Data volume" : info.VolumeLabel,
                            info.AvailableFreeSpace,
                            info.TotalSize);
                    }
            }
    }

    private static IReadOnlyList<AvailableDriveViewModel> FallbackVolumes() => DriveInfo.GetDrives()
        .Where(drive => drive.DriveType == DriveType.Removable && drive.IsReady && drive.TotalSize > 64L * 1024 * 1024)
        .Select(AvailableDriveViewModel.FromDrive)
        .OrderBy(device => device.Root)
        .ToArray();

    private static string Text(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    private sealed record DetectedVolume(string Root, string VolumeLabel, long AvailableBytes, long TotalBytes);
}
