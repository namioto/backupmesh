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
public sealed record RuleComputerOption(Guid Id, string Name);

public enum ActivityKind { Information, Started, Completed, Failed, Storage }

public sealed record ActivityItem(string Title, string Detail, DateTimeOffset At, ActivityKind Kind)
{
    public string IconSource => Kind switch
    {
        ActivityKind.Completed => "Assets/Icons/activity-complete.png",
        ActivityKind.Failed => "Assets/Icons/activity-failed.png",
        ActivityKind.Storage => "Assets/Icons/activity-storage.png",
        _ => "Assets/Icons/activity-info.png"
    };
    public string TimeDisplay => At.LocalDateTime.Date == DateTime.Today
        ? $"{Localization.Text("ActivityToday")} {At.LocalDateTime.ToString("t", Localization.Source.Culture)}"
        : At.LocalDateTime.ToString("g", Localization.Source.Culture);
    public string DetailAndTime => string.IsNullOrWhiteSpace(Detail) ? TimeDisplay : $"{Detail} · {TimeDisplay}";
    public override string ToString() => $"{Title} {DetailAndTime}";
}

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ConfigurationStore _store = new();
    private readonly IDeviceInventory _deviceInventory;
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _catalogTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _jobTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _nearbyTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ISourceCatalogClient _catalogClient;
    private readonly IStorageConfigurationClient _configurationClient;
    private readonly IBackupJobClient _jobClient;
    private readonly IPairingClient _pairingClient;
    private readonly ISourceConnectionsClient _connectionsClient;
    private readonly INearbyPairingClient _nearbyPairingClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<string> _connectedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _observedJobStates = [];
    private readonly Dictionary<Guid, string> _onlineSourceNames = [];
    private bool _jobsInitialized;
    // Mappings this session paused via the tray flyout's "Skip this time" - tracked separately from a
    // mapping the user disabled deliberately and permanently via the Backups grid's own checkbox, so
    // reconnecting the device only ever restores the ones *this* skip touched, never a mapping that was
    // already off before the skip happened.
    private readonly HashSet<Guid> _skipDisabledMappingIds = [];
    private IReadOnlyList<StorageDeviceStatusDto> _deviceStatuses = [];
    private IReadOnlyList<BackupCommandStatusDto> _backupCommands = [];
    private bool _refreshingJobs;
    private bool _serviceAutomaticBackups = true;
    private bool _legacyArrivalDelayNeedsUpdate;
    private bool _arrivalDelayMigrationAttempted;
    private BackupSetViewModel? _selectedBackupSet;
    private RemoteAgentViewModel? _selectedRemoteAgent;
    private SourceConnectionViewModel? _selectedSourceConnection;
    private NearbyComputerViewModel? _selectedNearbyComputer;
    private DeviceViewModel? _selectedDevice;
    private MappingViewModel? _selectedMapping;
    private BackupJobViewModel? _selectedJob;
    private string _overallStatus = Localization.Text("Text_Ready_5FA7AA");
    private string _footerStatus = Localization.Text("Text_Configurationloaded_F98E9C");
    private long _configurationRevision;
    private readonly bool _demoMode;
    private readonly bool _persistLocalState;

    public ObservableCollection<RemoteAgentViewModel> Sources { get; } = [];
    public ObservableCollection<SourceConnectionViewModel> SourceConnections { get; } = [];
    public ObservableCollection<NearbyComputerViewModel> NearbyComputers { get; } = [];
    public ObservableCollection<BackupSetViewModel> BackupSets { get; } = [];
    public ObservableCollection<RuleComputerOption> RuleComputerOptions { get; } = [];
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<BackupDestinationOptionViewModel> BackupDestinations { get; } = [];
    public ObservableCollection<MappingViewModel> Mappings { get; } = [];
    public ObservableCollection<AvailableDriveViewModel> AvailableDrives { get; } = [];
    public ObservableCollection<ActivityItem> Activity { get; } = [];
    public ObservableCollection<BackupJobViewModel> Jobs { get; } = [];
    public string ProductVersion => "BackupMesh v" + typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3);
    public IReadOnlyList<LanguageOption> Languages { get; } = [new("", ""), new("ko", "한국어"), new("en", "English")];
    private string _language = "";
    public string Language
    {
        get => _language;
        set
        {
            var normalized = value is "ko" or "en" ? value : "";
            if (normalized == _language) return;
            try
            {
                if (_persistLocalState) _store.Save(_store.Load() with { Language = normalized });
                Set(ref _language, normalized);
                Localization.Initialize(normalized);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                FooterStatus = Localization.Text("LanguageSaveFailed");
                OnPropertyChanged(nameof(Language));
            }
        }
    }

    public event EventHandler<AppNotification>? NotificationRequested;
    public event EventHandler<string>? StatusChanged;

    public ICommand RemoveMappingCommand { get; }
    public ICommand RefreshDrivesCommand { get; }
    public ICommand AddLocalBackupSetCommand { get; }
    public ICommand RemoveLocalBackupSetCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelJobCommand { get; }
    public ICommand RenameSourceCommand { get; }
    public ICommand ForgetSourceCommand { get; }
    public ICommand RotateStorageIdentityCommand { get; }
    public ICommand RefreshNearbyComputersCommand { get; }
    public ICommand RequestNearbyPairingCommand { get; }
    public ICommand CancelNearbyPairingCommand { get; }
    public ICommand QueueSelectedMappingCommand { get; }
    public ICommand QueueAllBackupsCommand { get; }

    public MainWindowViewModel(bool demoMode = false, ISourceCatalogClient? catalogClient = null, bool loadLocalState = true, IStorageConfigurationClient? configurationClient = null, IDeviceInventory? deviceInventory = null, IBackupJobClient? jobClient = null, IPairingClient? pairingClient = null, ISourceConnectionsClient? connectionsClient = null, INearbyPairingClient? nearbyPairingClient = null)
    {
        _demoMode = demoMode;
        _persistLocalState = loadLocalState;
        _catalogClient = catalogClient ?? new SourceCatalogClient();
        _configurationClient = configurationClient ?? new StorageConfigurationClient();
        _deviceInventory = deviceInventory ?? new WindowsDeviceInventory();
        _jobClient = jobClient ?? new BackupJobClient();
        _pairingClient = pairingClient ?? new PairingClient();
        _connectionsClient = connectionsClient ?? new SourceConnectionsClient();
        _nearbyPairingClient = nearbyPairingClient ?? new NearbyPairingClient();
        RemoveMappingCommand = new RelayCommand(() => _ = RemoveMappingAsync());
        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        AddLocalBackupSetCommand = new RelayCommand(() => _ = AddLocalBackupSetAsync());
        RemoveLocalBackupSetCommand = new RelayCommand(() => _ = RemoveLocalBackupSetAsync());
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        CancelJobCommand = new RelayCommand(() => _ = CancelSelectedJobAsync());
        RenameSourceCommand = new RelayCommand(() => _ = RenameSelectedSourceAsync());
        ForgetSourceCommand = new RelayCommand(() => _ = ForgetSelectedSourceAsync());
        RotateStorageIdentityCommand = new RelayCommand(() => _ = RotateStorageIdentityAsync());
        RefreshNearbyComputersCommand = new RelayCommand(() => _ = RefreshNearbyComputersAsync());
        RequestNearbyPairingCommand = new RelayCommand(() => _ = RequestNearbyPairingAsync());
        CancelNearbyPairingCommand = new RelayCommand(() => _ = CancelNearbyPairingAsync());
        QueueSelectedMappingCommand = new RelayCommand(() => _ = QueueSelectedMappingAsync());
        QueueAllBackupsCommand = new RelayCommand(QueueSelectedBackups);
        // Recomputed from Mappings itself, not from each call site that mutates it, so a test (or any
        // future caller) that adds/removes a mapping directly never needs to know this bookkeeping exists.
        Mappings.CollectionChanged += (_, _) => UpdateMappingRepeatMarkers();
        BackupSets.CollectionChanged += (_, _) => RefreshRuleComputerOptions();
        _deviceTimer.Tick += (_, _) => RefreshDrives();
        _catalogTimer.Tick += async (_, _) => { await RefreshCatalogsAsync(); await RefreshConnectionsAsync(); };
        _jobTimer.Tick += async (_, _) => await RefreshJobsAsync();
        _nearbyTimer.Tick += async (_, _) => await RefreshNearbyPairingStateAsync();
        if (loadLocalState) Load();
        else AddActivity(Localization.Text("Text_StorageAgentUIteststateinitial_49AF15"));
        if (_demoMode && BackupSets.Count == 0) LoadDemoSources();
        if (loadLocalState || demoMode) RefreshDrives();
        RefreshRuleComputerOptions();
        Localization.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var language in Languages) language.RefreshText();
        foreach (var item in Sources.Cast<ObservableObject>().Concat(SourceConnections).Concat(BackupSets).Concat(Devices).Concat(Mappings).Concat(Jobs))
            item.RefreshText();
        NotifyCounts();
        FooterStatus = Localization.Text("Text_SavedautomaticallyExitBackupMe_6E29AA");
        RefreshText();
    }

    public string OverallStatus { get => _overallStatus; private set { Set(ref _overallStatus, value); StatusChanged?.Invoke(this, Localization.Format("Text_BackupMeshStorageAgent0_8B0304", value)); } }
    public string FooterStatus { get => _footerStatus; private set => Set(ref _footerStatus, value); }

    // A status message from an action on one tab (e.g. "One-time pairing details generated…") otherwise
    // keeps following the user to unrelated tabs, since the footer is a single shared piece of state.
    public void ClearFooterStatusOnTabChange() => FooterStatus = string.Empty;
    public int ConnectedDeviceCount => Devices.Count(device => device.IsConnected);
    public int SourceCount => Sources.Count;
    public int MappingCount => Mappings.Count(mapping => mapping.Enabled);
    public string RuleHeaderCount => Localization.Format("RuleHeaderCount", Mappings.Count);
    public string LocalComputerName => Environment.MachineName;
    private RemoteAgentViewModel? DashboardRemoteAgent => Sources.FirstOrDefault(source => source.Id == DashboardJobMapping?.BackupSet.Model.SourceAgentId && source.Connection?.IsOnline == true)
        ?? Sources.FirstOrDefault(source => source.Connection?.IsOnline == true);
    private DeviceViewModel? DashboardDevice => DashboardJobMapping?.Device is { IsConnected: true } target
        ? target : Devices.FirstOrDefault(device => device.IsConnected);
    public string LaptopName => DashboardRemoteAgent?.DisplayName ?? Localization.Text("DashboardNoRemote");
    public string LaptopStatus => DashboardRemoteAgent?.StatusDisplay ?? (Sources.Any(source => source.Id != LocalSourceIdentity.AgentId) ? Localization.Text("DashboardWaiting") : Localization.Text("DashboardNoConnectedRemote"));
    public string DashboardDeviceName => DashboardDevice?.DisplayName ?? Localization.Text("DashboardNoDevice");
    public string DashboardDeviceStatus => DashboardDevice?.Status ?? (Devices.Count > 0 ? Localization.Text("DashboardWaiting") : Localization.Text("DashboardNoRegisteredDevice"));
    public string DashboardDescription => ConnectedDeviceCount == 0 ? Localization.Text("DashboardConnectStorage") : CanQueueAnyBackup ? Localization.Text("DashboardRulesReady") : Localization.Text("DashboardCheckRules");
    public System.Windows.Media.Brush StatusBrush => ConnectedDeviceCount > 0 ? System.Windows.Media.Brushes.Teal : System.Windows.Media.Brushes.DarkOrange;
    public bool CanQueueAnyBackup => Mappings.Any(mapping => mapping.Enabled && mapping.Device.IsConnected);
    public string QueueAllBackupsHint => CanQueueAnyBackup ? Localization.Text("DashboardRunRulesHint") : Localization.Text("DashboardNeedRulesHint");
    public string LastBackupSummary => Jobs.Where(job => job.State == "SUCCEEDED").OrderByDescending(job => job.UpdatedAt).FirstOrDefault()?.Updated ?? Localization.Text("DashboardNoCompletedBackup");
    private BackupJobViewModel? DashboardJob => Jobs.Where(job => !job.IsTerminal && Mappings.Any(mapping => mapping.Id == job.TargetMappingId))
        .OrderByDescending(job => job.State == "RUNNING").ThenByDescending(job => job.UpdatedAt).FirstOrDefault();
    private MappingViewModel? DashboardJobMapping => Mappings.FirstOrDefault(mapping => mapping.Id == DashboardJob?.TargetMappingId);
    public bool IsDashboardTransferActive => DashboardJobMapping?.Device.IsConnected == true;
    public bool IsRemoteDashboardTransferActive => IsDashboardTransferActive
        && DashboardJobMapping?.BackupSet.Model.SourceAgentId is { } sourceId
        && sourceId != LocalSourceIdentity.AgentId && DashboardRemoteAgent?.Id == sourceId;
    public double DashboardTransferPercent => DashboardJob?.PercentComplete ?? 0;
    public bool IsDashboardTransferIndeterminate => DashboardJob?.PercentComplete is null;
    public string DashboardTransferLabel => DashboardJob is { } job
        ? job.PercentComplete is { } percent ? $"{job.StateDisplay} {percent:0}%" : job.StateDisplay
        : string.Empty;
    public BackupSetViewModel? SelectedBackupSet { get => _selectedBackupSet; set => Set(ref _selectedBackupSet, value); }
    // The merged Computers grid selects a computer directly; picking one resolves (or clears) the
    // connection the action buttons act on, replacing what used to be a separate tree-selection handler
    // in code-behind now that there is only one list to select from.
    public RemoteAgentViewModel? SelectedRemoteAgent
    {
        get => _selectedRemoteAgent;
        set
        {
            if (!Set(ref _selectedRemoteAgent, value)) return;
            SelectedSourceConnection = value is null ? null : SourceConnections.FirstOrDefault(connection => connection.AgentId == value.Id);
        }
    }
    public SourceConnectionViewModel? SelectedSourceConnection { get => _selectedSourceConnection; set { if (Set(ref _selectedSourceConnection, value)) OnPropertyChanged(nameof(HasSelectedSourceConnection)); } }
    public bool HasSelectedSourceConnection => SelectedSourceConnection is not null;
    public NearbyComputerViewModel? SelectedNearbyComputer { get => _selectedNearbyComputer; set { if (Set(ref _selectedNearbyComputer, value)) OnPropertyChanged(nameof(HasSelectedNearbyComputer)); } }
    public bool HasSelectedNearbyComputer => SelectedNearbyComputer is not null && !_requestingNearbyPairing && _nearbyPairingRequest is null;
    private bool _requestingNearbyPairing;
    private bool _refreshingNearbyPairing;
    private NearbyPairingRequestDto? _nearbyPairingRequest;
    private string _nearbyPairingStatus = string.Empty;
    public string NearbyPairingStatus { get => _nearbyPairingStatus; private set => Set(ref _nearbyPairingStatus, value); }
    public string NearbyComparisonCode => _nearbyPairingRequest?.ComparisonCode ?? string.Empty;
    public bool HasNearbyComparisonCode => _nearbyPairingRequest is not null;
    public bool CanCancelNearbyPairing => _nearbyPairingRequest?.Status == "PENDING";
    public DeviceViewModel? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }
    public MappingViewModel? SelectedMapping
    {
        get => _selectedMapping;
        set
        {
            if (!Set(ref _selectedMapping, value)) return;
            OnPropertyChanged(nameof(HasSelectedMapping));
            OnPropertyChanged(nameof(CanStartSelectedMapping));
            OnPropertyChanged(nameof(StartSelectedMappingHint));
        }
    }
    public bool HasSelectedMapping => SelectedMapping is not null;
    public bool CanStartSelectedMapping => SelectedMapping is { Enabled: true } mapping
        && mapping.Device.IsConnected
        && !Jobs.Any(job => job.TargetMappingId == mapping.Id && !job.IsTerminal)
        && !_backupCommands.Any(command => command.TargetMappingId == mapping.Id && command.State is "PENDING" or "CLAIMED" or "RUNNING");
    public string StartSelectedMappingHint => SelectedMapping switch
    {
        null => Localization.Text("StartNowSelectRule"),
        { Enabled: false } => Localization.Text("StartNowPaused"),
        var mapping when Jobs.Any(job => job.TargetMappingId == mapping.Id && !job.IsTerminal) => Localization.Text("StartNowRunning"),
        var mapping when _backupCommands.Any(command => command.TargetMappingId == mapping.Id && command.State is "PENDING" or "CLAIMED" or "RUNNING") => Localization.Text("StartNowQueued"),
        var mapping when !mapping.Device.IsConnected => Localization.Format("StartNowConnectStorage", mapping.DeviceName),
        _ => string.Empty
    };
    public BackupJobViewModel? SelectedJob { get => _selectedJob; set => Set(ref _selectedJob, value); }
    public bool StartWithWindows { get; set; } = true;
    public bool NotifyOnDeviceArrival { get; set; } = true;
    private bool _automaticBackups = true;
    public bool AutomaticBackups
    {
        get => _automaticBackups;
        set { if (Set(ref _automaticBackups, value)) UpdateMappingLastBackupInfo(); }
    }
    // Replaces the Devices tab's per-device arrival-delay editor (removed with that tab): one global
    // default - the only one left in the tray - rather than a setting a person had to think to revisit
    // per device. It governs every device, not just newly-registered ones: an already-registered device
    // has no screen left to show or change its own value, so leaving that value in effect after this
    // changed would silently disagree with what the screen says (the same failure mode the removed
    // trigger-device editor had). SaveAsync() enforces this by writing this value onto every device.
    public int DefaultArrivalDelayMinutes { get; set; } = 1;
    // Controls the tray flyout popup (App.xaml.cs) - shown briefly near the tray icon when a backup
    // starts, skipped entirely under a fullscreen app. A person who finds it distracting can turn it off
    // without losing the information: Overview's Backup jobs grid and the removal banner already show
    // the same facts.
    public bool ShowFlyoutOnBackupStart { get; set; } = true;

    public void StartDeviceMonitoring()
    {
        _deviceTimer.Start();
        if (!_demoMode)
        {
            _catalogTimer.Start();
            _jobTimer.Start();
            _nearbyTimer.Start();
            _ = InitializeServiceStateAsync();
        }
    }

    private async Task InitializeServiceStateAsync()
    {
        await RefreshConfigurationAsync();
        await RefreshCatalogsAsync();
        await RefreshConnectionsAsync();
        await RefreshJobsAsync();
        await RefreshNearbyComputersAsync();
    }

    public async Task RefreshJobsAsync()
    {
        if (_refreshingJobs) return;
        _refreshingJobs = true;
        try
        {
            var selectedId = SelectedJob?.JobId;
            var jobs = await _jobClient.ListAsync(_shutdown.Token);
            var changedJobs = _jobsInitialized
                ? jobs.Where(job => !_observedJobStates.TryGetValue(job.JobId, out var state) || state != job.State).OrderBy(job => job.UpdatedAt)
                : jobs.OrderByDescending(job => job.UpdatedAt).Take(6).OrderBy(job => job.UpdatedAt);
            foreach (var job in changedJobs) RecordJobActivity(job);
            _observedJobStates.Clear();
            foreach (var job in jobs) _observedJobStates[job.JobId] = job.State;
            _jobsInitialized = true;
            Jobs.Clear();
            foreach (var job in jobs) Jobs.Add(new(job, Mappings.FirstOrDefault(mapping => mapping.Id == job.TargetMappingId)));
            SelectedJob = Jobs.FirstOrDefault(job => job.JobId == selectedId) ?? Jobs.FirstOrDefault();
            OnPropertyChanged(nameof(LastBackupSummary));
            NotifyDashboardTransfer();
            _deviceStatuses = await _configurationClient.GetDeviceStatusesAsync(_shutdown.Token);
            _backupCommands = await _jobClient.ListCommandsAsync(_shutdown.Token);
            UpdateMappingLastBackupInfo();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException) { _deviceStatuses = []; _backupCommands = []; UpdateMappingLastBackupInfo(); }
        catch (TaskCanceledException) { _deviceStatuses = []; _backupCommands = []; UpdateMappingLastBackupInfo(); }
        finally { _refreshingJobs = false; }
    }

    // "Last backup" answers both "when" and, if it's stale for a reason the user can act on, "why" in the
    // same cell; a separate Status column would only duplicate the time already shown here. The
    // reason only ever appears when it actually explains staleness (a failed/cancelled attempt, or the
    // source computer being offline), never as a caveat on an otherwise-healthy row.
    private void UpdateMappingLastBackupInfo()
    {
        foreach (var mapping in Mappings)
        {
            mapping.TriggerNote = ComputeTriggerNote(mapping.BackupSet.Model);
            var jobsForMapping = Jobs.Where(job => job.TargetMappingId == mapping.Id).ToArray();
            mapping.LastBackupAt = jobsForMapping.OrderByDescending(job => job.UpdatedAt).FirstOrDefault()?.UpdatedAt;
            mapping.NextBackupDisplay = ComputeNextBackupDisplay(mapping, _serviceAutomaticBackups,
                _deviceStatuses.FirstOrDefault(status => status.DeviceId == mapping.Device.Id),
                _backupCommands.FirstOrDefault(command => command.TargetMappingId == mapping.Id && command.State is "PENDING" or "CLAIMED" or "RUNNING"),
                jobsForMapping.Any(job => !job.IsTerminal));
            if (jobsForMapping.Any(job => !job.IsTerminal))
            {
                mapping.LastBackupDisplay = Localization.Text("Text_Backingupnow_CF4F1D");
                mapping.LastBackupIssue = string.Empty;
                continue;
            }
            var latest = jobsForMapping.Where(job => job.IsTerminal).OrderByDescending(job => job.UpdatedAt).FirstOrDefault();
            if (latest is null)
            {
                mapping.LastBackupDisplay = Localization.Text("Text_Never_6300EF");
                mapping.LastBackupIssue = string.Empty;
                continue;
            }
            mapping.LastBackupDisplay = RelativeTimeDisplay(latest.UpdatedAt);
            mapping.LastBackupIssue = latest.State switch
            {
                "FAILED" => Localization.Text("Text_Lastattemptfailed_96E5BC"),
                "CANCELLED" => Localization.Text("Text_Lastattemptwascancelled_AEA9F1"),
                _ when mapping.BackupSet.Model.SourceAgentId != LocalSourceIdentity.AgentId
                    && Sources.FirstOrDefault(source => source.Id == mapping.BackupSet.Model.SourceAgentId)?.StatusDisplay == Localization.Text("Text_Offline_A17947")
                    => Localization.Format("Text_0isoffline_966AA7", mapping.SourceAgentName),
                _ => string.Empty
            };
        }
        OnPropertyChanged(nameof(CanStartSelectedMapping));
        OnPropertyChanged(nameof(StartSelectedMappingHint));
    }

    // A Backup Set that names an explicit trigger device (the external-source-arrival case
    // from USER_GUIDE 6, "insert this card and it backs up wherever it's mapped" - or a config authored
    // before the per-row editor was removed) only starts for that device, not "whenever its Target
    // connects" the way an untriggered row's default behavior does. Read-only is enough here: the point is
    // making the existing behavior visible, not re-adding an editor for it.
    private string ComputeTriggerNote(SourceBackupSet set)
    {
        if (set.TriggerDeviceIds.Count == 0) return string.Empty;
        var names = set.TriggerDeviceIds.Select(id => Devices.FirstOrDefault(device => device.Id == id)?.DisplayName ?? Localization.Text("Text_aremoveddevice_DE214B")).ToArray();
        if (names.Length == 1) return Localization.Format("Text_Startswhen0connects_F34F79", names[0]);
        var connective = Localization.Text(set.TriggerPolicy == BackupSetTriggerPolicy.AllAvailable ? "And" : "Or");
        var joined = $"{string.Join(", ", names[..^1])} {connective} {names[^1]}";
        return set.TriggerPolicy == BackupSetTriggerPolicy.AllAvailable ? Localization.Format("Text_Startswhen0areallconnected_C94AE2", joined) : Localization.Format("Text_Startswhen0connects_F34F79", joined);
    }

    // Mark consecutive rows sharing a Source or Source folder so the view can group them visually. Recomputed
    // from scratch on every membership/order change rather than incrementally, since Mappings is small
    // (tens, not thousands, of rows) and an incremental version would need to invalidate on both neighbors
    // of any insert/remove - not worth the complexity for this size of list.
    private void UpdateMappingRepeatMarkers()
    {
        MappingViewModel? previous = null;
        foreach (var mapping in Mappings)
        {
            mapping.IsRepeatOfPreviousSource = previous is not null && previous.SourceAgentName == mapping.SourceAgentName;
            mapping.IsRepeatOfPreviousSourceFolder = mapping.IsRepeatOfPreviousSource && previous!.BackupSet.Id == mapping.BackupSet.Id;
            previous = mapping;
        }
    }

    private async Task CancelSelectedJobAsync()
    {
        if (SelectedJob is not { CanCancel: true } job) { FooterStatus = Localization.Text("Text_Selectanactivebackupjobfirst_487C65"); return; }
        try
        {
            await _jobClient.CancelAsync(job.JobId, _shutdown.Token);
            FooterStatus = Localization.Text("Text_CancellationrequestedThecomput_ACFD78");
            await RefreshJobsAsync();
        }
        catch (HttpRequestException) { FooterStatus = Localization.Text("Text_Thecancellationrequestcouldnot_5624F8"); }
        catch (TaskCanceledException) { FooterStatus = Localization.Text("Text_Thecancellationrequesttimedout_2623FB"); }
    }

    private bool _updatingStorageIdentity;
    private async Task RefreshCatalogsAsync()
    {
        try
        {
            var catalogs = await _catalogClient.ListAsync(_shutdown.Token);
            ApplyCatalogs(catalogs);
            FooterStatus = catalogs.Count == 0 ? Localization.Text("Text_Nocomputerhasreportedanythingt_2B5CCE") : Localization.Format("Text_Synchronized0_F08374", Pluralize(catalogs.Count, "computer"));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException)
        {
            FooterStatus = Localization.Text("Text_StorageServiceisunavailablesho_BAF1A3");
        }
        catch (TaskCanceledException)
        {
            FooterStatus = Localization.Text("Text_Sourcecatalogsynchronizationti_468130");
        }
    }

    public Task RefreshCatalogsOnceAsync() => RefreshCatalogsAsync();

    public async Task RefreshAgentConnectionsAsync()
    {
        await RefreshCatalogsAsync();
        await RefreshConnectionsAsync();
    }

    private async Task RefreshConnectionsAsync()
    {
        if (_demoMode) return;
        try
        {
            var connections = await _connectionsClient.ListAsync(_shutdown.Token);
            SourceConnections.Clear();
            foreach (var connection in connections.OrderBy(c => c.AgentName, StringComparer.OrdinalIgnoreCase)) SourceConnections.Add(new(connection));
            var currentlyOnline = SourceConnections.Where(connection => connection.IsOnline).ToDictionary(connection => connection.AgentId, connection => connection.AgentName);
            foreach (var connection in currentlyOnline.Where(pair => !_onlineSourceNames.ContainsKey(pair.Key)))
                AddActivity(Localization.Text("ActivitySourceConnected"), ActivityKind.Storage, connection.Value);
            foreach (var connection in _onlineSourceNames.Where(pair => !currentlyOnline.ContainsKey(pair.Key)))
                AddActivity(Localization.Text("ActivitySourceDisconnected"), ActivityKind.Storage, connection.Value);
            _onlineSourceNames.Clear();
            foreach (var connection in currentlyOnline) _onlineSourceNames.Add(connection.Key, connection.Value);
            ApplySourceConnections();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }

    private void RefreshRuleComputerOptions()
    {
        var wanted = BackupSets.Select(set => new RuleComputerOption(set.Model.SourceAgentId, set.SourceDisplayName))
            .DistinctBy(option => option.Id).OrderBy(option => option.Name).ToArray();
        foreach (var option in RuleComputerOptions.Where(option => !wanted.Contains(option)).ToArray()) RuleComputerOptions.Remove(option);
        foreach (var option in wanted.Where(option => !RuleComputerOptions.Contains(option))) RuleComputerOptions.Add(option);
    }

    internal static string ComputeNextBackupDisplay(MappingViewModel mapping, bool automaticBackups,
        StorageDeviceStatusDto? deviceStatus, BackupCommandStatusDto? command, bool backupRunning, DateTimeOffset? now = null)
    {
        if (backupRunning) return Localization.Text("Text_Backingupnow_CF4F1D");
        if (command is not null) return Localization.Text(command.State is "CLAIMED" or "RUNNING" ? "NextBackupStarting" : "NextBackupQueued");
        if (!mapping.Enabled) return Localization.Text("NextBackupPaused");
        if (!automaticBackups) return Localization.Text("NextBackupAutomaticOff");
        if (deviceStatus is null) return Localization.Text("NextBackupStatusUnavailable");
        if (!deviceStatus.Connected) return Localization.Format("NextBackupWaitingForStorage", mapping.DeviceName);
        if (!deviceStatus.Ready && deviceStatus.EligibleAt is { } eligibleAt)
        {
            var remaining = eligibleAt - (now ?? DateTimeOffset.Now);
            if (remaining <= TimeSpan.Zero) return Localization.Text("NextBackupWaiting");
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            return Localization.Format("NextBackupWaitEnds", eligibleAt.LocalDateTime.ToString("t"), minutes);
        }
        if (!deviceStatus.Ready) return Localization.Text("NextBackupWaiting");
        return Localization.Format("NextBackupEveryMinutes", mapping.BackupIntervalMinutes);
    }

    public Task RefreshConnectionsOnceAsync() => RefreshConnectionsAsync();

    public async Task RefreshNearbyComputersAsync(bool preserveStatus = false)
    {
        if (_demoMode) return;
        try
        {
            var selectedId = SelectedNearbyComputer?.AgentId;
            var connectedIds = SourceConnections.Select(connection => connection.AgentId).ToHashSet();
            var nearby = await _nearbyPairingClient.ListAsync(_shutdown.Token);
            NearbyComputers.Clear();
            foreach (var computer in nearby.Where(computer => !connectedIds.Contains(computer.AgentId)).OrderBy(computer => computer.AgentName, StringComparer.OrdinalIgnoreCase))
                NearbyComputers.Add(new(computer));
            SelectedNearbyComputer = NearbyComputers.FirstOrDefault(computer => computer.AgentId == selectedId);
            if (_nearbyPairingRequest is null && (!preserveStatus || string.IsNullOrEmpty(NearbyPairingStatus)))
                NearbyPairingStatus = NearbyComputers.Count == 0 ? Localization.Text("NearbyNone") : Localization.Format("NearbyFound", NearbyComputers.Count);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException) { if (!preserveStatus || string.IsNullOrEmpty(NearbyPairingStatus)) NearbyPairingStatus = Localization.Text("NearbyRefreshFailed"); }
        catch (TaskCanceledException) { if (!preserveStatus || string.IsNullOrEmpty(NearbyPairingStatus)) NearbyPairingStatus = Localization.Text("NearbyRefreshFailed"); }
        catch (JsonException) { if (!preserveStatus || string.IsNullOrEmpty(NearbyPairingStatus)) NearbyPairingStatus = Localization.Text("NearbyRefreshFailed"); }
    }

    private async Task RequestNearbyPairingAsync()
    {
        var computer = SelectedNearbyComputer;
        if (computer is null || _requestingNearbyPairing) return;
        _requestingNearbyPairing = true;
        OnPropertyChanged(nameof(HasSelectedNearbyComputer));
        try
        {
            _nearbyPairingRequest = await _nearbyPairingClient.RequestAsync(computer.AgentId, computer.Identity, _shutdown.Token);
            OnPropertyChanged(nameof(NearbyComparisonCode));
            OnPropertyChanged(nameof(HasNearbyComparisonCode));
            OnPropertyChanged(nameof(CanCancelNearbyPairing));
            OnPropertyChanged(nameof(HasSelectedNearbyComputer));
            NearbyPairingStatus = Localization.Format("NearbyPairingPending", computer.DisplayName);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException) { NearbyPairingStatus = Localization.Text("NearbyPairingFailed"); }
        catch (TaskCanceledException) { NearbyPairingStatus = Localization.Text("NearbyPairingFailed"); }
        catch (InvalidDataException) { NearbyPairingStatus = Localization.Text("NearbyPairingFailed"); }
        catch (JsonException) { NearbyPairingStatus = Localization.Text("NearbyPairingFailed"); }
        finally
        {
            _requestingNearbyPairing = false;
            OnPropertyChanged(nameof(HasSelectedNearbyComputer));
        }
    }

    private async Task CancelNearbyPairingAsync()
    {
        var pending = _nearbyPairingRequest;
        if (pending is null) return;
        try
        {
            await _nearbyPairingClient.CancelAsync(pending.RequestId, _shutdown.Token);
            if (_nearbyPairingRequest?.RequestId != pending.RequestId) return;
            _nearbyPairingRequest = null;
            OnPropertyChanged(nameof(HasNearbyComparisonCode));
            OnPropertyChanged(nameof(CanCancelNearbyPairing));
            OnPropertyChanged(nameof(HasSelectedNearbyComputer));
            NearbyPairingStatus = Localization.Text("NearbyPairingCancelled");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            NearbyPairingStatus = Localization.Text("NearbyPairingCancelFailed");
        }
    }

    private async Task RefreshNearbyPairingStateAsync()
    {
        if (_refreshingNearbyPairing) return;
        _refreshingNearbyPairing = true;
        try
        {
            await RefreshNearbyComputersAsync(preserveStatus: true);
            var pending = _nearbyPairingRequest;
            if (pending is null) return;
            if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                if (_nearbyPairingRequest?.RequestId != pending.RequestId) return;
                _nearbyPairingRequest = null;
                OnPropertyChanged(nameof(HasNearbyComparisonCode));
                OnPropertyChanged(nameof(CanCancelNearbyPairing));
                OnPropertyChanged(nameof(HasSelectedNearbyComputer));
                NearbyPairingStatus = Localization.Text("NearbyPairingExpired");
                return;
            }
            try
            {
                var current = await _nearbyPairingClient.GetAsync(pending.RequestId, _shutdown.Token);
                if (_nearbyPairingRequest?.RequestId != pending.RequestId) return;
                _nearbyPairingRequest = current;
                OnPropertyChanged(nameof(NearbyComparisonCode));
                OnPropertyChanged(nameof(CanCancelNearbyPairing));
                NearbyPairingStatus = current.Status switch
                {
                    "PENDING" => Localization.Format("NearbyPairingPending", current.AgentName),
                    "EXCHANGING" => Localization.Format("NearbyPairingFinishing", current.AgentName),
                    "ISSUED" => Localization.Format("NearbyPairingIssued", current.AgentName),
                    "PAIRED" => Localization.Format("NearbyPairingPaired", current.AgentName),
                    "EXPIRED" => Localization.Text("NearbyPairingExpired"),
                    "REPLACED" => Localization.Text("NearbyPairingReplaced"),
                    "REJECTED" => Localization.Text("NearbyPairingRejected"),
                    _ => Localization.Text("NearbyPairingFailed")
                };
                if (current.Status == "PAIRED")
                {
                    _nearbyPairingRequest = null;
                    OnPropertyChanged(nameof(HasNearbyComparisonCode));
                    OnPropertyChanged(nameof(CanCancelNearbyPairing));
                    OnPropertyChanged(nameof(HasSelectedNearbyComputer));
                    await RefreshConnectionsAsync();
                    await RefreshCatalogsAsync();
                    await RefreshNearbyComputersAsync(preserveStatus: true);
                }
                else if (current.Status is not ("PENDING" or "EXCHANGING" or "ISSUED"))
                {
                    _nearbyPairingRequest = null;
                    OnPropertyChanged(nameof(HasNearbyComparisonCode));
                    OnPropertyChanged(nameof(CanCancelNearbyPairing));
                    OnPropertyChanged(nameof(HasSelectedNearbyComputer));
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                if (_nearbyPairingRequest?.RequestId != pending.RequestId) return;
                NearbyPairingStatus = Localization.Text("NearbyPairingUnavailable");
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                if (_nearbyPairingRequest?.RequestId != pending.RequestId) return;
                _nearbyPairingRequest = null;
                OnPropertyChanged(nameof(HasNearbyComparisonCode));
                OnPropertyChanged(nameof(CanCancelNearbyPairing));
                OnPropertyChanged(nameof(HasSelectedNearbyComputer));
                NearbyPairingStatus = Localization.Text("NearbyPairingFailed");
            }
        }
        finally { _refreshingNearbyPairing = false; }
    }

    public Task RefreshNearbyPairingStateOnceAsync() => RefreshNearbyPairingStateAsync();

    // Sources is rebuilt from scratch (new RemoteAgentViewModel instances) on every catalog/config
    // refresh, so each one's Connection must be re-applied every time too, not just when
    // RefreshConnectionsAsync() itself runs - otherwise a catalog refresh 10 seconds later silently wipes
    // every computer's connection info from the merged grid until the next connections poll.
    private void ApplySourceConnections()
    {
        // A newly paired Remote Agent may intentionally publish an empty catalog. Keep it visible from
        // the authenticated connection record instead of requiring a fake Backup Set to create its row.
        foreach (var connection in SourceConnections.Where(connection => Sources.All(source => source.Id != connection.AgentId)))
            Sources.Add(new(connection.AgentId, connection.AgentName));
        foreach (var source in Sources) source.Connection = SourceConnections.FirstOrDefault(connection => connection.AgentId == source.Id);
        SelectedSourceConnection = SelectedRemoteAgent is null ? null : SourceConnections.FirstOrDefault(connection => connection.AgentId == SelectedRemoteAgent.Id);
        NotifyDashboardTransfer();
        UpdateMappingLastBackupInfo();
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
            FooterStatus = Localization.Text("Text_Computerrenamed_F1C8AC");
            await RefreshConnectionsAsync();
        }
        catch (HttpRequestException exception) { FooterStatus = Localization.Format("Text_Couldnotrename01_6AD77F", connection.AgentName, exception.Message); }
        catch (TaskCanceledException) { FooterStatus = Localization.Format("Text_Therenamerequestfor0timedout_EBEFC7", connection.AgentName); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ForgetSelectedSourceAsync()
    {
        var connection = SelectedSourceConnection;
        if (connection is null) return;
        var confirmed = System.Windows.MessageBox.Show(
            Localization.Format("Text_Remove0Itsaccesswillbeblockedi_7EB827", connection.AgentName),
            Localization.Text("Text_Removecomputer_3BE329"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        if (!confirmed) return;
        try
        {
            await _connectionsClient.ForgetAsync(connection.AgentId, _shutdown.Token);
            FooterStatus = Localization.Format("Text_Removed0Itsbackupsarepreserved_A71B5C", connection.AgentName);
            NotificationRequested?.Invoke(this, new(Localization.Text("Text_Computerconnection_6BB4A5"), FooterStatus));
            await RefreshConnectionsAsync();
            await RefreshCatalogsAsync();
        }
        catch (HttpRequestException exception) { FooterStatus = Localization.Format("Text_Couldnotremove01_0831D6", connection.AgentName, exception.Message); }
        catch (TaskCanceledException) { FooterStatus = Localization.Format("Text_Therequesttoremove0timedout_B33434", connection.AgentName); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task<bool> RotateStorageIdentityAsync()
    {
        if (_updatingStorageIdentity) return false;
        var confirmed = System.Windows.MessageBox.Show(
            Localization.Text("Text_ThisregeneratestheStoragescert_1C14DB"),
            Localization.Text("Text_RotateStorageidentity_8CA37E"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        if (!confirmed) return false;
        _updatingStorageIdentity = true;
        try
        {
            FooterStatus = Localization.Text("IdentityUpdating");
            await _pairingClient.RotateAuthorityAsync(_shutdown.Token);
            FooterStatus = Localization.Text("Text_StorageidentityrotatedRestartt_20367A");
            NotificationRequested?.Invoke(this, new(Localization.Text("Text_Storageidentityrotated_D5E024"), FooterStatus));
            return true;
        }
        catch (HttpRequestException exception) { FooterStatus = Localization.Format("Text_CouldnotrotatetheStorageidenti_B322A0", exception.Message); }
        catch (TaskCanceledException) { FooterStatus = Localization.Text("Text_TheStorageidentityrotationrequ_E2B876"); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return false; }
        finally { _updatingStorageIdentity = false; }
        System.Windows.MessageBox.Show(FooterStatus, Localization.Text("Text_Computerpairing_53ECA5"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return false;
    }

    public async Task RefreshConfigurationAsync()
    {
        if (_demoMode) return;
        try
        {
            var document = await _configurationClient.GetAsync(_shutdown.Token);
            ApplyTopology(document.Configuration);
            _configurationRevision = document.Revision;
            AutomaticBackups = _serviceAutomaticBackups = (await _configurationClient.GetAutomationAsync(_shutdown.Token)).Enabled;
            if (!_arrivalDelayMigrationAttempted && (_legacyArrivalDelayNeedsUpdate || Devices.Any(device => device.ArrivalDelayMinutes == 30)))
            {
                _arrivalDelayMigrationAttempted = true;
                _legacyArrivalDelayNeedsUpdate = false;
                await SaveAsync();
            }
            FooterStatus = Localization.Format("Text_LoadedStorageServiceconfigurat_E46147", document.Revision);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException)
        {
            FooterStatus = Localization.Text("Text_StorageServiceisunavailablecon_214300");
        }
        catch (TaskCanceledException)
        {
            FooterStatus = Localization.Text("Text_StorageServiceconfigurationreq_80A20B");
        }
        catch (InvalidDataException exception)
        {
            FooterStatus = exception.Message;
        }
    }

    private void ApplyCatalogs(IReadOnlyList<SourceCatalogDto> catalogs)
    {
        // Local Backup Sets are Storage's own authored data, never reported by any Source's catalog
        // sync, so they must never be marked "not reported" by this routine refresh.
        foreach (var existing in BackupSets.Where(set => set.Model.SourceAgentId != LocalSourceIdentity.AgentId)) existing.IsAvailable = false;
        foreach (var catalog in catalogs)
        {
            foreach (var set in catalog.BackupSets)
            {
                var existing = BackupSets.FirstOrDefault(item => item.Id == set.BackupSetId);
                // Trigger devices/policy are Storage-side configuration, not something a Source reports -
                // carry them over from what is already known instead of letting a routine catalog
                // refresh silently erase them.
                var model = new SourceBackupSet(set.BackupSetId, catalog.SourceAgentId, catalog.SourceAgentName, set.Name, set.SourcePaths,
                    existing?.Model.TriggerDeviceIds ?? [], existing?.Model.TriggerPolicy ?? BackupSetTriggerPolicy.AnyAvailable);
                if (existing is null) BackupSets.Add(new(model));
                else existing.Update(model);
            }
        }
        // Sources is rebuilt with brand new instances below, which would otherwise silently drop
        // whatever computer the user has selected in the merged grid every 10-second catalog refresh.
        var selectedSourceId = SelectedRemoteAgent?.Id;
        Sources.Clear();
        foreach (var group in BackupSets.Where(item => item.Model.SourceAgentId != LocalSourceIdentity.AgentId).GroupBy(set => new { set.Model.SourceAgentId, set.Model.SourceAgentName }).OrderBy(group => group.Key.SourceAgentName, StringComparer.OrdinalIgnoreCase))
        {
            var source = new RemoteAgentViewModel(group.Key.SourceAgentId, group.Key.SourceAgentName);
            foreach (var set in group.OrderBy(item => item.Model.Name, StringComparer.OrdinalIgnoreCase)) source.BackupSets.Add(set);
            Sources.Add(source);
        }
        SelectedBackupSet ??= BackupSets.FirstOrDefault(set => set.IsAvailable);
        SelectedRemoteAgent = Sources.FirstOrDefault(source => source.Id == selectedSourceId);
        ApplySourceConnections();
        RefreshDeviceTriggerRoles();
        NotifyCounts();
    }

    private void RefreshDeviceTriggerRoles()
    {
        var triggerDeviceIds = BackupSets.SelectMany(set => set.Model.TriggerDeviceIds).ToHashSet();
        var targetDeviceIds = Mappings.Where(mapping => mapping.Enabled).Select(mapping => mapping.Device.Id).ToHashSet();
        foreach (var device in Devices)
        {
            device.IsSourceTrigger = triggerDeviceIds.Contains(device.Id);
            device.IsUsedAsTarget = targetDeviceIds.Contains(device.Id);
        }
    }

    public void QueueSelectedBackups() => _ = QueueEligibleBackupsAsync();

    // The tray flyout's "Start now" on a pending-arrival card needs exactly this same "enqueue
    // immediately, bypassing the arrival delay" behavior for one specific device - StorageMonitorService
    // (the Windows Service background loop) is what actually enforces ArrivalDelayMinutes, entirely
    // independent of this client, so there is no delay to "cancel" client-side; enqueuing the job directly
    // is the correct way to start it now regardless of what the service's own poll timer is waiting on.
    public Task<int> QueueBackupsForDeviceAsync(Guid deviceId) => QueueEligibleBackupsAsync(mapping => mapping.Device.Id == deviceId);

    // "Skip this time" (tray flyout) must actually prevent the automatic backup it's warning about, not
    // just dismiss its own card - StorageMonitorService enqueues arrival commands entirely on its own
    // background poll, so the only lever this client has over that decision is the same Enabled flag the
    // Backups grid's own checkbox uses (BuildArrivalDrafts filters on mapping.Enabled server-side). Scoped
    // to "this connection" by RestoreSkippedMappings, called from RefreshDrives() the moment the device
    // disconnects - never left disabled past that, and never touching a mapping the user had already
    // turned off before the skip.
    public async Task SkipDeviceThisConnectionAsync(Guid deviceId)
    {
        var affected = Mappings.Where(mapping => mapping.Device.Id == deviceId && mapping.Enabled).ToArray();
        if (affected.Length == 0) return;
        foreach (var mapping in affected) { mapping.SetEnabledWithoutSaving(false); _skipDisabledMappingIds.Add(mapping.Id); }
        await SaveAsync();
    }

    private async Task RestoreSkippedMappings(Guid deviceId)
    {
        var affected = Mappings.Where(mapping => mapping.Device.Id == deviceId && _skipDisabledMappingIds.Contains(mapping.Id)).ToArray();
        if (affected.Length == 0) return;
        foreach (var mapping in affected) { mapping.SetEnabledWithoutSaving(true); _skipDisabledMappingIds.Remove(mapping.Id); }
        await SaveAsync();
    }

    // Applies whatever's already in _skipDisabledMappingIds to the current Mappings collection, without
    // clearing the set or persisting - safe to call every time Mappings is (re)built (Load(), ApplyTopology())
    // regardless of which one turns out to be authoritative for this run. Restart recovery (see
    // AppConfiguration.SkipDisabledMappingIds) always means every tracked mapping, not just one device's -
    // "this connection" stopped meaning anything the moment the process that was tracking it exited.
    private void RestoreSkipDisabledMappingsIntoCurrentSet()
    {
        foreach (var mapping in Mappings.Where(mapping => _skipDisabledMappingIds.Contains(mapping.Id)))
            mapping.SetEnabledWithoutSaving(true);
    }

    public Task<int> QueueEligibleBackupsAsync() => QueueEligibleBackupsAsync(_ => true);

    internal Task<int> QueueSelectedMappingAsync() => SelectedMapping is { } mapping
        ? QueueEligibleBackupsAsync(candidate => candidate.Id == mapping.Id)
        : Task.FromResult(0);

    internal Task<int> QueueMappingsAsync(IEnumerable<MappingViewModel> mappings)
    {
        var ids = mappings.Select(mapping => mapping.Id).ToHashSet();
        return ids.Count == 0 ? Task.FromResult(0) : QueueEligibleBackupsAsync(mapping => ids.Contains(mapping.Id));
    }

    private async Task<int> QueueEligibleBackupsAsync(Func<MappingViewModel, bool> filter)
    {
        var eligible = Mappings.Where(mapping => mapping.Enabled && mapping.Device.IsConnected && filter(mapping)).ToArray();
        if (eligible.Length == 0)
        {
            var noTargets = Localization.Text("Text_Nomappedbackupiscurrentlyeligi_D66941");
            FooterStatus = noTargets;
            AddActivity(noTargets);
            NotificationRequested?.Invoke(this, new("BackupMesh", noTargets));
            return 0;
        }

        try
        {
            var queued = await _jobClient.EnqueueAsync(eligible.Select(mapping => mapping.Id).ToArray(), "manual", _shutdown.Token);
            foreach (var mapping in eligible) AddActivity(Localization.Format("Text_Requestedbackupfor0to1_B5C1BB", mapping.BackupSetName, mapping.DeviceName));
            await RefreshJobsAsync();
            var message = queued == 0
                ? Localization.Text("Text_Nonewbackupswerequeuedmatching_5E559C")
                : Localization.Format("Text_Queued0_06E215", Pluralize(queued, "backup"));
            FooterStatus = message;
            NotificationRequested?.Invoke(this, new("BackupMesh", message));
            return queued;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return 0; }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            var message = Localization.Format("Text_Backupqueuerequestfailed0_55FABD", exception.Message);
            FooterStatus = message;
            AddActivity(message);
            NotificationRequested?.Invoke(this, new("BackupMesh", message, true));
            return 0;
        }
    }

    private void Load()
    {
        var state = _store.Load();
        _language = state.Language is "ko" or "en" ? state.Language : "";
        StartWithWindows = state.StartWithWindows;
        NotifyOnDeviceArrival = state.NotifyOnDeviceArrival;
        AutomaticBackups = state.AutomaticBackups;
        _legacyArrivalDelayNeedsUpdate = state.DefaultArrivalDelayMinutes == 30;
        DefaultArrivalDelayMinutes = _legacyArrivalDelayNeedsUpdate ? 1 : state.DefaultArrivalDelayMinutes;
        ShowFlyoutOnBackupStart = state.ShowFlyoutOnBackupStart;

        foreach (var device in state.Topology.Devices) Devices.Add(new(device));
        foreach (var group in state.Topology.BackupSets.GroupBy(set => new { set.SourceAgentId, set.SourceAgentName }))
        {
            var source = new RemoteAgentViewModel(group.Key.SourceAgentId, group.Key.SourceAgentName);
            foreach (var backupSet in group)
            {
                var item = new BackupSetViewModel(backupSet);
                source.BackupSets.Add(item);
                BackupSets.Add(item);
            }
            if (source.Id != LocalSourceIdentity.AgentId) Sources.Add(source);
        }
        foreach (var mapping in state.Topology.Mappings)
        {
            var set = BackupSets.FirstOrDefault(item => item.Id == mapping.BackupSetId);
            var device = Devices.FirstOrDefault(item => item.Id == mapping.DeviceId);
            if (set is not null && device is not null) Mappings.Add(CreateMapping(mapping, set, device));
        }
        if (state.SkipDisabledMappingIds is { Count: > 0 } skipped)
        {
            _skipDisabledMappingIds.UnionWith(skipped);
            RestoreSkipDisabledMappingsIntoCurrentSet();
        }
        AddActivity(Localization.Text("Text_StorageAgentUIstarted_99C199"));
    }

    private void LoadDemoSources()
    {
        var home = new RemoteAgentViewModel(Guid.Parse("c60280da-a03c-4887-a600-577def417af6"), "Home Server");
        AddDemoSet(home, new(Guid.Parse("7d750726-97ab-4f81-9f09-f06c34f524d1"), home.Id, home.DisplayName, "Photos", ["/srv/photos", "/srv/videos"]));
        AddDemoSet(home, new(Guid.Parse("e10a4df5-0f71-438d-93f0-34e587357f00"), home.Id, home.DisplayName, "Documents", ["/home/park/Documents"]));
        Sources.Add(home);

        var workstation = new RemoteAgentViewModel(Guid.Parse("0cdf358f-4b92-4bb0-b852-460520508952"), "Studio Workstation");
        AddDemoSet(workstation, new(Guid.Parse("bb452fc9-f616-4810-a649-3c37775d43d4"), workstation.Id, workstation.DisplayName, "Projects", ["D:/Projects"]));
        Sources.Add(workstation);

        // Demo remote computers also provide connection status.
        SourceConnections.Add(new(new(home.Id, home.DisplayName, home.DisplayName, DateTimeOffset.UtcNow.AddSeconds(-30), "192.168.1.42", 2, false, DateTimeOffset.UtcNow.AddDays(75))));
        // Demonstrates the "missed its own renewal window" status: last seen well before the renewal
        // window (30 days before expiry) opened, unlike Home Server above which is seen recently enough
        // that its certificate (renewed automatically) never needs a person's attention.
        SourceConnections.Add(new(new(workstation.Id, workstation.DisplayName, workstation.DisplayName, DateTimeOffset.UtcNow.AddDays(-45), "192.168.1.77", 1, false, DateTimeOffset.UtcNow.AddDays(5))));
        ApplySourceConnections();

        SelectedBackupSet = BackupSets.FirstOrDefault();
        AddActivity("Demo Source catalog loaded for UX validation.");
        NotifyCounts();
    }

    public void LoadPreviewRules()
    {
        if (!_demoMode) return;
        var drive = new DeviceViewModel(new RegisteredDevice(Guid.NewGuid(), "preview-drive", "Backup Drive (D:)", "Backup Drive", "D:\\", DateTimeOffset.Now, DateTimeOffset.Now));
        drive.IsConnected = true;
        Devices.Add(drive);
        var samples = new (string Name, string Path, string LastBackup, bool Enabled)[]
        {
            ("사진 보관함", @"C:\Users\사용자\Pictures", "오늘 14:32", true),
            ("문서", @"C:\Users\사용자\Documents", "오늘 14:28", true),
            ("작업 파일", @"C:\Users\사용자\Desktop\작업", "백업 중", true),
            ("개발 프로젝트", @"C:\Users\사용자\Projects", "오늘 12:01", true),
            ("가족 사진", @"C:\Users\사용자\Pictures\가족", "오늘 08:17", true),
            ("음악", @"C:\Users\사용자\Music", "없음", true),
            ("회계 자료", @"C:\Users\사용자\Documents\회계", "어제 21:03", true),
            ("동영상", @"C:\Users\사용자\Videos", "어제 19:45", true),
            ("스캔 문서", @"C:\Users\사용자\Documents\스캔", "어제 17:20", true),
            ("노트", @"C:\Users\사용자\OneDrive\노트", "어제 16:11", true),
            ("다운로드", @"C:\Users\사용자\Downloads", "2024-06-10 11:24", true),
            ("업무 자료", @"C:\Users\사용자\Desktop\업무", "어제 19:45", false)
        };
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var name = PreviewName(sample.Name);
            var path = PreviewPath(sample.Path);
            var set = new BackupSetViewModel(new SourceBackupSet(Guid.NewGuid(), LocalSourceIdentity.AgentId, LocalSourceIdentity.DisplayName, name, [path]));
            BackupSets.Add(set);
            var mapping = new MappingViewModel(new BackupTargetMapping(Guid.NewGuid(), set.Id, drive.Id, name, sample.Enabled), set, drive);
            mapping.LastBackupDisplay = sample.LastBackup == "백업 중" ? Localization.Text("Text_Backingupnow_CF4F1D")
                : sample.LastBackup == "없음" ? Localization.Text("Text_Never_6300EF") : PreviewTime(sample.LastBackup);
            mapping.LastBackupAt = sample.LastBackup == "없음" ? null : DateTimeOffset.Now.AddMinutes(-index * 40);
            Mappings.Add(mapping);
        }
        NotifyCounts(refreshJobInfo: false);
    }

    public void LoadPreviewAgents()
    {
        if (!_demoMode) return;
        Sources.Clear();
        SourceConnections.Clear();
        NearbyComputers.Clear();
        foreach (var set in BackupSets.Where(set => set.Model.SourceAgentId != LocalSourceIdentity.AgentId).ToArray()) BackupSets.Remove(set);
        var agents = new (string Name, string[] Folders, TimeSpan LastSeen, string? PreviewStatus)[]
        {
            ("가족 PC", ["사진", "문서", "동영상"], TimeSpan.FromSeconds(30), null),
            ("작업실 PC", ["작업 파일", "개발 프로젝트"], TimeSpan.FromMinutes(10), null),
            ("노트북", ["문서"], TimeSpan.FromHours(3), null),
            ("새 컴퓨터", [], TimeSpan.Zero, "승인 대기")
        };
        for (var index = 0; index < agents.Length; index++)
        {
            var sample = agents[index];
            var name = PreviewName(sample.Name);
            var agent = new RemoteAgentViewModel(Guid.NewGuid(), name) { PreviewStatus = sample.PreviewStatus is null ? null : "PreviewAwaitingApproval" };
            foreach (var folder in sample.Folders)
            {
                string[] paths = sample.Name == "가족 PC" && folder == "문서"
                    ? ["/doc/img", "/doc/db"] : [PreviewPath($"C:\\Users\\사용자\\{folder}")];
                var set = new BackupSetViewModel(new SourceBackupSet(Guid.NewGuid(), agent.Id, name, PreviewName(folder), paths));
                agent.BackupSets.Add(set);
                BackupSets.Add(set);
            }
            Sources.Add(agent);
            if (sample.PreviewStatus is null)
            {
                var connection = new SourceConnectionViewModel(new SourceConnectionDto(agent.Id, name, name,
                    DateTimeOffset.UtcNow - sample.LastSeen, null, sample.Folders.Length, false, DateTimeOffset.UtcNow.AddDays(90)));
                SourceConnections.Add(connection);
                agent.Connection = connection;
            }
        }
        NearbyComputers.Add(new(new NearbyComputerDto(Guid.NewGuid(), "NEW-PC", "preview-new-pc", DateTimeOffset.UtcNow.AddSeconds(-30))));
        NearbyComputers.Add(new(new NearbyComputerDto(Guid.NewGuid(), "STUDIO-LAPTOP", "preview-studio-laptop", DateTimeOffset.UtcNow.AddMinutes(-5))));
        SelectedRemoteAgent = Sources.FirstOrDefault();
        NotifyCounts(refreshJobInfo: false);
    }

    private static string PreviewName(string value) => Localization.Source.Culture.TwoLetterISOLanguageName != "en" ? value : value switch
    {
        "사진 보관함" => "Photo archive", "문서" => "Documents", "작업 파일" => "Work files",
        "개발 프로젝트" => "Development projects", "가족 사진" => "Family photos", "음악" => "Music",
        "회계 자료" => "Accounting", "동영상" => "Videos", "스캔 문서" => "Scanned documents",
        "노트" => "Notes", "다운로드" => "Downloads", "업무 자료" => "Business files",
        "가족 PC" => "Family PC", "작업실 PC" => "Studio PC", "노트북" => "Laptop", "새 컴퓨터" => "New computer",
        "사진" => "Photos", _ => value
    };

    private static string PreviewPath(string value) => Localization.Source.Culture.TwoLetterISOLanguageName != "en" ? value
        : value.Replace("사용자", "User").Replace("작업", "Work").Replace("가족", "Family")
            .Replace("회계", "Accounting").Replace("스캔", "Scans").Replace("노트", "Notes").Replace("업무", "Business");

    private static string PreviewTime(string value) => Localization.Source.Culture.TwoLetterISOLanguageName != "en" ? value
        : value.Replace("오늘", "Today").Replace("어제", "Yesterday");

    private void AddDemoSet(RemoteAgentViewModel source, SourceBackupSet model)
    {
        var backupSet = new BackupSetViewModel(model);
        source.BackupSets.Add(backupSet);
        BackupSets.Add(backupSet);
    }

    public async Task SaveAsync()
    {
        // DefaultArrivalDelayMinutes is the only arrival-delay setting left in the tray (the removed
        // Devices tab's per-device editor is gone) - so it must actually govern every device, not just
        // ones registered after it was last changed. Forcing every device's value to match on every save
        // means the model field a device already carries is never silently stale relative to what the
        // screen says is in effect; StorageMonitor.cs still reads it per device, unchanged, since it's a
        // single value everywhere by construction from this point on.
        foreach (var device in Devices) device.ArrivalDelayMinutes = DefaultArrivalDelayMinutes;
        var topology = new StorageAgentConfiguration(
            Devices.Select(device => device.ToModel()).ToArray(),
            BackupSets.Select(set => set.Model).ToArray(),
            Mappings.Select(mapping => mapping.ToModel()).ToArray());
        var errors = BackupTopologyValidator.Validate(topology);
        if (errors.Count > 0)
        {
            FooterStatus = errors[0];
            NotificationRequested?.Invoke(this, new(Localization.Text("Text_Configurationnotsaved_111EDE"), errors[0], true));
            return;
        }
        if (_demoMode)
        {
            FooterStatus = Localization.Text("Text_Democonfigurationvalidatednotp_0E3623");
            AddActivity(Localization.Text("Text_Configurationvalidated_66FB81"));
            return;
        }

        try
        {
            var document = await _configurationClient.UpdateAsync(_configurationRevision, topology, _shutdown.Token);
            AutomaticBackups = _serviceAutomaticBackups = (await _configurationClient.UpdateAutomationAsync(AutomaticBackups, _shutdown.Token)).Enabled;
            UpdateMappingLastBackupInfo();
            _configurationRevision = document.Revision;
            if (_persistLocalState)
            {
                _store.Save(new(topology, StartWithWindows, NotifyOnDeviceArrival, AutomaticBackups, DefaultArrivalDelayMinutes, ShowFlyoutOnBackupStart, _skipDisabledMappingIds.ToArray(), Language));
                ConfigureStartup(StartWithWindows);
            }
            FooterStatus = Localization.Format("Text_SavedtoStorageServiceat0trevis_F376AF", DateTime.Now, document.Revision);
            AddActivity(Localization.Text("Text_ConfigurationsavedtoStorageSer_42EE6B"));
        }
        catch (StorageConfigurationConflictException)
        {
            FooterStatus = Localization.Text("Text_ConfigurationchangedelsewhereR_A78194");
            NotificationRequested?.Invoke(this, new(Localization.Text("Text_Configurationnotsaved_111EDE"), FooterStatus, true));
            await RefreshConfigurationAsync();
        }
        catch (HttpRequestException)
        {
            FooterStatus = Localization.Text("Text_StorageServiceisunavailablecon_6AFB61");
            NotificationRequested?.Invoke(this, new(Localization.Text("Text_Configurationnotsaved_111EDE"), FooterStatus, true));
        }
        catch (TaskCanceledException)
        {
            FooterStatus = Localization.Text("Text_StorageServiceconfigurationsav_29FCF3");
            NotificationRequested?.Invoke(this, new(Localization.Text("Text_Configurationnotsaved_111EDE"), FooterStatus, true));
        }
    }

    private void ApplyTopology(StorageAgentConfiguration topology)
    {
        var selectedSourceId = SelectedRemoteAgent?.Id;
        Devices.Clear();
        BackupSets.Clear();
        Sources.Clear();
        Mappings.Clear();
        foreach (var device in topology.Devices) Devices.Add(new(device));
        foreach (var model in topology.BackupSets.Where(set => set.SourceAgentId == LocalSourceIdentity.AgentId))
        {
            var backupSet = new BackupSetViewModel(model);
            BackupSets.Add(backupSet);
        }
        foreach (var group in topology.BackupSets.Where(set => set.SourceAgentId != LocalSourceIdentity.AgentId).GroupBy(set => new { set.SourceAgentId, set.SourceAgentName }))
        {
            var source = new RemoteAgentViewModel(group.Key.SourceAgentId, group.Key.SourceAgentName);
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
            if (backupSet is not null && device is not null) Mappings.Add(CreateMapping(mapping, backupSet, device));
        }
        SelectedBackupSet = BackupSets.FirstOrDefault();
        SelectedDevice = Devices.FirstOrDefault();
        SelectedRemoteAgent = Sources.FirstOrDefault(source => source.Id == selectedSourceId);
        ApplySourceConnections();
        RefreshDrives();
        RefreshDeviceTriggerRoles();
        NotifyCounts();
        // This is the authoritative rebuild of Mappings (from the Storage Service, not just the local
        // file Load() read before the service was reachable), so this is also the authoritative point to
        // finish restoring anything Skip this time left disabled across a restart and clear the tracking
        // list for good - Load()'s own restore of the same IDs may have applied to a Mappings snapshot
        // that's already being replaced here, so nothing is lost by redoing it once more before persisting.
        if (_skipDisabledMappingIds.Count > 0)
        {
            RestoreSkipDisabledMappingsIntoCurrentSet();
            _skipDisabledMappingIds.Clear();
            _ = SaveAsync();
        }
    }

    // Persists the mapping's own pause toggle immediately (Enabled has no separate "save" step, matching
    // every other in-screen edit this pass made auto-saving), and every other place a MappingViewModel is
    // constructed shares this same callback rather than each wiring up SaveAsync() independently.
    private MappingViewModel CreateMapping(BackupTargetMapping model, BackupSetViewModel set, DeviceViewModel device) =>
        new(model, set, device, mapping => { UpdateMappingLastBackupInfo(); _ = SaveAsync(); });

    internal async Task<string?> SaveMappingAsync(MappingViewModel? existing, BackupSetViewModel? backupSet, DeviceViewModel? device, string destination, bool enabled, IReadOnlyList<string>? selectedSourcePaths = null, int? intervalMinutes = null, bool? delayWhenBusy = null, int? uploadLimitKiBps = null)
    {
        if (backupSet is null || device is null) return Localization.Text("Text_Choosewhattobackupandatargetde_50AF40");
        var repositoryPath = RelativeDestinationPath(device, destination);
        if (repositoryPath is null) return Localization.Text("Text_Chooseadestinationfolderinside_ACC42F");
        if (Mappings.Any(mapping => mapping.Id != existing?.Id
            && mapping.BackupSet.Id == backupSet.Id
            && mapping.Device.Id == device.Id
            && string.Equals(NormalizeRepositoryPath(mapping.RepositoryPath), NormalizeRepositoryPath(repositoryPath), StringComparison.OrdinalIgnoreCase)))
            return Localization.Text("Text_Thatbackuprulealreadyexists_35EA38");

        var candidate = new BackupTargetMapping(existing?.Id ?? Guid.NewGuid(), backupSet.Id, device.Id, repositoryPath, enabled, selectedSourcePaths ?? existing?.SelectedSourcePaths,
            intervalMinutes ?? existing?.BackupIntervalMinutes ?? 30, delayWhenBusy ?? existing?.DelayWhenBusy ?? true,
            uploadLimitKiBps == -1 ? null : uploadLimitKiBps ?? existing?.UploadLimitKiBps);
        if (BackupTopologyValidator.SourcePathsFor(candidate, backupSet.Model) is null)
            return Localization.Text("RulePathSelectionRequired");
        var all = existing is null
            ? Mappings.Select(mapping => mapping.ToModel()).Append(candidate).ToArray()
            : Mappings.Select(mapping => mapping.Id == existing.Id ? candidate : mapping.ToModel()).ToArray();
        var topology = new StorageAgentConfiguration(Devices.Select(device => device.ToModel()).ToArray(), BackupSets.Select(set => set.Model).ToArray(), all);
        var errors = BackupTopologyValidator.Validate(topology);
        if (errors.Count > 0) return errors[0];

        var saved = CreateMapping(candidate, backupSet, device);
        if (existing is null) Mappings.Add(saved);
        else
        {
            var index = Mappings.IndexOf(existing);
            if (index < 0) return Localization.Text("Text_Thebackuprulenolongerexists_6BD0E4");
            Mappings[index] = saved;
        }
        SelectedMapping = saved;
        RemoveUnreferencedDevices();
        RefreshDeviceTriggerRoles();
        NotifyCounts();
        await SaveAsync();
        return null;
    }

    internal async Task<string?> SaveMappingAsync(MappingViewModel? existing, BackupSetViewModel? backupSet, BackupDestinationOptionViewModel? destinationOption, string destination, bool enabled, IReadOnlyList<string>? selectedSourcePaths = null, int? intervalMinutes = null, bool? delayWhenBusy = null, int? uploadLimitKiBps = null)
    {
        if (destinationOption is null) return Localization.Text("Text_Choosewheretostorethebackup_870A83");
        var device = destinationOption.Device;
        var added = false;
        if (device is null && destinationOption.AvailableDrive is { } drive)
        {
            var model = new RegisteredDevice(Guid.NewGuid(), drive.StableId, drive.HardwareName, drive.VolumeLabel, drive.Root,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DefaultArrivalDelayMinutes);
            device = new DeviceViewModel(model)
            {
                CurrentRoot = drive.Root,
                IsConnected = true,
                CanEject = drive.CanEject,
                AvailableBytes = drive.AvailableBytes,
                TotalBytes = drive.TotalBytes
            };
            Devices.Add(device);
            added = true;
        }

        var error = await SaveMappingAsync(existing, backupSet, device, destination, enabled, selectedSourcePaths, intervalMinutes, delayWhenBusy, uploadLimitKiBps);
        if (error is not null && added) Devices.Remove(device!);
        RefreshBackupDestinations();
        return error;
    }

    internal BackupDestinationOptionViewModel AddFolderDestination(string root)
    {
        root = Path.GetFullPath(root);
        var stableId = FolderStorageIdentity.Create(root);
        var existing = Devices.FirstOrDefault(device => string.Equals(device.StableId, stableId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return BackupDestinations.FirstOrDefault(option => option.Device?.Id == existing.Id) ?? new(existing, null);
        long available = 0;
        long total = 0;
        try { var drive = new DriveInfo(Path.GetPathRoot(root) ?? root); available = drive.AvailableFreeSpace; total = drive.TotalSize; }
        catch (Exception exception) when (exception is ArgumentException or IOException) { }
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } folderName ? folderName : root;
        var option = new BackupDestinationOptionViewModel(null, new(stableId, root, Localization.Text("Text_Folder_74CCD4"), available, total, name, 1, false));
        BackupDestinations.Add(option);
        return option;
    }

    internal static string NormalizeRepositoryPath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);

    internal static string? RelativeDestinationPath(DeviceViewModel device, string destination)
    {
        var root = device.CurrentRoot ?? device.LastKnownRoot;
        return RelativeDestinationPath(root, destination);
    }

    internal static string? RelativeDestinationPath(string? root, string destination)
    {
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

    private async Task RemoveMappingAsync()
    {
        if (SelectedMapping is null) return;
        await RemoveMappingsAsync([SelectedMapping]);
    }

    internal async Task RemoveMappingsAsync(IEnumerable<MappingViewModel> mappings)
    {
        var selected = mappings.Distinct().Where(Mappings.Contains).ToArray();
        if (selected.Length == 0) return;
        foreach (var mapping in selected) Mappings.Remove(mapping);
        SelectedMapping = null;
        RemoveUnreferencedDevices();
        RefreshDeviceTriggerRoles();
        NotifyCounts();
        await SaveAsync();
    }

    private void RemoveUnreferencedDevices()
    {
        var referenced = Mappings.Select(mapping => mapping.Device.Id)
            .Concat(BackupSets.SelectMany(set => set.Model.TriggerDeviceIds))
            .ToHashSet();
        foreach (var device in Devices.Where(device => !referenced.Contains(device.Id)).ToArray()) Devices.Remove(device);
        if (SelectedDevice is not null && !Devices.Contains(SelectedDevice)) SelectedDevice = Devices.FirstOrDefault();
        RefreshBackupDestinations();
    }

    // "This PC" needs no pairing, Source Agent, or enable step: choosing a folder here is the entire
    // flow, mirroring RegisterFolder()'s own no-separate-name-prompt pattern.
    private async Task AddLocalBackupSetAsync()
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = Localization.Text("Text_Choosealocalfoldertobackup_3357E4"), ShowNewFolderButton = false };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        var path = Path.GetFullPath(dialog.SelectedPath);
        if (BackupSets.Any(set => set.Model.SourceAgentId == LocalSourceIdentity.AgentId && set.Model.SourcePaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            FooterStatus = Localization.Text("Text_Thatfolderisalreadybeingbacked_D63D12");
            return;
        }
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } folderName ? folderName : path;
        var backupSet = new BackupSetViewModel(new SourceBackupSet(Guid.NewGuid(), LocalSourceIdentity.AgentId, LocalSourceIdentity.DisplayName, name, [path]));
        BackupSets.Add(backupSet);
        SelectedBackupSet = backupSet;
        AddActivity(Localization.Format("Text_Addedabackupfor0Chooseadevicef_85F762", path));
        NotifyCounts();
        await SaveAsync();
    }

    private async Task RemoveLocalBackupSetAsync()
    {
        if (SelectedBackupSet is not { } backupSet || backupSet.Model.SourceAgentId != LocalSourceIdentity.AgentId)
        {
            FooterStatus = Localization.Text("Text_SelectabackupofafolderonthisPC_A6612F");
            return;
        }
        foreach (var mapping in Mappings.Where(mapping => mapping.BackupSet.Id == backupSet.Id).ToArray()) Mappings.Remove(mapping);
        BackupSets.Remove(backupSet);
        SelectedBackupSet = BackupSets.FirstOrDefault();
        RemoveUnreferencedDevices();
        RefreshDeviceTriggerRoles();
        NotifyCounts();
        await SaveAsync();
    }

    private void RefreshDrives()
    {
        var drives = _deviceInventory.GetStorageDevices();
        AvailableDrives.Clear();
        foreach (var drive in drives) AvailableDrives.Add(drive);

        var nowConnected = drives.Select(drive => drive.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in Devices)
        {
            var match = drives.FirstOrDefault(drive => string.Equals(drive.StableId, device.StableId, StringComparison.OrdinalIgnoreCase));
            var folderConnected = FolderStorageIdentity.TryGetPath(device.StableId, out var folderRoot) && Directory.Exists(folderRoot);
            var wasConnected = device.IsConnected;
            device.IsConnected = match is not null || folderConnected;
            device.CanEject = match?.CanEject == true;
            device.CurrentRoot = match?.Root ?? (folderConnected ? folderRoot : null);
            if (match is not null) { device.AvailableBytes = match.AvailableBytes; device.TotalBytes = match.TotalBytes; }
            if (!wasConnected && device.IsConnected)
            {
                device.LastSeenAt = DateTimeOffset.UtcNow;
                device.ConnectedAt = DateTimeOffset.UtcNow;
                AddActivity(Localization.Text("ActivityStorageConnected"), ActivityKind.Storage, device.DisplayName);
                if (NotifyOnDeviceArrival) NotificationRequested?.Invoke(this, new(Localization.Text("Text_Backupstorageconnected_B99FB5"), DeviceArrivalMessage(device.DisplayName, device.ArrivalDelayMinutes)));
            }
            else if (wasConnected && !device.IsConnected)
            {
                device.ConnectedAt = null;
                AddActivity(Localization.Text("ActivityStorageDisconnected"), ActivityKind.Storage, device.DisplayName);
                _ = RestoreSkippedMappings(device.Id);
            }
        }
        _connectedRoots.Clear();
        foreach (var root in nowConnected) _connectedRoots.Add(root);
        RefreshBackupDestinations();
        NotifyCounts();
    }

    private void RefreshBackupDestinations()
    {
        var desired = new List<BackupDestinationOptionViewModel>();
        foreach (var drive in AvailableDrives)
        {
            var registered = Devices.FirstOrDefault(device => string.Equals(device.StableId, drive.StableId, StringComparison.OrdinalIgnoreCase));
            desired.Add(new(registered, drive));
        }
        foreach (var device in Devices.Where(device => desired.All(option => option.Device?.Id != device.Id)))
            desired.Add(new(device, null));
        foreach (var draft in BackupDestinations.Where(option => option.Device is null
            && FolderStorageIdentity.TryGetPath(option.StableId, out _)
            && desired.All(candidate => !string.Equals(candidate.StableId, option.StableId, StringComparison.OrdinalIgnoreCase))))
            desired.Add(draft);

        for (var index = 0; index < desired.Count; index++)
        {
            var candidate = desired[index];
            var currentIndex = BackupDestinations.ToList().FindIndex(option => string.Equals(option.StableId, candidate.StableId, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0) BackupDestinations.Insert(index, candidate);
            else
            {
                var current = BackupDestinations[currentIndex];
                current.Update(candidate.Device, candidate.AvailableDrive);
                if (currentIndex != index) BackupDestinations.Move(currentIndex, index);
            }
        }
        RefreshRuleComputerOptions();
        while (BackupDestinations.Count > desired.Count) BackupDestinations.RemoveAt(BackupDestinations.Count - 1);
    }

    private void RecordJobActivity(BackupJobDto job)
    {
        var (key, kind) = job.State switch
        {
            "RUNNING" => ("ActivityBackupStarted", ActivityKind.Started),
            "SUCCEEDED" => ("ActivityBackupCompleted", ActivityKind.Completed),
            "FAILED" => ("ActivityBackupFailed", ActivityKind.Failed),
            "CANCELLED" => ("ActivityBackupCancelled", ActivityKind.Information),
            _ => (string.Empty, ActivityKind.Information)
        };
        if (key.Length == 0) return;
        var mapping = Mappings.FirstOrDefault(item => item.Id == job.TargetMappingId);
        var detail = mapping is null ? string.Empty : $"{mapping.BackupSetOnlyName} · {mapping.DeviceName}";
        AddActivity(Localization.Text(key), kind, detail, job.State == "RUNNING" ? job.StartedAt ?? job.UpdatedAt : job.UpdatedAt);
    }

    private void AddActivity(string text, ActivityKind kind = ActivityKind.Information, string detail = "", DateTimeOffset? at = null)
    {
        var item = new ActivityItem(text, detail, at ?? DateTimeOffset.Now, kind);
        var index = 0;
        while (index < Activity.Count && Activity[index].At > item.At) index++;
        Activity.Insert(index, item);
        while (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1);
    }

    // The header badge must reflect ConnectedDeviceCount immediately after any action that can change
    // it (forgetting, registering) - not only on the next 3-second RefreshDrives() tick - so every
    // mutation path that used to call NotifyCounts() alone gets the badge update for free here too.
    private void NotifyCounts(bool refreshJobInfo = true)
    {
        OverallStatus = ConnectedDeviceCount > 0 ? Localization.Format("Text_0device1connected_5378A0", ConnectedDeviceCount, (ConnectedDeviceCount == 1 ? "" : "s")) : Localization.Text("Text_Waitingforstorage_AB8528");
        OnPropertyChanged(nameof(ConnectedDeviceCount));
        OnPropertyChanged(nameof(SourceCount));
        OnPropertyChanged(nameof(MappingCount));
        OnPropertyChanged(nameof(RuleHeaderCount));
        OnPropertyChanged(nameof(LaptopName));
        OnPropertyChanged(nameof(LaptopStatus));
        OnPropertyChanged(nameof(DashboardDeviceName));
        OnPropertyChanged(nameof(DashboardDeviceStatus));
        OnPropertyChanged(nameof(DashboardDescription));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(CanQueueAnyBackup));
        OnPropertyChanged(nameof(QueueAllBackupsHint));
        NotifyDashboardTransfer();
        if (refreshJobInfo) UpdateMappingLastBackupInfo();
    }

    private void NotifyDashboardTransfer()
    {
        OnPropertyChanged(nameof(LaptopName));
        OnPropertyChanged(nameof(LaptopStatus));
        OnPropertyChanged(nameof(DashboardDeviceName));
        OnPropertyChanged(nameof(DashboardDeviceStatus));
        OnPropertyChanged(nameof(IsDashboardTransferActive));
        OnPropertyChanged(nameof(IsRemoteDashboardTransferActive));
        OnPropertyChanged(nameof(DashboardTransferPercent));
        OnPropertyChanged(nameof(IsDashboardTransferIndeterminate));
        OnPropertyChanged(nameof(DashboardTransferLabel));
    }

    private static void ConfigureStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return;
        key.DeleteValue("BackupMesh.Storage.Agent", throwOnMissingValue: false);
        if (enabled) key.SetValue("BackupMesh Storage Agent", BuildStartupCommand(AppContext.BaseDirectory, Environment.ProcessPath));
        else key.DeleteValue("BackupMesh Storage Agent", throwOnMissingValue: false);
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
        ? Localization.Format("Text_0isconnectedandreadyforbackup_85786B", displayName)
        : Localization.Format("Text_0isconnectedBackupsbecomeeligi_11599A", displayName, arrivalDelayMinutes);

    internal static string Pluralize(int count, string singularNoun) => Localization.Count(count, singularNoun);

    // Shared by every "how long ago" display in the tray (computer last-seen, and the Backups grid's Last
    // backup column) so "just now" vs. "6 days ago" phrasing - and its threshold for falling back to an
    // absolute date past 30 days - only needs deciding once.
    internal static string RelativeTimeDisplay(DateTimeOffset at)
    {
        var elapsed = DateTimeOffset.UtcNow - at;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed switch
        {
            { TotalSeconds: < 60 } => Localization.Text("Text_justnow_7DDB44"),
            { TotalMinutes: < 60 } => Localization.Format("TimeAgo", Pluralize((int)elapsed.TotalMinutes, "minute")),
            { TotalHours: < 24 } => Localization.Format("TimeAgo", Pluralize((int)elapsed.TotalHours, "hour")),
            { TotalDays: < 30 } => Localization.Format("TimeAgo", Pluralize((int)elapsed.TotalDays, "day")),
            _ => at.LocalDateTime.ToString("g", Localization.Source.Culture)
        };
    }

    public void Dispose()
    {
        Localization.LanguageChanged -= OnLanguageChanged;
        _shutdown.Cancel();
        _deviceTimer.Stop();
        _catalogTimer.Stop();
        _jobTimer.Stop();
        _nearbyTimer.Stop();
        if (_catalogClient is IDisposable disposable) disposable.Dispose();
        if (_configurationClient is IDisposable configurationDisposable) configurationDisposable.Dispose();
        if (_jobClient is IDisposable jobDisposable) jobDisposable.Dispose();
        if (_pairingClient is IDisposable pairingDisposable) pairingDisposable.Dispose();
        if (_connectionsClient is IDisposable connectionsDisposable) connectionsDisposable.Dispose();
        if (_nearbyPairingClient is IDisposable nearbyDisposable) nearbyDisposable.Dispose();
        _shutdown.Dispose();
    }
}

// Doubles as a row in the merged Source Agents grid. Connection is null for "This PC"
// (no certificate/connection of its own) and for any computer
// that hasn't connected since Storage started - LastSeenDisplay/StatusDisplay fall back to "-" rather
// than leaving the cell blank or, worse, putting a non-status value like "This PC" in the Status column.
public sealed class RemoteAgentViewModel(Guid id, string displayName) : ObservableObject
{
    private SourceConnectionViewModel? _connection;
    internal string? PreviewStatus { get; init; }
    public Guid Id { get; } = id;
    public string ComputerIdDisplay => "PC-" + Id.ToString("N")[..7].ToUpperInvariant();
    public bool HasConnection => Connection is not null;
    public string DisplayName => Id == LocalSourceIdentity.AgentId ? Localization.Text("ThisPC") : displayName;
    // State why Address and Status do not apply to the local source.
    public string DisplayNameWithHint => Id == LocalSourceIdentity.AgentId ? Localization.Format("Text_0noagentneeded_182A79", DisplayName) : DisplayName;
    public ObservableCollection<BackupSetViewModel> BackupSets { get; } = [];
    public SourceConnectionViewModel? Connection
    {
        get => _connection;
        set
        {
            if (!Set(ref _connection, value)) return;
            OnPropertyChanged(nameof(LastSeenDisplay));
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(ListStatusDisplay));
            OnPropertyChanged(nameof(ListStatusBackground));
            OnPropertyChanged(nameof(HasConnection));
            OnPropertyChanged(nameof(AddressDisplay));
        }
    }
    public string LastSeenDisplay => Connection?.LastSeenDisplay ?? "—";
    public string StatusDisplay => PreviewStatus is null ? Connection?.StatusDisplay ?? "—" : Localization.Text(PreviewStatus);
    public string ListStatusDisplay => PreviewStatus is not null ? Localization.Text(PreviewStatus)
        : Connection?.IsOnline == true ? Localization.Text("UX_70716b3e6e") : Connection?.StatusDisplay ?? "—";
    public System.Windows.Media.Brush ListStatusBackground => PreviewStatus is not null ? System.Windows.Media.Brushes.LemonChiffon
        : Connection?.IsOnline == true ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(225, 249, 245))
        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 246, 252));
    public System.Windows.Media.Brush ListStatusDotBrush => PreviewStatus is not null ? System.Windows.Media.Brushes.Orange
        : Connection?.IsOnline == true ? System.Windows.Media.Brushes.Teal : System.Windows.Media.Brushes.SlateGray;
    public string AddressDisplay => Connection?.AddressDisplay ?? "—";
    // "Offers" (formerly "Paths served") - the names of this computer's own Backup Sets. "Backup Set" is
    // this tray's own internal grouping; a person configuring the other end thinks of these as folders.
    public string PathsServedDisplay => BackupSets.Count == 0 ? "—" : string.Join(", ", BackupSets.Select(set => set.Model.Name));
    // UI Automation reads Name from ToString(); DisplayMemberPath and item templates do not apply to it.
    public override string ToString() => DisplayName;
}

public sealed class SourceConnectionViewModel(SourceConnectionDto model) : ObservableObject
{
    public Guid AgentId { get; } = model.AgentId;
    public string AgentName { get; } = model.AgentName;
    public string ReportedAgentName { get; } = model.ReportedAgentName;
    public DateTimeOffset LastSeenAt { get; } = model.LastSeenAt;
    // Relative phrasing makes recent connectivity easier to recognize than an absolute timestamp.
    public string LastSeenDisplay => MainWindowViewModel.RelativeTimeDisplay(LastSeenAt);
    // Captured server-side at the same moment as LastSeenAt (the Source's most recent catalog upload), so
    // the two describe the same event rather than two different points in time. DHCP-mutable and shown for
    // context only - the certificate fingerprint below is this Source's actual identity.
    public string AddressDisplay => model.Address ?? "—";
    public int BackupSetCount { get; } = model.BackupSetCount;
    public bool IsRevoked { get; } = model.Revoked;
    public DateTimeOffset? CertificateExpiresAt { get; } = model.CertificateExpiresAt;
    // The Source Agent renews its own certificate starting 30 days before expiry (checked at the start
    // of every backup and once a day
    // while watching), so a healthy, regularly-connecting computer never approaches expiry at all. The
    // only case that actually needs a person is a computer that hasn't been seen since that renewal
    // window opened - it has missed its own chance to renew and genuinely needs Re-pair.
    // The Source Agent's watch loop polls every 5 seconds by default (poll-interval), so a genuinely live
    // agent's LastSeenAt is always at most a few seconds old; 2 minutes gives generous slack for a slower
    // configured interval or a brief hiccup without claiming "Connected" for a computer that has actually
    // gone quiet.
    private static readonly TimeSpan OnlineThreshold = TimeSpan.FromMinutes(2);

    public bool IsOnline
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return !IsRevoked && (CertificateExpiresAt is not { } expires ||
                (expires > now && (now < expires.AddDays(-30) || LastSeenAt >= expires.AddDays(-30))))
                && now - LastSeenAt <= OnlineThreshold;
        }
    }

    public string StatusDisplay
    {
        get
        {
            if (IsRevoked) return Localization.Text("Text_Revoked_F6F738");
            var now = DateTimeOffset.UtcNow;
            if (CertificateExpiresAt is { } expires)
            {
                var renewalWindowStart = expires.AddDays(-30);
                if (expires <= now) return Localization.Text("Text_Expiredrepairtoreconnect_ECB494");
                // Must also check whether the renewal window has actually opened yet - otherwise a
                // healthy computer seen minutes ago, with a certificate not due for renewal for months,
                // reads "hasn't been seen since {a still-future date}" as true and wrongly warns.
                if (now >= renewalWindowStart && LastSeenAt < renewalWindowStart) return Localization.Format("Text_Offlinerepairbefore0d_A7CF69", expires.LocalDateTime);
            }
            return IsOnline ? Localization.Text("Text_Connected_229655") : Localization.Text("Text_Offline_A17947");
        }
    }
    public string DisplayName => Localization.Format("Text_01lastseen2_55F71D", AgentName, StatusDisplay, LastSeenDisplay);
    // UI Automation reads Name from ToString(); DisplayMemberPath and item templates do not apply to it.
    public override string ToString() => DisplayName;
}

// Discovery supplies an unverified label and address hint only. Trust starts after remote approval and
// authenticated pairing, when this row moves to the connected list above.
public sealed class NearbyComputerViewModel(NearbyComputerDto model)
{
    public Guid AgentId => model.AgentId;
    public string ComputerIdDisplay => "PC-" + AgentId.ToString("N")[..7].ToUpperInvariant();
    public string Identity => model.Identity;
    public string DisplayName => model.AgentName;
    public string LastSeenDisplay => MainWindowViewModel.RelativeTimeDisplay(model.LastSeenAt);
    public string DiscoveryStatus => Localization.Text("UX_4b197f0a06");
    public string EmptyOffer => "—";
}

public sealed class BackupSetViewModel : ObservableObject
{
    private SourceBackupSet _model;
    private bool _isAvailable = true;
    public BackupSetViewModel(SourceBackupSet model) => _model = model;
    public SourceBackupSet Model => _model;
    public Guid Id => Model.Id;
    public bool IsAvailable { get => _isAvailable; set { if (Set(ref _isAvailable, value)) OnPropertyChanged(nameof(DisplayName)); } }
    public string SourceDisplayName => Model.SourceAgentId == LocalSourceIdentity.AgentId ? Localization.Text("ThisPC") : Model.SourceAgentName;
    public string DisplayName => $"{SourceDisplayName} / {Model.Name}{(IsAvailable ? string.Empty : Localization.Text("Text_notreported_EAAB4F"))}";
    public string SourcePathsDisplay => string.Join(Environment.NewLine, Model.SourcePaths);
    public string IconGlyph => Model.Name.ToLowerInvariant() switch
    {
        var name when name.Contains("사진") || name.Contains("photo") || name.Contains("picture") => "▧",
        var name when name.Contains("문서") || name.Contains("document") => "▤",
        var name when name.Contains("동영상") || name.Contains("video") => "▶",
        var name when name.Contains("음악") || name.Contains("music") => "♫",
        var name when name.Contains("개발") || name.Contains("project") => "⌘",
        var name when name.Contains("회계") => "▥",
        var name when name.Contains("노트") || name.Contains("note") => "≡",
        var name when name.Contains("다운로드") || name.Contains("download") => "↓",
        _ => "▰"
    };
    public System.Windows.Media.Brush IconBrush => IconGlyph switch
    {
        "▧" => System.Windows.Media.Brushes.MediumSeaGreen,
        "▤" => System.Windows.Media.Brushes.DodgerBlue,
        "▶" or "⌘" => System.Windows.Media.Brushes.MediumPurple,
        "♫" => System.Windows.Media.Brushes.DeepPink,
        "▥" => System.Windows.Media.Brushes.IndianRed,
        "≡" => System.Windows.Media.Brushes.Goldenrod,
        "↓" => System.Windows.Media.Brushes.DeepSkyBlue,
        _ => System.Windows.Media.Brushes.Goldenrod
    };
    public void Update(SourceBackupSet model)
    {
        _model = model;
        IsAvailable = true;
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SourcePathsDisplay));
        OnPropertyChanged(nameof(IconGlyph));
        OnPropertyChanged(nameof(IconBrush));
    }

    // UI Automation reads Name from ToString(); DisplayMemberPath and item templates do not apply to it.
    public override string ToString() => DisplayName;
}

public sealed class BackupDestinationOptionViewModel(DeviceViewModel? device, AvailableDriveViewModel? availableDrive) : ObservableObject
{
    public DeviceViewModel? Device { get; private set; } = device;
    public AvailableDriveViewModel? AvailableDrive { get; private set; } = availableDrive;
    public string StableId => Device?.StableId ?? AvailableDrive!.StableId;
    public string Root => AvailableDrive?.Root ?? Device?.CurrentRoot ?? Device?.LastKnownRoot ?? string.Empty;
    public string DisplayName => AvailableDrive?.DisplayName ?? Localization.Format("Text_0notconnected_78FCB7", Device!.DisplayNameWithDetails);
    public void Update(DeviceViewModel? updatedDevice, AvailableDriveViewModel? updatedDrive)
    {
        var stableId = StableId;
        Device = updatedDevice;
        AvailableDrive = updatedDrive;
        if (!string.Equals(stableId, StableId, StringComparison.OrdinalIgnoreCase)) OnPropertyChanged(nameof(StableId));
        OnPropertyChanged(nameof(Root));
        OnPropertyChanged(nameof(DisplayName));
    }
    public override string ToString() => DisplayName;
}

public sealed class DeviceViewModel : ObservableObject
{
    private bool _isConnected;
    private bool _canEject;
    private string? _currentRoot;
    private DateTimeOffset? _lastSeenAt;
    private bool _isSourceTrigger;
    private bool _isUsedAsTarget;
    private long? _availableBytes;
    private long? _totalBytes;
    public DeviceViewModel(RegisteredDevice model) { Id = model.Id; StableId = model.StableId; DisplayName = model.DisplayName; VolumeLabel = model.VolumeLabel; LastKnownRoot = model.LastKnownRoot; RegisteredAt = model.RegisteredAt; _lastSeenAt = model.LastSeenAt; ArrivalDelayMinutes = model.ArrivalDelayMinutes; }
    public Guid Id { get; }
    public string StableId { get; }
    public string DisplayName { get; }
    public string? VolumeLabel { get; }
    public string? LastKnownRoot { get; }
    public DateTimeOffset RegisteredAt { get; }
    public DateTimeOffset? LastSeenAt { get => _lastSeenAt; set { Set(ref _lastSeenAt, value); OnPropertyChanged(nameof(LastSeenDisplay)); } }
    public bool IsConnected { get => _isConnected; set { Set(ref _isConnected, value); OnPropertyChanged(nameof(Status)); } }
    // When this device was last observed to transition from disconnected to connected - not persisted, so
    // for a device already connected when the app starts this is the app's start time, not the drive's
    // true plug-in time. The removal banner only counts jobs that started at or after this timestamp, so
    // days-old, already-persisted job history for that device can't read as "just finished" the moment
    // the app happens to notice it; a genuinely new job that starts and completes after that still counts.
    public DateTimeOffset? ConnectedAt { get; set; }
    public bool CanEject { get => _canEject; set => Set(ref _canEject, value); }
    public string? CurrentRoot { get => _currentRoot; set { if (Set(ref _currentRoot, value)) OnPropertyChanged(nameof(DisplayNameWithDetails)); } }
    // Not persisted to RegisteredDevice/config - this is live capacity, refreshed opportunistically
    // alongside RefreshDrives() while the device is connected, the same source the pre-registration
    // AvailableDriveViewModel.DisplayName uses the same capacity source before registration.
    public long? AvailableBytes { get => _availableBytes; set { if (Set(ref _availableBytes, value)) { OnPropertyChanged(nameof(FreeSpaceDisplay)); OnPropertyChanged(nameof(DisplayNameWithDetails)); } } }
    public long? TotalBytes { get => _totalBytes; set { if (Set(ref _totalBytes, value)) OnPropertyChanged(nameof(FreeSpaceDisplay)); } }
    public string FreeSpaceDisplay => AvailableBytes is { } bytes ? Localization.Format("Text_000GBfree_D5E243", bytes / 1_073_741_824d) : "—";

    // Mirror AvailableDriveViewModel.DisplayName's "(root), n GB free" pattern after registration.
    public string DisplayNameWithDetails => CurrentRoot is { Length: > 0 } root
        ? AvailableBytes is { } bytes ? Localization.Format("Text_01200GBfree_964D13", DisplayName, root, bytes / 1_073_741_824d) : $"{DisplayName} ({root})"
        : DisplayName;
    // Use name and drive letter only in the removal banner; capacity belongs in device selection/status UI.
    public string DisplayNameWithRoot => CurrentRoot is { Length: > 0 } root ? $"{DisplayName} ({root})" : DisplayName;
    public string Status => IsConnected ? Localization.Text("Text_Connected_229655") : Localization.Text("Text_Offline_A17947");
    public string LastSeenDisplay => LastSeenAt?.LocalDateTime.ToString("g") ?? Localization.Text("Text_Never_6300EF");
    public int ArrivalDelayMinutes { get; set; }
    // Explicitly set only when a Backup Set names this device as its trigger, or a mapping targets it -
    // never inferred, per the same "don't let the UI guess" principle as the arrival logic itself.
    public bool IsSourceTrigger { get => _isSourceTrigger; set { if (Set(ref _isSourceTrigger, value)) OnPropertyChanged(nameof(RoleDisplay)); } }
    public bool IsUsedAsTarget { get => _isUsedAsTarget; set { if (Set(ref _isUsedAsTarget, value)) OnPropertyChanged(nameof(RoleDisplay)); } }
    public string RoleDisplay => (IsUsedAsTarget, IsSourceTrigger) switch
    {
        (true, true) => Localization.Text("Text_TargetTrigger_EAE2DB"),
        (false, true) => Localization.Text("Text_Trigger_8B9C64"),
        (true, false) => Localization.Text("Text_Target_978354"),
        (false, false) => Localization.Text("Text_Unassigned_14D33B")
    };
    public RegisteredDevice ToModel() => new(Id, StableId, DisplayName, VolumeLabel, CurrentRoot ?? LastKnownRoot, RegisteredAt, LastSeenAt, ArrivalDelayMinutes);

    // UI Automation reads Name from ToString(); DisplayMemberPath and item templates do not apply to it.
    public override string ToString() => DisplayName;
}

// Keep Source and Target symmetric so both halves of the mapping remain visible.
public sealed class MappingViewModel : ObservableObject
{
    private readonly Action<MappingViewModel>? _onEnabledChanged;
    private bool _enabled;
    private bool _isRepeatOfPreviousSource;
    private bool _isRepeatOfPreviousSourceFolder;
    private string _lastBackupDisplay = Localization.Text("Text_Never_6300EF");
    private DateTimeOffset? _lastBackupAt;
    private string _lastBackupIssue = string.Empty;
    private string _triggerNote = string.Empty;
    private string _nextBackupDisplay = string.Empty;

    public MappingViewModel(BackupTargetMapping model, BackupSetViewModel set, DeviceViewModel device, Action<MappingViewModel>? onEnabledChanged = null)
    {
        Id = model.Id;
        BackupSet = set;
        Device = device;
        RepositoryPath = model.RepositoryPath;
        SelectedSourcePaths = model.SelectedSourcePaths;
        BackupIntervalMinutes = model.BackupIntervalMinutes;
        DelayWhenBusy = model.DelayWhenBusy;
        UploadLimitKiBps = model.UploadLimitKiBps;
        _enabled = model.Enabled;
        _onEnabledChanged = onEnabledChanged;
    }

    public Guid Id { get; }
    public BackupSetViewModel BackupSet { get; }
    public DeviceViewModel Device { get; }
    public string BackupSetName => BackupSet.DisplayName;
    public string SourceAgentName => BackupSet.SourceDisplayName;
    public string BackupSetOnlyName => BackupSet.Model.Name;
    public IReadOnlyList<string>? SelectedSourcePaths { get; }
    public int BackupIntervalMinutes { get; }
    public bool DelayWhenBusy { get; }
    public int? UploadLimitKiBps { get; }
    public string SourcePathsDisplay => string.Join(" · ", SelectedSourcePaths ?? BackupSet.Model.SourcePaths);
    public string DeviceName => Device.DisplayName;
    public string RepositoryPath { get; }
    public string DestinationFolder => Path.GetFullPath(Path.Combine(Device.LastKnownRoot ?? string.Empty, RepositoryPath));
    // A real pause toggle, not a read-only status dot - previously nothing in the UI ever changed this
    // after a mapping was created, so the column only ever displayed "true". Persists immediately, same
    // as every other in-screen edit this pass made auto-saving.
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { OnPropertyChanged(nameof(StatusDisplay)); OnPropertyChanged(nameof(RuleStatusBrush)); _onEnabledChanged?.Invoke(this); } } }
    public string StatusDisplay => !Enabled ? Localization.Text("UX_13293e6028")
        : LastBackupDisplay == Localization.Text("Text_Backingupnow_CF4F1D") ? Localization.Text("Text_Backingupnow_632CAE")
        : LastBackupIssue.Length > 0 ? Localization.Text("UX_5982e28d0e")
        : LastBackupDisplay == Localization.Text("Text_Never_6300EF") ? Localization.Text("State_ACCEPTED") : Localization.Text("UX_8d8680373c");
    public System.Windows.Media.Brush RuleStatusBrush => !Enabled ? System.Windows.Media.Brushes.SlateGray
        : LastBackupDisplay == Localization.Text("Text_Backingupnow_CF4F1D") ? System.Windows.Media.Brushes.DodgerBlue
        : LastBackupIssue.Length > 0 ? System.Windows.Media.Brushes.DarkOrange
        : LastBackupDisplay == Localization.Text("Text_Never_6300EF") ? System.Windows.Media.Brushes.SlateGray
        : System.Windows.Media.Brushes.SeaGreen;
    // For a caller that's about to toggle several mappings at once and save them together (e.g.
    // MainWindowViewModel.SkipDeviceThisConnection) - the normal setter's per-mapping auto-save would fire
    // one overlapping SaveAsync() per mapping, and a config revision conflict on any but the first could
    // leave a mapping toggled in memory but not actually persisted to the server that enforces it.
    internal void SetEnabledWithoutSaving(bool value) { if (Set(ref _enabled, value)) { OnPropertyChanged(nameof(StatusDisplay)); OnPropertyChanged(nameof(RuleStatusBrush)); } }
    // Consecutive rows sharing the same Source, or the same Source folder, are flagged here so the view
    // can dim the repeated text - recomputed for the whole list by
    // MainWindowViewModel.UpdateMappingRepeatMarkers() whenever Mappings changes, never inferred by the
    // view itself. The view repeats the real text with muted styling rather than substituting a marker.
    public bool IsRepeatOfPreviousSource { get => _isRepeatOfPreviousSource; set => Set(ref _isRepeatOfPreviousSource, value); }
    public bool IsRepeatOfPreviousSourceFolder { get => _isRepeatOfPreviousSourceFolder; set => Set(ref _isRepeatOfPreviousSourceFolder, value); }
    // Set by MainWindowViewModel.UpdateMappingLastBackupInfo() from the job list - a mapping does not hold
    // its own reference to Jobs, so this is pushed in rather than computed here.
    public string LastBackupDisplay { get => _lastBackupDisplay; set { if (Set(ref _lastBackupDisplay, value)) { OnPropertyChanged(nameof(StatusDisplay)); OnPropertyChanged(nameof(RuleStatusBrush)); } } }
    public DateTimeOffset? LastBackupAt { get => _lastBackupAt; set => Set(ref _lastBackupAt, value); }
    public string LastBackupIssue { get => _lastBackupIssue; set { if (Set(ref _lastBackupIssue, value)) { OnPropertyChanged(nameof(StatusDisplay)); OnPropertyChanged(nameof(RuleStatusBrush)); } } }
    // Read-only surface for a Backup Set's TriggerDeviceIds/TriggerPolicy, set by
    // MainWindowViewModel.UpdateMappingTriggerNotes() - the per-row editor for these is gone (peer
    // review: its default, starting when the mapped destination connects, covers the ordinary case with
    // no user choice needed), but a Backup Set that already names an explicit trigger device (the
    // external-source-arrival case from USER_GUIDE 6, or a config written before this pass) still only
    // starts for that device, regardless of Target. Without this line the grid would silently imply
    // "starts when Target connects" for a row that in fact does not.
    public string TriggerNote { get => _triggerNote; set => Set(ref _triggerNote, value); }
    public string NextBackupDisplay { get => _nextBackupDisplay; set => Set(ref _nextBackupDisplay, value); }
    public BackupTargetMapping ToModel() => new(Id, BackupSet.Id, Device.Id, RepositoryPath, Enabled, SelectedSourcePaths, BackupIntervalMinutes, DelayWhenBusy, UploadLimitKiBps);
}

public sealed record AvailableDriveViewModel(string StableId, string Root, string VolumeLabel, long AvailableBytes, long TotalBytes, string HardwareName, int VolumeCount, bool CanEject = false)
{
    public string DisplayName => Localization.Format("Text_012300GBfree_F81620", HardwareName, VolumeLabel, Root, AvailableBytes / 1_073_741_824d);

    // Without this the compiler-generated record ToString() becomes the UI Automation Name, leaking
    // StableId and volume serials to screen readers. DisplayMemberPath does not affect the UIA Name.
    public override string ToString() => DisplayName;

    public static AvailableDriveViewModel FromDrive(DriveInfo drive)
    {
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? Localization.Text("Text_Localdisk_8C7556") : drive.VolumeLabel;
        // The production Windows provider replaces this provisional identifier with volume GUID + hardware identity.
        var stableId = $"{drive.DriveFormat}|{label}|{drive.TotalSize}";
        return new(stableId, drive.RootDirectory.FullName, label, drive.AvailableFreeSpace, drive.TotalSize, label, 1);
    }
}

// SkipDisabledMappingIds must survive a restart even though "this connection only" is the whole promise
// of the flyout's Skip this time - the in-memory tracking set that normally restores them on disconnect
// is gone if the app exits (crash, forced close, or simply quitting) before that disconnect happens, and
// without this, the mapping stays persisted as Enabled=false forever with nothing in the UI explaining why
// because silently leaving a backup disabled after restart would be unsafe.
public sealed record AppConfiguration(StorageAgentConfiguration Topology, bool StartWithWindows = true, bool NotifyOnDeviceArrival = true, bool AutomaticBackups = true, int DefaultArrivalDelayMinutes = 1, bool ShowFlyoutOnBackupStart = true, IReadOnlyList<Guid>? SkipDisabledMappingIds = null, string Language = "");

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
    public void RefreshText() => OnPropertyChanged(string.Empty);
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

public sealed class RelayCommand<T>(Action<T> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { if (parameter is T value) execute(value); }
}
