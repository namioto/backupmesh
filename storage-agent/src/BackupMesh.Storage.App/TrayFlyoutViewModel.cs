using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Threading;

namespace BackupMesh.Storage.App;

// Adapter/projection over MainWindowViewModel for the tray flyout popup (TrayFlyoutWindow).
// MainWindowViewModel already keeps Jobs / Devices / Mappings fresh via its own timers (2s job poll, 3s
// device poll, 10s catalog poll), so this class never polls the Storage Service itself - it only listens
// to those collections and re-derives its own "in progress" / "queued" / "just arrived" groupings whenever
// they change. The one timer it owns (_displayTimer) exists solely to re-render wall-clock-dependent text
// (ETA, "eligible in Nm") between upstream data changes; it never fetches anything.
//
// Start now / Skip this time DO write back through _main (QueueBackupsForDeviceAsync /
// SkipDeviceThisConnectionAsync) - an earlier version of this file left both as inert stubs that only
// updated this class's own local state, leaving the buttons with no effect on whether a backup ran.
public sealed class TrayFlyoutViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly HashSet<Guid> _observedDeviceIds = [];

    public ObservableCollection<FlyoutJobViewModel> InProgressJobs { get; } = [];
    public ObservableCollection<FlyoutJobViewModel> QueuedJobs { get; } = [];
    public ObservableCollection<PendingArrivalViewModel> PendingArrivals { get; } = [];

    // This ViewModel never shows MainWindow itself - it only raises this so a session that owns
    // App.xaml.cs can wire it up without this file referencing MainWindow directly.
    public event EventHandler? OpenMainWindowRequested;

    public ICommand OpenMainWindowCommand { get; }
    public ICommand CancelAllCommand { get; }
    public ICommand CancelJobCommand { get; }
    public ICommand StartNowCommand { get; }
    public ICommand SkipThisTimeCommand { get; }

    public TrayFlyoutViewModel(MainWindowViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));

        OpenMainWindowCommand = new RelayCommand(() => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty));
        CancelAllCommand = new RelayCommand(CancelAll);
        CancelJobCommand = new RelayCommand<FlyoutJobViewModel>(CancelJob);
        StartNowCommand = new RelayCommand<PendingArrivalViewModel>(pending => _ = _main.QueueBackupsForDeviceAsync(pending.Device.Id));
        // SkipDeviceThisConnectionAsync flips each mapping's Enabled synchronously before its first await,
        // so by the time this line runs the card's disappearance condition (RefreshPendingArrivals'
        // mapping.Enabled filter) is already true - refreshing here makes the card vanish immediately
        // instead of waiting for the next 5-second _displayTimer tick, since Mappings.CollectionChanged
        // (already wired below) only fires on add/remove, not on an existing item's property changing.
        SkipThisTimeCommand = new RelayCommand<PendingArrivalViewModel>(pending => { _ = _main.SkipDeviceThisConnectionAsync(pending.Device.Id); RefreshPendingArrivals(); });

        _main.Jobs.CollectionChanged += OnJobsChanged;
        _main.Devices.CollectionChanged += OnDevicesChanged;
        _main.Mappings.CollectionChanged += (_, _) => RefreshPendingArrivals();
        AttachDeviceHandlers();
        RefreshJobs();
        RefreshPendingArrivals();

        _displayTimer.Tick += (_, _) => { RefreshJobs(); RefreshPendingArrivals(); };
        _displayTimer.Start();
    }

    public bool HasAnyActivity => InProgressJobs.Count > 0 || QueuedJobs.Count > 0 || PendingArrivals.Count > 0;
    public bool IsEmpty => !HasAnyActivity;
    public bool HasInProgressJobs => InProgressJobs.Count > 0;
    public bool HasQueuedJobs => QueuedJobs.Count > 0;
    public bool HasPendingArrivals => PendingArrivals.Count > 0;
    public bool CanCancelAny => InProgressJobs.Concat(QueuedJobs).Any(job => job.CanCancel);

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshJobs();
        RefreshPendingArrivals();
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        AttachDeviceHandlers();
        RefreshPendingArrivals();
    }

    // DeviceViewModel raises PropertyChanged for IsConnected but not for ConnectedAt/ArrivalDelayMinutes
    // (plain auto-properties upstream) - that's still exactly the signal that matters here: a
    // pending-arrival card should appear or disappear precisely when a device's connected state flips.
    private void AttachDeviceHandlers()
    {
        foreach (var device in _main.Devices)
        {
            if (!_observedDeviceIds.Add(device.Id)) continue;
            device.PropertyChanged += OnDevicePropertyChanged;
        }
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DeviceViewModel.IsConnected)) return;
        RefreshPendingArrivals();
    }

    private void RefreshJobs()
    {
        Resync(InProgressJobs, _main.Jobs.Where(job => job.State == "RUNNING"));
        Resync(QueuedJobs, _main.Jobs.Where(job => job.State == "ACCEPTED"));
        NotifyDerivedPropertiesChanged();
    }

    private void NotifyDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasAnyActivity));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasInProgressJobs));
        OnPropertyChanged(nameof(HasQueuedJobs));
        OnPropertyChanged(nameof(HasPendingArrivals));
        OnPropertyChanged(nameof(CanCancelAny));
    }

    private static void Resync(ObservableCollection<FlyoutJobViewModel> target, IEnumerable<BackupJobViewModel> source)
    {
        var wanted = source.Select(job => new FlyoutJobViewModel(job)).ToArray();
        target.Clear();
        foreach (var item in wanted) target.Add(item);
    }

    // A "just arrived" card: the device is connected, has at least one enabled mapping targeting it, and
    // nothing has run for it since it connected - regardless of whether that's because it's still inside
    // its arrival delay or because Automatic backups is turned off. Mirrors the same "since ConnectedAt"
    // freshness rule MainWindowViewModel.UpdateRemovalBanners() already applies, so this can never disagree
    // with the header removal banner about whether a device has already been backed up this connection.
    // "Skip this time" needs no separate tracking here - MainWindowViewModel.SkipDeviceThisConnectionAsync
    // disables the mapping(s) for real, so the mapping.Enabled filter below already excludes them.
    private void RefreshPendingArrivals()
    {
        var cards = new List<PendingArrivalViewModel>();
        foreach (var device in _main.Devices)
        {
            if (!device.IsConnected || device.ConnectedAt is not { } connectedAt) continue;
            var mappingsForDevice = _main.Mappings.Where(mapping => mapping.Enabled && mapping.Device.Id == device.Id).ToArray();
            if (mappingsForDevice.Length == 0) continue;
            var mappingIds = mappingsForDevice.Select(mapping => mapping.Id).ToHashSet();
            var hasRunOrRunning = _main.Jobs.Any(job => job.TargetMappingId is { } id && mappingIds.Contains(id) && job.StartedAt >= connectedAt);
            if (hasRunOrRunning) continue;
            var eligibleAt = connectedAt.AddMinutes(device.ArrivalDelayMinutes);
            cards.Add(new PendingArrivalViewModel(device, mappingsForDevice.Length, eligibleAt));
        }
        PendingArrivals.Clear();
        foreach (var card in cards) PendingArrivals.Add(card);
        NotifyDerivedPropertiesChanged();
    }

    // MainWindowViewModel exposes cancellation only through CancelJobCommand, which always acts on
    // SelectedJob - there is no public "cancel this specific job" entry point (see report: a
    // RelayCommand<BackupJobViewModel> CancelJobCommand, or a public CancelJobAsync(BackupJobViewModel),
    // would replace this). Setting SelectedJob then invoking the command is safe here only because both
    // the assignment and CancelSelectedJobAsync's own SelectedJob read happen synchronously before its
    // first await, so back-to-back calls in CancelAll() cannot race each other.
    private void CancelJob(FlyoutJobViewModel? flyoutJob)
    {
        if (flyoutJob is null) return;
        var match = _main.Jobs.FirstOrDefault(job => job.JobId == flyoutJob.Job.JobId);
        if (match is not { CanCancel: true }) return;
        var previous = _main.SelectedJob;
        _main.SelectedJob = match;
        _main.CancelJobCommand.Execute(null);
        _main.SelectedJob = previous;
    }

    private void CancelAll()
    {
        var previous = _main.SelectedJob;
        foreach (var job in _main.Jobs.Where(job => job.CanCancel).ToArray())
        {
            _main.SelectedJob = job;
            _main.CancelJobCommand.Execute(null);
        }
        _main.SelectedJob = previous;
    }

    public void Dispose()
    {
        _displayTimer.Stop();
        _main.Jobs.CollectionChanged -= OnJobsChanged;
        _main.Devices.CollectionChanged -= OnDevicesChanged;
        foreach (var device in _main.Devices) device.PropertyChanged -= OnDevicePropertyChanged;
    }
}

// Wraps a BackupJobViewModel (built fresh by MainWindowViewModel.RefreshJobsAsync every 2s) for flyout
// display. BackupJobDto's raw byte/file counts are private to BackupJobViewModel - Progress is the only
// public surface for them - so PercentComplete parses that already-formatted string back into a number
// rather than duplicating its rounding/formatting logic here. A job with no known total has no "%" to find
// and falls back to an indeterminate bar instead of reporting a misleading 0%.
public sealed class FlyoutJobViewModel
{
    private static readonly Regex PercentPattern = new(@"(\d+(?:\.\d+)?)%", RegexOptions.Compiled);

    public FlyoutJobViewModel(BackupJobViewModel job) => Job = job;

    public BackupJobViewModel Job { get; }
    public string Target => Job.Target;
    public string ProgressText => Job.Progress;
    public bool CanCancel => Job.CanCancel;
    public bool IsRunning => Job.State == "RUNNING";

    public double? PercentComplete
    {
        get
        {
            var match = PercentPattern.Match(Job.Progress);
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
    }

    public bool IsIndeterminate => IsRunning && PercentComplete is null;

    public string EtaDisplay => Job.EstimatedTimeRemaining switch
    {
        { TotalHours: >= 1 } remaining => $"About {remaining.TotalHours:0.0} h remaining",
        { TotalMinutes: >= 1 } remaining => $"About {(int)remaining.TotalMinutes} min remaining",
        { } => "Less than a minute remaining",
        null => IsRunning ? "Estimating time remaining…" : "Waiting to start"
    };
}

// A device whose arrival is "news" for the flyout: connected, mapped, and not yet backed up this
// connection. IsEligibleNow just describes whether the arrival delay has elapsed - it says nothing about
// whether Automatic backups is on, since that toggle isn't observed here (MainWindowViewModel.
// AutomaticBackups is a plain property, not a bindable one); "Start now" always means "start immediately,
// bypassing the delay", regardless of which reason the card is showing.
public sealed class PendingArrivalViewModel
{
    private readonly DateTimeOffset _eligibleAt;

    public PendingArrivalViewModel(DeviceViewModel device, int eligibleMappingCount, DateTimeOffset eligibleAt)
    {
        Device = device;
        EligibleMappingCount = eligibleMappingCount;
        _eligibleAt = eligibleAt;
    }

    public DeviceViewModel Device { get; }
    // State explicitly that the device connected, regardless of section header wording.
    public string TitleDisplay => $"{Device.DisplayNameWithRoot} connected";
    public int EligibleMappingCount { get; }
    public bool IsEligibleNow => DateTimeOffset.UtcNow >= _eligibleAt;

    public string StatusDisplay => IsEligibleNow
        ? $"Ready to back up ({MainWindowViewModel.Pluralize(EligibleMappingCount, "backup")} queued)"
        // Use a countdown to one specific, named event.
        : $"Starts automatically in {FormatRemaining(_eligibleAt - DateTimeOffset.UtcNow)}";

    private static string FormatRemaining(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? "under a minute" : remaining.TotalMinutes >= 1 ? $"{Math.Ceiling(remaining.TotalMinutes):0} min" : "under a minute";
}
