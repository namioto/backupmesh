using System.Text.Json;

namespace BackupMesh.Storage.Service;

public sealed class SourceCatalogOptions
{
    public string? PersistencePath { get; set; }
}

public sealed class SourceCatalogStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string? _persistencePath;
    private Dictionary<Guid, SourceCatalog> _catalogs;

    public SourceCatalogStore(SourceCatalogOptions? options = null)
    {
        _persistencePath = ResolvePersistencePath(options?.PersistencePath);
        _catalogs = Load(_persistencePath);
    }

    public StoreOutcome Upsert(SourceCatalog catalog)
    {
        lock (_gate)
        {
            if (_catalogs.TryGetValue(catalog.SourceAgentId, out var current))
            {
                if (catalog.UpdatedAt < current.UpdatedAt)
                {
                    return StoreOutcome.InvalidSequence;
                }

                if (catalog.UpdatedAt == current.UpdatedAt)
                {
                    return CatalogsEqual(current, catalog) ? StoreOutcome.Replayed : StoreOutcome.Conflict;
                }
            }

            var updated = new Dictionary<Guid, SourceCatalog>(_catalogs)
            {
                [catalog.SourceAgentId] = catalog
            };

            Persist(_persistencePath, updated.Values);
            _catalogs = updated;
            return StoreOutcome.Accepted;
        }
    }

    private static bool CatalogsEqual(SourceCatalog left, SourceCatalog right) =>
        left.SourceAgentId == right.SourceAgentId
        && left.SourceAgentName == right.SourceAgentName
        && left.UpdatedAt == right.UpdatedAt
        && left.BackupSets.Length == right.BackupSets.Length
        && left.BackupSets.Zip(right.BackupSets).All(pair =>
            pair.First.BackupSetId == pair.Second.BackupSetId
            && pair.First.Name == pair.Second.Name
            && pair.First.SourcePaths.SequenceEqual(pair.Second.SourcePaths, StringComparer.Ordinal));

    public IReadOnlyList<SourceCatalog> List()
    {
        lock (_gate)
        {
            return _catalogs.Values
                .OrderBy(catalog => catalog.SourceAgentName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static string? ResolvePersistencePath(string? configuredPath)
    {
        if (configuredPath == string.Empty)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BackupMesh",
            "source-catalogs.json");
    }

    private static Dictionary<Guid, SourceCatalog> Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var catalogs = JsonSerializer.Deserialize<SourceCatalog[]>(File.ReadAllText(path), SerializerOptions)
                ?? throw new InvalidDataException("The source catalog file contains no catalog collection.");
            return catalogs.ToDictionary(catalog => catalog.SourceAgentId);
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException)
        {
            throw new InvalidDataException($"Could not load the source catalog file '{path}'.", exception);
        }
    }

    private static void Persist(string? path, IEnumerable<SourceCatalog> catalogs)
    {
        if (path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The source catalog persistence path must include a directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var ordered = catalogs.OrderBy(catalog => catalog.SourceAgentName, StringComparer.OrdinalIgnoreCase).ToArray();
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(ordered, SerializerOptions));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
