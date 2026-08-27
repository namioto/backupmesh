using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using BackupMesh.Storage.Core;
using Microsoft.Win32;

namespace BackupMesh.Storage.App;

public sealed record AppNotification(string Title, string Message, bool IsError = false);

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ConfigurationStore _store = new();
    private readonly IDeviceInventory _deviceInventory = new WindowsDeviceInventory();
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly HashSet<string> _connectedRoots = new(StringComparer.OrdinalIgnoreCase);
    private BackupSetViewModel? _selectedBackupSet;
    private DeviceViewModel? _selectedDevice;
    private AvailableDriveViewModel? _selectedAvailableDrive;
    private MappingViewModel? _selectedMapping;
    private string _newRepositoryPath = "backupmesh/repository";
    private string _overallStatus = "Ready";
    private string _footerStatus = "Configuration loaded.";
    private bool _paused;

    public ObservableCollection<SourceAgentViewModel> Sources { get; } = [];
    public ObservableCollection<BackupSetViewModel> BackupSets { get; } = [];
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<MappingViewModel> Mappings { get; } = [];
    public ObservableCollection<AvailableDriveViewModel> AvailableDrives { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];

    public event EventHandler<AppNotification>? NotificationRequested;
    public event EventHandler<string>? StatusChanged;

    public ICommand AddMappingCommand { get; }
    public ICommand RemoveMappingCommand { get; }
    public ICommand RefreshDrivesCommand { get; }
    public ICommand RegisterDeviceCommand { get; }
    public ICommand ForgetDeviceCommand { get; }
    public ICommand SaveCommand { get; }

    public MainWindowViewModel()
    {
        AddMappingCommand = new RelayCommand(AddMapping);
        RemoveMappingCommand = new RelayCommand(RemoveMapping);
        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        RegisterDeviceCommand = new RelayCommand(RegisterDevice);
        ForgetDeviceCommand = new RelayCommand(ForgetDevice);
        SaveCommand = new RelayCommand(Save);
        _deviceTimer.Tick += (_, _) => RefreshDrives();
        Load();
        RefreshDrives();
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
    public string NewRepositoryPath { get => _newRepositoryPath; set => Set(ref _newRepositoryPath, value); }
    public bool StartWithWindows { get; set; } = true;
    public bool NotifyOnDeviceArrival { get; set; } = true;
    public bool AutomaticBackups { get; set; } = true;
    public int GracePeriodMinutes { get; set; } = 30;

    public void StartDeviceMonitoring() => _deviceTimer.Start();

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
        GracePeriodMinutes = state.GracePeriodMinutes;

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

    private void Save()
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
        _store.Save(new(topology, StartWithWindows, NotifyOnDeviceArrival, AutomaticBackups, GracePeriodMinutes));
        ConfigureStartup(StartWithWindows);
        FooterStatus = $"Saved at {DateTime.Now:t}.";
        AddActivity("Configuration saved.");
    }

    private void AddMapping()
    {
        if (SelectedBackupSet is null || SelectedDevice is null)
        {
            FooterStatus = "Choose a backup set and a device first.";
            return;
        }
        if (!BackupTopologyValidator.IsSafeRelativeRepositoryPath(NewRepositoryPath))
        {
            FooterStatus = "Repository path must be a safe relative path.";
            return;
        }
        var candidate = new BackupTargetMapping(Guid.NewGuid(), SelectedBackupSet.Id, SelectedDevice.Id, NewRepositoryPath.Trim(), true);
        var all = Mappings.Select(mapping => mapping.ToModel()).Append(candidate).ToArray();
        var topology = new StorageAgentConfiguration(Devices.Select(device => device.ToModel()).ToArray(), BackupSets.Select(set => set.Model).ToArray(), all);
        var errors = BackupTopologyValidator.Validate(topology);
        if (errors.Count > 0) { FooterStatus = errors[0]; return; }
        Mappings.Add(new(candidate, SelectedBackupSet, SelectedDevice));
        NotifyCounts();
        FooterStatus = "Mapping added. Save settings to persist it.";
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
        var model = new RegisteredDevice(Guid.NewGuid(), SelectedAvailableDrive.StableId, SelectedAvailableDrive.DisplayName, SelectedAvailableDrive.VolumeLabel, SelectedAvailableDrive.Root, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Devices.Add(new(model) { CurrentRoot = SelectedAvailableDrive.Root, IsConnected = true });
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
                if (NotifyOnDeviceArrival) NotificationRequested?.Invoke(this, new("Backup storage connected", $"{device.DisplayName} is ready. Eligible backups will start after {GracePeriodMinutes} minutes."));
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

    public void Dispose() => _deviceTimer.Stop();
}

public sealed class SourceAgentViewModel(Guid id, string displayName)
{
    public Guid Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public ObservableCollection<BackupSetViewModel> BackupSets { get; } = [];
}

public sealed class BackupSetViewModel(SourceBackupSet model)
{
    public SourceBackupSet Model { get; } = model;
    public Guid Id => Model.Id;
    public string DisplayName => $"{Model.SourceAgentName} / {Model.Name}";
}

public sealed class DeviceViewModel : ObservableObject
{
    private bool _isConnected;
    private string? _currentRoot;
    private DateTimeOffset? _lastSeenAt;
    public DeviceViewModel(RegisteredDevice model) { Id = model.Id; StableId = model.StableId; DisplayName = model.DisplayName; VolumeLabel = model.VolumeLabel; LastKnownRoot = model.LastKnownRoot; RegisteredAt = model.RegisteredAt; _lastSeenAt = model.LastSeenAt; }
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
    public RegisteredDevice ToModel() => new(Id, StableId, DisplayName, VolumeLabel, CurrentRoot ?? LastKnownRoot, RegisteredAt, LastSeenAt);
}

public sealed class MappingViewModel(BackupTargetMapping model, BackupSetViewModel set, DeviceViewModel device)
{
    public Guid Id { get; } = model.Id;
    public BackupSetViewModel BackupSet { get; } = set;
    public DeviceViewModel Device { get; } = device;
    public string BackupSetName => BackupSet.DisplayName;
    public string DeviceName => Device.DisplayName;
    public string RepositoryPath { get; } = model.RepositoryPath;
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

public sealed record AppConfiguration(StorageAgentConfiguration Topology, bool StartWithWindows = true, bool NotifyOnDeviceArrival = true, bool AutomaticBackups = true, int GracePeriodMinutes = 30);

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
