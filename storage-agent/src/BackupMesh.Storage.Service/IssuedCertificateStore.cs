namespace BackupMesh.Storage.Service;

public sealed class IssuedCertificateOptions { public string? RecordPath { get; set; } }

// Records the expiry of the most recently issued client certificate per Source Agent, purely so the
// tray's Connections view and the renewal check below have something to read. It is not itself a trust
// decision: a Source that presents a certificate this store has never heard of is still validated
// normally by mTLS chain validation and ControlApiAuthenticationFilter.
public sealed class IssuedCertificateStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Dictionary<Guid, (DateTimeOffset ExpiresAt, string? Fingerprint)> _issued;

    public IssuedCertificateStore(IssuedCertificateOptions? options = null)
    {
        _path = options is null ? null : ResolvePath(options.RecordPath);
        _issued = _path is not null && File.Exists(_path) ? Parse(File.ReadAllLines(_path)) : [];
    }

    // fingerprint is optional so every existing caller (and every line in a file persisted before this
    // field existed) keeps working unchanged - a Source paired before this shows an expiry with no
    // fingerprint until it next renews or re-pairs, rather than losing its expiry record outright.
    public void Record(Guid agentId, DateTimeOffset expiresAt, string? fingerprint = null)
    {
        lock (_gate) { _issued[agentId] = (expiresAt, fingerprint); Persist(); }
    }

    public DateTimeOffset? GetExpiry(Guid agentId)
    {
        lock (_gate) return _issued.TryGetValue(agentId, out var record) ? record.ExpiresAt : null;
    }

    public string? GetFingerprint(Guid agentId)
    {
        lock (_gate) return _issued.TryGetValue(agentId, out var record) ? record.Fingerprint : null;
    }

    private static Dictionary<Guid, (DateTimeOffset, string?)> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<Guid, (DateTimeOffset, string?)>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Trim().Split('|', 3);
            if (parts.Length >= 2 && Guid.TryParse(parts[0], out var agentId) && DateTimeOffset.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt))
                result[agentId] = (expiresAt, parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null);
        }
        return result;
    }

    private void Persist()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Issued certificate record path must include a directory."));
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try { File.WriteAllLines(temporary, _issued.Select(entry => $"{entry.Key}|{entry.Value.ExpiresAt:O}|{entry.Value.Fingerprint}")); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? ResolvePath(string? path) => path == string.Empty ? null : !string.IsNullOrWhiteSpace(path)
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "issued-certificates.txt");
}
