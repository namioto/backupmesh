using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace BackupMesh.Storage.App;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _window;
    private TrayFlyoutWindow? _flyout;
    private string _baseTrayText = "BackupMesh Storage Agent — starting";
    private bool _wasBackingUp;
    private bool _wasAwaitingDecision;
    private readonly DispatcherTimer _flyoutAutoHideTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var demoMode = e.Args.Any(argument => argument.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var endpointArgument = e.Args.FirstOrDefault(argument => argument.StartsWith("--service-endpoint=", StringComparison.OrdinalIgnoreCase));
        var serviceEndpoint = endpointArgument is null ? null : endpointArgument[(endpointArgument.IndexOf('=') + 1)..];
        _window = new MainWindow(demoMode, serviceEndpoint);
        _window.Closing += (_, args) => { args.Cancel = true; _window.Hide(); };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open BackupMesh", null, (_, _) => ShowWindow());
        menu.Items.Add("Back up now", null, (_, _) => _window.ViewModel.QueueSelectedBackups());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "backupmesh-tray.ico");
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = new Icon(iconPath),
            Text = _baseTrayText,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
        _window.ViewModel.NotificationRequested += OnNotificationRequested;
        _window.ViewModel.StatusChanged += (_, status) => { _baseTrayText = status; UpdateTrayText(); };

        var flyoutViewModel = new TrayFlyoutViewModel(_window.ViewModel);
        _flyout = new TrayFlyoutWindow(flyoutViewModel);
        flyoutViewModel.OpenMainWindowRequested += (_, _) => { _flyout?.Hide(); ShowWindow(); };
        // Start now and Skip this time update the Storage configuration through TrayFlyoutViewModel;
        // collection changes then drive visibility and auto-hide behavior below.
        _flyoutAutoHideTimer.Tick += (_, _) => { _flyoutAutoHideTimer.Stop(); _flyout?.Hide(); };
        _window.ViewModel.Jobs.CollectionChanged += (_, _) => UpdateFlyoutState();
        flyoutViewModel.PendingArrivals.CollectionChanged += (_, _) => UpdateFlyoutState();

        _window.ViewModel.StartDeviceMonitoring();
        ShowWindow();
    }

    // Fires on every job-list/pending-arrival refresh, not only on a real state transition, so the two
    // "_was..." fields gate auto-show to the actual edge - otherwise it would reappear on every poll tick
    // for the whole duration of a backup instead of once at the start.
    //
    // Pending Start now/Skip this time decisions must remain visible until the user acts. A decision
    // (HasPendingArrivals) is a hard veto on the auto-hide timer, re-checked on every state change - not
    // just suppressed at the moment the decision first appears - so it can never be left running
    // underneath a decision that shows up while it's already ticking down.
    private void UpdateFlyoutState()
    {
        if (_window is null || _flyout is null) return;
        var isBackingUp = _window.ViewModel.Jobs.Any(job => job.State == "RUNNING");
        var awaitingDecision = _flyout.ViewModel.HasPendingArrivals;
        var justStartedNeedingAttention = (awaitingDecision && !_wasAwaitingDecision) || (isBackingUp && !_wasBackingUp);

        if (justStartedNeedingAttention && _window.ViewModel.ShowFlyoutOnBackupStart && !IsFullScreenAppActive())
            _flyout.ShowNearTray();

        if (awaitingDecision) _flyoutAutoHideTimer.Stop();
        else if (justStartedNeedingAttention || (_wasAwaitingDecision && isBackingUp))
        {
            // Either a fresh progress-only show, or a decision that just resolved into a running backup -
            // both are now the auto-hiding kind of popup.
            _flyoutAutoHideTimer.Stop();
            _flyoutAutoHideTimer.Start();
        }

        _wasBackingUp = isBackingUp;
        _wasAwaitingDecision = awaitingDecision;
        UpdateTrayText();
    }

    // The tray tooltip normally mirrors OverallStatus ("N devices connected"), but a running backup is
    // more specific and more useful news - shown in its place for as long as one is active, then reverting
    // to the base status text on its own via the same code path (OnJobsChanged fires again once the job
    // list no longer has a RUNNING entry).
    private void UpdateTrayText()
    {
        if (_trayIcon is null || _window is null) return;
        var running = _window.ViewModel.Jobs.FirstOrDefault(job => job.State == "RUNNING");
        var text = running is null ? _baseTrayText : $"{running.Target} · {running.Progress}";
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    // A backup-start popup that covers a video call or a game would be actively harmful, not just
    // unwelcome - approximated here as "the foreground window's bounds exactly cover its screen", which
    // catches real exclusive/borderless-fullscreen apps without needing a window-style inspection API.
    private static bool IsFullScreenAppActive()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero || !GetWindowRect(hWnd, out var rect)) return false;
        var screen = Forms.Screen.FromHandle(hWnd).Bounds;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnNotificationRequested(object? sender, AppNotification notification)
    {
        if (_trayIcon is null) return;
        _trayIcon.BalloonTipTitle = notification.Title;
        _trayIcon.BalloonTipText = notification.Message;
        _trayIcon.BalloonTipIcon = notification.IsError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(6000);
    }

    private void ExitApplication()
    {
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        _flyoutAutoHideTimer.Stop();
        _flyout?.ViewModel.Dispose();
        _flyout?.Close();
        _window?.ViewModel.Dispose();
        Shutdown();
    }
}
