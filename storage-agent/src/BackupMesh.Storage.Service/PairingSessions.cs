using System.Security.Cryptography;
using System.Text;

namespace BackupMesh.Storage.Service;

// RebindAgentId, when set, is the only agent_id this code's /pairing/exchange may claim - it is how a
// tray-driven "re-pair this existing Source" action is distinguished from ordinary new-Source pairing.
// Without it, anyone holding a valid one-time code (issued for a brand new Source) could name an
// unrelated, already-paired agent_id - which is not a secret, it is shown in the tray's Connections list
// - and take over that Source's identity and catalog. See ControlApi's /pairing/exchange handler.
public sealed record PairingSession(string Code, DateTimeOffset ExpiresAt, Guid? RebindAgentId = null);

public sealed class PairingSessionStore
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly TimeProvider _clock;

    public PairingSessionStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public PairingSession Create(Guid? rebindAgentId = null)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = _clock.GetUtcNow().AddMinutes(10);
        lock (_gate)
        {
            _entries.RemoveAll(entry => entry.ExpiresAt <= _clock.GetUtcNow());
            _entries.Add(new(SHA256.HashData(Encoding.UTF8.GetBytes(code)), expiresAt, rebindAgentId));
        }
        return new(code, expiresAt, rebindAgentId);
    }

    public bool TryConsume(string code, out Guid? rebindAgentId)
    {
        rebindAgentId = null;
        if (string.IsNullOrWhiteSpace(code)) return false;
        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var index = _entries.FindIndex(entry => entry.ExpiresAt > now && CryptographicOperations.FixedTimeEquals(entry.Hash, candidate));
            if (index < 0) return false;
            rebindAgentId = _entries[index].RebindAgentId;
            _entries.RemoveAt(index);
            return true;
        }
    }

    private sealed record Entry(byte[] Hash, DateTimeOffset ExpiresAt, Guid? RebindAgentId);
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
            // /pairing/exchange is reachable from the network, so without this an attacker holding a
            // large address range (an IPv6 /64 costs nothing) could grow this dictionary without bound
            // by sending one bad code from each address. Entries still inside their window, or still
            // locked out, are kept.
            foreach (var stale in _windows.Where(pair => now >= pair.Value.ExpiresAt && now >= pair.Value.LockedUntil).Select(pair => pair.Key).ToArray())
                _windows.Remove(stale);
            _windows.TryGetValue(remote, out var existing);
            // Carry an active lockout across a window rollover so a new failure cannot shorten it.
            var lockedUntil = existing is not null && now < existing.LockedUntil ? existing.LockedUntil : DateTimeOffset.MinValue;
            var window = existing is not null && now < existing.ExpiresAt ? existing : new Window(0, now.Add(WindowDuration), lockedUntil);
            var count = window.Count + 1;
            _windows[remote] = window with { Count = count, LockedUntil = count >= MaxFailuresPerWindow ? now.Add(LockoutDuration) : window.LockedUntil };
        }
    }

    // Test seam: how many remote addresses are currently being tracked.
    internal int TrackedAddressCount { get { lock (_gate) return _windows.Count; } }

    public void RecordSuccess(System.Net.IPAddress? remote)
    {
        if (remote is not null) lock (_gate) _windows.Remove(remote);
    }

    private sealed record Window(int Count, DateTimeOffset ExpiresAt, DateTimeOffset LockedUntil);
}
