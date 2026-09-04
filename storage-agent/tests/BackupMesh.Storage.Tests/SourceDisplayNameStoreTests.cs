using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class SourceDisplayNameStoreTests
{
    [Fact]
    public void UnnamedAgentHasNoOverride()
    {
        var store = new SourceDisplayNameStore();
        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void SettingAnEmptyNameClearsTheOverride()
    {
        var store = new SourceDisplayNameStore();
        var agentId = Guid.NewGuid();
        store.Set(agentId, "Basement NAS");
        Assert.Equal("Basement NAS", store.Get(agentId));

        store.Set(agentId, "  ");
        Assert.Null(store.Get(agentId));
    }

    [Fact]
    public void OverrideNamePersistsAcrossStoreInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-display-name-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "source-display-names.json");
        var agentId = Guid.NewGuid();
        try
        {
            new SourceDisplayNameStore(new SourceDisplayNameOptions { RecordPath = path }).Set(agentId, "Basement NAS");

            var reloaded = new SourceDisplayNameStore(new SourceDisplayNameOptions { RecordPath = path });
            Assert.Equal("Basement NAS", reloaded.Get(agentId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
