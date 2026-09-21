using BackupMesh.Storage.App;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Tests;

[Collection("Localization")]
public sealed class TrayFlyoutViewModelTests
{
    public TrayFlyoutViewModelTests() => Localization.Initialize("en");
    [Fact]
    public void StartNowQueuesTheConnectedDeviceAndDismissesItsDecisionCard()
    {
        var connectedAt = DateTimeOffset.UtcNow;
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive HDD", "A", "E:\\", connectedAt, null))
        {
            IsConnected = true,
            ConnectedAt = connectedAt,
            ArrivalDelayMinutes = 30
        };
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "This PC", "Documents", ["C:\\Data"]));
        var mapping = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "BackupMesh\\Documents"), set, device);
        var jobs = new RecordingJobClient();
        using var main = new MainWindowViewModel(loadLocalState: false, jobClient: jobs);
        main.Devices.Add(device);
        main.BackupSets.Add(set);
        main.Mappings.Add(mapping);
        using var flyout = new TrayFlyoutViewModel(main);
        var pending = Assert.Single(flyout.PendingArrivals);

        flyout.StartNowCommand.Execute(pending);

        Assert.True(SpinWait.SpinUntil(() => flyout.PendingArrivals.Count == 0, TimeSpan.FromSeconds(2)));
        Assert.Equal(mapping.Id, Assert.Single(jobs.EnqueuedMappingIds));
    }

    private sealed class RecordingJobClient : IBackupJobClient
    {
        public List<Guid> EnqueuedMappingIds { get; } = [];
        public Task<IReadOnlyList<BackupJobDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BackupJobDto>>([]);
        public Task<int> EnqueueAsync(Guid[] mappingIds, string reason, CancellationToken cancellationToken)
        {
            EnqueuedMappingIds.AddRange(mappingIds);
            return Task.FromResult(mappingIds.Length);
        }
        public Task CancelAsync(Guid jobId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
