using System.Text.Json;

namespace BackupMesh.Storage.Service;

public sealed class AutomationSettingsOptions { public string? PersistencePath { get; set; } }
public sealed record AutomationSettings(bool Enabled = true);

public sealed class AutomationSettingsStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private AutomationSettings _settings;

    public AutomationSettingsStore(AutomationSettingsOptions? options = null)
    {
        _path = ResolvePath(options?.PersistencePath);
        _settings = _path is not null && File.Exists(_path)
            ? JsonSerializer.Deserialize<AutomationSettings>(File.ReadAllText(_path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new()
            : new();
    }

    public AutomationSettings Get() { lock (_gate) return _settings; }

    public AutomationSettings Update(AutomationSettings settings)
    {
        lock (_gate)
        {
            if (_path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Automation settings path must include a directory."));
                var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
                try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })); File.Move(temporary, _path, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return _settings = settings;
        }
    }

    private static string? ResolvePath(string? path) => path == string.Empty ? null : !string.IsNullOrWhiteSpace(path)
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "automation-settings.json");
}
