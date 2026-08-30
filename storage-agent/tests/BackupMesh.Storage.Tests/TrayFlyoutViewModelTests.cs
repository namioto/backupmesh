using BackupMesh.Storage.App;

namespace BackupMesh.Storage.Tests;

public sealed class TrayFlyoutViewModelTests
{
    [Fact]
    public void PendingArrivalTitleNamesTheDeviceAndStatesItConnected()
    {
        // Measured: "Just arrived" alone left evaluators unsure whether files or the device itself had
        // arrived. The card's own title states it explicitly, regardless of the section header's wording.
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive HDD", "A", "E:\\", DateTimeOffset.UtcNow, null)) { CurrentRoot = "E:\\" };
        var pending = new PendingArrivalViewModel(device, eligibleMappingCount: 1, eligibleAt: DateTimeOffset.UtcNow);

        Assert.Equal("Archive HDD (E:\\) connected", pending.TitleDisplay);
    }

    [Fact]
    public void PendingArrivalStatusStatesACountdownNotAThreshold()
    {
        // Measured: "Backing up in 2 min unless started now" read as ambiguous between "within 2 min" and
        // "after 2 min". A plain countdown to one named, automatic event reads as neither.
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
}
