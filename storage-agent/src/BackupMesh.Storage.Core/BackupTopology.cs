namespace BackupMesh.Storage.Core;

public sealed record RegisteredDevice(
    Guid Id,
    string StableId,
    string DisplayName,
    string? VolumeLabel,
    string? LastKnownRoot,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSeenAt,
    int ArrivalDelayMinutes = 30);

public sealed record SourceBackupSet(
    Guid Id,
    Guid SourceAgentId,
    string SourceAgentName,
    string Name,
    IReadOnlyList<string> SourcePaths);

public sealed record BackupTargetMapping(
    Guid Id,
    Guid BackupSetId,
    Guid DeviceId,
    string RepositoryPath,
    bool Enabled = true);

public sealed record StorageAgentConfiguration(
    IReadOnlyList<RegisteredDevice> Devices,
    IReadOnlyList<SourceBackupSet> BackupSets,
    IReadOnlyList<BackupTargetMapping> Mappings)
{
    public static StorageAgentConfiguration Empty { get; } = new([], [], []);
}

public static class FolderStorageIdentity
{
    private const string Prefix = "folder:";
    public static string Create(string path) => Prefix + Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
    public static bool TryGetPath(string stableId, out string path)
    {
        path = string.Empty;
        if (!stableId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var candidate = stableId[Prefix.Length..];
        if (!Path.IsPathRooted(candidate)) return false;
        path = Path.GetFullPath(candidate);
        return true;
    }
}

public static class BackupTopologyValidator
{
    public static IReadOnlyList<string> Validate(StorageAgentConfiguration configuration)
    {
        var errors = new List<string>();
        var deviceIds = configuration.Devices.Select(device => device.Id).ToHashSet();
        var backupSetIds = configuration.BackupSets.Select(set => set.Id).ToHashSet();

        if (deviceIds.Count != configuration.Devices.Count) errors.Add("Registered device IDs must be unique.");
        if (configuration.Devices.Select(device => device.StableId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.Devices.Count)
            errors.Add("Registered device identities must be unique.");
        if (backupSetIds.Count != configuration.BackupSets.Count) errors.Add("Backup Set IDs must be unique.");
        foreach (var device in configuration.Devices)
        {
            if (device.Id == Guid.Empty || string.IsNullOrWhiteSpace(device.StableId) || string.IsNullOrWhiteSpace(device.DisplayName))
                errors.Add("Registered devices must have an ID, stable identity, and display name.");
        }
        foreach (var backupSet in configuration.BackupSets)
        {
            if (backupSet.Id == Guid.Empty || backupSet.SourceAgentId == Guid.Empty || string.IsNullOrWhiteSpace(backupSet.Name))
                errors.Add("Backup Sets must have valid IDs, a Source Agent ID, and a name.");
        }

        foreach (var mapping in configuration.Mappings)
        {
            if (mapping.Id == Guid.Empty) errors.Add("Mappings must have a valid ID.");
            if (!deviceIds.Contains(mapping.DeviceId)) errors.Add($"Mapping {mapping.Id} references an unknown device.");
            if (!backupSetIds.Contains(mapping.BackupSetId)) errors.Add($"Mapping {mapping.Id} references an unknown backup set.");
            if (!IsSafeRelativeRepositoryPath(mapping.RepositoryPath)) errors.Add($"Mapping {mapping.Id} has an unsafe repository path.");
        }

        foreach (var device in configuration.Devices)
            if (device.ArrivalDelayMinutes is < 0 or > 1440) errors.Add($"Device {device.Id} has an invalid arrival delay.");

        var duplicates = configuration.Mappings
            .Where(mapping => mapping.Enabled)
            .GroupBy(mapping => new { mapping.DeviceId, Path = Normalize(mapping.RepositoryPath) })
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicates)
            errors.Add($"Multiple enabled mappings target the same device path '{duplicate.Key.Path}'.");

        return errors;
    }

    public static bool IsSafeRelativeRepositoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
        if (path.Trim() == ".") return false;
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        const string invalidWindowsCharacters = "<>:\"|?*";
        return segments.Length > 0 && segments.All(segment =>
            segment is not "." and not ".."
            && !string.IsNullOrWhiteSpace(segment)
            && !segment.EndsWith(' ')
            && !segment.EndsWith('.')
            && !segment.Any(character => char.IsControl(character) || invalidWindowsCharacters.Contains(character)));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/').ToUpperInvariant();
}
