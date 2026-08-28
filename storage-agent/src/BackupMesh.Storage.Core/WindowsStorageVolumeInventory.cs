using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;

namespace BackupMesh.Storage.Core;

public sealed record StorageVolumeInfo(string StableId, string Root, string VolumeLabel, long AvailableBytes, long TotalBytes, string HardwareName, int VolumeCount, bool CanEject = false, string? DeviceInstanceId = null);

public interface IStorageVolumeInventory
{
    IReadOnlyList<StorageVolumeInfo> GetVolumes();
}

public sealed class WindowsStorageVolumeInventory : IStorageVolumeInventory
{
    public IReadOnlyList<StorageVolumeInfo> GetVolumes()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try { return QueryPhysicalDisks(); }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException) { return FallbackVolumes(); }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<StorageVolumeInfo> QueryPhysicalDisks()
    {
        var devices = new List<StorageVolumeInfo>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Model, SerialNumber, PNPDeviceID, InterfaceType, MediaType FROM Win32_DiskDrive");
        using var disks = searcher.Get();
        foreach (ManagementObject disk in disks)
        {
            using (disk)
            {
                var volumes = GetDiskVolumes(disk).Where(volume => volume.TotalBytes > 64L * 1024 * 1024).ToArray();
                if (volumes.Length == 0) continue;
                var serial = Text(disk["SerialNumber"]).Trim();
                var pnpId = Text(disk["PNPDeviceID"]).Trim();
                var deviceId = Text(disk["DeviceID"]).Trim();
                var physicalId = !string.IsNullOrWhiteSpace(serial) ? $"serial:{serial}" : !string.IsNullOrWhiteSpace(pnpId) ? $"pnp:{pnpId}" : $"device:{deviceId}";
                var model = Text(disk["Model"]).Trim();
                var canEject = Text(disk["InterfaceType"]).Equals("USB", StringComparison.OrdinalIgnoreCase)
                    || Text(disk["MediaType"]).Contains("removable", StringComparison.OrdinalIgnoreCase);
                foreach (var volume in volumes)
                {
                    var stableId = $"{physicalId}|volume:{volume.VolumeLabel}:{volume.TotalBytes}";
                    var name = string.IsNullOrWhiteSpace(model) ? volume.VolumeLabel : model;
                    devices.Add(new(stableId, volume.Root, volume.VolumeLabel, volume.AvailableBytes, volume.TotalBytes, name, volumes.Length, canEject, canEject ? pnpId : null));
                }
            }
        }
        return devices.OrderBy(device => device.HardwareName, StringComparer.OrdinalIgnoreCase).ThenBy(device => device.Root).ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<DetectedVolume> GetDiskVolumes(ManagementObject disk)
    {
        foreach (ManagementObject partition in disk.GetRelated("Win32_DiskPartition"))
            using (partition)
                foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                    using (logical)
                    {
                        var root = Text(logical["DeviceID"]);
                        if (string.IsNullOrWhiteSpace(root)) continue;
                        var info = new DriveInfo(root + Path.DirectorySeparatorChar);
                        if (!info.IsReady) continue;
                        yield return new(info.RootDirectory.FullName, string.IsNullOrWhiteSpace(info.VolumeLabel) ? "Data volume" : info.VolumeLabel, info.AvailableFreeSpace, info.TotalSize);
                    }
    }

    private static IReadOnlyList<StorageVolumeInfo> FallbackVolumes() => DriveInfo.GetDrives()
        .Where(drive => (drive.DriveType is DriveType.Fixed or DriveType.Removable) && drive.IsReady && drive.TotalSize > 64L * 1024 * 1024)
        .Select(FromDrive).OrderBy(device => device.Root).ToArray();

    private static StorageVolumeInfo FromDrive(DriveInfo drive)
    {
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local disk" : drive.VolumeLabel;
        return new($"{drive.DriveFormat}|{label}|{drive.TotalSize}", drive.RootDirectory.FullName, label, drive.AvailableFreeSpace, drive.TotalSize, label, 1);
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    private sealed record DetectedVolume(string Root, string VolumeLabel, long AvailableBytes, long TotalBytes);
}

public sealed record StorageEjectResult(bool Succeeded, string Message);
public interface IStorageDeviceEjector { StorageEjectResult Eject(StorageVolumeInfo volume); }

public sealed class WindowsStorageDeviceEjector : IStorageDeviceEjector
{
    public StorageEjectResult Eject(StorageVolumeInfo volume)
    {
        if (!OperatingSystem.IsWindows() || !volume.CanEject || string.IsNullOrWhiteSpace(volume.DeviceInstanceId))
            return new(false, "This storage device does not support safe removal.");
        var locate = CM_Locate_DevNodeW(out var device, volume.DeviceInstanceId, 0);
        if (locate != 0) return new(false, $"Windows could not locate the storage device (code {locate}).");
        var vetoName = new StringBuilder(260);
        var result = CM_Request_Device_EjectW(device, out var veto, vetoName, vetoName.Capacity, 0);
        if (result == 0) return new(true, "Windows accepted the safe-removal request.");
        var detail = vetoName.Length == 0 ? veto.ToString() : vetoName.ToString();
        return new(false, $"Windows refused safe removal ({detail}, code {result}).");
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint deviceInstance, string deviceId, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_EjectW(uint deviceInstance, out PnpVetoType vetoType, StringBuilder vetoName, int nameLength, int flags);
    private enum PnpVetoType { Unknown, LegacyDevice, PendingClose, WindowsApp, WindowsService, OutstandingOpen, Device, Driver, IllegalDeviceRequest, InsufficientPower, NonDisableable, LegacyDriver }
}
