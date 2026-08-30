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
    // Mappings this session paused via the tray flyout's "Skip this time" - tracked separately from a
    // mapping the user disabled deliberately and permanently via the Backups grid's own checkbox, so
    // reconnecting the device only ever restores the ones *this* skip touched, never a mapping that was
    // already off before the skip happened.
    private readonly HashSet<Guid> _skipDisabledMappingIds = [];
    private BackupSetViewModel? _selectedBackupSet;
    private SourceAgentViewModel? _selectedSourceAgent;
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
    // Every first-click study measurement (18/18) failed the "confirm the backup finished, then safely
    // remove the drive" task, because the confirmation and the action lived on different tabs no matter
    // how those tabs were named or organized. This surfaces both, together, wherever the user already is.
    public ObservableCollection<DeviceRemovalBannerViewModel> RemovalBanners { get; } = [];

    public event EventHandler<AppNotification>? NotificationRequested;
    public event EventHandler<string>? StatusChanged;

    public ICommand AddMappingCommand { get; }
    public ICommand BrowseDestinationCommand { get; }
    public ICommand RemoveMappingCommand { get; }
    public ICommand RefreshDrivesCommand { get; }
    public ICommand RegisterDeviceCommand { get; }
    public ICommand RegisterFolderCommand { get; }
    public ICommand OpenRegisterDeviceDialogCommand { get; }
    public ICommand ForgetDeviceCommand { get; }
    public ICommand AddLocalBackupSetCommand { get; }
    public ICommand RemoveLocalBackupSetCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelJobCommand { get; }
    public ICommand EjectDeviceCommand { get; }
    public ICommand SafelyRemoveDeviceCommand { get; }
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
        AddMappingCommand = new RelayCommand(() => _ = AddMappingAsync());
        BrowseDestinationCommand = new RelayCommand(BrowseDestination);
        RemoveMappingCommand = new RelayCommand(() => _ = RemoveMappingAsync());
        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        RegisterDeviceCommand = new RelayCommand(() => _ = RegisterDeviceAsync());
        RegisterFolderCommand = new RelayCommand(() => _ = RegisterFolderAsync());
        OpenRegisterDeviceDialogCommand = new RelayCommand(OpenRegisterDeviceDialog);
        ForgetDeviceCommand = new RelayCommand(() => _ = ForgetDeviceAsync());
        AddLocalBackupSetCommand = new RelayCommand(() => _ = AddLocalBackupSetAsync());
        RemoveLocalBackupSetCommand = new RelayCommand(() => _ = RemoveLocalBackupSetAsync());
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        CancelJobCommand = new RelayCommand(() => _ = CancelSelectedJobAsync());
        EjectDeviceCommand = new RelayCommand(() => _ = EjectSelectedDeviceAsync());
        SafelyRemoveDeviceCommand = new RelayCommand<DeviceViewModel>(device => _ = EjectDeviceAsync(device));
        PairSourceCommand = new RelayCommand(() => _ = PairSourceAsync(rebind: null));
        RePairSourceCommand = new RelayCommand(() => _ = PairSourceAsync(rebind: SelectedSourceConnection));
        RevokeSourceCommand = new RelayCommand(() => _ = SetSourceRevocationAsync(revoked: true));
        UnrevokeSourceCommand = new RelayCommand(() => _ = SetSourceRevocationAsync(revoked: false));
        RenameSourceCommand = new RelayCommand(() => _ = RenameSelectedSourceAsync());
        ForgetSourceCommand = new RelayCommand(() => _ = ForgetSelectedSourceAsync());
        RotateStorageIdentityCommand = new RelayCommand(() => _ = RotateStorageIdentityAsync());
        // Recomputed from Mappings itself, not from each call site that mutates it, so a test (or any
        // future caller) that adds/removes a mapping directly never needs to know this bookkeeping exists.
        Mappings.CollectionChanged += (_, _) => UpdateMappingRepeatMarkers();
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

    // A status message from an action on one tab (e.g. "One-time pairing details generated…") otherwise
    // keeps following the user to unrelated tabs, since the footer is a single shared piece of state.
    public void ClearFooterStatusOnTabChange() => FooterStatus = string.Empty;
    public int ConnectedDeviceCount => Devices.Count(device => device.IsConnected);
    public int SourceCount => Sources.Count;
    public int MappingCount => Mappings.Count(mapping => mapping.Enabled);
    public BackupSetViewModel? SelectedBackupSet { get => _selectedBackupSet; set => Set(ref _selectedBackupSet, value); }
    // The merged Computers grid selects a computer directly; picking one resolves (or clears) the
    // connection the action buttons act on, replacing what used to be a separate tree-selection handler
    // in code-behind now that there is only one list to select from.
    public SourceAgentViewModel? SelectedSourceAgent
    {
        get => _selectedSourceAgent;
        set
        {
            if (!Set(ref _selectedSourceAgent, value)) return;
            SelectedSourceConnection = value is null ? null : SourceConnections.FirstOrDefault(connection => connection.AgentId == value.Id);
            OnPropertyChanged(nameof(SelectedComputerActionHint));
        }
    }
    public SourceConnectionViewModel? SelectedSourceConnection { get => _selectedSourceConnection; set { if (Set(ref _selectedSourceConnection, value)) { OnPropertyChanged(nameof(HasSelectedSourceConnection)); OnPropertyChanged(nameof(SelectedComputerActionHint)); } } }
    public bool HasSelectedSourceConnection => SelectedSourceConnection is not null;
    // Visible explanation (not just a tooltip) for why Block access/Re-pair/Rename/Remove Source Agent are
    // disabled for the current selection - evaluators guessed correctly that "This PC" disables them, but
    // weren't sure, and had no way to tell a not-yet-connected Source Agent from one that would never work.
    public string SelectedComputerActionHint => SelectedSourceAgent switch
    {
        null => string.Empty,
        { Id: var id } when id == LocalSourceIdentity.AgentId => "This PC has no Source Agent to manage - these actions apply only to other rows.",
        _ when SelectedSourceConnection is null => "This Source Agent hasn't connected yet, so there's nothing to manage here.",
        _ => string.Empty
    };
    public DeviceViewModel? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }
    public AvailableDriveViewModel? SelectedAvailableDrive { get => _selectedAvailableDrive; set => Set(ref _selectedAvailableDrive, value); }
    public MappingViewModel? SelectedMapping { get => _selectedMapping; set => Set(ref _selectedMapping, value); }
    public BackupJobViewModel? SelectedJob { get => _selectedJob; set => Set(ref _selectedJob, value); }
    public string NewDestinationFolder { get => _newDestinationFolder; set => Set(ref _newDestinationFolder, value); }
    public bool StartWithWindows { get; set; } = true;
    public bool NotifyOnDeviceArrival { get; set; } = true;
    public bool AutomaticBackups { get; set; } = true;
    // Replaces the Devices tab's per-device arrival-delay editor (removed with that tab): one global
    // default - the only one left in the tray - rather than a setting a person had to think to revisit
    // per device. It governs every device, not just newly-registered ones: an already-registered device
    // has no screen left to show or change its own value, so leaving that value in effect after this
    // changed would silently disagree with what the screen says (the same failure mode the removed
    // trigger-device editor had). SaveAsync() enforces this by writing this value onto every device.
    public int DefaultArrivalDelayMinutes { get; set; } = 30;
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
            foreach (var job in jobs) Jobs.Add(new(job, Mappings.FirstOrDefault(mapping => mapping.Id == job.TargetMappingId)));
            SelectedJob = Jobs.FirstOrDefault(job => job.JobId == selectedId) ?? Jobs.FirstOrDefault();
            UpdateRemovalBanners();
            UpdateMappingLastBackupInfo();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }

    // Recomputed on every job-list refresh (2s) and every drive-connection poll (3s), so a banner reacts
    // to a backup starting or finishing within one of those cycles - the same cadence the rest of the tray
    // already runs on, not a new promise. STORAGE_BUSY blocks an actually-stale removal request server
    // side regardless (ControlApi checks HasActiveJobs itself), so this only needs to be timely, not exact.
    private void UpdateRemovalBanners()
    {
        RemovalBanners.Clear();
        foreach (var device in Devices)
        {
            if (!device.IsConnected || !device.CanEject) continue;
            var jobsForDevice = Jobs.Where(job => job.TargetMappingId is { } mappingId
                && Mappings.FirstOrDefault(mapping => mapping.Id == mappingId)?.Device.Id == device.Id).ToArray();
            // A job in progress is a real-time fact, not a history claim, so this doesn't need ConnectedAt
            // gating - and it must win over "safe", never the reverse: this is a same-line status swap
            // (never disappear, never show both), so it's checked and added first, before anything that
            // could otherwise report "safe" for a device that is, right now, being written to.
            if (jobsForDevice.FirstOrDefault(job => !job.IsTerminal) is { } activeJob) { RemovalBanners.Add(DeviceRemovalBannerViewModel.BackingUp(device, activeJob.EstimatedTimeRemaining)); continue; }
            if (device.ConnectedAt is not { } connectedAt) continue;
            var completedSinceConnecting = jobsForDevice.Where(job => job.StartedAt is { } startedAt && startedAt >= connectedAt).ToArray();
            if (completedSinceConnecting.Length == 0) continue; // nothing happened on this device during this connection - stay quiet rather than claim old history "just finished".
            var anyDidNotFinish = completedSinceConnecting.Any(job => job.State is "FAILED" or "CANCELLED");
            RemovalBanners.Add(DeviceRemovalBannerViewModel.Finished(device, anyDidNotFinish));
        }
    }

    // "Last backup" answers both "when" and, if it's stale for a reason the user can act on, "why" in the
    // same cell - a study found a separate Status column just duplicated the time already shown here. The
    // reason only ever appears when it actually explains staleness (a failed/cancelled attempt, or the
    // source computer being offline), never as a caveat on an otherwise-healthy row.
    private void UpdateMappingLastBackupInfo()
    {
        foreach (var mapping in Mappings)
        {
            mapping.TriggerNote = ComputeTriggerNote(mapping.BackupSet.Model);
            var jobsForMapping = Jobs.Where(job => job.TargetMappingId == mapping.Id).ToArray();
            if (jobsForMapping.Any(job => !job.IsTerminal))
            {
                mapping.LastBackupDisplay = "Backing up now…";
                mapping.LastBackupIssue = string.Empty;
                continue;
            }
            var latest = jobsForMapping.Where(job => job.IsTerminal).OrderByDescending(job => job.UpdatedAt).FirstOrDefault();
            if (latest is null)
            {
                mapping.LastBackupDisplay = "Never";
                mapping.LastBackupIssue = string.Empty;
                continue;
            }
            mapping.LastBackupDisplay = RelativeTimeDisplay(latest.UpdatedAt);
            mapping.LastBackupIssue = latest.State switch
            {
                "FAILED" => "Last attempt failed",
                "CANCELLED" => "Last attempt was cancelled",
                _ when mapping.BackupSet.Model.SourceAgentId != LocalSourceIdentity.AgentId
                    && Sources.FirstOrDefault(source => source.Id == mapping.BackupSet.Model.SourceAgentId)?.StatusDisplay == "Offline"
                    => $"{mapping.SourceAgentName} is offline",
                _ => string.Empty
            };
        }
    }

    // A Backup Set that names an explicit trigger device (peer review: the external-source-arrival case
    // from USER_GUIDE 6, "insert this card and it backs up wherever it's mapped" - or a config authored
    // before the per-row editor was removed) only starts for that device, not "whenever its Target
    // connects" the way an untriggered row's default behavior does. Read-only is enough here: the point is
    // making the existing behavior visible, not re-adding an editor for it.
    private string ComputeTriggerNote(SourceBackupSet set)
    {
        if (set.TriggerDeviceIds.Count == 0) return string.Empty;
        var names = set.TriggerDeviceIds.Select(id => Devices.FirstOrDefault(device => device.Id == id)?.DisplayName ?? "a removed device").ToArray();
        if (names.Length == 1) return $"Starts when {names[0]} connects";
        var connective = set.TriggerPolicy == BackupSetTriggerPolicy.AllAvailable ? "and" : "or";
        var joined = $"{string.Join(", ", names[..^1])} {connective} {names[^1]}";
        return set.TriggerPolicy == BackupSetTriggerPolicy.AllAvailable ? $"Starts when {joined} are all connected" : $"Starts when {joined} connects";
    }

    // Consecutive rows sharing the same Source, or the same Source folder, collapse their repeated cell to
    // a ditto mark - a study found evaluators needed "same folder, two destinations" to read as a
    // relationship between adjacent rows, not as two unrelated rows that happen to repeat text. Recomputed
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
        if (SelectedJob is not { CanCancel: true } job) { FooterStatus = "Select an active backup job first."; return; }
        try
        {
            await _jobClient.CancelAsync(job.JobId, _shutdown.Token);
            FooterStatus = "Cancellation requested. The computer will stop at the next safe point.";
            await RefreshJobsAsync();
        }
        catch (HttpRequestException) { FooterStatus = "The cancellation request could not reach Storage Service."; }
        catch (TaskCanceledException) { FooterStatus = "The cancellation request timed out."; }
    }

    private Task EjectSelectedDeviceAsync()
    {
        if (SelectedDevice is not { IsConnected: true, CanEject: true } device) { FooterStatus = "Select a connected removable device first."; return Task.CompletedTask; }
        return EjectDeviceAsync(device);
    }

    // Shared by the Devices tab's "Safely remove selected device" button and the header removal banner's
    // per-device "Remove safely" button, so both paths get the same STORAGE_BUSY/EJECT_REFUSED handling.
    private async Task EjectDeviceAsync(DeviceViewModel device)
    {
        try
        {
            await _storageDeviceClient.EjectAsync(device.Id, _shutdown.Token);
            FooterStatus = $"Safe-removal requested for {device.DisplayName}.";
            AddActivity(FooterStatus);
        }
        catch (StorageDeviceEjectRefusedException exception)
        {
            // STORAGE_BUSY means the answer is "wait" - the removal banner itself is only ever shown once
            // this client's own job list agrees nothing is active, so seeing this at all means the server
            // knows about a job the client doesn't yet (a race, not a bug) and it will resolve on its own
            // shortly. EJECT_REFUSED means Windows itself said no (e.g. a file still open) and waiting
            // will not fix it - the two need different next actions from the user, so they read
            // differently.
            FooterStatus = exception.Code switch
            {
                "STORAGE_BUSY" => $"Can't remove {device.DisplayName} yet — a backup is still running. Wait a moment and try again.",
                "EJECT_REFUSED" => $"Windows would not remove {device.DisplayName}: {exception.Message} Close any File Explorer window or app using the drive, then try again.",
                _ => $"Safe removal was refused: {exception.Message}"
            };
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
            NotificationRequested?.Invoke(this, new("Computer pairing", FooterStatus));
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
            FooterStatus = catalogs.Count == 0 ? "No computer has reported anything to back up yet." : $"Synchronized {Pluralize(catalogs.Count, "computer")}.";
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
            SourceConnections.Clear();
            foreach (var connection in connections.OrderBy(c => c.AgentName, StringComparer.OrdinalIgnoreCase)) SourceConnections.Add(new(connection));
            ApplySourceConnections();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }

    // Sources is rebuilt from scratch (new SourceAgentViewModel instances) on every catalog/config
    // refresh, so each one's Connection must be re-applied every time too, not just when
    // RefreshConnectionsAsync() itself runs - otherwise a catalog refresh 10 seconds later silently wipes
    // every computer's connection info from the merged grid until the next connections poll.
    private void ApplySourceConnections()
    {
        foreach (var source in Sources) source.Connection = SourceConnections.FirstOrDefault(connection => connection.AgentId == source.Id);
        SelectedSourceConnection = SelectedSourceAgent is null ? null : SourceConnections.FirstOrDefault(connection => connection.AgentId == SelectedSourceAgent.Id);
        UpdateMappingLastBackupInfo();
    }

    private async Task SetSourceRevocationAsync(bool revoked)
    {
        var connection = SelectedSourceConnection;
        if (connection is null) return;
        if (revoked)
        {
            var confirmed = System.Windows.MessageBox.Show(
                $"Block access for \"{connection.AgentName}\"? It will be immediately blocked from sending backups, even with a valid certificate and token. Nothing is deleted - its backups are kept and reporting resumes as soon as you choose Restore access.",
                "Block access", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
            if (!confirmed) return;
        }
        try
        {
            if (revoked) await _connectionsClient.RevokeAsync(connection.AgentId, _shutdown.Token);
            else await _connectionsClient.UnrevokeAsync(connection.AgentId, _shutdown.Token);
            FooterStatus = revoked ? $"Blocked access for {connection.AgentName}." : $"Restored access for {connection.AgentName}.";
            NotificationRequested?.Invoke(this, new("Computer connection", FooterStatus));
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
            FooterStatus = "Computer renamed.";
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
            $"Remove \"{connection.AgentName}\"? Its access will be blocked immediately, and it disappears from this list. Nothing is deleted - its backups are kept and stay listed as unresolved until it is re-paired.",
            "Remove computer", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        if (!confirmed) return;
        try
        {
            await _connectionsClient.ForgetAsync(connection.AgentId, _shutdown.Token);
            FooterStatus = $"Removed {connection.AgentName}. Its backups are preserved as unresolved.";
            NotificationRequested?.Invoke(this, new("Computer connection", FooterStatus));
            await RefreshConnectionsAsync();
            await RefreshCatalogsAsync();
        }
        catch (HttpRequestException exception) { FooterStatus = $"Could not remove {connection.AgentName}: {exception.Message}"; }
        catch (TaskCanceledException) { FooterStatus = $"The request to remove {connection.AgentName} timed out."; }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task RotateStorageIdentityAsync()
    {
        var confirmed = System.Windows.MessageBox.Show(
            "This regenerates the Storage's certificate authority and server certificate. Every currently paired computer will lose access until it is re-paired, and this only takes effect after you restart the BackupMesh Storage service. Continue?",
            "Rotate Storage identity", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        if (!confirmed) return;
        try
        {
            await _pairingClient.RotateAuthorityAsync(_shutdown.Token);
            FooterStatus = "Storage identity rotated. Restart the BackupMesh Storage service, then re-pair every computer.";
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
        var selectedSourceId = SelectedSourceAgent?.Id;
        Sources.Clear();
        // "This PC" always appears first, even with no local Backup Sets yet.
        var localSource = new SourceAgentViewModel(LocalSourceIdentity.AgentId, LocalSourceIdentity.DisplayName);
        foreach (var set in BackupSets.Where(item => item.Model.SourceAgentId == LocalSourceIdentity.AgentId).OrderBy(item => item.Model.Name, StringComparer.OrdinalIgnoreCase))
            localSource.BackupSets.Add(set);
        Sources.Add(localSource);
        foreach (var group in BackupSets.Where(item => item.Model.SourceAgentId != LocalSourceIdentity.AgentId).GroupBy(set => new { set.Model.SourceAgentId, set.Model.SourceAgentName }).OrderBy(group => group.Key.SourceAgentName, StringComparer.OrdinalIgnoreCase))
        {
            var source = new SourceAgentViewModel(group.Key.SourceAgentId, group.Key.SourceAgentName);
            foreach (var set in group.OrderBy(item => item.Model.Name, StringComparer.OrdinalIgnoreCase)) source.BackupSets.Add(set);
            Sources.Add(source);
        }
        SelectedBackupSet ??= BackupSets.FirstOrDefault(set => set.IsAvailable);
        SelectedSourceAgent = Sources.FirstOrDefault(source => source.Id == selectedSourceId);
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
    public Task QueueBackupsForDeviceAsync(Guid deviceId) => QueueEligibleBackupsAsync(mapping => mapping.Device.Id == deviceId);

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

    public Task QueueEligibleBackupsAsync() => QueueEligibleBackupsAsync(_ => true);

    private async Task QueueEligibleBackupsAsync(Func<MappingViewModel, bool> filter)
    {
        var eligible = Mappings.Where(mapping => mapping.Enabled && mapping.Device.IsConnected && filter(mapping)).ToArray();
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
                : $"Queued {Pluralize(queued, "backup")}.";
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
        DefaultArrivalDelayMinutes = state.DefaultArrivalDelayMinutes;
        ShowFlyoutOnBackupStart = state.ShowFlyoutOnBackupStart;

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
            if (set is not null && device is not null) Mappings.Add(CreateMapping(mapping, set, device));
        }
        if (state.SkipDisabledMappingIds is { Count: > 0 } skipped)
        {
            _skipDisabledMappingIds.UnionWith(skipped);
            RestoreSkipDisabledMappingsIntoCurrentSet();
        }
        Activity.Add("Storage Agent UI started.");
    }

    private void LoadDemoSources()
    {
        Sources.Add(new SourceAgentViewModel(LocalSourceIdentity.AgentId, LocalSourceIdentity.DisplayName));

        var home = new SourceAgentViewModel(Guid.Parse("c60280da-a03c-4887-a600-577def417af6"), "Home Server");
        AddDemoSet(home, new(Guid.Parse("7d750726-97ab-4f81-9f09-f06c34f524d1"), home.Id, home.DisplayName, "Photos", ["/srv/photos", "/srv/videos"]));
        AddDemoSet(home, new(Guid.Parse("e10a4df5-0f71-438d-93f0-34e587357f00"), home.Id, home.DisplayName, "Documents", ["/home/park/Documents"]));
        Sources.Add(home);

        var workstation = new SourceAgentViewModel(Guid.Parse("0cdf358f-4b92-4bb0-b852-460520508952"), "Studio Workstation");
        AddDemoSet(workstation, new(Guid.Parse("bb452fc9-f616-4810-a649-3c37775d43d4"), workstation.Id, workstation.DisplayName, "Projects", ["D:/Projects"]));
        Sources.Add(workstation);

        // "This PC" is local and has no connection of its own; every other demo Source is a paired
        // computer, and must appear here too - otherwise the demo (used both by UiTests and for UX
        // verification) shows a self-contradicting screen: computers with Backup Sets, but "No paired
        // computers yet" in the same merged grid.
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

    private void AddDemoSet(SourceAgentViewModel source, SourceBackupSet model)
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
                _store.Save(new(topology, StartWithWindows, NotifyOnDeviceArrival, AutomaticBackups, DefaultArrivalDelayMinutes, ShowFlyoutOnBackupStart, _skipDisabledMappingIds.ToArray()));
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
        var selectedSourceId = SelectedSourceAgent?.Id;
        Devices.Clear();
        BackupSets.Clear();
        Sources.Clear();
        Mappings.Clear();
        foreach (var device in topology.Devices) Devices.Add(new(device));
        // "This PC" always appears first, even with no local Backup Sets yet: local backups need no
        // Source Agent, pairing, or explicit enable step to be available in the tray.
        var localSource = new SourceAgentViewModel(LocalSourceIdentity.AgentId, LocalSourceIdentity.DisplayName);
        Sources.Add(localSource);
        foreach (var model in topology.BackupSets.Where(set => set.SourceAgentId == LocalSourceIdentity.AgentId))
        {
            var backupSet = new BackupSetViewModel(model);
            BackupSets.Add(backupSet);
            localSource.BackupSets.Add(backupSet);
        }
        foreach (var group in topology.BackupSets.Where(set => set.SourceAgentId != LocalSourceIdentity.AgentId).GroupBy(set => new { set.SourceAgentId, set.SourceAgentName }))
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
            if (backupSet is not null && device is not null) Mappings.Add(CreateMapping(mapping, backupSet, device));
        }
        SelectedBackupSet = BackupSets.FirstOrDefault();
        SelectedDevice = Devices.FirstOrDefault();
        SelectedSourceAgent = Sources.FirstOrDefault(source => source.Id == selectedSourceId);
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
        new(model, set, device, mapping => _ = SaveAsync());

    private async Task AddMappingAsync()
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
        Mappings.Add(CreateMapping(candidate, SelectedBackupSet, SelectedDevice));
        RefreshDeviceTriggerRoles();
        NotifyCounts();
        // A study found evaluators had to be told across two separate tabs to add a backup, then
        // remember to go save it, or it silently reverted on the next configuration refresh - saving
        // immediately here (SaveAsync sets its own success/failure FooterStatus) removes the gap.
        await SaveAsync();
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

    private async Task RemoveMappingAsync()
    {
        if (SelectedMapping is null) return;
        Mappings.Remove(SelectedMapping);
        SelectedMapping = null;
        RefreshDeviceTriggerRoles();
        NotifyCounts();
        await SaveAsync();
    }

    // Lets a backup be created start-to-finish from the Backups group without first knowing to visit the
    // Devices tab - a study found evaluators had no way to guess that step was needed. RegisterDeviceAsync
    // and RegisterFolderAsync both auto-select the new device on SelectedDevice, which MappingDeviceCombo
    // is already bound to, so closing the dialog leaves the new device chosen with no extra step.
    private void OpenRegisterDeviceDialog() => new RegisterDeviceWindow(this).ShowDialog();

    private async Task RegisterDeviceAsync()
    {
        if (SelectedAvailableDrive is null) { FooterStatus = "Select a connected drive first."; return; }
        if (Devices.Any(device => string.Equals(device.StableId, SelectedAvailableDrive.StableId, StringComparison.OrdinalIgnoreCase)))
        {
            FooterStatus = "That device is already registered.";
            return;
        }
        var model = new RegisteredDevice(Guid.NewGuid(), SelectedAvailableDrive.StableId, SelectedAvailableDrive.HardwareName, SelectedAvailableDrive.VolumeLabel, SelectedAvailableDrive.Root, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DefaultArrivalDelayMinutes);
        var registered = new DeviceViewModel(model) { CurrentRoot = SelectedAvailableDrive.Root, IsConnected = true, AvailableBytes = SelectedAvailableDrive.AvailableBytes, TotalBytes = SelectedAvailableDrive.TotalBytes };
        Devices.Add(registered);
        SelectedDevice = registered;
        AddActivity($"Registered device {model.DisplayName}.");
        NotifyCounts();
        await SaveAsync();
    }

    private async Task RegisterFolderAsync()
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
        var model = new RegisteredDevice(Guid.NewGuid(), stableId, displayName, "Folder", root, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DefaultArrivalDelayMinutes);
        var registered = new DeviceViewModel(model) { CurrentRoot = root, IsConnected = true, CanEject = false };
        try { var driveInfo = new DriveInfo(Path.GetPathRoot(root) ?? root); registered.AvailableBytes = driveInfo.AvailableFreeSpace; registered.TotalBytes = driveInfo.TotalSize; }
        catch (Exception exception) when (exception is ArgumentException or IOException) { }
        Devices.Add(registered);
        SelectedDevice = registered;
        AddActivity($"Registered storage folder {root}.");
        NotifyCounts();
        await SaveAsync();
    }

    // "This PC" needs no pairing, Source Agent, or enable step: choosing a folder here is the entire
    // flow, mirroring RegisterFolder()'s own no-separate-name-prompt pattern.
    private async Task AddLocalBackupSetAsync()
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose a local folder to back up", ShowNewFolderButton = false };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        var path = Path.GetFullPath(dialog.SelectedPath);
        if (BackupSets.Any(set => set.Model.SourceAgentId == LocalSourceIdentity.AgentId && set.Model.SourcePaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            FooterStatus = "That folder is already being backed up.";
            return;
        }
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } folderName ? folderName : path;
        var backupSet = new BackupSetViewModel(new SourceBackupSet(Guid.NewGuid(), LocalSourceIdentity.AgentId, LocalSourceIdentity.DisplayName, name, [path]));
        BackupSets.Add(backupSet);
        var localSource = Sources.FirstOrDefault(source => source.Id == LocalSourceIdentity.AgentId);
        if (localSource is null)
        {
            localSource = new SourceAgentViewModel(LocalSourceIdentity.AgentId, LocalSourceIdentity.DisplayName);
            Sources.Insert(0, localSource);
        }
        localSource.BackupSets.Add(backupSet);
        SelectedBackupSet = backupSet;
        AddActivity($"Added a backup for {path}. Choose a device for it below.");
        NotifyCounts();
        await SaveAsync();
    }

    private async Task RemoveLocalBackupSetAsync()
    {
        if (SelectedBackupSet is not { } backupSet || backupSet.Model.SourceAgentId != LocalSourceIdentity.AgentId)
        {
            FooterStatus = "Select a backup of a folder on this PC first.";
            return;
        }
        foreach (var mapping in Mappings.Where(mapping => mapping.BackupSet.Id == backupSet.Id).ToArray()) Mappings.Remove(mapping);
        BackupSets.Remove(backupSet);
        Sources.FirstOrDefault(source => source.Id == LocalSourceIdentity.AgentId)?.BackupSets.Remove(backupSet);
        SelectedBackupSet = BackupSets.FirstOrDefault();
        RefreshDeviceTriggerRoles();
        NotifyCounts();
        await SaveAsync();
    }

    private async Task ForgetDeviceAsync()
    {
        if (SelectedDevice is null) return;
        if (Mappings.Any(mapping => mapping.Device.Id == SelectedDevice.Id))
        {
            FooterStatus = "Remove mappings for this device before forgetting it.";
            return;
        }
        var confirmed = System.Windows.MessageBox.Show(
            $"Stop using \"{SelectedDevice.DisplayName}\"? Backup data already stored on it is not deleted - only Storage stops sending new backups to it, until you register it again.",
            "Stop using this device", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        if (!confirmed) return;
        var displayName = SelectedDevice.DisplayName;
        Devices.Remove(SelectedDevice);
        SelectedDevice = null;
        AddActivity($"Stopped using {displayName}. Backup data on it was not deleted.");
        NotifyCounts();
        await SaveAsync();
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
            if (match is not null) { device.AvailableBytes = match.AvailableBytes; device.TotalBytes = match.TotalBytes; }
            if (!wasConnected && device.IsConnected)
            {
                device.LastSeenAt = DateTimeOffset.UtcNow;
                device.ConnectedAt = DateTimeOffset.UtcNow;
                AddActivity($"Registered device connected: {device.DisplayName}.");
                if (NotifyOnDeviceArrival) NotificationRequested?.Invoke(this, new("Backup storage connected", DeviceArrivalMessage(device.DisplayName, device.ArrivalDelayMinutes)));
            }
            else if (wasConnected && !device.IsConnected)
            {
                device.ConnectedAt = null;
                _ = RestoreSkippedMappings(device.Id);
            }
        }
        _connectedRoots.Clear();
        foreach (var root in nowConnected) _connectedRoots.Add(root);
        NotifyCounts();
        UpdateRemovalBanners();
    }

    private void AddActivity(string text)
    {
        Activity.Insert(0, $"{DateTime.Now:t}  {text}");
        while (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1);
    }

    // The header badge must reflect ConnectedDeviceCount immediately after any action that can change
    // it (forgetting, registering) - not only on the next 3-second RefreshDrives() tick - so every
    // mutation path that used to call NotifyCounts() alone gets the badge update for free here too.
    private void NotifyCounts()
    {
        OverallStatus = ConnectedDeviceCount > 0 ? $"{ConnectedDeviceCount} device{(ConnectedDeviceCount == 1 ? "" : "s")} connected" : "Waiting for storage";
        OnPropertyChanged(nameof(ConnectedDeviceCount));
        OnPropertyChanged(nameof(SourceCount));
        OnPropertyChanged(nameof(MappingCount));
        UpdateMappingLastBackupInfo();
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

    internal static string Pluralize(int count, string singularNoun) => $"{count} {singularNoun}{(count == 1 ? "" : "s")}";

    // Shared by every "how long ago" display in the tray (computer last-seen, and the Backups grid's Last
    // backup column) so "just now" vs. "6 days ago" phrasing - and its threshold for falling back to an
    // absolute date past 30 days - only needs deciding once.
    internal static string RelativeTimeDisplay(DateTimeOffset at)
    {
        var elapsed = DateTimeOffset.UtcNow - at;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => Pluralize((int)elapsed.TotalMinutes, "minute") + " ago",
            { TotalHours: < 24 } => Pluralize((int)elapsed.TotalHours, "hour") + " ago",
            { TotalDays: < 30 } => Pluralize((int)elapsed.TotalDays, "day") + " ago",
            _ => at.LocalDateTime.ToString("g")
        };
    }

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

// Doubles as a row in the merged Computers grid: a blind first-click study found evaluators never used
// the separate tree this used to feed (0/4 on a task built around it), and a second, separate tree +
// grid pairing created its own ambiguity ("does this button act on the computer or the folder under
// it?"). Connection is null for "This PC" (no certificate/connection of its own) and for any computer
// that hasn't connected since Storage started - LastSeenDisplay/StatusDisplay fall back to "-" rather
// than leaving the cell blank or, worse, putting a non-status value like "This PC" in the Status column.
public sealed class SourceAgentViewModel(Guid id, string displayName) : ObservableObject
{
    private SourceConnectionViewModel? _connection;
    public Guid Id { get; } = id;
    public string DisplayName { get; } = displayName;
    // "This PC" alone left Address/Status's "—" reading as an error rather than "not applicable" (measured:
    // 2/2 evaluators unsure). Naming the reason directly in the same cell the "—"s sit next to answers it
    // without relying on the reader already knowing "This PC" is special.
    public string DisplayNameWithHint => Id == LocalSourceIdentity.AgentId ? $"{DisplayName} (no agent needed)" : DisplayName;
    public ObservableCollection<BackupSetViewModel> BackupSets { get; } = [];
    public SourceConnectionViewModel? Connection
    {
        get => _connection;
        set
        {
            if (!Set(ref _connection, value)) return;
            OnPropertyChanged(nameof(LastSeenDisplay));
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(AddressDisplay));
        }
    }
    public string LastSeenDisplay => Connection?.LastSeenDisplay ?? "—";
    public string StatusDisplay => Connection?.StatusDisplay ?? "—";
    public string AddressDisplay => Connection?.AddressDisplay ?? "—";
    // "Offers" (formerly "Paths served") - the names of this computer's own Backup Sets. "Backup Set" is
    // this tray's own internal grouping; a person configuring the other end thinks of these as folders.
    public string PathsServedDisplay => BackupSets.Count == 0 ? "—" : string.Join(", ", BackupSets.Select(set => set.Model.Name));
    // UI Automation reads Name from ToString(); DisplayMemberPath and item templates do not apply to it.
    public override string ToString() => DisplayName;
}

public sealed class SourceConnectionViewModel(SourceConnectionDto model)
{
    public Guid AgentId { get; } = model.AgentId;
    public string AgentName { get; } = model.AgentName;
    public string ReportedAgentName { get; } = model.ReportedAgentName;
    public DateTimeOffset LastSeenAt { get; } = model.LastSeenAt;
    // An absolute timestamp ("8/30/26 5:12 PM") made evaluators do the arithmetic themselves to tell
    // "just now" from "yesterday"; relative phrasing answers the question ("is this computer connected
    // right now?") directly instead of via Status, which describes access, not connectivity.
    public string LastSeenDisplay { get; } = MainWindowViewModel.RelativeTimeDisplay(model.LastSeenAt);
    // Captured server-side at the same moment as LastSeenAt (the Source's most recent catalog upload), so
    // the two describe the same event rather than two different points in time. DHCP-mutable and shown for
    // context only - the certificate fingerprint below is this Source's actual identity.
    public string AddressDisplay => model.Address ?? "—";
    public int BackupSetCount { get; } = model.BackupSetCount;
    public bool IsRevoked { get; } = model.Revoked;
    public DateTimeOffset? CertificateExpiresAt { get; } = model.CertificateExpiresAt;
    // A "Certificate expires" column shown for every row measured as meaningless to evaluators ("why
    // does connecting a computer involve a certificate?") - correctly so: the Source Agent renews its own
    // certificate starting 30 days before expiry (checked at the start of every backup and once a day
    // while watching), so a healthy, regularly-connecting computer never approaches expiry at all. The
    // only case that actually needs a person is a computer that hasn't been seen since that renewal
    // window opened - it has missed its own chance to renew and genuinely needs Re-pair.
    // The Source Agent's watch loop polls every 5 seconds by default (poll-interval), so a genuinely live
    // agent's LastSeenAt is always at most a few seconds old; 2 minutes gives generous slack for a slower
    // configured interval or a brief hiccup without claiming "Connected" for a computer that has actually
    // gone quiet.
    private static readonly TimeSpan OnlineThreshold = TimeSpan.FromMinutes(2);

    public string StatusDisplay
    {
        get
        {
            if (IsRevoked) return "Revoked";
            var now = DateTimeOffset.UtcNow;
            if (CertificateExpiresAt is { } expires)
            {
                var renewalWindowStart = expires.AddDays(-30);
                if (expires <= now) return "Expired — re-pair to reconnect";
                // Must also check whether the renewal window has actually opened yet - otherwise a
                // healthy computer seen minutes ago, with a certificate not due for renewal for months,
                // reads "hasn't been seen since {a still-future date}" as true and wrongly warns.
                if (now >= renewalWindowStart && LastSeenAt < renewalWindowStart) return $"Offline — re-pair before {expires.LocalDateTime:d}";
            }
            return now - LastSeenAt <= OnlineThreshold ? "Connected" : "Offline";
        }
    }
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
    // AvailableDriveViewModel.DisplayName already reads "465.8 GB free" from. Evaluators saw that figure
    // before registering a drive and asked where it went afterward.
    public long? AvailableBytes { get => _availableBytes; set { if (Set(ref _availableBytes, value)) { OnPropertyChanged(nameof(FreeSpaceDisplay)); OnPropertyChanged(nameof(DisplayNameWithDetails)); } } }
    public long? TotalBytes { get => _totalBytes; set { if (Set(ref _totalBytes, value)) OnPropertyChanged(nameof(FreeSpaceDisplay)); } }
    public string FreeSpaceDisplay => AvailableBytes is { } bytes ? $"{bytes / 1_073_741_824d:0.0} GB free" : "—";

    // A plain device name (e.g. "Archive drive") gave evaluators no way to tell whether it was really
    // the external drive they thought it was; this mirrors AvailableDriveViewModel.DisplayName's own
    // "(root), n GB free" pattern so the same physical clues survive registration instead of vanishing.
    public string DisplayNameWithDetails => CurrentRoot is { Length: > 0 } root
        ? AvailableBytes is { } bytes ? $"{DisplayName} ({root}), {bytes / 1_073_741_824d:0.0} GB free" : $"{DisplayName} ({root})"
        : DisplayName;
    // Name + drive letter only, no capacity - capacity matters when *choosing* a device (the combo box,
    // the Devices grid), not when deciding whether it's safe to remove one already chosen; measured as
    // making the removal banner read as "a bit long" with no benefit to the decision it supports.
    public string DisplayNameWithRoot => CurrentRoot is { Length: > 0 } root ? $"{DisplayName} ({root})" : DisplayName;
    public string Status => IsConnected ? "Connected" : "Offline";
    public string LastSeenDisplay => LastSeenAt?.LocalDateTime.ToString("g") ?? "Never";
    public int ArrivalDelayMinutes { get; set; }
    // Explicitly set only when a Backup Set names this device as its trigger, or a mapping targets it -
    // never inferred, per the same "don't let the UI guess" principle as the arrival logic itself.
    public bool IsSourceTrigger { get => _isSourceTrigger; set { if (Set(ref _isSourceTrigger, value)) OnPropertyChanged(nameof(RoleDisplay)); } }
    public bool IsUsedAsTarget { get => _isUsedAsTarget; set { if (Set(ref _isUsedAsTarget, value)) OnPropertyChanged(nameof(RoleDisplay)); } }
    public string RoleDisplay => (IsUsedAsTarget, IsSourceTrigger) switch
    {
        (true, true) => "Target + Trigger",
        (false, true) => "Trigger",
        (true, false) => "Target",
        (false, false) => "Unassigned"
    };
    public RegisteredDevice ToModel() => new(Id, StableId, DisplayName, VolumeLabel, CurrentRoot ?? LastKnownRoot, RegisteredAt, LastSeenAt, ArrivalDelayMinutes);

    // UI Automation reads Name from ToString(); DisplayMemberPath and item templates do not apply to it.
    public override string ToString() => DisplayName;
}

// Source/Target are kept deliberately symmetric (peer review, measured against a merged "what backs up
// to what" grid that made both halves of that relationship visible for the first time - previously
// Source folder's own paths, and any Backup Set with no destination yet, were not shown anywhere at all).
public sealed class MappingViewModel : ObservableObject
{
    private readonly Action<MappingViewModel>? _onEnabledChanged;
    private bool _enabled;
    private bool _isRepeatOfPreviousSource;
    private bool _isRepeatOfPreviousSourceFolder;
    private string _lastBackupDisplay = "Never";
    private string _lastBackupIssue = string.Empty;
    private string _triggerNote = string.Empty;

    public MappingViewModel(BackupTargetMapping model, BackupSetViewModel set, DeviceViewModel device, Action<MappingViewModel>? onEnabledChanged = null)
    {
        Id = model.Id;
        BackupSet = set;
        Device = device;
        RepositoryPath = model.RepositoryPath;
        _enabled = model.Enabled;
        _onEnabledChanged = onEnabledChanged;
    }

    public Guid Id { get; }
    public BackupSetViewModel BackupSet { get; }
    public DeviceViewModel Device { get; }
    public string BackupSetName => BackupSet.DisplayName;
    public string SourceAgentName => BackupSet.Model.SourceAgentName;
    public string BackupSetOnlyName => BackupSet.Model.Name;
    public string SourcePathsDisplay => string.Join(" · ", BackupSet.Model.SourcePaths);
    public string DeviceName => Device.DisplayName;
    public string RepositoryPath { get; }
    public string DestinationFolder => Path.GetFullPath(Path.Combine(Device.LastKnownRoot ?? string.Empty, RepositoryPath));
    // A real pause toggle, not a read-only status dot - previously nothing in the UI ever changed this
    // after a mapping was created, so the column only ever displayed "true". Persists immediately, same
    // as every other in-screen edit this pass made auto-saving.
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) _onEnabledChanged?.Invoke(this); } }
    // For a caller that's about to toggle several mappings at once and save them together (e.g.
    // MainWindowViewModel.SkipDeviceThisConnection) - the normal setter's per-mapping auto-save would fire
    // one overlapping SaveAsync() per mapping, and a config revision conflict on any but the first could
    // leave a mapping toggled in memory but not actually persisted to the server that enforces it.
    internal void SetEnabledWithoutSaving(bool value) => Set(ref _enabled, value);
    // Consecutive rows sharing the same Source, or the same Source folder, are flagged here so the view
    // can dim the repeated text - recomputed for the whole list by
    // MainWindowViewModel.UpdateMappingRepeatMarkers() whenever Mappings changes, never inferred by the
    // view itself. A study first tried collapsing the repeat to a "″" ditto mark; re-measured as read as a
    // typo or an empty cell, and as making the Enabled checkbox's per-row scope ambiguous (two rows
    // sharing a mark read as one unit). Repeating the real text, just visually muted, avoided both.
    public bool IsRepeatOfPreviousSource { get => _isRepeatOfPreviousSource; set => Set(ref _isRepeatOfPreviousSource, value); }
    public bool IsRepeatOfPreviousSourceFolder { get => _isRepeatOfPreviousSourceFolder; set => Set(ref _isRepeatOfPreviousSourceFolder, value); }
    // Set by MainWindowViewModel.UpdateMappingLastBackupInfo() from the job list - a mapping does not hold
    // its own reference to Jobs, so this is pushed in rather than computed here.
    public string LastBackupDisplay { get => _lastBackupDisplay; set => Set(ref _lastBackupDisplay, value); }
    public string LastBackupIssue { get => _lastBackupIssue; set => Set(ref _lastBackupIssue, value); }
    // Read-only surface for a Backup Set's TriggerDeviceIds/TriggerPolicy, set by
    // MainWindowViewModel.UpdateMappingTriggerNotes() - the per-row editor for these is gone (peer
    // review: its default, starting when the mapped destination connects, covers the ordinary case with
    // no user choice needed), but a Backup Set that already names an explicit trigger device (the
    // external-source-arrival case from USER_GUIDE 6, or a config written before this pass) still only
    // starts for that device, regardless of Target. Without this line the grid would silently imply
    // "starts when Target connects" for a row that in fact does not.
    public string TriggerNote { get => _triggerNote; set => Set(ref _triggerNote, value); }
    public BackupTargetMapping ToModel() => new(Id, BackupSet.Id, Device.Id, RepositoryPath, Enabled);
}

// One line, wherever the user already is, answering the exact task a first-click study found no tab
// arrangement could pass: "confirm the backup finished, then safely remove the drive." The same line
// swaps wording rather than appearing/disappearing/growing a second line, so "safe" and "backing up" are
// never ambiguous about which device they describe or momentarily absent while a job starts.
public enum DeviceRemovalBannerKind { BackingUp, Safe, SafeButIncomplete }

public sealed class DeviceRemovalBannerViewModel
{
    private DeviceRemovalBannerViewModel(DeviceViewModel device, DeviceRemovalBannerKind kind, string message, bool showRemoveButton)
    {
        Device = device;
        Kind = kind;
        Message = message;
        ShowRemoveButton = showRemoveButton;
    }

    public DeviceViewModel Device { get; }
    public DeviceRemovalBannerKind Kind { get; }
    public string Message { get; }
    public bool ShowRemoveButton { get; }

    // The discriminating word comes first in every line - measured: with the device name (the longest,
    // identical-looking part) leading instead, evaluators skimming quickly read past it before reaching
    // the one word that actually differs between states.
    public static DeviceRemovalBannerViewModel BackingUp(DeviceViewModel device, TimeSpan? remaining)
    {
        var etaClause = remaining is { } eta
            ? eta.TotalHours >= 1 ? $" (about {MainWindowViewModel.Pluralize((int)Math.Round(eta.TotalHours), "hour")} left)" : $" (about {MainWindowViewModel.Pluralize(Math.Max(1, (int)eta.TotalMinutes), "minute")} left)"
            : string.Empty;
        return new(device, DeviceRemovalBannerKind.BackingUp, $"Do not remove — {device.DisplayNameWithRoot} is backing up now{etaClause}.", showRemoveButton: false);
    }

    public static DeviceRemovalBannerViewModel Finished(DeviceViewModel device, bool anyDidNotFinish) => anyDidNotFinish
        // Unlike the other two states, "is it safe to remove" is not the main news here - the backup not
        // finishing is - so the safe/unsafe prefix pattern used for those two backfires: measured as
        // reading like a contradiction ("safe... but... did not finish?") that made evaluators hesitate
        // on a question ("can I remove it?") the other two states answered instantly with the same lead.
        ? new(device, DeviceRemovalBannerKind.SafeButIncomplete, $"Backup did not finish — nothing new was saved to {device.DisplayNameWithRoot}. You can still remove it safely. See the Overview tab for details.", showRemoveButton: true)
        : new(device, DeviceRemovalBannerKind.Safe, $"Safe to remove — {device.DisplayNameWithRoot} finished all backups.", showRemoveButton: true);
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

// SkipDisabledMappingIds must survive a restart even though "this connection only" is the whole promise
// of the flyout's Skip this time - the in-memory tracking set that normally restores them on disconnect
// is gone if the app exits (crash, forced close, or simply quitting) before that disconnect happens, and
// without this, the mapping stays persisted as Enabled=false forever with nothing in the UI explaining why
// (a peer review finding: this is a backup product, and a silently-disabled backup is the worst failure).
public sealed record AppConfiguration(StorageAgentConfiguration Topology, bool StartWithWindows = true, bool NotifyOnDeviceArrival = true, bool AutomaticBackups = true, int DefaultArrivalDelayMinutes = 30, bool ShowFlyoutOnBackupStart = true, IReadOnlyList<Guid>? SkipDisabledMappingIds = null);

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

public sealed class RelayCommand<T>(Action<T> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { if (parameter is T value) execute(value); }
}
