using System.Security.Cryptography;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class RemotePairingRequestStoreTests
{
    [Fact]
    public void ReplacedRequestCodeCannotPairAndSelectedIdentityCannotChange()
    {
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        var store = new RemotePairingRequestStore();
        var sessions = new PairingSessionStore();
        var agentId = Guid.NewGuid();
        var firstKey = first.ExportSubjectPublicKeyInfo();
        var firstIdentity = Convert.ToHexString(SHA256.HashData(firstKey)).ToLowerInvariant();
        var secondKey = second.ExportSubjectPublicKeyInfo();
        var secondIdentity = Convert.ToHexString(SHA256.HashData(secondKey)).ToLowerInvariant();

        Assert.True(store.Announce(agentId, "remote", firstIdentity, Convert.ToBase64String(firstKey)));
        var replaced = Assert.IsType<RemotePairingRequest>(store.Create(agentId, firstIdentity, new string('a', 64), sessions));
        var replacedCode = Decrypt(first, store.Claim(replaced.RequestId, agentId, firstIdentity));
        var active = Assert.IsType<RemotePairingRequest>(store.Create(agentId, firstIdentity, new string('a', 64), sessions));
        Assert.Equal("REPLACED", store.Get(replaced.RequestId)!.Status);
        Assert.Equal(RemoteCodeAuthorization.Denied, store.Authorize(replacedCode, agentId));
        Assert.False(store.Announce(agentId, "spoof", secondIdentity, Convert.ToBase64String(secondKey)));
        Assert.Equal("PENDING", store.Get(active.RequestId)!.Status);
    }

    [Fact]
    public void ComparisonCodeMatchesSharedLittleEndianVector()
    {
        Assert.Equal("349932", RemotePairingRequestStore.ComparisonCode(
            "0123456789abcdefghijklmnopq", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), new string('A', 64), new string('B', 64)));
    }

    [Fact]
    public void IssuedBundleReplaysOnlyForItsAgentUntilConnectedOrExpired()
    {
        using var key = RSA.Create(2048);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new RemotePairingRequestStore(clock);
        var sessions = new PairingSessionStore(clock);
        var agentId = Guid.NewGuid();
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var identity = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        Assert.True(store.Announce(agentId, "remote", identity, Convert.ToBase64String(publicKey)));
        var request = Assert.IsType<RemotePairingRequest>(store.Create(agentId, identity, new string('a', 64), sessions));
        var code = Decrypt(key, store.Claim(request.RequestId, agentId, identity));
        Assert.Equal(RemoteCodeAuthorization.Authorized, store.Authorize(code, agentId));
        var bundle = new PairingExchangeResponse(agentId, "https://storage:7443", "credential", "certificate", "private", "authority", clock.GetUtcNow().AddHours(1), clock.GetUtcNow());
        store.MarkIssued(code, agentId, bundle);

        Assert.Same(bundle, store.GetIssued(code, agentId));
        Assert.Null(store.GetIssued(code, Guid.NewGuid()));
        store.MarkConnected(agentId);
        Assert.Null(store.GetIssued(code, agentId));

        var second = Assert.IsType<RemotePairingRequest>(store.Create(agentId, identity, new string('a', 64), sessions));
        var secondCode = Decrypt(key, store.Claim(second.RequestId, agentId, identity));
        Assert.Equal(RemoteCodeAuthorization.Authorized, store.Authorize(secondCode, agentId));
        store.MarkIssued(secondCode, agentId, bundle);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(store.GetIssued(secondCode, agentId));
    }

    private static string Decrypt(RSA key, object? claim)
    {
        if (claim is null) return string.Empty;
        var json = System.Text.Json.JsonSerializer.SerializeToElement(claim);
        return System.Text.Encoding.UTF8.GetString(key.Decrypt(Convert.FromBase64String(json.GetProperty("encrypted_code").GetString()!), RSAEncryptionPadding.OaepSHA256));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
