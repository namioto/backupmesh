namespace BackupMesh.Storage.Service;

// Persists which Source Agents have had their access revoked, so an operator can immediately cut off
// a lost or decommissioned Source without waiting for a certificate to expire or rotating the CA.
// Checked by ControlApiAuthenticationFilter in addition to the usual certificate/token checks.
public sealed class RevokedSourceStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly HashSet<Guid> _revoked;

    public RevokedSourceStore(PairingOptions? pairing = null)
    {
        _path = pairing is null ? null : ResolvePath(pairing.RevokedAgentsPath);
        _revoked = _path is not null && File.Exists(_path)
            ? File.ReadAllLines(_path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => Guid.Parse(line.Trim())).ToHashSet()
            : [];
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
