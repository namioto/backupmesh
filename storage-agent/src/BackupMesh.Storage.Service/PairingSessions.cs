using System.Security.Cryptography;
using System.Text;

namespace BackupMesh.Storage.Service;

public sealed record PairingSession(string Code, DateTimeOffset ExpiresAt);

public sealed class PairingSessionStore
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly TimeProvider _clock;

    public PairingSessionStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public PairingSession Create()
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = _clock.GetUtcNow().AddMinutes(10);
        lock (_gate)
        {
            _entries.RemoveAll(entry => entry.ExpiresAt <= _clock.GetUtcNow());
            _entries.Add(new(SHA256.HashData(Encoding.UTF8.GetBytes(code)), expiresAt));
        }
        return new(code, expiresAt);
    }

    public bool Consume(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var index = _entries.FindIndex(entry => entry.ExpiresAt > now && CryptographicOperations.FixedTimeEquals(entry.Hash, candidate));
            if (index < 0) return false;
            _entries.RemoveAt(index);
            return true;
        }
    }

    private sealed record Entry(byte[] Hash, DateTimeOffset ExpiresAt);
}

// Throttles /pairing/exchange by remote address so repeated wrong-code guesses get locked out even though
// the 160-bit code itself is not brute-forceable in practice. Never key or log by the code, token, or
// private key involved in an attempt.
public sealed class PairingAttemptThrottle(TimeProvider? clock = null)
{
    private const int MaxFailuresPerWindow = 5;
    private static readonly TimeSpan WindowDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<System.Net.IPAddress, Window> _windows = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public bool IsLockedOut(System.Net.IPAddress? remote)
    {
        if (remote is null) return false;
        lock (_gate)
            return _windows.TryGetValue(remote, out var window) && _clock.GetUtcNow() < window.LockedUntil;
    }

    public void RecordFailure(System.Net.IPAddress? remote)
    {
        if (remote is null) return;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var window = _windows.TryGetValue(remote, out var existing) && now < existing.ExpiresAt ? existing : new Window(0, now.Add(WindowDuration), DateTimeOffset.MinValue);
            var count = window.Count + 1;
            _windows[remote] = window with { Count = count, LockedUntil = count >= MaxFailuresPerWindow ? now.Add(LockoutDuration) : window.LockedUntil };
        }
    }

    public void RecordSuccess(System.Net.IPAddress? remote)
    {
        if (remote is not null) lock (_gate) _windows.Remove(remote);
    }

    private sealed record Window(int Count, DateTimeOffset ExpiresAt, DateTimeOffset LockedUntil);
}
