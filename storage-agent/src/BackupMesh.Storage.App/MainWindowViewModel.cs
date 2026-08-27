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
    private readonly IDeviceInventory _deviceInventory = new WindowsDeviceInventory();
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _catalogTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly ISourceCatalogClient _catalogClient;
    private readonly IStorageConfigurationClient _configurationClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<string> _connectedRoots = new(StringComparer.OrdinalIgnoreCase);
    private BackupSetViewModel? _selectedBackupSet;
    private DeviceViewModel? _selectedDevice;
    private AvailableDriveViewModel? _selectedAvailableDrive;
    private MappingViewModel? _selectedMapping;
    private string _newDestinationFolder = string.Empty;
    private string _overallStatus = "Ready";
    private string _footerStatus = "Configuration loaded.";
    private bool _paused;
    private long _configurationRevision;
    private readonly bool _demoMode;
    private readonly bool _persistLocalState;

    public ObservableCollection<SourceAgentViewModel> Sources { get; } = [];
    public ObservableCollection<BackupSetViewModel> BackupSets { get; } = [];
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<MappingViewModel> Mappings { get; } = [];
    public ObservableCollection<AvailableDriveViewModel> AvailableDrives { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];

    public event EventHandler<AppNotification>? NotificationRequested;
    public event EventHandler<string>? StatusChanged;

    public ICommand AddMappingCommand { get; }
    public ICommand BrowseDestinationCommand { get; }
    public ICommand RemoveMappingCommand { get; }
    public ICommand RefreshDrivesCommand { get; }
    public ICommand RegisterDeviceCommand { get; }
    public ICommand ForgetDeviceCommand { get; }
    public ICommand SaveCommand { get; }

    public MainWindowViewModel(bool demoMode = false, ISourceCatalogClient? catalogClient = null, bool loadLocalState = true, IStorageConfigurationClient? configurationClient = null)
    {
        _demoMode = demoMode;
        _persistLocalState = loadLocalState;
        _catalogClient = catalogClient ?? new SourceCatalogClient();
        _configurationClient = configurationClient ?? new StorageConfigurationClient();
        AddMappingCommand = new RelayCommand(AddMapping);
        BrowseDestinationCommand = new RelayCommand(BrowseDestination);
        RemoveMappingCommand = new RelayCommand(RemoveMapping);
        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        RegisterDeviceCommand = new RelayCommand(RegisterDevice);
        ForgetDeviceCommand = new RelayCommand(ForgetDevice);
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        _deviceTimer.Tick += (_, _) => RefreshDrives();
        _catalogTimer.Tick += async (_, _) => await RefreshCatalogsAsync();
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
    public DeviceViewModel? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }
    public AvailableDriveViewModel? SelectedAvailableDrive { get => _selectedAvailableDrive; set => Set(ref _selectedAvailableDrive, value); }
    public MappingViewModel? SelectedMapping { get => _selectedMapping; set => Set(ref _selectedMapping, value); }
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
            _ = InitializeServiceStateAsync();
        }
    }

    private async Task InitializeServiceStateAsync()
    {
        await RefreshConfigurationAsync();
        await RefreshCatalogsAsync();
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

    public async Task RefreshConfigurationAsync()
    {
        if (_demoMode) return;
        try
        {
            var document = await _configurationClient.GetAsync(_shutdown.Token);
            ApplyTopology(document.Configuration);
            _configurationRevision = document.Revision;
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

    public void TogglePause()
    {
        _paused = !_paused;
        OverallStatus = _paused ? "Automation paused" : "Ready";
        AddActivity(_paused ? "Automatic backups paused." : "Automatic backups resumed.");
    }

    public void QueueSelectedBackups()
    {
        var eligible = Mappings.Count(mapping => mapping.Enabled && mapping.Device.IsConnected);
        var message = eligible == 0 ? "No mapped backup is currently eligible." : $"Queued {eligible} mapped backup target(s).";
        AddActivity(message);
        NotificationRequested?.Invoke(this, new("BackupMesh", message));
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

    private static string? RelativeDestinationPath(DeviceViewModel device, string destination)
    {
        var root = device.CurrentRoot ?? device.LastKnownRoot;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(destination)) return null;
        try
        {
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
        var drives = _deviceInventory.GetStorageDevices();
        AvailableDrives.Clear();
        foreach (var drive in drives) AvailableDrives.Add(drive);
        SelectedAvailableDrive = AvailableDrives.FirstOrDefault();

        var nowConnected = drives.Select(drive => drive.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in Devices)
        {
            var match = drives.FirstOrDefault(drive => string.Equals(drive.StableId, device.StableId, StringComparison.OrdinalIgnoreCase));
            var wasConnected = device.IsConnected;
            device.IsConnected = match is not null;
            device.CurrentRoot = match?.Root;
            if (!wasConnected && match is not null)
            {
                device.LastSeenAt = DateTimeOffset.UtcNow;
                AddActivity($"Registered device connected: {device.DisplayName}.");
                if (NotifyOnDeviceArrival) NotificationRequested?.Invoke(this, new("Backup storage connected", $"{device.DisplayName} is ready. Eligible backups will start after {device.ArrivalDelayMinutes} minutes."));
            }
        }
        _connectedRoots.Clear();
        foreach (var root in nowConnected) _connectedRoots.Add(root);
        OverallStatus = _paused ? "Automation paused" : ConnectedDeviceCount > 0 ? $"{ConnectedDeviceCount} device(s) connected" : "Waiting for storage";
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
        if (enabled) key.SetValue("BackupMesh.Storage.Agent", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("BackupMesh.Storage.Agent", throwOnMissingValue: false);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _deviceTimer.Stop();
        _catalogTimer.Stop();
        if (_catalogClient is IDisposable disposable) disposable.Dispose();
        if (_configurationClient is IDisposable configurationDisposable) configurationDisposable.Dispose();
        _shutdown.Dispose();
    }
}

public sealed class SourceAgentViewModel(Guid id, string displayName)
{
    public Guid Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public ObservableCollection<BackupSetViewModel> BackupSets { get; } = [];
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
}

public sealed class DeviceViewModel : ObservableObject
{
    private bool _isConnected;
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
    public string? CurrentRoot { get => _currentRoot; set => Set(ref _currentRoot, value); }
    public string Status => IsConnected ? "Connected" : "Offline";
    public string LastSeenDisplay => LastSeenAt?.LocalDateTime.ToString("g") ?? "Never";
    public int ArrivalDelayMinutes { get; set; }
    public RegisteredDevice ToModel() => new(Id, StableId, DisplayName, VolumeLabel, CurrentRoot ?? LastKnownRoot, RegisteredAt, LastSeenAt, ArrivalDelayMinutes);
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

public sealed record AvailableDriveViewModel(string StableId, string Root, string VolumeLabel, long AvailableBytes, long TotalBytes, string HardwareName, int VolumeCount)
{
    public string DisplayName => $"{HardwareName} — {VolumeLabel} ({Root}), {AvailableBytes / 1_073_741_824d:0.0} GB free";
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
