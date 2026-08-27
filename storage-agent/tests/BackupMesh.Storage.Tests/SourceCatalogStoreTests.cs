using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class SourceCatalogStoreTests
{
    [Fact]
    public void UpsertReplacesOnlyThePublishingSource()
    {
        var store = new SourceCatalogStore();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        store.Upsert(Catalog(firstId, "Zulu", "Photos"));
        store.Upsert(Catalog(secondId, "Alpha", "Projects"));
        store.Upsert(Catalog(firstId, "Zulu", "Documents"));

        var catalogs = store.List();
        Assert.Equal(2, catalogs.Count);
        Assert.Equal("Alpha", catalogs[0].SourceAgentName);
        Assert.Equal("Documents", catalogs.Single(item => item.SourceAgentId == firstId).BackupSets.Single().Name);
    }

    private static SourceCatalog Catalog(Guid id, string name, string setName) =>
        new(id, name, DateTimeOffset.UtcNow, [new(Guid.NewGuid(), setName, ["/data"])]);
}
