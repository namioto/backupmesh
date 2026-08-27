namespace BackupMesh.Storage.Core;

public sealed record RegisteredDevice(
    Guid Id,
    string StableId,
    string DisplayName,
    string? VolumeLabel,
    string? LastKnownRoot,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSeenAt);

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

public static class BackupTopologyValidator
{
    public static IReadOnlyList<string> Validate(StorageAgentConfiguration configuration)
    {
        var errors = new List<string>();
        var deviceIds = configuration.Devices.Select(device => device.Id).ToHashSet();
        var backupSetIds = configuration.BackupSets.Select(set => set.Id).ToHashSet();

        foreach (var mapping in configuration.Mappings)
        {
            if (!deviceIds.Contains(mapping.DeviceId)) errors.Add($"Mapping {mapping.Id} references an unknown device.");
            if (!backupSetIds.Contains(mapping.BackupSetId)) errors.Add($"Mapping {mapping.Id} references an unknown backup set.");
            if (!IsSafeRelativeRepositoryPath(mapping.RepositoryPath)) errors.Add($"Mapping {mapping.Id} has an unsafe repository path.");
        }

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
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment is not "." and not "..");
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/').ToUpperInvariant();
}
