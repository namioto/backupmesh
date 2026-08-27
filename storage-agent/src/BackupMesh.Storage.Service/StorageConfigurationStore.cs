using System.Text.Json;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Service;

public sealed class StorageConfigurationOptions
{
    public string? PersistencePath { get; set; }
}

public sealed record StorageConfigurationDocument(long Revision, DateTimeOffset UpdatedAt, StorageAgentConfiguration Configuration);
public sealed record StorageConfigurationUpdate(long ExpectedRevision, StorageAgentConfiguration Configuration);

public sealed class StorageConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string? _path;
    private StorageConfigurationDocument _document;

    public StorageConfigurationStore(StorageConfigurationOptions? options = null)
    {
        _path = ResolvePath(options?.PersistencePath);
        _document = Load(_path);
    }

    public StorageConfigurationDocument Get()
    {
        lock (_gate) return _document;
    }

    public (StoreOutcome Outcome, StorageConfigurationDocument Document) Update(StorageConfigurationUpdate update)
    {
        lock (_gate)
        {
            if (update.ExpectedRevision != _document.Revision) return (StoreOutcome.Conflict, _document);
            var next = new StorageConfigurationDocument(_document.Revision + 1, DateTimeOffset.UtcNow, update.Configuration);
            Persist(_path, next);
            _document = next;
            return (StoreOutcome.Accepted, next);
        }
    }

    private static string? ResolvePath(string? configuredPath)
    {
        if (configuredPath == string.Empty) return null;
        if (!string.IsNullOrWhiteSpace(configuredPath)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "storage-configuration.json");
    }

    private static StorageConfigurationDocument Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new(0, DateTimeOffset.MinValue, StorageAgentConfiguration.Empty);
        try
        {
            return JsonSerializer.Deserialize<StorageConfigurationDocument>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("The storage configuration file is empty.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException)
        {
            throw new InvalidDataException($"Could not load the storage configuration file '{path}'.", exception);
        }
    }

    private static void Persist(string? path, StorageConfigurationDocument document)
    {
        if (path is null) return;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The storage configuration path must include a directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
