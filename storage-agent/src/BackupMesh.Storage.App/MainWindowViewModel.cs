using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using BackupMesh.Storage.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BackupMesh.Storage.App;

public sealed record AppNotification(string Title, string Message, bool IsError = false);

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int NewDeviceArrivalDelayMinutes = 30;
    private readonly ConfigurationStore _store = new();
    private readonly IDeviceInventory _deviceInventory;
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _catalogTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _jobTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ISourceCatalogClient _catalogClient;
    private readonly IStorageConfigurationClient _configurationClient;
    private readonly IBackupJobClient _jobClient;
    private readonly IStorageDeviceClient _storageDeviceClient;
    private readonly IPairingClient _pairingClient;
    private readonly ISourceConnectionsClient _connectionsClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<string> _connectedRoots = new(StringComparer.OrdinalIgnoreCase);
    private BackupSetViewModel? _selectedBackupSet;
    private SourceConnectionViewModel? _selectedSourceConnection;
    private DeviceViewModel? _selectedDevice;
    private AvailableDriveViewModel? _selectedAvailableDrive;
    private MappingViewModel? _selectedMapping;
    private BackupJobViewModel? _selectedJob;
    private string _newDestinationFolder = string.Empty;
    private string _overallStatus = "Ready";
    private string _footerStatus = "Configuration loaded.";
    private long _configurationRevision;
    private readonly bool _demoMode;
    private readonly bool _persistLocalState;

    public ObservableCollection<SourceAgentViewModel> Sources { get; } = [];
    public ObservableCollection<SourceConnectionViewModel> SourceConnections { get; } = [];
    public ObservableCollection<BackupSetViewModel> BackupSets { get; } = [];
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<MappingViewModel> Mappings { get; } = [];
    public ObservableCollection<AvailableDriveViewModel> AvailableDrives { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];
    public ObservableCollection<BackupJobViewModel> Jobs { get; } = [];

    public event EventHandler<AppNotification>? NotificationRequested;
    public event EventHandler<string>? StatusChanged;

    public ICommand AddMappingCommand { get; }
    public ICommand BrowseDestinationCommand { get; }
    public ICommand RemoveMappingCommand { get; }
    public ICommand RefreshDrivesCommand { get; }
    public ICommand RegisterDeviceCommand { get; }
    public ICommand RegisterFolderCommand { get; }
    public ICommand ForgetDeviceCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelJobCommand { get; }
    public ICommand EjectDeviceCommand { get; }
    public ICommand PairSourceCommand { get; }
    public ICommand RePairSourceCommand { get; }
    public ICommand RevokeSourceCommand { get; }
    public ICommand UnrevokeSourceCommand { get; }
    public ICommand RenameSourceCommand { get; }
    public ICommand ForgetSourceCommand { get; }
    public ICommand RotateStorageIdentityCommand { get; }

    public MainWindowViewModel(bool demoMode = false, ISourceCatalogClient? catalogClient = null, bool loadLocalState = true, IStorageConfigurationClient? configurationClient = null, IDeviceInventory? deviceInventory = null, IBackupJobClient? jobClient = null, IStorageDeviceClient? storageDeviceClient = null, IPairingClient? pairingClient = null, ISourceConnectionsClient? connectionsClient = null)
    {
        _demoMode = demoMode;
        _persistLocalState = loadLocalState;
        _catalogClient = catalogClient ?? new SourceCatalogClient();
        _configurationClient = configurationClient ?? new StorageConfigurationClient();
        _deviceInventory = deviceInventory ?? new WindowsDeviceInventory();
        _jobClient = jobClient ?? new BackupJobClient();
        _storageDeviceClient = storageDeviceClient ?? new StorageDeviceClient();
        _pairingClient = pairingClient ?? new PairingClient();
        _connectionsClient = connectionsClient ?? new SourceConnectionsClient();
        AddMappingCommand = new RelayCommand(AddMapping);
        BrowseDestinationCommand = new RelayCommand(BrowseDestination);
        RemoveMappingCommand = new RelayCommand(RemoveMapping);
        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        RegisterDeviceCommand = new RelayCommand(RegisterDevice);
        RegisterFolderCommand = new RelayCommand(RegisterFolder);
        ForgetDeviceCommand = new RelayCommand(ForgetDevice);
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        CancelJobCommand = new RelayCommand(() => _ = CancelSelectedJobAsync());
        EjectDeviceCommand = new RelayCommand(() => _ = EjectSelectedDeviceAsync());
        PairSourceCommand = new RelayCommand(() => _ = PairSourceAsync(rebind: null));
        RePairSourceCommand = new RelayCommand(() => _ = PairSourceAsync(rebind: SelectedSourceConnection));
        RevokeSourceCommand = new RelayCommand(() => _ = SetSourceRevocationAsync(revoked: true));
        UnrevokeSourceCommand = new RelayCommand(() => _ = SetSourceRevocationAsync(revoked: false));
        RenameSourceCommand = new RelayCommand(() => _ = RenameSelectedSourceAsync());
        ForgetSourceCommand = new RelayCommand(() => _ = ForgetSelectedSourceAsync());
        RotateStorageIdentityCommand = new RelayCommand(() => _ = RotateStorageIdentityAsync());
        _deviceTimer.Tick += (_, _) => RefreshDrives();
        _catalogTimer.Tick += async (_, _) => { await RefreshCatalogsAsync(); await RefreshConnectionsAsync(); };
        _jobTimer.Tick += async (_, _) => await RefreshJobsAsync();
        if (loadLocalState) Load();
        else Activity.Add("Storage Agent UI test state initialized.");
        if (_demoMode && BackupSets.Count == 0) LoadDemoSources();
        if (loadLocalState) RefreshDrives();
    }

    public string OverallStatus { get => _overallStatus; private set { Set(ref _overallStatus, value); StatusChanged?.Invoke(this, $"BackupMesh Storage Agent — {value}"); } }
    public string FooterStatus { get => _footerStatus; private set => Set(ref _footerStatus, value); }
    public int ConnectedDeviceCount => Devices.Count(device => device.IsConnected);
    public int SourceCount => Sources.Count;
    public int MappingCount => Mappings.Count(mapping => mapping.Enabled);
    public BackupSetViewModel? SelectedBackupSet { get => _selectedBackupSet; set => Set(ref _selectedBackupSet, value); }
    public SourceConnectionViewModel? SelectedSourceConnection { get => _selectedSourceConnection; set => Set(ref _selectedSourceConnection, value); }
    public DeviceViewModel? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }
    public AvailableDriveViewModel? SelectedAvailableDrive { get => _selectedAvailableDrive; set => Set(ref _selectedAvailableDrive, value); }
    public MappingViewModel? SelectedMapping { get => _selectedMapping; set => Set(ref _selectedMapping, value); }
    public BackupJobViewModel? SelectedJob { get => _selectedJob; set => Set(ref _selectedJob, value); }
    public string NewDestinationFolder { get => _newDestinationFolder; set => Set(ref _newDestinationFolder, value); }
    public bool StartWithWindows { get; set; } = true;
    public bool NotifyOnDeviceArrival { get; set; } = true;
    public bool AutomaticBackups { get; set; } = true;

    public void StartDeviceMonitoring()
    {
        _deviceTimer.Start();
        if (!_demoMode)
        {
            _catalogTimer.Start();
            _jobTimer.Start();
            _ = InitializeServiceStateAsync();
        }
    }

    private async Task InitializeServiceStateAsync()
    {
        await RefreshConfigurationAsync();
        await RefreshCatalogsAsync();
        await RefreshConnectionsAsync();
        await RefreshJobsAsync();
    }

    public async Task RefreshJobsAsync()
    {
        try
        {
            var selectedId = SelectedJob?.JobId;
            var jobs = await _jobClient.ListAsync(_shutdown.Token);
            Jobs.Clear();
            foreach (var job in jobs) Jobs.Add(new(job));
            SelectedJob = Jobs.FirstOrDefault(job => job.JobId == selectedId) ?? Jobs.FirstOrDefault();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }

    private async Task CancelSelectedJobAsync()
    {
        if (SelectedJob is not { CanCancel: true } job) { FooterStatus = "Select an active backup job first."; return; }
        try
        {
            await _jobClient.CancelAsync(job.JobId, _shutdown.Token);
            FooterStatus = "Cancellation requested. The Source Agent will stop at the next safe point.";
            await RefreshJobsAsync();
        }
        catch (HttpRequestException) { FooterStatus = "The cancellation request could not reach Storage Service."; }
        catch (TaskCanceledException) { FooterStatus = "The cancellation request timed out."; }
    }

    private async Task EjectSelectedDeviceAsync()
    {
        if (SelectedDevice is not { IsConnected: true, CanEject: true } device) { FooterStatus = "Select a connected removable device first."; return; }
        try
        {
            await _storageDeviceClient.EjectAsync(device.Id, _shutdown.Token);
            FooterStatus = $"Safe-removal requested for {device.DisplayName}.";
            AddActivity(FooterStatus);
        }
        catch (HttpRequestException exception) { FooterStatus = $"Safe removal was refused: {exception.Message}"; }
        catch (TaskCanceledException) { FooterStatus = "Safe-removal request timed out."; }
    }

    private async Task PairSourceAsync(SourceConnectionViewModel? rebind)
    {
        try
        {
            var pairing = await _pairingClient.CreateSessionAsync(rebind?.AgentId, _shutdown.Token);
            new PairingDetailsWindow(pairing, rebind?.AgentName).ShowDialog();
            FooterStatus = rebind is null
                ? "One-time pairing details generated. The code expires in 10 minutes and works once."
                : $"One-time re-pairing details generated for {rebind.AgentName}. The code expires in 10 minutes and works once.";
            NotificationRequested?.Invoke(this, new("Source Agent pairing", FooterStatus));
        }
        catch (HttpRequestException exception) { FooterStatus = $"Pairing session could not be created: {exception.Message}"; }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task RefreshCatalogsAsync()
    {
        try
        {
            var catalogs = await _catalogClient.ListAsync(_shutdown.Token);
            ApplyCatalogs(catalogs);
            FooterStatus = catalogs.Count == 0 ? "No Source Agent has published a catalog yet." : $"Synchronized {catalogs.Count} Source Agent catalog(s).";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException)
        {
            FooterStatus = "Storage Service is unavailable; showing the last known Source catalog.";
        }
        catch (TaskCanceledException)
        {
            FooterStatus = "Source catalog synchronization timed out.";
        }
    }

    public Task RefreshCatalogsOnceAsync() => RefreshCatalogsAsync();

    private async Task RefreshConnectionsAsync()
    {
        if (_demoMode) return;
        try
        {
            var connections = await _connectionsClient.ListAsync(_shutdown.Token);
            var selectedId = SelectedSourceConnection?.AgentId;
            SourceConnections.Clear();
            foreach (var connection in connections.OrderBy(c => c.AgentName, StringComparer.OrdinalIgnoreCase)) SourceConnections.Add(new(connection));
            SelectedSourceConnection = SourceConnections.FirstOrDefault(c => c.AgentId == selectedId) ?? SourceConnections.FirstOrDefault();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }

    private async Task SetSourceRevocationAsync(bool revoked)
    {
        var connection = SelectedSourceConnection;
        if (connection is null) return;
        try
        {
            if (revoked) await _connectionsClient.RevokeAsync(connection.AgentId, _shutdown.Token);
            else await _connectionsClient.UnrevokeAsync(connection.AgentId, _shutdown.Token);
            FooterStatus = revoked ? $"Revoked access for {connection.AgentName}." : $"Restored access for {connection.AgentName}.";
            NotificationRequested?.Invoke(this, new("Source Agent connection", FooterStatus));
            await RefreshConnectionsAsync();
        }
        catch (HttpRequestException exception) { FooterStatus = $"Could not update {connection.AgentName}'s access: {exception.Message}"; }
        catch (TaskCanceledException) { FooterStatus = $"The request to update {connection.AgentName}'s access timed out."; }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task RenameSelectedSourceAsync()
    {
        var connection = SelectedSourceConnection;
        if (connection is null) return;
        var dialog = new RenameSourceWindow(connection.AgentName == connection.ReportedAgentName ? string.Empty : connection.AgentName, connection.ReportedAgentName);
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _connectionsClient.RenameAsync(connection.AgentId, dialog.ResultDisplayName, _shutdown.Token);
            FooterStatus = "Source Agent renamed.";
            await RefreshConnectionsAsync();
        }
        catch (HttpRequestException exception) { FooterStatus = $"Could not rename {connection.AgentName}: {exception.Message}"; }
        catch (TaskCanceledException) { FooterStatus = $"The rename request for {connection.AgentName} timed out."; }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ForgetSelectedSourceAsync()
    {
        var connection = SelectedSourceConnection;
        if (connection is null) return;
        var confirmed = System.Windows.MessageBox.Show(
            $"Forget \"{connection.AgentName}\"? It will be revoked immediately. Backup Set mappings that reference it are kept but stop being reported until it is re-paired.",
            "Forget Source Agent", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        if (!confirmed) return;
        try
        {
            await _connectionsClient.ForgetAsync(connection.AgentId, _shutdown.Token);
            FooterStatus = $"Forgot {connection.AgentName}. Its mappings are preserved as unresolved.";
            NotificationRequested?.Invoke(this, new("Source Agent connection", FooterStatus));
            await RefreshConnectionsAsync();
            await RefreshCatalogsAsync();
        }
        catch (HttpRequestException exception) { FooterStatus = $"Could not forget {connection.AgentName}: {exception.Message}"; }
        catch (TaskCanceledException) { FooterStatus = $"The request to forget {connection.AgentName} timed out."; }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task RotateStorageIdentityAsync()
    {
        var confirmed = System.Windows.MessageBox.Show(
            "This regenerates the Storage's certificate authority and server certificate. Every currently paired Source Agent will lose access until it is re-paired, and this only takes effect after you restart the BackupMesh Storage service. Continue?",
            "Rotate Storage identity", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        if (!confirmed) return;
        try
        {
            await _pairingClient.RotateAuthorityAsync(_shutdown.Token);
            FooterStatus = "Storage identity rotated. Restart the BackupMesh Storage service, then re-pair every Source Agent.";
            NotificationRequested?.Invoke(this, new("Storage identity rotated", FooterStatus));
        }
        catch (HttpRequestException exception) { FooterStatus = $"Could not rotate the Storage identity: {exception.Message}"; }
        catch (TaskCanceledException) { FooterStatus = "The Storage identity rotation request timed out."; }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public async Task RefreshConfigurationAsync()
    {
        if (_demoMode) return;
        try
        {
            var document = await _configurationClient.GetAsync(_shutdown.Token);
            ApplyTopology(document.Configuration);
            _configurationRevision = document.Revision;
            AutomaticBackups = (await _configurationClient.GetAutomationAsync(_shutdown.Token)).Enabled;
            FooterStatus = $"Loaded Storage Service configuration revision {document.Revision}.";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException)
        {
            FooterStatus = "Storage Service is unavailable; configuration changes cannot be saved.";
        }
        catch (TaskCanceledException)
        {
            FooterStatus = "Storage Service configuration request timed out.";
        }
        catch (InvalidDataException exception)
        {
            FooterStatus = exception.Message;
        }
    }

    private void ApplyCatalogs(IReadOnlyList<SourceCatalogDto> catalogs)
    {
        foreach (var existing in BackupSets) existing.IsAvailable = false;
        foreach (var catalog in catalogs)
        {
            foreach (var set in catalog.BackupSets)
            {
                var model = new SourceBackupSet(set.BackupSetId, catalog.SourceAgentId, catalog.SourceAgentName, set.Name, set.SourcePaths);
                var existing = BackupSets.FirstOrDefault(item => item.Id == set.BackupSetId);
                if (existing is null) BackupSets.Add(new(model));
                else existing.Update(model);
            }
        }
        Sources.Clear();
        foreach (var group in BackupSets.GroupBy(set => new { set.Model.SourceAgentId, set.Model.SourceAgentName }).OrderBy(group => group.Key.SourceAgentName, StringComparer.OrdinalIgnoreCase))
        {
            var source = new SourceAgentViewModel(group.Key.SourceAgentId, group.Key.SourceAgentName);
            foreach (var set in group.OrderBy(item => item.Model.Name, StringComparer.OrdinalIgnoreCase)) source.BackupSets.Add(set);
            Sources.Add(source);
        }
        SelectedBackupSet ??= BackupSets.FirstOrDefault(set => set.IsAvailable);
        NotifyCounts();
    }

    public void QueueSelectedBackups() => _ = QueueEligibleBackupsAsync();

    public async Task QueueEligibleBackupsAsync()
    {
        var eligible = Mappings.Where(mapping => mapping.Enabled && mapping.Device.IsConnected).ToArray();
        if (eligible.Length == 0)
        {
            const string noTargets = "No mapped backup is currently eligible.";
            FooterStatus = noTargets;
            AddActivity(noTargets);
            NotificationRequested?.Invoke(this, new("BackupMesh", noTargets));
            return;
        }

        try
        {
            var queued = await _jobClient.EnqueueAsync(eligible.Select(mapping => mapping.Id).ToArray(), "manual", _shutdown.Token);
            foreach (var mapping in eligible) AddActivity($"Requested backup for {mapping.BackupSetName} to {mapping.DeviceName}.");
            await RefreshJobsAsync();
            var message = queued == 0
                ? "No new backups were queued; matching backup commands may already be pending."
                : $"Queued {queued} mapped backup target(s).";
            FooterStatus = message;
            NotificationRequested?.Invoke(this, new("BackupMesh", message));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            var message = $"Backup queue request failed: {exception.Message}";
            FooterStatus = message;
            AddActivity(message);
            NotificationRequested?.Invoke(this, new("BackupMesh", message, true));
        }
    }

    private void Load()
    {
        var state = _store.Load();
        StartWithWindows = state.StartWithWindows;
        NotifyOnDeviceArrival = state.NotifyOnDeviceArrival;
        AutomaticBackups = state.AutomaticBackups;

        foreach (var device in state.Topology.Devices) Devices.Add(new(device));
        foreach (var group in state.Topology.BackupSets.GroupBy(set => new { set.SourceAgentId, set.SourceAgentName }))
        {
            var source = new SourceAgentViewModel(group.Key.SourceAgentId, group.Key.SourceAgentName);
            foreach (var backupSet in group)
            {
                var item = new BackupSetViewModel(backupSet);
                source.BackupSets.Add(item);
                BackupSets.Add(item);
            }
            Sources.Add(source);
        }
        foreach (var mapping in state.Topology.Mappings)
        {
            var set = BackupSets.FirstOrDefault(item => item.Id == mapping.BackupSetId);
            var device = Devices.FirstOrDefault(item => item.Id == mapping.DeviceId);
            if (set is not null && device is not null) Mappings.Add(new(mapping, set, device));
        }
        Activity.Add("Storage Agent UI started.");
    }

    private void LoadDemoSources()
    {
        var home = new SourceAgentViewModel(Guid.Parse("c60280da-a03c-4887-a600-577def417af6"), "Home Server");
        AddDemoSet(home, new(Guid.Parse("7d750726-97ab-4f81-9f09-f06c34f524d1"), home.Id, home.DisplayName, "Photos", ["/srv/photos", "/srv/videos"]));
        AddDemoSet(home, new(Guid.Parse("e10a4df5-0f71-438d-93f0-34e587357f00"), home.Id, home.DisplayName, "Documents", ["/home/park/Documents"]));
        Sources.Add(home);

        var workstation = new SourceAgentViewModel(Guid.Parse("0cdf358f-4b92-4bb0-b852-460520508952"), "Studio Workstation");
        AddDemoSet(workstation, new(Guid.Parse("bb452fc9-f616-4810-a649-3c37775d43d4"), workstation.Id, workstation.DisplayName, "Projects", ["D:/Projects"]));
        Sources.Add(workstation);
        SelectedBackupSet = BackupSets.FirstOrDefault();
        AddActivity("Demo Source catalog loaded for UX validation.");
        NotifyCounts();
    }

    private void AddDemoSet(SourceAgentViewModel source, SourceBackupSet model)
    {
        var backupSet = new BackupSetViewModel(model);
        source.BackupSets.Add(backupSet);
        BackupSets.Add(backupSet);
    }

    public async Task SaveAsync()
    {
        var topology = new StorageAgentConfiguration(
            Devices.Select(device => device.ToModel()).ToArray(),
            BackupSets.Select(set => set.Model).ToArray(),
            Mappings.Select(mapping => mapping.ToModel()).ToArray());
        var errors = BackupTopologyValidator.Validate(topology);
        if (errors.Count > 0)
        {
            FooterStatus = errors[0];
            NotificationRequested?.Invoke(this, new("Configuration not saved", errors[0], true));
            return;
        }
        if (_demoMode)
        {
            FooterStatus = "Demo configuration validated (not persisted).";
            AddActivity("Configuration validated.");
            return;
        }

        try
        {
            var document = await _configurationClient.UpdateAsync(_configurationRevision, topology, _shutdown.Token);
            AutomaticBackups = (await _configurationClient.UpdateAutomationAsync(AutomaticBackups, _shutdown.Token)).Enabled;
            _configurationRevision = document.Revision;
            if (_persistLocalState)
            {
                _store.Save(new(topology, StartWithWindows, NotifyOnDeviceArrival, AutomaticBackups));
                ConfigureStartup(StartWithWindows);
            }
            FooterStatus = $"Saved to Storage Service at {DateTime.Now:t} (revision {document.Revision}).";
            AddActivity("Configuration saved to Storage Service.");
        }
        catch (StorageConfigurationConflictException)
        {
            FooterStatus = "Configuration changed elsewhere. Reloading before you save again.";
            NotificationRequested?.Invoke(this, new("Configuration not saved", FooterStatus, true));
            await RefreshConfigurationAsync();
        }
        catch (HttpRequestException)
        {
            FooterStatus = "Storage Service is unavailable; configuration was not saved.";
            NotificationRequested?.Invoke(this, new("Configuration not saved", FooterStatus, true));
        }
        catch (TaskCanceledException)
        {
            FooterStatus = "Storage Service configuration save timed out.";
            NotificationRequested?.Invoke(this, new("Configuration not saved", FooterStatus, true));
        }
    }

    private void ApplyTopology(StorageAgentConfiguration topology)
    {
        Devices.Clear();
        BackupSets.Clear();
        Sources.Clear();
        Mappings.Clear();
        foreach (var device in topology.Devices) Devices.Add(new(device));
        foreach (var group in topology.BackupSets.GroupBy(set => new { set.SourceAgentId, set.SourceAgentName }))
        {
            var source = new SourceAgentViewModel(group.Key.SourceAgentId, group.Key.SourceAgentName);
            foreach (var model in group)
            {
                var backupSet = new BackupSetViewModel(model);
                BackupSets.Add(backupSet);
                source.BackupSets.Add(backupSet);
            }
            Sources.Add(source);
        }
        foreach (var mapping in topology.Mappings)
        {
            var backupSet = BackupSets.FirstOrDefault(set => set.Id == mapping.BackupSetId);
            var device = Devices.FirstOrDefault(item => item.Id == mapping.DeviceId);
            if (backupSet is not null && device is not null) Mappings.Add(new(mapping, backupSet, device));
        }
        SelectedBackupSet = BackupSets.FirstOrDefault();
        SelectedDevice = Devices.FirstOrDefault();
        RefreshDrives();
        NotifyCounts();
    }

    private void AddMapping()
    {
        if (SelectedBackupSet is null || SelectedDevice is null)
        {
            FooterStatus = "Choose a backup set and a device first.";
            return;
        }
        var repositoryPath = RelativeDestinationPath(SelectedDevice, NewDestinationFolder);
        if (repositoryPath is null)
        {
            FooterStatus = "Choose a destination folder inside the selected device.";
            return;
        }
        var candidate = new BackupTargetMapping(Guid.NewGuid(), SelectedBackupSet.Id, SelectedDevice.Id, repositoryPath, true);
        var all = Mappings.Select(mapping => mapping.ToModel()).Append(candidate).ToArray();
        var topology = new StorageAgentConfiguration(Devices.Select(device => device.ToModel()).ToArray(), BackupSets.Select(set => set.Model).ToArray(), all);
        var errors = BackupTopologyValidator.Validate(topology);
        if (errors.Count > 0) { FooterStatus = errors[0]; return; }
        Mappings.Add(new(candidate, SelectedBackupSet, SelectedDevice));
        NotifyCounts();
        FooterStatus = "Mapping added. Save settings to persist it.";
    }

    private void BrowseDestination()
    {
        if (SelectedDevice is null)
        {
            FooterStatus = "Choose a device first.";
            return;
        }

        var root = SelectedDevice.CurrentRoot ?? SelectedDevice.LastKnownRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            FooterStatus = "The selected device is not currently available.";
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose or create the folder that will contain this backup repository.",
            InitialDirectory = root,
            SelectedPath = root,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) NewDestinationFolder = dialog.SelectedPath;
    }

    internal static string? RelativeDestinationPath(DeviceViewModel device, string destination)
    {
        var root = device.CurrentRoot ?? device.LastKnownRoot;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(destination)) return null;
        try
        {
            if (!Path.IsPathRooted(destination))
            {
                return BackupTopologyValidator.IsSafeRelativeRepositoryPath(destination)
                    ? destination.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar)
                    : null;
            }
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(destination));
            return BackupTopologyValidator.IsSafeRelativeRepositoryPath(relative) ? relative : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void RemoveMapping()
    {
        if (SelectedMapping is null) return;
        Mappings.Remove(SelectedMapping);
        SelectedMapping = null;
        NotifyCounts();
        FooterStatus = "Mapping removed. Save settings to persist it.";
    }

    private void RegisterDevice()
    {
        if (SelectedAvailableDrive is null) { FooterStatus = "Select a connected drive first."; return; }
        if (Devices.Any(device => string.Equals(device.StableId, SelectedAvailableDrive.StableId, StringComparison.OrdinalIgnoreCase)))
        {
            FooterStatus = "That device is already registered.";
            return;
        }
        var model = new RegisteredDevice(Guid.NewGuid(), SelectedAvailableDrive.StableId, SelectedAvailableDrive.HardwareName, SelectedAvailableDrive.VolumeLabel, SelectedAvailableDrive.Root, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, NewDeviceArrivalDelayMinutes);
        var registered = new DeviceViewModel(model) { CurrentRoot = SelectedAvailableDrive.Root, IsConnected = true };
        Devices.Add(registered);
        SelectedDevice = registered;
        AddActivity($"Registered device {model.DisplayName}.");
        NotifyCounts();
    }

    private void RegisterFolder()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder to use as a logical BackupMesh storage device.",
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        var root = Path.GetFullPath(dialog.SelectedPath);
        var stableId = FolderStorageIdentity.Create(root);
        if (Devices.Any(device => string.Equals(device.StableId, stableId, StringComparison.OrdinalIgnoreCase)))
        {
            FooterStatus = "That folder is already registered.";
            return;
        }
        var displayName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : root;
        var model = new RegisteredDevice(Guid.NewGuid(), stableId, displayName, "Folder", root, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, NewDeviceArrivalDelayMinutes);
        var registered = new DeviceViewModel(model) { CurrentRoot = root, IsConnected = true, CanEject = false };
        Devices.Add(registered);
        SelectedDevice = registered;
        AddActivity($"Registered storage folder {root}.");
        FooterStatus = "Storage folder registered. Save settings to persist it.";
        NotifyCounts();
    }

    private void ForgetDevice()
    {
        if (SelectedDevice is null) return;
        if (Mappings.Any(mapping => mapping.Device.Id == SelectedDevice.Id))
        {
            FooterStatus = "Remove mappings for this device before forgetting it.";
            return;
        }
        Devices.Remove(SelectedDevice);
        SelectedDevice = null;
        NotifyCounts();
    }

    private void RefreshDrives()
    {
        var selectedStableId = SelectedAvailableDrive?.StableId;
        var drives = _deviceInventory.GetStorageDevices();
        AvailableDrives.Clear();
        foreach (var drive in drives) AvailableDrives.Add(drive);
        SelectedAvailableDrive = AvailableDrives.FirstOrDefault(drive => string.Equals(drive.StableId, selectedStableId, StringComparison.OrdinalIgnoreCase))
            ?? AvailableDrives.FirstOrDefault();

        var nowConnected = drives.Select(drive => drive.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in Devices)
        {
            var match = drives.FirstOrDefault(drive => string.Equals(drive.StableId, device.StableId, StringComparison.OrdinalIgnoreCase));
            var folderConnected = FolderStorageIdentity.TryGetPath(device.StableId, out var folderRoot) && Directory.Exists(folderRoot);
            var wasConnected = device.IsConnected;
            device.IsConnected = match is not null || folderConnected;
            device.CanEject = match?.CanEject == true;
            device.CurrentRoot = match?.Root ?? (folderConnected ? folderRoot : null);
            if (!wasConnected && device.IsConnected)
            {
                device.LastSeenAt = DateTimeOffset.UtcNow;
                AddActivity($"Registered device connected: {device.DisplayName}.");
                if (NotifyOnDeviceArrival) NotificationRequested?.Invoke(this, new("Backup storage connected", DeviceArrivalMessage(device.DisplayName, device.ArrivalDelayMinutes)));
            }
        }
        _connectedRoots.Clear();
        foreach (var root in nowConnected) _connectedRoots.Add(root);
        OverallStatus = ConnectedDeviceCount > 0 ? $"{ConnectedDeviceCount} device(s) connected" : "Waiting for storage";
        NotifyCounts();
    }

    private void AddActivity(string text)
    {
        Activity.Insert(0, $"{DateTime.Now:t}  {text}");
        while (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1);
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(ConnectedDeviceCount));
        OnPropertyChanged(nameof(SourceCount));
        OnPropertyChanged(nameof(MappingCount));
    }

    private static void ConfigureStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return;
        if (enabled) key.SetValue("BackupMesh.Storage.Agent", BuildStartupCommand(AppContext.BaseDirectory, Environment.ProcessPath));
        else key.DeleteValue("BackupMesh.Storage.Agent", throwOnMissingValue: false);
    }

    internal static string BuildStartupCommand(string appDirectory, string? processPath)
    {
        var launcher = Path.GetFullPath(Path.Combine(appDirectory, "..", "Start-BackupMesh.ps1"));
        if (File.Exists(launcher))
        {
            var windowsPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            return $"\"{windowsPowerShell}\" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{launcher}\"";
        }
        return $"\"{processPath ?? Path.Combine(appDirectory, "BackupMesh.Storage.App.exe")}\"";
    }

    internal static string DeviceArrivalMessage(string displayName, int arrivalDelayMinutes) => arrivalDelayMinutes == 0
        ? $"{displayName} is connected and ready for backup."
        : $"{displayName} is connected. Backups become eligible after its {arrivalDelayMinutes}-minute arrival delay.";

    public void Dispose()
    {
        _shutdown.Cancel();
        _deviceTimer.Stop();
        _catalogTimer.Stop();
        _jobTimer.Stop();
        if (_catalogClient is IDisposable disposable) disposable.Dispose();
        if (_configurationClient is IDisposable configurationDisposable) configurationDisposable.Dispose();
        if (_jobClient is IDisposable jobDisposable) jobDisposable.Dispose();
        if (_storageDeviceClient is IDisposable storageDeviceDisposable) storageDeviceDisposable.Dispose();
        if (_pairingClient is IDisposable pairingDisposable) pairingDisposable.Dispose();
        if (_connectionsClient is IDisposable connectionsDisposable) connectionsDisposable.Dispose();
        _shutdown.Dispose();
    }
}

public sealed class SourceAgentViewModel(Guid id, string displayName)
{
    public Guid Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public ObservableCollection<BackupSetViewModel> BackupSets { get; } = [];
    // UI Automation reads Name from ToString(); DisplayMemberPath and item templates do not apply to it.
    public override string ToString() => DisplayName;
}

public sealed class SourceConnectionViewModel(SourceConnectionDto model)
{
    public Guid AgentId { get; } = model.AgentId;
    public string AgentName { get; } = model.AgentName;
    public string ReportedAgentName { get; } = model.ReportedAgentName;
    public DateTimeOffset LastSeenAt { get; } = model.LastSeenAt;
    public string LastSeenDisplay { get; } = model.LastSeenAt.LocalDateTime.ToString("g");
    public int BackupSetCount { get; } = model.BackupSetCount;
    public bool IsRevoked { get; } = model.Revoked;
    public DateTimeOffset? CertificateExpiresAt { get; } = model.CertificateExpiresAt;
    public string CertificateExpiresDisplay => CertificateExpiresAt is { } expires ? expires.LocalDateTime.ToString("g") : "Unknown";
    public string StatusDisplay => IsRevoked ? "Revoked" : "Allowed";
    public string DisplayName => $"{AgentName} — {StatusDisplay}, last seen {LastSeenDisplay}";
    // UI Automation reads Name from ToString(); DisplayMemberPath and item templates do not apply to it.
    public override string ToString() => DisplayName;
}

public sealed class BackupSetViewModel : ObservableObject
{
    private SourceBackupSet _model;
    private bool _isAvailable = true;
    public BackupSetViewModel(SourceBackupSet model) => _model = model;
    public SourceBackupSet Model => _model;
    public Guid Id => Model.Id;
    public bool IsAvailable { get => _isAvailable; set { if (Set(ref _isAvailable, value)) OnPropertyChanged(nameof(DisplayName)); } }
    public string DisplayName => $"{Model.SourceAgentName} / {Model.Name}{(IsAvailable ? string.Empty : " (not reported)")}";
    public void Update(SourceBackupSet model)
    {
        _model = model;
        IsAvailable = true;
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(DisplayName));
    }

    // UI Automation reads Name from ToString(); DisplayMemberPath and item templates do not apply to it.
    public override string ToString() => DisplayName;
}

public sealed class DeviceViewModel : ObservableObject
{
    private bool _isConnected;
    private bool _canEject;
    private string? _currentRoot;
    private DateTimeOffset? _lastSeenAt;
    public DeviceViewModel(RegisteredDevice model) { Id = model.Id; StableId = model.StableId; DisplayName = model.DisplayName; VolumeLabel = model.VolumeLabel; LastKnownRoot = model.LastKnownRoot; RegisteredAt = model.RegisteredAt; _lastSeenAt = model.LastSeenAt; ArrivalDelayMinutes = model.ArrivalDelayMinutes; }
    public Guid Id { get; }
    public string StableId { get; }
    public string DisplayName { get; }
    public string? VolumeLabel { get; }
    public string? LastKnownRoot { get; }
    public DateTimeOffset RegisteredAt { get; }
    public DateTimeOffset? LastSeenAt { get => _lastSeenAt; set { Set(ref _lastSeenAt, value); OnPropertyChanged(nameof(LastSeenDisplay)); } }
    public bool IsConnected { get => _isConnected; set { Set(ref _isConnected, value); OnPropertyChanged(nameof(Status)); } }
    public bool CanEject { get => _canEject; set => Set(ref _canEject, value); }
    public string? CurrentRoot { get => _currentRoot; set => Set(ref _currentRoot, value); }
    public string Status => IsConnected ? "Connected" : "Offline";
    public string LastSeenDisplay => LastSeenAt?.LocalDateTime.ToString("g") ?? "Never";
    public int ArrivalDelayMinutes { get; set; }
    public RegisteredDevice ToModel() => new(Id, StableId, DisplayName, VolumeLabel, CurrentRoot ?? LastKnownRoot, RegisteredAt, LastSeenAt, ArrivalDelayMinutes);

    // UI Automation reads Name from ToString(); DisplayMemberPath and item templates do not apply to it.
    public override string ToString() => DisplayName;
}

public sealed class MappingViewModel(BackupTargetMapping model, BackupSetViewModel set, DeviceViewModel device)
{
    public Guid Id { get; } = model.Id;
    public BackupSetViewModel BackupSet { get; } = set;
    public DeviceViewModel Device { get; } = device;
    public string BackupSetName => BackupSet.DisplayName;
    public string DeviceName => Device.DisplayName;
    public string RepositoryPath { get; } = model.RepositoryPath;
    public string DestinationFolder => Path.GetFullPath(Path.Combine(Device.LastKnownRoot ?? string.Empty, RepositoryPath));
    public bool Enabled { get; } = model.Enabled;
    public BackupTargetMapping ToModel() => new(Id, BackupSet.Id, Device.Id, RepositoryPath, Enabled);
}

public sealed record AvailableDriveViewModel(string StableId, string Root, string VolumeLabel, long AvailableBytes, long TotalBytes, string HardwareName, int VolumeCount, bool CanEject = false)
{
    public string DisplayName => $"{HardwareName} — {VolumeLabel} ({Root}), {AvailableBytes / 1_073_741_824d:0.0} GB free";

    // Without this the compiler-generated record ToString() becomes the UI Automation Name, leaking
    // StableId and volume serials to screen readers. DisplayMemberPath does not affect the UIA Name.
    public override string ToString() => DisplayName;

    public static AvailableDriveViewModel FromDrive(DriveInfo drive)
    {
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local disk" : drive.VolumeLabel;
        // The production Windows provider replaces this provisional identifier with volume GUID + hardware identity.
        var stableId = $"{drive.DriveFormat}|{label}|{drive.TotalSize}";
        return new(stableId, drive.RootDirectory.FullName, label, drive.AvailableFreeSpace, drive.TotalSize, label, 1);
    }
}

public sealed record AppConfiguration(StorageAgentConfiguration Topology, bool StartWithWindows = true, bool NotifyOnDeviceArrival = true, bool AutomaticBackups = true);

internal sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupMesh", "storage-agent.json");
    public AppConfiguration Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StorageAgentConfiguration.Empty);
            return JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(_path), JsonOptions) ?? new(StorageAgentConfiguration.Empty);
        }
        catch (JsonException) { return new(StorageAgentConfiguration.Empty); }
    }
    public void Save(AppConfiguration configuration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(configuration, JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
