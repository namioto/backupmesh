using BackupMesh.Storage.App;

namespace BackupMesh.Storage.Tests;

public sealed class NearbyPairingViewModelTests
{
    [Fact]
    public async Task RequestUsesSelectedAdvertisedIdentityAndCanBeCancelled()
    {
        var agentId = Guid.NewGuid();
        var client = new FakeNearbyPairingClient
        {
            Nearby = [new(agentId, "Laptop", new string('a', 64), DateTimeOffset.UtcNow)]
        };
        using var viewModel = new MainWindowViewModel(loadLocalState: false, nearbyPairingClient: client);
        await viewModel.RefreshNearbyComputersAsync();
        viewModel.SelectedNearbyComputer = Assert.Single(viewModel.NearbyComputers);

        viewModel.RequestNearbyPairingCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasNearbyComparisonCode);

        Assert.Equal((agentId, new string('a', 64)), client.Requested);
        Assert.Equal("123456", viewModel.NearbyComparisonCode);
        viewModel.CancelNearbyPairingCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.HasNearbyComparisonCode);
        Assert.Equal(client.Request.RequestId, client.CancelledRequestId);
    }

    [Fact]
    public async Task AuthenticatedConnectionWithoutBackupSetsStillAppears()
    {
        var connection = new SourceConnectionDto(Guid.NewGuid(), "Empty laptop", "Empty laptop", DateTimeOffset.UtcNow, "192.0.2.2", 0, false, null);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, connectionsClient: new FakeConnectionsClient([connection]));

        await viewModel.RefreshConnectionsOnceAsync();

        Assert.Equal(connection.AgentId, Assert.Single(viewModel.Sources).Id);
    }

    [Fact]
    public async Task ExpiredRequestClearsLocallyWhenServiceIsUnavailable()
    {
        var client = new FakeNearbyPairingClient
        {
            Nearby = [new(Guid.NewGuid(), "Laptop", new string('a', 64), DateTimeOffset.UtcNow)],
            Request = new(Guid.NewGuid(), Guid.Empty, "Laptop", "", "123456", DateTimeOffset.UtcNow.AddSeconds(-1), "PENDING"),
            GetError = new HttpRequestException()
        };
        using var viewModel = new MainWindowViewModel(loadLocalState: false, nearbyPairingClient: client);
        await viewModel.RefreshNearbyComputersAsync();
        viewModel.SelectedNearbyComputer = Assert.Single(viewModel.NearbyComputers);
        viewModel.RequestNearbyPairingCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasNearbyComparisonCode);

        await viewModel.RefreshNearbyPairingStateOnceAsync();

        Assert.False(viewModel.HasNearbyComparisonCode);
        Assert.Equal(Localization.Text("NearbyPairingExpired"), viewModel.NearbyPairingStatus);
    }

    [Fact]
    public async Task TerminalStatusSurvivesTimerRefresh()
    {
        var client = new FakeNearbyPairingClient
        {
            Nearby = [new(Guid.NewGuid(), "Laptop", new string('a', 64), DateTimeOffset.UtcNow)]
        };
        client.Current = client.Request with { Status = "REJECTED" };
        using var viewModel = new MainWindowViewModel(loadLocalState: false, nearbyPairingClient: client);
        await viewModel.RefreshNearbyComputersAsync();
        viewModel.SelectedNearbyComputer = Assert.Single(viewModel.NearbyComputers);
        viewModel.RequestNearbyPairingCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasNearbyComparisonCode);

        await viewModel.RefreshNearbyPairingStateOnceAsync();
        var terminalStatus = viewModel.NearbyPairingStatus;
        await viewModel.RefreshNearbyPairingStateOnceAsync();

        Assert.False(viewModel.HasNearbyComparisonCode);
        Assert.Equal(Localization.Text("NearbyPairingRejected"), terminalStatus);
        Assert.Equal(terminalStatus, viewModel.NearbyPairingStatus);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeNearbyPairingClient : INearbyPairingClient
    {
        public IReadOnlyList<NearbyComputerDto> Nearby { get; init; } = [];
        public NearbyPairingRequestDto Request { get; init; } = new(Guid.NewGuid(), Guid.Empty, "Laptop", "", "123456", DateTimeOffset.UtcNow.AddMinutes(10), "PENDING");
        public NearbyPairingRequestDto? Current { get; set; }
        public Exception? GetError { get; init; }
        public (Guid AgentId, string Identity) Requested { get; private set; }
        public Guid? CancelledRequestId { get; private set; }
        public Task<IReadOnlyList<NearbyComputerDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(Nearby);
        public Task<NearbyPairingRequestDto> RequestAsync(Guid agentId, string identity, CancellationToken cancellationToken)
        {
            Requested = (agentId, identity);
            return Task.FromResult(Request with { AgentId = agentId, Identity = identity });
        }
        public Task<NearbyPairingRequestDto> GetAsync(Guid requestId, CancellationToken cancellationToken) => GetError is null
            ? Task.FromResult(Current ?? Request)
            : Task.FromException<NearbyPairingRequestDto>(GetError);
        public Task CancelAsync(Guid requestId, CancellationToken cancellationToken) { CancelledRequestId = requestId; return Task.CompletedTask; }
    }

    private sealed class FakeConnectionsClient(IReadOnlyList<SourceConnectionDto> connections) : ISourceConnectionsClient
    {
        public Task<IReadOnlyList<SourceConnectionDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(connections);
        public Task RevokeAsync(Guid agentId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UnrevokeAsync(Guid agentId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RenameAsync(Guid agentId, string? displayName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ForgetAsync(Guid agentId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
