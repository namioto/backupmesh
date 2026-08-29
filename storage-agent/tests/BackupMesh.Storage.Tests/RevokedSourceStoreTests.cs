using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class RevokedSourceStoreTests
{
    [Fact]
    public void AgentIsNotRevokedByDefault()
    {
        var store = new RevokedSourceStore();
        Assert.False(store.IsRevoked(Guid.NewGuid()));
    }

    [Fact]
    public void RevokeThenUnrevokeRestoresAccess()
    {
        var store = new RevokedSourceStore();
        var agentId = Guid.NewGuid();

        store.Revoke(agentId);
        Assert.True(store.IsRevoked(agentId));
        Assert.Contains(agentId, store.List());

        Assert.True(store.Unrevoke(agentId));
        Assert.False(store.IsRevoked(agentId));
        Assert.DoesNotContain(agentId, store.List());
    }

    [Fact]
    public void UnrevokeAnAlreadyAllowedAgentReportsNoChange()
    {
        var store = new RevokedSourceStore();
        Assert.False(store.Unrevoke(Guid.NewGuid()));
    }

    [Fact]
    public void RevocationPersistsAcrossStoreInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-revoked-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "revoked-agents.txt");
        var agentId = Guid.NewGuid();
        try
        {
            new RevokedSourceStore(new PairingOptions { RevokedAgentsPath = path }).Revoke(agentId);

            var reloaded = new RevokedSourceStore(new PairingOptions { RevokedAgentsPath = path });
            Assert.True(reloaded.IsRevoked(agentId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// The store is constructed while DI builds ControlApiAuthenticationFilter, so throwing on a
    /// malformed line took the whole control API down with 500s - including the tray's own requests -
    /// and left no way to recover but deleting the file. Readable entries must survive a corrupt one.
    /// </summary>
    [Fact]
    public void AMalformedLineIsSkippedInsteadOfBreakingTheWholeStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-revoked-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "revoked-agents.txt");
        var revoked = Guid.NewGuid();
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllLines(path, [revoked.ToString(), "not-a-guid", "  ", "7f3c"]);

            var store = new RevokedSourceStore(new PairingOptions { RevokedAgentsPath = path });

            Assert.True(store.IsRevoked(revoked));
            Assert.Single(store.List());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
