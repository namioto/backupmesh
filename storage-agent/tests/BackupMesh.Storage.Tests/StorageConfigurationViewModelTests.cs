using BackupMesh.Storage.App;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Tests;

public sealed class StorageConfigurationViewModelTests
{
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
        public Task<IReadOnlyList<BackupJobDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(jobs);
        public Task CancelAsync(Guid jobId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
