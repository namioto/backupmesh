using BackupMesh.Storage.App;

namespace BackupMesh.Storage.Tests;

public sealed class SourceCatalogViewModelTests
{
    [Fact]
    public async Task PublishedCatalogPopulatesSourcesAndBackupSets()
    {
        var sourceId = Guid.NewGuid();
        var firstSetId = Guid.NewGuid();
        var secondSetId = Guid.NewGuid();
        var client = new FakeCatalogClient([
            new(sourceId, "Integration Source", DateTimeOffset.UtcNow,
            [new(firstSetId, "photos", ["/srv/photos"]), new(secondSetId, "documents", ["/srv/documents"])])
        ]);
        using var viewModel = new MainWindowViewModel(catalogClient: client, loadLocalState: false);

        await viewModel.RefreshCatalogsOnceAsync();

        // "This PC" is always present alongside whatever Sources report a catalog.
        Assert.Equal(2, viewModel.SourceCount);
        Assert.Equal(2, viewModel.BackupSets.Count);
        Assert.Contains(viewModel.Sources, source => source.DisplayName == "Integration Source");
        Assert.Contains(viewModel.BackupSets, set => set.Id == firstSetId && set.IsAvailable);
    }

    [Fact]
    public async Task MissingSetIsRetainedAndMarkedUnavailableForMappingReview()
    {
        var sourceId = Guid.NewGuid();
        var retainedSet = Guid.NewGuid();
        var client = new MutableCatalogClient();
        using var viewModel = new MainWindowViewModel(catalogClient: client, loadLocalState: false);
        client.Catalogs = [new(sourceId, "Source", DateTimeOffset.UtcNow, [new(retainedSet, "photos", ["/photos"])])];
        await viewModel.RefreshCatalogsOnceAsync();
        client.Catalogs = [new(sourceId, "Source", DateTimeOffset.UtcNow, [])];

        await viewModel.RefreshCatalogsOnceAsync();

        var backupSet = Assert.Single(viewModel.BackupSets);
        Assert.False(backupSet.IsAvailable);
        Assert.Contains("not reported", backupSet.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThisPCIsAlwaysPresentEvenWithNoCatalogsAtAll()
    {
        using var viewModel = new MainWindowViewModel(catalogClient: new FakeCatalogClient([]), loadLocalState: false);

        await viewModel.RefreshCatalogsOnceAsync();

        Assert.Contains(viewModel.Sources, source => source.DisplayName == BackupMesh.Storage.Core.LocalSourceIdentity.DisplayName);
    }

    [Fact]
    public async Task ALocalBackupSetSurvivesARoutineCatalogRefreshWithoutBeingMarkedUnavailable()
    {
        using var viewModel = new MainWindowViewModel(catalogClient: new FakeCatalogClient([]), loadLocalState: false);
        await viewModel.RefreshCatalogsOnceAsync();
        var localSource = viewModel.Sources.Single(source => source.DisplayName == BackupMesh.Storage.Core.LocalSourceIdentity.DisplayName);
        var localBackupSet = new BackupSetViewModel(new(Guid.NewGuid(), BackupMesh.Storage.Core.LocalSourceIdentity.AgentId, BackupMesh.Storage.Core.LocalSourceIdentity.DisplayName, "Documents", ["C:/Documents"]));
        viewModel.BackupSets.Add(localBackupSet);
        localSource.BackupSets.Add(localBackupSet);

        await viewModel.RefreshCatalogsOnceAsync();

        Assert.True(localBackupSet.IsAvailable);
        Assert.DoesNotContain("not reported", localBackupSet.DisplayName, StringComparison.Ordinal);
    }

    private sealed class FakeCatalogClient(IReadOnlyList<SourceCatalogDto> catalogs) : ISourceCatalogClient
    {
        public Task<IReadOnlyList<SourceCatalogDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(catalogs);
    }

    private sealed class MutableCatalogClient : ISourceCatalogClient
    {
        public IReadOnlyList<SourceCatalogDto> Catalogs { get; set; } = [];
        public Task<IReadOnlyList<SourceCatalogDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(Catalogs);
    }
}
