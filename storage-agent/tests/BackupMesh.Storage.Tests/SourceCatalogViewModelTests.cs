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

        Assert.Equal(1, viewModel.SourceCount);
        Assert.Equal(2, viewModel.BackupSets.Count);
        Assert.Equal("Integration Source", viewModel.Sources.Single().DisplayName);
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
