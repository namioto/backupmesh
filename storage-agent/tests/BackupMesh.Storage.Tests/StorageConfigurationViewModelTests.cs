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
        Assert.Contains("Queued 1 mapped backup target", viewModel.FooterStatus);
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
    }

    private sealed class FakeDeviceInventory(IReadOnlyList<AvailableDriveViewModel> drives) : IDeviceInventory
    {
        public IReadOnlyList<AvailableDriveViewModel> GetStorageDevices() => drives;
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
