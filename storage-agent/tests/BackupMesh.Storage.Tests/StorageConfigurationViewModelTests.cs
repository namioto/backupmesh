using BackupMesh.Storage.App;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Tests;

public sealed class StorageConfigurationViewModelTests
{
    [Fact]
    public void SafeRemovalWithoutSelectedRemovableDeviceDoesNotCallService()
    {
        var client = new FakeStorageDeviceClient();
        using var viewModel = new MainWindowViewModel(loadLocalState: false, storageDeviceClient: client);

        viewModel.EjectDeviceCommand.Execute(null);

        Assert.Equal(0, client.CallCount);
        Assert.Contains("connected removable device", viewModel.FooterStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, "connected and ready")]
    [InlineData(15, "15-minute arrival delay")]
    public void ArrivalNotificationDescribesActualEligibility(int delay, string expected)
    {
        var message = MainWindowViewModel.DeviceArrivalMessage("Archive", delay);
        Assert.Contains(expected, message, StringComparison.OrdinalIgnoreCase);
        if (delay > 0) Assert.DoesNotContain("is ready", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshDrivesPreservesTheUsersSelectedDevice()
    {
        var first = new AvailableDriveViewModel("disk:first", "C:\\", "FIRST", 1, 2, "First disk", 1);
        var second = new AvailableDriveViewModel("disk:second", "D:\\", "SECOND", 1, 2, "Second disk", 1);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, deviceInventory: new FakeDeviceInventory([first, second]));
        viewModel.RefreshDrivesCommand.Execute(null);
        viewModel.SelectedAvailableDrive = second;

        viewModel.RefreshDrivesCommand.Execute(null);

        Assert.Equal(second.StableId, viewModel.SelectedAvailableDrive?.StableId);
    }

    [Fact]
    public void RelativeDestinationFolderIsAcceptedInsideTheSelectedDevice()
    {
        var model = new RegisteredDevice(Guid.NewGuid(), "disk:usb", "USB disk", "USB", "D:\\", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var device = new DeviceViewModel(model) { CurrentRoot = "D:\\", IsConnected = true };

        var result = MainWindowViewModel.RelativeDestinationPath(device, "BackupMesh\\Documents");

        Assert.Equal("BackupMesh\\Documents", result);
    }

    [Fact]
    public void PackagedStartupCommandLaunchesTheServiceAndTrayLauncher()
    {
        var package = Path.Combine(Path.GetTempPath(), "backupmesh-startup-" + Guid.NewGuid());
        var app = Path.Combine(package, "App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(package, "Start-BackupMesh.ps1"), "# test launcher");
        try
        {
            var command = MainWindowViewModel.BuildStartupCommand(app, Path.Combine(app, "BackupMesh.Storage.App.exe"));

            Assert.Contains("Start-BackupMesh.ps1", command);
            Assert.Contains("-WindowStyle Hidden", command);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task ServiceConfigurationReplacesLocalTopology()
    {
        var device = new RegisteredDevice(Guid.NewGuid(), "volume:test", "Service device", "TEST", "X:\\", DateTimeOffset.UtcNow, null);
        var client = new FakeConfigurationClient(new(7, DateTimeOffset.UtcNow, new([device], [], [])));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client);

        await viewModel.RefreshConfigurationAsync();

        Assert.Equal("Service device", Assert.Single(viewModel.Devices).DisplayName);
    }

    [Fact]
    public async Task BackupJobsExposeProgressAndResultInTheOverviewModel()
    {
        var job = new BackupJobDto(Guid.NewGuid(), "RUNNING", DateTimeOffset.UtcNow, new(50, 100, 2, 4), null);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: new FakeJobClient([job]));

        await viewModel.RefreshJobsAsync();

        var shown = Assert.Single(viewModel.Jobs);
        Assert.Equal("RUNNING", shown.State);
        Assert.Contains("50.0%", shown.Progress);
        Assert.True(shown.CanCancel);
    }

    [Fact]
    public async Task BackupJobResolvesItsTargetMappingToABackupSetAndDeviceName()
    {
        var device = new RegisteredDevice(Guid.NewGuid(), "volume:target", "Archive drive", "ARCHIVE", "D:\\", DateTimeOffset.UtcNow, null);
        var backupSet = new SourceBackupSet(Guid.NewGuid(), Guid.NewGuid(), "Home Server", "Photos", ["/srv/photos"]);
        var mapping = new BackupTargetMapping(Guid.NewGuid(), backupSet.Id, device.Id, "photos");
        var configurationClient = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, new([device], [backupSet], [mapping])));
        var job = new BackupJobDto(Guid.NewGuid(), "RUNNING", DateTimeOffset.UtcNow, new(50, 100, 2, 4), null, TargetMappingId: mapping.Id);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: configurationClient, jobClient: new FakeJobClient([job]));
        await viewModel.RefreshConfigurationAsync();

        await viewModel.RefreshJobsAsync();

        var shown = Assert.Single(viewModel.Jobs);
        Assert.Contains("Photos", shown.Target);
        Assert.Contains("Archive drive", shown.Target);
    }

    private static (BackupSetViewModel Set, DeviceViewModel Device, MappingViewModel Mapping) MakeConnectedDeviceWithMapping(DateTimeOffset connectedAt)
    {
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive drive", "A", "D:\\", DateTimeOffset.UtcNow, null)) { IsConnected = true, CanEject = true, ConnectedAt = connectedAt };
        var mapping = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "docs"), set, device);
        return (set, device, mapping);
    }

    [Fact]
    public async Task DeviceWithAnActiveJobShowsABackingUpBannerWithNoButton()
    {
        var (_, device, mapping) = MakeConnectedDeviceWithMapping(DateTimeOffset.UtcNow.AddMinutes(-10));
        var job = new BackupJobDto(Guid.NewGuid(), "RUNNING", DateTimeOffset.UtcNow, null, null, TargetMappingId: mapping.Id, StartedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: new FakeJobClient([job]));
        viewModel.Devices.Add(device);
        viewModel.Mappings.Add(mapping);

        await viewModel.RefreshJobsAsync();

        var banner = Assert.Single(viewModel.RemovalBanners);
        Assert.Equal(DeviceRemovalBannerKind.BackingUp, banner.Kind);
        Assert.StartsWith("Do not remove", banner.Message);
        Assert.False(banner.ShowRemoveButton);
    }

    [Fact]
    public async Task BackingUpBannerNamesTheDeviceWithoutItsFreeSpace()
    {
        // Free space matters when choosing a device, not in the removal banner.
        var (_, device, mapping) = MakeConnectedDeviceWithMapping(DateTimeOffset.UtcNow.AddMinutes(-10));
        device.AvailableBytes = 5_000_000_000;
        device.TotalBytes = 10_000_000_000;
        var job = new BackupJobDto(Guid.NewGuid(), "RUNNING", DateTimeOffset.UtcNow, null, null, TargetMappingId: mapping.Id, StartedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: new FakeJobClient([job]));
        viewModel.Devices.Add(device);
        viewModel.Mappings.Add(mapping);

        await viewModel.RefreshJobsAsync();

        var banner = Assert.Single(viewModel.RemovalBanners);
        Assert.DoesNotContain("GB free", banner.Message);
        Assert.Contains(device.DisplayName, banner.Message);
    }

    [Fact]
    public async Task DeviceWithAllSucceededJobsSinceConnectingShowsASafeBanner()
    {
        var (_, device, mapping) = MakeConnectedDeviceWithMapping(DateTimeOffset.UtcNow.AddMinutes(-10));
        var job = new BackupJobDto(Guid.NewGuid(), "SUCCEEDED", DateTimeOffset.UtcNow, null, new("SUCCEEDED", "snap", null), TargetMappingId: mapping.Id, StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: new FakeJobClient([job]));
        viewModel.Devices.Add(device);
        viewModel.Mappings.Add(mapping);

        await viewModel.RefreshJobsAsync();

        var banner = Assert.Single(viewModel.RemovalBanners);
        Assert.Equal(DeviceRemovalBannerKind.Safe, banner.Kind);
        Assert.StartsWith("Safe to remove", banner.Message);
        Assert.Contains("finished all backups", banner.Message);
        Assert.True(banner.ShowRemoveButton);
    }

    [Fact]
    public async Task DeviceWithAFailedJobSinceConnectingShowsASafeButIncompleteBanner()
    {
        var (_, device, mapping) = MakeConnectedDeviceWithMapping(DateTimeOffset.UtcNow.AddMinutes(-10));
        var job = new BackupJobDto(Guid.NewGuid(), "FAILED", DateTimeOffset.UtcNow, null, new("FAILED", null, "disk full"), TargetMappingId: mapping.Id, StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: new FakeJobClient([job]));
        viewModel.Devices.Add(device);
        viewModel.Mappings.Add(mapping);

        await viewModel.RefreshJobsAsync();

        var banner = Assert.Single(viewModel.RemovalBanners);
        Assert.Equal(DeviceRemovalBannerKind.SafeButIncomplete, banner.Kind);
        // Unlike Safe/BackingUp, this state leads with the failed backup and states safe removal separately.
        Assert.StartsWith("Backup did not finish", banner.Message);
        Assert.Contains("You can still remove it safely", banner.Message);
        Assert.Contains("Overview tab", banner.Message);
        Assert.DoesNotContain("finished all backups", banner.Message);
        Assert.True(banner.ShowRemoveButton, "A failed backup still means nothing is actively writing to the device, so removal must remain offered.");
    }

    [Fact]
    public async Task JobThatStartedBeforeThisConnectionDoesNotCountTowardTheSafeBanner()
    {
        // Regression guard: job history is now persisted (up to 20 terminal jobs per mapping), so without
        // this filter, plugging in a drive backed up days ago would immediately claim "just finished".
        var (_, device, mapping) = MakeConnectedDeviceWithMapping(connectedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var staleJob = new BackupJobDto(Guid.NewGuid(), "SUCCEEDED", DateTimeOffset.UtcNow, null, new("SUCCEEDED", "snap", null), TargetMappingId: mapping.Id, StartedAt: DateTimeOffset.UtcNow.AddDays(-3));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: new FakeJobClient([staleJob]));
        viewModel.Devices.Add(device);
        viewModel.Mappings.Add(mapping);

        await viewModel.RefreshJobsAsync();

        Assert.Empty(viewModel.RemovalBanners);
    }

    [Fact]
    public async Task DeviceWithUnknownConnectionTimeNeverShowsASafeBanner()
    {
        // Conservative-by-design: a device already connected when the app started has no known
        // connection time (ConnectedAt stays null), so even a job that just succeeded is not trusted.
        var (_, device, mapping) = MakeConnectedDeviceWithMapping(connectedAt: DateTimeOffset.UtcNow);
        device.ConnectedAt = null;
        var job = new BackupJobDto(Guid.NewGuid(), "SUCCEEDED", DateTimeOffset.UtcNow, null, new("SUCCEEDED", "snap", null), TargetMappingId: mapping.Id, StartedAt: DateTimeOffset.UtcNow.AddSeconds(-1));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: new FakeJobClient([job]));
        viewModel.Devices.Add(device);
        viewModel.Mappings.Add(mapping);

        await viewModel.RefreshJobsAsync();

        Assert.Empty(viewModel.RemovalBanners);
    }

    [Fact]
    public async Task NonEjectableOrDisconnectedDevicesNeverShowARemovalBanner()
    {
        var (_, device, mapping) = MakeConnectedDeviceWithMapping(DateTimeOffset.UtcNow.AddMinutes(-10));
        device.CanEject = false;
        var job = new BackupJobDto(Guid.NewGuid(), "SUCCEEDED", DateTimeOffset.UtcNow, null, new("SUCCEEDED", "snap", null), TargetMappingId: mapping.Id, StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: new FakeJobClient([job]));
        viewModel.Devices.Add(device);
        viewModel.Mappings.Add(mapping);

        await viewModel.RefreshJobsAsync();

        Assert.Empty(viewModel.RemovalBanners);
    }

    [Fact]
    public async Task MultipleQualifyingDevicesEachGetTheirOwnBanner()
    {
        var (_, deviceA, mappingA) = MakeConnectedDeviceWithMapping(DateTimeOffset.UtcNow.AddMinutes(-10));
        var (_, deviceB, mappingB) = MakeConnectedDeviceWithMapping(DateTimeOffset.UtcNow.AddMinutes(-10));
        var jobA = new BackupJobDto(Guid.NewGuid(), "SUCCEEDED", DateTimeOffset.UtcNow, null, new("SUCCEEDED", "snap", null), TargetMappingId: mappingA.Id, StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var jobB = new BackupJobDto(Guid.NewGuid(), "RUNNING", DateTimeOffset.UtcNow, null, null, TargetMappingId: mappingB.Id, StartedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: new FakeJobClient([jobA, jobB]));
        viewModel.Devices.Add(deviceA);
        viewModel.Devices.Add(deviceB);
        viewModel.Mappings.Add(mappingA);
        viewModel.Mappings.Add(mappingB);

        await viewModel.RefreshJobsAsync();

        Assert.Equal(2, viewModel.RemovalBanners.Count);
        Assert.Contains(viewModel.RemovalBanners, banner => banner.Device == deviceA && banner.Kind == DeviceRemovalBannerKind.Safe);
        Assert.Contains(viewModel.RemovalBanners, banner => banner.Device == deviceB && banner.Kind == DeviceRemovalBannerKind.BackingUp);
    }

    [Fact]
    public void RecentlyConnectedComputerWithDistantCertificateExpiryIsJustConnected()
    {
        // Regression test: a healthy computer, seen moments ago, whose certificate isn't due for
        // self-renewal for months, must not be flagged - LastSeenAt (recent past) being earlier than a
        // still-future renewal window start is not evidence the computer missed that window.
        var connection = new SourceConnectionViewModel(new(Guid.NewGuid(), "Home Server", "Home Server", DateTimeOffset.UtcNow.AddSeconds(-30), null, 2, false, DateTimeOffset.UtcNow.AddDays(75)));
        Assert.Equal("Connected", connection.StatusDisplay);
    }

    // The certificate fingerprint/expiry line is omitted from the routine Source Agents UI. Server-side recording
    // (IssuedCertificateStoreTests) is kept for later use; SourceConnectionDto.CertificateFingerprint is
    // now dormant client-side data with no UI consumer.

    [Fact]
    public void DisplayNameWithHintExplainsThisPCNeedsNoAgent()
    {
        var thisPc = new SourceAgentViewModel(LocalSourceIdentity.AgentId, LocalSourceIdentity.DisplayName);
        Assert.Equal("This PC (no agent needed)", thisPc.DisplayNameWithHint);

        var remote = new SourceAgentViewModel(Guid.NewGuid(), "Home Server");
        Assert.Equal("Home Server", remote.DisplayNameWithHint);
    }

    [Fact]
    public void ComputerUnseenSinceItsRenewalWindowOpenedNeedsRePairing()
    {
        var connection = new SourceConnectionViewModel(new(Guid.NewGuid(), "Studio Workstation", "Studio Workstation", DateTimeOffset.UtcNow.AddDays(-45), null, 1, false, DateTimeOffset.UtcNow.AddDays(5)));
        Assert.StartsWith("Offline — re-pair before", connection.StatusDisplay);
    }

    [Fact]
    public void ComputerSeenAfterItsRenewalWindowOpenedIsStillJustConnected()
    {
        // The renewal window opened 5 days ago (35-day-out certificate, 30-day renewal threshold), but
        // this computer was seen moments ago - after the window opened - so it has had its chance to
        // renew. Seen just now (not merely "after the window opened") so this also stays within the
        // separate real-time online threshold, isolating the renewal-window check from that one.
        var connection = new SourceConnectionViewModel(new(Guid.NewGuid(), "Home Server", "Home Server", DateTimeOffset.UtcNow.AddSeconds(-30), null, 2, false, DateTimeOffset.UtcNow.AddDays(35)));
        Assert.Equal("Connected", connection.StatusDisplay);
    }

    [Fact]
    public void ComputerNotSeenRecentlyIsOfflineEvenWithNoCertificateConcern()
    {
        // Real connectivity is a separate axis from the certificate-renewal check above: a computer can
        // be in no danger of missing its renewal window and still not be connected right now.
        var connection = new SourceConnectionViewModel(new(Guid.NewGuid(), "Home Server", "Home Server", DateTimeOffset.UtcNow.AddMinutes(-10), null, 2, false, DateTimeOffset.UtcNow.AddDays(75)));
        Assert.Equal("Offline", connection.StatusDisplay);
    }

    [Fact]
    public void ExpiredCertificateAlwaysNeedsRePairingRegardlessOfLastSeen()
    {
        var connection = new SourceConnectionViewModel(new(Guid.NewGuid(), "Home Server", "Home Server", DateTimeOffset.UtcNow.AddMinutes(-1), null, 2, false, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal("Expired — re-pair to reconnect", connection.StatusDisplay);
    }

    [Fact]
    public void RevokedComputerShowsRevokedEvenWithAnExpiringCertificate()
    {
        var connection = new SourceConnectionViewModel(new(Guid.NewGuid(), "Home Server", "Home Server", DateTimeOffset.UtcNow.AddDays(-45), null, 2, true, DateTimeOffset.UtcNow.AddDays(5)));
        Assert.Equal("Revoked", connection.StatusDisplay);
    }

    [Fact]
    public async Task BackupNowEnqueuesEveryConnectedEnabledMapping()
    {
        var sourceId = Guid.NewGuid();
        var set = new BackupSetViewModel(new(Guid.NewGuid(), sourceId, "Studio", "Documents", ["C:\\Data"]));
        var connectedDevice = new DeviceViewModel(new(Guid.NewGuid(), "disk:connected", "Connected disk", "READY", "D:\\", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)) { IsConnected = true };
        var offlineDevice = new DeviceViewModel(new(Guid.NewGuid(), "disk:offline", "Offline disk", "OFFLINE", "E:\\", DateTimeOffset.UtcNow, null));
        var connectedMapping = new BackupTargetMapping(Guid.NewGuid(), set.Id, connectedDevice.Id, "BackupMesh\\Documents");
        var offlineMapping = new BackupTargetMapping(Guid.NewGuid(), set.Id, offlineDevice.Id, "BackupMesh\\Documents");
        var client = new FakeJobClient([]);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: client);
        viewModel.BackupSets.Add(set);
        viewModel.Devices.Add(connectedDevice);
        viewModel.Devices.Add(offlineDevice);
        viewModel.Mappings.Add(new(connectedMapping, set, connectedDevice));
        viewModel.Mappings.Add(new(offlineMapping, set, offlineDevice));

        await viewModel.QueueEligibleBackupsAsync();

        var mappingId = Assert.Single(client.EnqueuedMappingIds);
        Assert.Equal(connectedMapping.Id, mappingId);
        Assert.Contains("Queued 1 backup.", viewModel.FooterStatus);
    }

    [Fact]
    public void ConsecutiveMappingsSharingASourceOrSourceFolderAreFlaggedAsRepeats()
    {
        // Keep the real repeated value and use visual dimming in MainWindow.xaml rather than text substitution. The
        // text is always the bound value; only these two flags change.
        var sourceId = Guid.NewGuid();
        var set = new BackupSetViewModel(new(Guid.NewGuid(), sourceId, "Studio", "Documents", ["C:\\Data"]));
        var otherSet = new BackupSetViewModel(new(Guid.NewGuid(), sourceId, "Studio", "Photos", ["C:\\Photos"]));
        var deviceA = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Disk A", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var deviceB = new DeviceViewModel(new(Guid.NewGuid(), "disk:b", "Disk B", "B", "E:\\", DateTimeOffset.UtcNow, null));
        using var viewModel = new MainWindowViewModel(loadLocalState: false);
        var first = new MappingViewModel(new(Guid.NewGuid(), set.Id, deviceA.Id, "docs-a"), set, deviceA);
        var second = new MappingViewModel(new(Guid.NewGuid(), set.Id, deviceB.Id, "docs-b"), set, deviceB);
        var unrelated = new MappingViewModel(new(Guid.NewGuid(), otherSet.Id, deviceA.Id, "photos-a"), otherSet, deviceA);

        viewModel.Mappings.Add(first);
        viewModel.Mappings.Add(second);
        viewModel.Mappings.Add(unrelated);

        Assert.False(first.IsRepeatOfPreviousSourceFolder);
        Assert.Equal("Studio", first.SourceAgentName);
        Assert.True(second.IsRepeatOfPreviousSource);
        Assert.True(second.IsRepeatOfPreviousSourceFolder);
        Assert.Equal("Studio", second.SourceAgentName);
        Assert.Equal("Documents", second.BackupSetOnlyName);
        // Same computer, different Backup Set: the Source repeats but the folder does not, so only the
        // Source half is flagged.
        Assert.True(unrelated.IsRepeatOfPreviousSource);
        Assert.False(unrelated.IsRepeatOfPreviousSourceFolder);
        Assert.Equal("Photos", unrelated.BackupSetOnlyName);
    }

    [Fact]
    public async Task LastBackupShowsRelativeTimeAndAFailureReasonForTheMostRecentAttempt()
    {
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Disk A", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var mapping = new BackupTargetMapping(Guid.NewGuid(), set.Id, device.Id, "docs");
        var jobs = new[]
        {
            new BackupJobDto(Guid.NewGuid(), "SUCCEEDED", DateTimeOffset.UtcNow.AddDays(-6), null, null, mapping.Id),
            new BackupJobDto(Guid.NewGuid(), "FAILED", DateTimeOffset.UtcNow.AddHours(-1), null, null, mapping.Id)
        };
        var client = new FakeJobClient(jobs);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: client);
        viewModel.Mappings.Add(new(mapping, set, device));

        await viewModel.RefreshJobsAsync();

        var view = Assert.Single(viewModel.Mappings);
        Assert.Contains("hour", view.LastBackupDisplay);
        Assert.Equal("Last attempt failed", view.LastBackupIssue);
    }

    [Fact]
    public async Task LastBackupIsNeverWithNoJobHistory()
    {
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Disk A", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var mapping = new BackupTargetMapping(Guid.NewGuid(), set.Id, device.Id, "docs");
        var client = new FakeJobClient([]);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: client);
        viewModel.Mappings.Add(new(mapping, set, device));

        await viewModel.RefreshJobsAsync();

        var view = Assert.Single(viewModel.Mappings);
        Assert.Equal("Never", view.LastBackupDisplay);
        Assert.Equal(string.Empty, view.LastBackupIssue);
    }

    [Fact]
    public async Task TriggerNoteDescribesAnExplicitTriggerDeviceReadOnly()
    {
        // The per-row editor for TriggerDeviceIds/TriggerPolicy is gone from this screen, but a Backup Set
        // that already names an explicit trigger device (e.g. the external-source-arrival case, or a config
        // authored before the editor was removed) still only starts for that device - never "whenever its
        // Target connects", the way an untriggered row's default behavior does. This must stay visible even
        // with no editor for it, or the grid silently implies behavior the mapping doesn't actually have.
        var cameraCard = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Camera card", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var backupUsb = new DeviceViewModel(new(Guid.NewGuid(), "disk:b", "Backup USB", "B", "E:\\", DateTimeOffset.UtcNow, null));
        var archive = new DeviceViewModel(new(Guid.NewGuid(), "disk:c", "Archive drive", "C", "F:\\", DateTimeOffset.UtcNow, null));
        var set = new SourceBackupSet(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Photos", ["C:\\Photos"], [cameraCard.Id, backupUsb.Id], BackupSetTriggerPolicy.AllAvailable);
        var mapping = new BackupTargetMapping(Guid.NewGuid(), set.Id, archive.Id, "photos");
        var client = new FakeJobClient([]);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: client);
        viewModel.Devices.Add(cameraCard);
        viewModel.Devices.Add(backupUsb);
        viewModel.Devices.Add(archive);
        viewModel.Mappings.Add(new(mapping, new BackupSetViewModel(set), archive));

        await viewModel.RefreshJobsAsync();

        var view = Assert.Single(viewModel.Mappings);
        Assert.Equal("Starts when Camera card and Backup USB are all connected", view.TriggerNote);
    }

    [Fact]
    public async Task TriggerNoteIsEmptyWithoutAnExplicitTriggerDevice()
    {
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Disk A", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var mapping = new BackupTargetMapping(Guid.NewGuid(), set.Id, device.Id, "docs");
        var client = new FakeJobClient([]);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: client);
        viewModel.Mappings.Add(new(mapping, set, device));

        await viewModel.RefreshJobsAsync();

        Assert.Equal(string.Empty, Assert.Single(viewModel.Mappings).TriggerNote);
    }

    [Fact]
    public async Task BackupNowDoesNotCallServiceWhenNoMappingIsEligible()
    {
        var sourceId = Guid.NewGuid();
        var set = new BackupSetViewModel(new(Guid.NewGuid(), sourceId, "Studio", "Documents", ["C:\\Data"]));
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:offline", "Offline disk", "OFFLINE", "E:\\", DateTimeOffset.UtcNow, null));
        var client = new FakeJobClient([]);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: client);
        viewModel.BackupSets.Add(set);
        viewModel.Devices.Add(device);
        viewModel.Mappings.Add(new(new(Guid.NewGuid(), set.Id, device.Id, "BackupMesh\\Documents"), set, device));

        await viewModel.QueueEligibleBackupsAsync();

        Assert.Empty(client.EnqueuedMappingIds);
        Assert.Equal("No mapped backup is currently eligible.", viewModel.FooterStatus);
    }

    [Fact]
    public async Task SaveUsesTheRevisionLoadedFromService()
    {
        var client = new FakeConfigurationClient(new(4, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client);
        await viewModel.RefreshConfigurationAsync();

        await viewModel.SaveAsync();

        Assert.Equal(4, client.LastExpectedRevision);
        Assert.Equal(5, client.Document.Revision);
    }

    [Fact]
    public async Task SavingForcesEveryDeviceOntoTheGlobalArrivalDelay()
    {
        // The Devices tab's per-device arrival-delay editor is gone, and DefaultArrivalDelayMinutes is
        // the only place left to see or change this - so it must actually govern already-registered
        // devices too, not just ones registered after it was last changed, or the setting on screen
        // would silently disagree with what's really in effect (the same failure mode the removed
        // trigger-device editor had).
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Disk A", "A", "D:\\", DateTimeOffset.UtcNow, null, ArrivalDelayMinutes: 90));
        var client = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client) { DefaultArrivalDelayMinutes = 5 };
        await viewModel.RefreshConfigurationAsync();
        viewModel.Devices.Add(device);

        await viewModel.SaveAsync();

        Assert.Equal(5, device.ArrivalDelayMinutes);
        Assert.Equal(5, client.Document.Configuration.Devices.Single().ArrivalDelayMinutes);
    }

    private sealed class FakeConfigurationClient(StorageConfigurationDocumentDto document) : IStorageConfigurationClient
    {
        public StorageConfigurationDocumentDto Document { get; private set; } = document;
        public long? LastExpectedRevision { get; private set; }
        public Task<StorageConfigurationDocumentDto> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Document);
        public Task<StorageConfigurationDocumentDto> UpdateAsync(long expectedRevision, StorageAgentConfiguration configuration, CancellationToken cancellationToken)
        {
            LastExpectedRevision = expectedRevision;
            Document = new(expectedRevision + 1, DateTimeOffset.UtcNow, configuration);
            return Task.FromResult(Document);
        }
        public Task<AutomationSettingsDto> GetAutomationAsync(CancellationToken cancellationToken) => Task.FromResult(new AutomationSettingsDto(true));
        public Task<AutomationSettingsDto> UpdateAutomationAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(new AutomationSettingsDto(enabled));
    }

    // Regression coverage for a peer-review finding: Start now / Skip this time on the tray flyout looked
    // functional but had no effect on whether a backup actually ran - StorageMonitorService (the Windows
    // Service background loop) enforces the arrival delay entirely on its own, independent of this client,
    // so a client-local-only "skip" flag could never have worked.
    [Fact]
    public async Task SkipDeviceThisConnectionDisablesOnlyThatDevicesCurrentlyEnabledMappings()
    {
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive drive", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var otherDevice = new DeviceViewModel(new(Guid.NewGuid(), "disk:b", "Other drive", "B", "E:\\", DateTimeOffset.UtcNow, null));
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var alreadyDisabled = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "docs-a", Enabled: false), set, device);
        var enabled = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "docs-b"), set, device);
        var unrelated = new MappingViewModel(new(Guid.NewGuid(), set.Id, otherDevice.Id, "docs-c"), set, otherDevice);
        var client = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client);
        await viewModel.RefreshConfigurationAsync();
        viewModel.Devices.Add(device);
        viewModel.Devices.Add(otherDevice);
        viewModel.BackupSets.Add(set);
        viewModel.Mappings.Add(alreadyDisabled);
        viewModel.Mappings.Add(enabled);
        viewModel.Mappings.Add(unrelated);

        await viewModel.SkipDeviceThisConnectionAsync(device.Id);

        Assert.False(alreadyDisabled.Enabled);
        Assert.False(enabled.Enabled);
        Assert.True(unrelated.Enabled);
        // Persisted, not just changed in memory - StorageMonitorService reads the saved configuration, not
        // this process's live objects.
        Assert.Contains(client.Document.Configuration.Mappings, m => m.Id == enabled.Id && !m.Enabled);
    }

    // Regression coverage for a peer-review finding: SkipDeviceThisConnectionAsync persisted Enabled=false
    // via SaveAsync(), but the tracking set that undoes it on disconnect was in-memory only - if the app
    // exited before the device disconnected (crash, forced close, or just quitting), the mapping stayed
    // disabled forever with nothing on screen explaining why. AppConfiguration.SkipDisabledMappingIds
    // persists that tracking, and ApplyTopology - the authoritative rebuild of Mappings from the Storage
    // Service, which always runs once at startup - restores every tracked mapping unconditionally and
    // clears the list, since "this connection" stopped meaning anything the moment the tracking process
    // that remembered it was gone.
    [Fact]
    public async Task ConfigRefreshAfterRestartRestoresAMappingSkipLeftDisabled()
    {
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive drive", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var mapping = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "docs"), set, device);
        var client = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client);
        await viewModel.RefreshConfigurationAsync();
        viewModel.Devices.Add(device);
        viewModel.BackupSets.Add(set);
        viewModel.Mappings.Add(mapping);
        await viewModel.SkipDeviceThisConnectionAsync(device.Id);
        Assert.False(mapping.Enabled);
        Assert.Contains(client.Document.Configuration.Mappings, m => m.Id == mapping.Id && !m.Enabled);

        // Simulates the app restarting and refreshing configuration from the Storage Service, rather than
        // the device disconnecting (which is the path already covered above) - ApplyTopology rebuilds
        // Mappings from scratch, so this is intentionally a fresh MappingViewModel instance, not `mapping`.
        await viewModel.RefreshConfigurationAsync();

        var rebuilt = Assert.Single(viewModel.Mappings);
        Assert.Equal(mapping.Id, rebuilt.Id);
        Assert.True(rebuilt.Enabled);
    }

    [Fact]
    public void SkippedMappingsAreRestoredOnlyWhenTheSameDeviceDisconnectsAgain()
    {
        var inventory = new MutableDeviceInventory();
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive drive", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var alreadyDisabled = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "docs-a", Enabled: false), set, device);
        var enabled = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "docs-b"), set, device);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, deviceInventory: inventory);
        viewModel.Devices.Add(device);
        viewModel.Mappings.Add(alreadyDisabled);
        viewModel.Mappings.Add(enabled);

        inventory.Drives = [new("disk:a", "D:\\", "ARCHIVE", 100, 200, "Archive drive", 1, true)];
        viewModel.RefreshDrivesCommand.Execute(null);
        _ = viewModel.SkipDeviceThisConnectionAsync(device.Id);

        Assert.False(enabled.Enabled);

        inventory.Drives = [];
        viewModel.RefreshDrivesCommand.Execute(null);

        Assert.True(enabled.Enabled);
        Assert.False(alreadyDisabled.Enabled); // never touched by the skip - stays exactly as the user left it.
    }

    [Fact]
    public async Task QueueBackupsForDeviceOnlyEnqueuesThatDevicesEligibleMappings()
    {
        var deviceA = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive drive", "A", "D:\\", DateTimeOffset.UtcNow, null)) { IsConnected = true };
        var deviceB = new DeviceViewModel(new(Guid.NewGuid(), "disk:b", "Other drive", "B", "E:\\", DateTimeOffset.UtcNow, null)) { IsConnected = true };
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var mappingA = new MappingViewModel(new(Guid.NewGuid(), set.Id, deviceA.Id, "docs-a"), set, deviceA);
        var mappingB = new MappingViewModel(new(Guid.NewGuid(), set.Id, deviceB.Id, "docs-b"), set, deviceB);
        var client = new FakeJobClient([]);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: client);
        viewModel.Devices.Add(deviceA);
        viewModel.Devices.Add(deviceB);
        viewModel.Mappings.Add(mappingA);
        viewModel.Mappings.Add(mappingB);

        await viewModel.QueueBackupsForDeviceAsync(deviceA.Id);

        Assert.Equal(mappingA.Id, Assert.Single(client.EnqueuedMappingIds));
    }

    private sealed class FakeDeviceInventory(IReadOnlyList<AvailableDriveViewModel> drives) : IDeviceInventory
    {
        public IReadOnlyList<AvailableDriveViewModel> GetStorageDevices() => drives;
    }

    private sealed class MutableDeviceInventory : IDeviceInventory
    {
        public IReadOnlyList<AvailableDriveViewModel> Drives { get; set; } = [];
        public IReadOnlyList<AvailableDriveViewModel> GetStorageDevices() => Drives;
    }

    private sealed class FakeJobClient(IReadOnlyList<BackupJobDto> jobs) : IBackupJobClient
    {
        public List<Guid> EnqueuedMappingIds { get; } = [];
        public Task<IReadOnlyList<BackupJobDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(jobs);
        public Task<int> EnqueueAsync(Guid[] mappingIds, string reason, CancellationToken cancellationToken)
        {
            EnqueuedMappingIds.AddRange(mappingIds);
            return Task.FromResult(mappingIds.Length);
        }
        public Task CancelAsync(Guid jobId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

file sealed class FakeStorageDeviceClient : IStorageDeviceClient
{
    public int CallCount { get; private set; }
    public Task EjectAsync(Guid deviceId, CancellationToken cancellationToken) { CallCount++; return Task.CompletedTask; }
}
