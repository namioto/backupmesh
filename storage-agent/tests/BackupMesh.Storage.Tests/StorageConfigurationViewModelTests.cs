using BackupMesh.Storage.App;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Tests;

[Collection("Localization")]
public sealed class StorageConfigurationViewModelTests
{
    public StorageConfigurationViewModelTests() => Localization.Initialize("en");
    [Fact]
    public void JobDisplayDistinguishesDestinationsAndPreservesFailureReason()
    {
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", [@"C:\Data"]));
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Disk A", "READY", @"D:\", DateTimeOffset.UtcNow, null));
        var first = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "one"), set, device);
        var second = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "two"), set, device);
        const string reason = "Access denied: D:\\one";
        var failed = new BackupJobViewModel(new(Guid.NewGuid(), "FAILED", DateTimeOffset.UtcNow, null, new("FAILED", null, reason)), first);
        var other = new BackupJobViewModel(new(Guid.NewGuid(), "SUCCEEDED", DateTimeOffset.UtcNow, null, new("SUCCEEDED", "1234567890", null)), second);

        Assert.NotEqual(failed.Target, other.Target);
        Assert.Contains(first.DestinationFolder, failed.Target);
        Assert.Contains(reason, failed.Result);
        Assert.Contains("12345678", other.Result);
    }

    [Fact]
    public void DashboardTransferTracksOnlyActiveMappedJobs()
    {
        using var viewModel = new MainWindowViewModel(loadLocalState: false);
        var agentId = Guid.NewGuid();
        var source = new BackupSetViewModel(new(Guid.NewGuid(), agentId, "Remote", "Documents", [@"C:\Data"]));
        var agent = new RemoteAgentViewModel(agentId, "Remote")
        {
            Connection = new(new(agentId, "Remote", "Remote", DateTimeOffset.UtcNow, null, 1, false, null))
        };
        viewModel.Sources.Add(agent);
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive drive", "A", @"D:\", DateTimeOffset.UtcNow, null)) { IsConnected = true };
        viewModel.Devices.Add(device);
        var mapping = new MappingViewModel(new(Guid.NewGuid(), source.Id, device.Id, "backup"), source, device);
        viewModel.Mappings.Add(mapping);
        viewModel.Jobs.Add(new(new(Guid.NewGuid(), "RUNNING", DateTimeOffset.UtcNow, new(50, 100, 1, 2), null, mapping.Id)));

        Assert.True(viewModel.IsDashboardTransferActive);
        Assert.True(viewModel.IsRemoteDashboardTransferActive);
        Assert.Equal(50, viewModel.DashboardTransferPercent);
        Assert.Contains("50%", viewModel.DashboardTransferLabel);
        Assert.False(viewModel.IsDashboardTransferIndeterminate);

        device.IsConnected = false;
        Assert.Equal("저장 장치 없음", viewModel.DashboardDeviceName);
        Assert.False(viewModel.IsDashboardTransferActive);
        device.IsConnected = true;

        viewModel.Jobs.Clear();
        viewModel.Jobs.Add(new(new(Guid.NewGuid(), "SUCCEEDED", DateTimeOffset.UtcNow, null, null, mapping.Id)));
        Assert.False(viewModel.IsDashboardTransferActive);
    }

    [Fact]
    public void DashboardShowsConnectedNamesInsteadOfOfflineRegistrations()
    {
        var inventory = new MutableDeviceInventory();
        using var viewModel = new MainWindowViewModel(loadLocalState: false, deviceInventory: inventory);
        var offline = new DeviceViewModel(new(Guid.NewGuid(), "disk:old", "Old drive", "OLD", @"E:\", DateTimeOffset.UtcNow, null));
        var connected = new DeviceViewModel(new(Guid.NewGuid(), "disk:new", "Current drive", "NEW", @"F:\", DateTimeOffset.UtcNow, null));
        viewModel.Devices.Add(offline);
        viewModel.Devices.Add(connected);
        var nameChanged = 0;
        viewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(viewModel.DashboardDeviceName)) nameChanged++; };
        inventory.Drives = [new("disk:new", @"F:\", "NEW", 1, 2, "Current drive", 1)];
        viewModel.RefreshDrivesCommand.Execute(null);
        Assert.Equal("Current drive", viewModel.DashboardDeviceName);

        var oldAgentId = Guid.NewGuid();
        var oldAgent = new RemoteAgentViewModel(oldAgentId, "Old computer")
        {
            Connection = new(new(oldAgentId, "Old computer", "Old computer", DateTimeOffset.UtcNow.AddMinutes(-10), null, 1, false, null))
        };
        var currentAgentId = Guid.NewGuid();
        var currentAgent = new RemoteAgentViewModel(currentAgentId, "Current computer")
        {
            Connection = new(new(currentAgentId, "Current computer", "Current computer", DateTimeOffset.UtcNow, null, 1, false, null))
        };
        viewModel.Sources.Add(oldAgent);
        viewModel.Sources.Add(currentAgent);
        Assert.Equal("Current computer", viewModel.LaptopName);

        inventory.Drives = [];
        viewModel.RefreshDrivesCommand.Execute(null);
        Assert.Equal("저장 장치 없음", viewModel.DashboardDeviceName);
        Assert.Equal("연결 대기 중", viewModel.DashboardDeviceStatus);
        Assert.True(nameChanged >= 2);
        currentAgent.Connection = new(new(currentAgentId, "Current computer", "Current computer", DateTimeOffset.UtcNow.AddMinutes(-10), null, 1, false, null));
        Assert.Equal("원격 컴퓨터 없음", viewModel.LaptopName);
    }

    [Fact]
    public async Task RecentActivityRecordsJobTransitionsOnceWithTimeAndKind()
    {
        var jobs = new List<BackupJobDto>();
        var configuration = new FakeConfigurationClient(new(0, DateTimeOffset.UtcNow, new([], [], [])));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: new FakeJobClient(jobs), configurationClient: configuration);
        await viewModel.RefreshJobsAsync();

        var id = Guid.NewGuid();
        jobs.Add(new(id, "RUNNING", DateTimeOffset.UtcNow, new(25, 100, 1, 4), null));
        await viewModel.RefreshJobsAsync();
        await viewModel.RefreshJobsAsync();
        Assert.Single(viewModel.Activity, item => item.Kind == ActivityKind.Started);

        jobs[0] = new(id, "SUCCEEDED", DateTimeOffset.UtcNow, null, new("SUCCEEDED", "snapshot", null));
        await viewModel.RefreshJobsAsync();
        var completed = Assert.Single(viewModel.Activity, item => item.Kind == ActivityKind.Completed);
        Assert.EndsWith("activity-complete.png", completed.IconSource);
        Assert.Contains("Today", completed.DetailAndTime);

        jobs.Add(new(Guid.NewGuid(), "FAILED", DateTimeOffset.UtcNow.AddDays(-1), null, new("FAILED", null, "Old failure")));
        await viewModel.RefreshJobsAsync();
        Assert.Equal(ActivityKind.Failed, viewModel.Activity.Last().Kind);
    }

    [Fact]
    public void RefreshDrivesPublishesEveryAvailableBackupDestination()
    {
        var first = new AvailableDriveViewModel("disk:first", "C:\\", "FIRST", 1, 2, "First disk", 1);
        var second = new AvailableDriveViewModel("disk:second", "D:\\", "SECOND", 1, 2, "Second disk", 1);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, deviceInventory: new FakeDeviceInventory([first, second]));
        viewModel.RefreshDrivesCommand.Execute(null);

        Assert.Equal([first.StableId, second.StableId], viewModel.BackupDestinations.Select(option => option.StableId));
    }

    [Fact]
    public void RefreshDrivesKeepsSelectedAndDraftDestinationOptions()
    {
        var inventory = new MutableDeviceInventory
        {
            Drives = [new("disk:first", "D:\\", "FIRST", 1, 2, "First disk", 1)]
        };
        using var viewModel = new MainWindowViewModel(loadLocalState: false, deviceInventory: inventory);
        viewModel.RefreshDrivesCommand.Execute(null);
        var selected = Assert.Single(viewModel.BackupDestinations);
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "backupmesh-destination-" + Guid.NewGuid())).FullName;
        try
        {
            var draft = viewModel.AddFolderDestination(folder);
            inventory.Drives = [new("disk:first", "D:\\", "FIRST", 2, 3, "First disk", 1)];

            viewModel.RefreshDrivesCommand.Execute(null);

            Assert.Same(selected, viewModel.BackupDestinations[0]);
            Assert.Same(draft, viewModel.BackupDestinations[1]);
        }
        finally { Directory.Delete(folder); }
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
    public async Task ChosenFolderIsSavedAsTheExactRepositoryDestination()
    {
        var drive = new AvailableDriveViewModel("disk:target", "D:\\", "TARGET", 100, 200, "Target disk", 1);
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "This PC", "Immich", ["C:\\Photos"]));
        var client = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client, deviceInventory: new FakeDeviceInventory([drive]));
        viewModel.BackupSets.Add(set);
        viewModel.RefreshDrivesCommand.Execute(null);

        var error = await viewModel.SaveMappingAsync(null, set, Assert.Single(viewModel.BackupDestinations), "D:\\immich test", true);

        Assert.Null(error);
        var mapping = Assert.Single(viewModel.Mappings);
        Assert.Equal("immich test", mapping.RepositoryPath);
        Assert.Equal("D:\\immich test", mapping.DestinationFolder);
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
    public async Task StartNowEnqueuesOnlySelectedRule()
    {
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Disk A", "READY", "D:\\", DateTimeOffset.UtcNow, null)) { IsConnected = true };
        var selected = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "one"), set, device);
        var other = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "two"), set, device);
        var client = new FakeJobClient([]);
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: client);
        viewModel.Mappings.Add(selected);
        viewModel.Mappings.Add(other);
        viewModel.SelectedMapping = selected;

        await viewModel.QueueSelectedMappingAsync();

        Assert.Equal([selected.Id], client.EnqueuedMappingIds);
    }

    [Fact]
    public async Task SelectedRulesQueueAndRemoveOnlyChosenMappings()
    {
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", [@"C:\Data"]));
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Disk A", "READY", @"D:\", DateTimeOffset.UtcNow, null)) { IsConnected = true };
        var selected = new[] { "one", "two" }.Select(folder => new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, folder), set, device)).ToArray();
        var other = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "three"), set, device);
        var jobs = new FakeJobClient([]);
        var configuration = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, jobClient: jobs, configurationClient: configuration);
        viewModel.BackupSets.Add(set);
        viewModel.Devices.Add(device);
        foreach (var mapping in selected) viewModel.Mappings.Add(mapping);
        viewModel.Mappings.Add(other);

        await viewModel.QueueMappingsAsync(selected);
        Assert.Equal(selected.Select(mapping => mapping.Id), jobs.EnqueuedMappingIds);

        await viewModel.RemoveMappingsAsync(selected);
        Assert.Equal(other, Assert.Single(viewModel.Mappings));
        Assert.Contains(device, viewModel.Devices);
    }

    [Fact]
    public void NextBackupUsesServiceEligibilityTime()
    {
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Disk A", "READY", "D:\\", DateTimeOffset.UtcNow, null));
        var mapping = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "one"), set, device);
        var now = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        var status = new StorageDeviceStatusDto(device.Id, true, false, now.AddMinutes(6));

        var display = MainWindowViewModel.ComputeNextBackupDisplay(mapping, true, status, null, false, now);

        Assert.Contains("6 min", display);
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

    [Fact]
    public async Task SavingAnIdenticalBackupRuleIsRejectedWithoutAddingAnotherRow()
    {
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive drive", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var existing = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "BackupMesh\\Documents"), set, device);
        var client = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client);
        viewModel.Devices.Add(device);
        viewModel.BackupSets.Add(set);
        viewModel.Mappings.Add(existing);

        var error = await viewModel.SaveMappingAsync(null, set, device, "backupmesh/Documents/", enabled: true);

        Assert.Equal("That backup rule already exists.", error);
        Assert.Single(viewModel.Mappings);
    }

    [Fact]
    public async Task EditingABackupRuleKeepsItsIdentityAndReplacesTheRow()
    {
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive drive", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Studio", "Documents", ["C:\\Data"]));
        var existing = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "old"), set, device);
        var client = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client);
        viewModel.Devices.Add(device);
        viewModel.BackupSets.Add(set);
        viewModel.Mappings.Add(existing);

        var error = await viewModel.SaveMappingAsync(existing, set, device, "new", enabled: false);

        Assert.Null(error);
        var saved = Assert.Single(viewModel.Mappings);
        Assert.Equal(existing.Id, saved.Id);
        Assert.Equal("new", saved.RepositoryPath);
        Assert.False(saved.Enabled);
    }

    [Fact]
    public async Task BackupRulePersistsSelectedPathsForOneDestination()
    {
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:docs", "Archive", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "Remote", "Documents", ["/doc/img", "/doc/db"]));
        var client = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client);
        viewModel.Devices.Add(device);
        viewModel.BackupSets.Add(set);

        Assert.Null(await viewModel.SaveMappingAsync(null, set, device, "BackupMesh/docs", true, ["/doc/db"]));
        var mapping = Assert.Single(viewModel.Mappings);
        Assert.Equal(["/doc/db"], mapping.SelectedSourcePaths);
        Assert.Equal("/doc/db", mapping.SourcePathsDisplay);
        Assert.Equal(["/doc/db"], Assert.Single(client.Document.Configuration.Mappings).SelectedSourcePaths);
    }

    [Fact]
    public async Task ChoosingAConnectedDriveCreatesItsInternalDeviceWithTheRule()
    {
        var drive = new AvailableDriveViewModel("disk:new", "E:\\", "BACKUP", 100, 200, "USB drive", 1, true);
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "This PC", "Documents", ["C:\\Data"]));
        var client = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client, deviceInventory: new FakeDeviceInventory([drive]));
        viewModel.BackupSets.Add(set);
        viewModel.RefreshDrivesCommand.Execute(null);

        var error = await viewModel.SaveMappingAsync(null, set, Assert.Single(viewModel.BackupDestinations), "E:\\BackupMesh\\Documents", true);

        Assert.Null(error);
        Assert.Single(viewModel.Devices);
        Assert.Equal("disk:new", Assert.Single(viewModel.Devices).StableId);
        Assert.Single(viewModel.Mappings);
    }

    [Fact]
    public async Task RemovingTheLastRuleAlsoRemovesItsUnreferencedInternalDevice()
    {
        var device = new DeviceViewModel(new(Guid.NewGuid(), "disk:a", "Archive drive", "A", "D:\\", DateTimeOffset.UtcNow, null));
        var set = new BackupSetViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "This PC", "Documents", ["C:\\Data"]));
        var mapping = new MappingViewModel(new(Guid.NewGuid(), set.Id, device.Id, "BackupMesh\\Documents"), set, device);
        var client = new FakeConfigurationClient(new(1, DateTimeOffset.UtcNow, StorageAgentConfiguration.Empty));
        using var viewModel = new MainWindowViewModel(loadLocalState: false, configurationClient: client);
        viewModel.Devices.Add(device);
        viewModel.BackupSets.Add(set);
        viewModel.Mappings.Add(mapping);
        viewModel.SelectedMapping = mapping;

        viewModel.RemoveMappingCommand.Execute(null);
        await Task.Delay(50);

        Assert.Empty(viewModel.Mappings);
        Assert.Empty(viewModel.Devices);
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
