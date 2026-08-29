using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class IssuedCertificateStoreTests
{
    [Fact]
    public void UnknownAgentHasNoRecordedExpiry()
    {
        var store = new IssuedCertificateStore();
        Assert.Null(store.GetExpiry(Guid.NewGuid()));
    }

    [Fact]
    public void RecordingReplacesAnyPreviousExpiryForTheSameAgent()
    {
        var store = new IssuedCertificateStore();
        var agentId = Guid.NewGuid();
        var first = DateTimeOffset.UtcNow.AddYears(1);
        var renewed = DateTimeOffset.UtcNow.AddYears(2);

        store.Record(agentId, first);
        store.Record(agentId, renewed);

        Assert.Equal(renewed, store.GetExpiry(agentId));
    }

    [Fact]
    public void ExpiryPersistsAcrossStoreInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-issued-cert-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "issued-certificates.txt");
        var agentId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddYears(1);
        try
        {
            new IssuedCertificateStore(new IssuedCertificateOptions { RecordPath = path }).Record(agentId, expiresAt);

            var reloaded = new IssuedCertificateStore(new IssuedCertificateOptions { RecordPath = path });
            Assert.Equal(expiresAt, reloaded.GetExpiry(agentId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
