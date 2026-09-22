using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;

namespace BackupMesh.Storage.Service;

public sealed record NearbyRemoteAgent([property: System.Text.Json.Serialization.JsonPropertyName("agent_id")] Guid AgentId, [property: System.Text.Json.Serialization.JsonPropertyName("agent_name")] string AgentName, [property: System.Text.Json.Serialization.JsonPropertyName("identity")] string Identity, [property: System.Text.Json.Serialization.JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt);
public sealed record RemotePairingRequest([property: System.Text.Json.Serialization.JsonPropertyName("request_id")] Guid RequestId, [property: System.Text.Json.Serialization.JsonPropertyName("agent_id")] Guid AgentId, [property: System.Text.Json.Serialization.JsonPropertyName("agent_name")] string AgentName, [property: System.Text.Json.Serialization.JsonPropertyName("identity")] string Identity, [property: System.Text.Json.Serialization.JsonPropertyName("comparison_code")] string ComparisonCode, [property: System.Text.Json.Serialization.JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt, [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status);
public sealed record PairingExchangeResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("agent_id")] Guid AgentId,
    [property: System.Text.Json.Serialization.JsonPropertyName("control_endpoint")] string ControlEndpoint,
    [property: System.Text.Json.Serialization.JsonPropertyName("credential")] string Credential,
    [property: System.Text.Json.Serialization.JsonPropertyName("certificate_pem")] string CertificatePem,
    [property: System.Text.Json.Serialization.JsonPropertyName("private_key_pem")] string PrivateKeyPem,
    [property: System.Text.Json.Serialization.JsonPropertyName("authority_pem")] string AuthorityPem,
    [property: System.Text.Json.Serialization.JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("issued_at")] DateTimeOffset IssuedAt);

public sealed class RemotePairingRequestStore(TimeProvider? clock = null)
{
    private const int MaxNearby = 256;
    private const int MaxRequests = 256;
    private static readonly TimeSpan NearbyLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, NearbyEntry> _nearby = [];
    private readonly Dictionary<Guid, RequestEntry> _requests = [];

    public bool Announce(Guid agentId, string? agentName, string? identity, string? publicKey)
    {
        if (agentId == Guid.Empty || string.IsNullOrWhiteSpace(agentName) || agentName.Length > 128 ||
            agentName.Any(char.IsControl) ||
            identity is not { Length: 64 } || !identity.All(char.IsAsciiHexDigit) ||
            string.IsNullOrWhiteSpace(publicKey) || publicKey.Length > 1024) return false;
        byte[] key;
        try
        {
            key = Convert.FromBase64String(publicKey);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(key, out var read);
            if (read != key.Length || rsa.KeySize < 2048 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(key), Convert.FromHexString(identity))) return false;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException) { return false; }

        lock (_gate)
        {
            Prune();
            // Do not silently replace a live identity. A forged announcement must not redirect a request
            // after the Storage user selected the computer shown in the UI.
            if (_nearby.TryGetValue(agentId, out var current) && current.Identity != identity.ToLowerInvariant()) return false;
            if (_requests.Values.Any(x => x.AgentId == agentId && x.Status == "PENDING" && x.Identity != identity.ToLowerInvariant())) return false;
            if (!_nearby.ContainsKey(agentId) && _nearby.Count >= MaxNearby) return false;
            _nearby[agentId] = new(agentId, agentName.Trim(), identity.ToLowerInvariant(), key, _clock.GetUtcNow());
            return true;
        }
    }

    public IReadOnlyList<NearbyRemoteAgent> ListNearby()
    {
        lock (_gate)
        {
            Prune();
            return _nearby.Values.Select(x => new NearbyRemoteAgent(x.AgentId, x.AgentName, x.Identity, x.LastSeenAt)).ToArray();
        }
    }

    public RemotePairingRequest? Create(Guid agentId, string identity, string storageIdentity, PairingSessionStore sessions)
    {
        lock (_gate)
        {
            Prune();
            if (!_nearby.TryGetValue(agentId, out var remote) || remote.Identity != identity.ToLowerInvariant()) return null;
            if (_requests.Count >= MaxRequests) return null;
            foreach (var pending in _requests.Values.Where(x => x.AgentId == agentId && x.Status == "PENDING")) pending.Status = "REPLACED";
            var session = sessions.Create();
            var requestId = Guid.NewGuid();
            var comparison = ComparisonCode(session.Code, requestId, remote.Identity, storageIdentity);
            var encryptedCode = Encrypt(remote.PublicKey, session.Code);
            var entry = new RequestEntry(requestId, remote.AgentId, remote.AgentName, remote.Identity, comparison,
                _clock.GetUtcNow().Add(RequestLifetime), "PENDING", SHA256.HashData(Encoding.UTF8.GetBytes(session.Code)), encryptedCode);
            _requests.Add(requestId, entry);
            return Public(entry);
        }
    }

    public RemotePairingRequest? Get(Guid requestId)
    {
        lock (_gate) { Prune(); return _requests.TryGetValue(requestId, out var entry) ? Public(entry) : null; }
    }

    public IReadOnlyList<RemotePairingRequest> ListPending(Guid agentId)
    {
        lock (_gate)
        {
            Prune();
            return _requests.Values.Where(x => x.AgentId == agentId && x.Status is "PENDING" or "ISSUED").Select(Public).ToArray();
        }
    }

    public object? Claim(Guid requestId, Guid agentId, string identity)
    {
        lock (_gate)
        {
            Prune();
            if (!_requests.TryGetValue(requestId, out var entry) || entry.AgentId != agentId || entry.Identity != identity.ToLowerInvariant() || entry.Status is not ("PENDING" or "ISSUED")) return null;
            return new { request_id = entry.RequestId, encrypted_code = Convert.ToBase64String(entry.EncryptedCode), expires_at = entry.ExpiresAt };
        }
    }

    public bool Reject(Guid requestId)
    {
        lock (_gate)
        {
            Prune();
            if (!_requests.TryGetValue(requestId, out var entry) || entry.Status != "PENDING") return false;
            entry.Status = "REJECTED";
            return true;
        }
    }

    public bool Reject(Guid requestId, Guid agentId, string identity, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        lock (_gate)
        {
            Prune();
            if (!_requests.TryGetValue(requestId, out var entry) || entry.AgentId != agentId ||
                entry.Identity != identity.ToLowerInvariant() || entry.Status != "PENDING" ||
                !CryptographicOperations.FixedTimeEquals(entry.CodeHash, hash)) return false;
            entry.Status = "REJECTED";
            return true;
        }
    }

    public PairingExchangeResponse? GetIssued(string? code, Guid agentId)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        lock (_gate)
        {
            Prune();
            var entry = _requests.Values.FirstOrDefault(x => x.AgentId == agentId && x.Status == "ISSUED" && CryptographicOperations.FixedTimeEquals(x.CodeHash, hash));
            return entry?.Issued;
        }
    }

    public void MarkConnected(Guid agentId)
    {
        lock (_gate)
        {
            Prune();
            foreach (var entry in _requests.Values)
                if (entry.AgentId == agentId && entry.Status == "ISSUED")
                {
                    entry.Status = "PAIRED";
                    entry.Issued = null;
                }
        }
    }

    public RemoteCodeAuthorization Authorize(string code, Guid agentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        lock (_gate)
        {
            Prune();
            var entry = _requests.Values.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(x.CodeHash, hash));
            if (entry is null) return RemoteCodeAuthorization.Unmanaged;
            if (entry.Status != "PENDING" || entry.AgentId != agentId) return RemoteCodeAuthorization.Denied;
            entry.Status = "EXCHANGING";
            return RemoteCodeAuthorization.Authorized;
        }
    }

    public void MarkIssued(string code, Guid agentId, PairingExchangeResponse response)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        lock (_gate)
        {
            foreach (var entry in _requests.Values)
                if (entry.AgentId == agentId && entry.Status == "EXCHANGING" && CryptographicOperations.FixedTimeEquals(entry.CodeHash, hash))
                {
                    entry.Issued = response;
                    entry.Status = "ISSUED";
                }
        }
    }

    public void MarkFailed(string code)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        lock (_gate)
            foreach (var entry in _requests.Values)
                if (entry.Status == "EXCHANGING" && CryptographicOperations.FixedTimeEquals(entry.CodeHash, hash)) entry.Status = "FAILED";
    }

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var id in _nearby.Where(x => x.Value.LastSeenAt.Add(NearbyLifetime) <= now).Select(x => x.Key).ToArray()) _nearby.Remove(id);
        foreach (var entry in _requests.Values.Where(x => x.Status is "PENDING" or "EXCHANGING" or "ISSUED" or "PAIRED" && x.ExpiresAt <= now))
        {
            entry.Status = "EXPIRED";
            entry.Issued = null;
        }
        foreach (var id in _requests.Where(x => x.Value.ExpiresAt.Add(RequestLifetime) <= now).Select(x => x.Key).ToArray()) _requests.Remove(id);
    }

    private static byte[] Encrypt(byte[] publicKey, string value)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        return rsa.Encrypt(Encoding.UTF8.GetBytes(value), RSAEncryptionPadding.OaepSHA256);
    }

    internal static string ComparisonCode(string code, Guid requestId, string remoteIdentity, string storageIdentity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{code}|{requestId:N}|{remoteIdentity.ToLowerInvariant()}|{storageIdentity.ToLowerInvariant()}"));
        return (BinaryPrimitives.ReadUInt32LittleEndian(hash) % 1_000_000).ToString("D6");
    }

    private static RemotePairingRequest Public(RequestEntry x) => new(x.RequestId, x.AgentId, x.AgentName, x.Identity, x.ComparisonCode, x.ExpiresAt, x.Status);
    private sealed record NearbyEntry(Guid AgentId, string AgentName, string Identity, byte[] PublicKey, DateTimeOffset LastSeenAt);
    private sealed class RequestEntry(Guid requestId, Guid agentId, string agentName, string identity, string comparisonCode, DateTimeOffset expiresAt, string status, byte[] codeHash, byte[] encryptedCode)
    {
        public Guid RequestId { get; } = requestId; public Guid AgentId { get; } = agentId; public string AgentName { get; } = agentName;
        public string Identity { get; } = identity; public string ComparisonCode { get; } = comparisonCode; public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string Status { get; set; } = status; public byte[] CodeHash { get; } = codeHash; public byte[] EncryptedCode { get; } = encryptedCode; public PairingExchangeResponse? Issued { get; set; }
    }
}

public enum RemoteCodeAuthorization { Unmanaged, Authorized, Denied }
