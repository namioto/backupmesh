using System.Text.Json;

namespace BackupMesh.Storage.Service;

public sealed class SourceDisplayNameOptions { public string? RecordPath { get; set; } }

// A Storage-side rename overrides only what the tray displays; it never changes the name a Source
// reports in its own catalog sync, so renaming here cannot desynchronize the two - the Source keeps
// calling itself whatever is in its own config.
public sealed class SourceDisplayNameStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Dictionary<Guid, string> _names;

    public SourceDisplayNameStore(SourceDisplayNameOptions? options = null)
    {
        _path = options is null ? null : ResolvePath(options.RecordPath);
        _names = _path is not null && File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    public void Set(Guid agentId, string? displayName)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(displayName)) _names.Remove(agentId);
            else _names[agentId] = displayName.Trim();
            Persist();
        }
    }

    public string? Get(Guid agentId)
    {
        lock (_gate) return _names.TryGetValue(agentId, out var name) ? name : null;
    }

    private void Persist()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Source display name path must include a directory."));
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(_names)); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? ResolvePath(string? path) => path == string.Empty ? null : !string.IsNullOrWhiteSpace(path)
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "source-display-names.json");
}
