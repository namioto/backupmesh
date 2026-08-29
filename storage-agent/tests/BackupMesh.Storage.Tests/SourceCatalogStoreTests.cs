using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class SourceCatalogStoreTests
{
    [Fact]
    public void UpsertReplacesOnlyThePublishingSource()
    {
        var store = new SourceCatalogStore(new SourceCatalogOptions { PersistencePath = string.Empty });
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

    [Fact]
    public void CatalogsSurviveStoreRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-catalog-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "catalogs.json");

        try
        {
            var sourceId = Guid.NewGuid();
            var options = new SourceCatalogOptions { PersistencePath = path };
            new SourceCatalogStore(options).Upsert(Catalog(sourceId, "Home Server", "Photos"));

            var restored = new SourceCatalogStore(options).List();

            Assert.Single(restored);
            Assert.Equal(sourceId, restored[0].SourceAgentId);
            Assert.Equal("Photos", restored[0].BackupSets.Single().Name);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CorruptCatalogFileStopsStartupInsteadOfSilentlyLosingMappings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-catalog-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "catalogs.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "not-json");

            var error = Assert.Throws<InvalidDataException>(() =>
                new SourceCatalogStore(new SourceCatalogOptions { PersistencePath = path }));

            Assert.Contains(path, error.Message);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void OlderCatalogCannotOverwriteNewerCatalog()
    {
        var store = new SourceCatalogStore(new SourceCatalogOptions { PersistencePath = string.Empty });
        var sourceId = Guid.NewGuid();
        var newer = Catalog(sourceId, "Home Server", "Current");
        var older = newer with
        {
            UpdatedAt = newer.UpdatedAt.AddMinutes(-1),
            BackupSets = [new(Guid.NewGuid(), "Obsolete", ["/old"])]
        };

        Assert.Equal(StoreOutcome.Accepted, store.Upsert(newer));
        Assert.Equal(StoreOutcome.InvalidSequence, store.Upsert(older));
        Assert.Equal("Current", store.List().Single().BackupSets.Single().Name);
    }

    [Fact]
    public void ExactRetryIsReplayedButChangedContentAtSameTimestampConflicts()
    {
        var store = new SourceCatalogStore(new SourceCatalogOptions { PersistencePath = string.Empty });
        var catalog = Catalog(Guid.NewGuid(), "Home Server", "Photos");
        var changed = catalog with { SourceAgentName = "Changed name" };

        Assert.Equal(StoreOutcome.Accepted, store.Upsert(catalog));
        Assert.Equal(StoreOutcome.Replayed, store.Upsert(catalog));
        Assert.Equal(StoreOutcome.Conflict, store.Upsert(changed));
    }

    [Fact]
    public void RemoveDeletesOnlyTheNamedCatalogAndPersists()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-catalog-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "catalogs.json");

        try
        {
            var options = new SourceCatalogOptions { PersistencePath = path };
            var store = new SourceCatalogStore(options);
            var removedId = Guid.NewGuid();
            var keptId = Guid.NewGuid();
            store.Upsert(Catalog(removedId, "Zulu", "Photos"));
            store.Upsert(Catalog(keptId, "Alpha", "Projects"));

            Assert.True(store.Remove(removedId));
            Assert.Equal(keptId, store.List().Single().SourceAgentId);

            var restored = new SourceCatalogStore(options).List();
            Assert.Equal(keptId, restored.Single().SourceAgentId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RemovingAnUnknownSourceReportsFalse()
    {
        var store = new SourceCatalogStore(new SourceCatalogOptions { PersistencePath = string.Empty });
        Assert.False(store.Remove(Guid.NewGuid()));
    }

    private static SourceCatalog Catalog(Guid id, string name, string setName) =>
        new(id, name, DateTimeOffset.UtcNow, [new(Guid.NewGuid(), setName, ["/data"])]);
}
