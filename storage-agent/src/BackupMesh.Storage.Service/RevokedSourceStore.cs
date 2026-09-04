namespace BackupMesh.Storage.Service;

// Persists which Source Agents have had their access revoked, so an operator can immediately cut off
// a lost or decommissioned Source without waiting for a certificate to expire or rotating the CA.
// Checked by ControlApiAuthenticationFilter in addition to the usual certificate/token checks.
public sealed class RevokedSourceStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly HashSet<Guid> _revoked;

    public RevokedSourceStore(PairingOptions? pairing = null, ILogger<RevokedSourceStore>? logger = null)
    {
        _path = pairing is null ? null : ResolvePath(pairing.RevokedAgentsPath);
        _revoked = _path is not null && File.Exists(_path) ? Parse(File.ReadAllLines(_path), _path, logger) : [];
    }

    // Skip unreadable entries rather than throwing. This store is constructed while DI builds
    // ControlApiAuthenticationFilter, so one malformed line would otherwise fail every control-API
    // request - including the tray's - with no recovery but deleting the file. The file sits beside the
    // pairing credential hashes under the same ProgramData ACL, so anyone able to corrupt a line could
    // equally delete it; the warning makes the resulting loss of revocation visible.
    private static HashSet<Guid> Parse(IEnumerable<string> lines, string path, ILogger<RevokedSourceStore>? logger)
    {
        var revoked = new HashSet<Guid>();
        var skipped = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (Guid.TryParse(line.Trim(), out var agentId)) revoked.Add(agentId);
            else skipped++;
        }
        if (skipped > 0)
            logger?.LogWarning("Ignored {SkippedCount} unreadable entries in {RevokedAgentsPath}; any Source Agent they named is no longer revoked.", skipped, path);
        return revoked;
    }

    public bool IsRevoked(Guid agentId)
    {
        lock (_gate) return _revoked.Contains(agentId);
    }

    public void Revoke(Guid agentId)
    {
        lock (_gate)
        {
            if (_revoked.Add(agentId)) Persist();
        }
    }

    public bool Unrevoke(Guid agentId)
    {
        lock (_gate)
        {
            if (!_revoked.Remove(agentId)) return false;
            Persist();
            return true;
        }
    }

    public IReadOnlyCollection<Guid> List()
    {
        lock (_gate) return _revoked.ToArray();
    }

    private void Persist()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Revoked-agents path must include a directory."));
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try { File.WriteAllLines(temporary, _revoked.Select(id => id.ToString())); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? ResolvePath(string? path) => path == string.Empty ? null : !string.IsNullOrWhiteSpace(path)
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "revoked-agents.txt");
}
