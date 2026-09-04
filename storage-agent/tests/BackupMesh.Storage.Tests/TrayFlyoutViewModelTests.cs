using BackupMesh.Storage.App;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Tests;

public sealed class TrayFlyoutViewModelTests
{
    [Fact]
    public void PendingArrivalTitleNamesTheDeviceAndStatesItConnected()
    {
        // The card title explicitly identifies the device connection event.
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive HDD", "A", "E:\\", DateTimeOffset.UtcNow, null)) { CurrentRoot = "E:\\" };
        var pending = new PendingArrivalViewModel(device, eligibleMappingCount: 1, eligibleAt: DateTimeOffset.UtcNow);

        Assert.Equal("Archive HDD (E:\\) connected", pending.TitleDisplay);
    }

    [Fact]
    public void PendingArrivalStatusStatesACountdownNotAThreshold()
    {
        // Use a plain countdown to one named automatic event.
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive HDD", "A", "E:\\", DateTimeOffset.UtcNow, null));
        var pending = new PendingArrivalViewModel(device, eligibleMappingCount: 1, eligibleAt: DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.Contains("Starts automatically in", pending.StatusDisplay);
        Assert.False(pending.IsEligibleNow);
    }

    [Fact]
    public void PendingArrivalIsEligibleOnceTheDelayHasElapsed()
    {
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive HDD", "A", "E:\\", DateTimeOffset.UtcNow, null));
        var pending = new PendingArrivalViewModel(device, eligibleMappingCount: 2, eligibleAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.True(pending.IsEligibleNow);
        Assert.Equal("Ready to back up (2 backups queued)", pending.StatusDisplay);
    }

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
