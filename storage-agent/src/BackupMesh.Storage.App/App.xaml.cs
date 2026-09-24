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
    private string _baseTrayText = Localization.Text("Text_BackupMeshStorageAgentstarting_01492B");
    private bool _wasBackingUp;
    private bool _wasAwaitingDecision;
    private bool _flyoutStateUpdateScheduled;
    private readonly DispatcherTimer _flyoutAutoHideTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private Mutex? _instanceMutex;
    private EventWaitHandle? _openRequest;
    private RegisteredWaitHandle? _openRequestWait;
    private bool _ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var preview = e.Args.Contains("--preview-backups", StringComparer.OrdinalIgnoreCase);
        var instanceName = @"Local\BackupMesh.Storage.App" + (preview ? ".Preview" : string.Empty);
        _openRequest = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Show");
        _instanceMutex = new Mutex(true, instanceName, out _ownsInstance);
        if (!_ownsInstance)
        {
            _openRequest.Set();
            Shutdown();
            return;
        }
        _openRequestWait = ThreadPool.RegisterWaitForSingleObject(_openRequest, (_, _) =>
            Dispatcher.BeginInvoke(new Action(ShowWindow)), null, Timeout.Infinite, false);
        if (preview) DispatcherUnhandledException += (_, args) =>
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "backupmesh-preview-error.txt"), args.Exception.ToString());
        var demoMode = e.Args.Any(argument => argument.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var languageArgument = e.Args.FirstOrDefault(argument => argument.StartsWith("--language=", StringComparison.OrdinalIgnoreCase));
        Localization.Initialize(languageArgument is not null ? languageArgument["--language=".Length..] : demoMode ? "en" : new ConfigurationStore().Load().Language);
        _baseTrayText = Localization.Text("Text_BackupMeshStorageAgentstarting_01492B");
        var endpointArgument = e.Args.FirstOrDefault(argument => argument.StartsWith("--service-endpoint=", StringComparison.OrdinalIgnoreCase));
        var serviceEndpoint = endpointArgument is null ? null : endpointArgument[(endpointArgument.IndexOf('=') + 1)..];
        _window = new MainWindow(demoMode, serviceEndpoint);
        if (e.Args.Any(argument => argument.Equals("--preview-backups", StringComparison.OrdinalIgnoreCase)))
        {
            _window.Title = Localization.Source.Culture.TwoLetterISOLanguageName == "ko"
                ? "BackupMesh 백업 규칙 미리보기"
                : "BackupMesh Backup Rules Preview";
            _window.ViewModel.LoadPreviewRules();
            _window.ViewModel.LoadPreviewAgents();
            _window.ViewModel.SelectedMapping = _window.ViewModel.Mappings.FirstOrDefault();
            _window.ShowBackupRules();
            void CapturePreview(string filename, FrameworkElement? target = null)
            {
                var path = Path.Combine(Path.GetTempPath(), filename);
                if (target is not null)
                {
                    var width = (int)target.ActualWidth;
                    var height = (int)target.ActualHeight;
                    var dialogImage = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    var drawing = new System.Windows.Media.DrawingVisual();
                    using (var context = drawing.RenderOpen())
                    {
                        var bounds = new Rect(0, 0, width, height);
                        context.DrawRectangle(System.Windows.Media.Brushes.White, null, bounds);
                        context.DrawRectangle(new System.Windows.Media.VisualBrush(target), null, bounds);
                    }
                    dialogImage.Render(drawing);
                    var dialogEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    dialogEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(dialogImage));
                    using var dialogOutput = File.Create(path);
                    dialogEncoder.Save(dialogOutput);
                    return;
                }
                var image = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)_window.ActualWidth, (int)_window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                image.Render(_window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using var output = File.Create(path);
                encoder.Save(output);
            }
            var captured = false;
            _window.ContentRendered += (_, _) =>
            {
                if (captured) return;
                captured = true;
                _window.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    _window.MainTabControl.SelectedIndex = 0;
                    await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    CapturePreview("backupmesh-overview-preview.png");
                    _window.ShowBackupRules();
                    await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    CapturePreview("backupmesh-rules-preview.png");
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "backupmesh-rules-check.txt"), await _window.VerifyPreviewRuleControlsAsync()); }
                    catch (Exception error) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "backupmesh-rules-check.txt"), error.ToString()); }
                    try
                    {
                        _window.ViewModel.SelectedRemoteAgent = _window.ViewModel.Sources.FirstOrDefault();
                        _window.ShowRemoteAgents();
                        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        CapturePreview("backupmesh-agents-preview.png");
                        _window.IncludeNearbyAgents.Focus();
                        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        CapturePreview("backupmesh-toggle-focus-preview.png");
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "backupmesh-agents-check.txt"), await _window.VerifyPreviewAgentControlsAsync());
                        _window.ShowSettings();
                        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        CapturePreview("backupmesh-settings-preview.png");
                        _window.StartWithWindowsToggle.Focus();
                        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        CapturePreview("backupmesh-settings-toggle-focus-preview.png");
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "backupmesh-settings-check.txt"), await _window.VerifyPreviewSettingsControlsAsync());
                        _window.ShowBackupRules();
                        _window.ViewModel.SelectedBackupSet = _window.ViewModel.BackupSets.FirstOrDefault(set => set.Model.SourcePaths.Count > 1);
                        _window.OpenBackupRule(null);
                        var ruleDialog = _window.RuleEditor ?? throw new InvalidOperationException("Backup rule editor did not open in the page.");
                        if (_window.RuleEditorHost.Visibility != Visibility.Visible)
                            throw new InvalidOperationException("Backup rule editor is not visible inside the rules page.");
                        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (ruleDialog.SourcePathsList.Items.Count != 2 || ruleDialog.SelectedSourcePaths.Count != 2)
                            throw new InvalidOperationException("Backup rule did not offer and select both source paths.");
                        CapturePreview("backupmesh-new-rule-preview.png");
                        ruleDialog.ChooseRuleIconButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (!ruleDialog.RuleIconPopup.IsOpen || ruleDialog.RuleIconChoices.Items.Count != 72)
                            throw new InvalidOperationException("Backup rule icon picker did not show all 72 choices.");
                        CapturePreview("backupmesh-rule-icons-preview.png", (FrameworkElement)ruleDialog.RuleIconPopup.Child);
                        ruleDialog.RuleIconChoices.SelectedIndex = 42;
                        ruleDialog.RuleNameInput.Text = "가족 자료 백업";
                        if (ruleDialog.SelectedRuleIcon.IconId != 42 || ruleDialog.RuleIconPopup.IsOpen)
                            throw new InvalidOperationException("Backup rule icon selection failed.");
                        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        CapturePreview("backupmesh-rule-name-icon-preview.png");
                        ruleDialog.SourcePathSearch.Text = "/doc/img";
                        if (ruleDialog.SourcePathsList.Items.Count != 1 || ruleDialog.SelectedSourcePaths.Count != 2)
                            throw new InvalidOperationException("Searching source paths lost a hidden selection.");
                        ruleDialog.ClearVisibleSourcePathsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        if (ruleDialog.SelectedSourcePaths.Count != 1 || ruleDialog.SelectedSourcePaths[0] != "/doc/db")
                            throw new InvalidOperationException("Clearing visible source paths changed a hidden selection.");
                        ruleDialog.SourcePathSearch.Clear();
                        ruleDialog.SelectVisibleSourcePathsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        if (ruleDialog.SelectedSourcePaths.Count != 2)
                            throw new InvalidOperationException("Selecting all source paths failed.");
                        ruleDialog.CancelButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        if (_window.RuleEditorHost.Visibility != Visibility.Collapsed)
                            throw new InvalidOperationException("Cancel did not return to the rules list.");
                        var sampleRule = _window.ViewModel.Mappings.First();
                        _window.OpenBackupRule(sampleRule);
                        if (_window.RuleEditor?.HeadingText.Text != Localization.Text("Text_Editbackuprule_A40603"))
                            throw new InvalidOperationException("Edit did not open in the rules page.");
                        _window.CloseRuleEditor();
                        _window.OpenBackupRule(sampleRule, copy: true);
                        if (_window.RuleEditor?.HeadingText.Text != Localization.Text("RuleCopyHeading"))
                            throw new InvalidOperationException($"Duplicate heading was '{_window.RuleEditor?.HeadingText.Text}', expected '{Localization.Text("RuleCopyHeading")}'.");
                        _window.CloseRuleEditor();
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "backupmesh-rule-paths-check.txt"), "In-page add, cancel, edit, duplicate, search and visible bulk selection: passed.");
                        var agentDialog = new ConnectAgentWindow { Owner = _window };
                        agentDialog.Show();
                        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        CapturePreview("backupmesh-connect-agent-preview.png", agentDialog);
                        agentDialog.Close();
                        _window.ShowBackupRules();
                        if (_window.MainTabControl.SelectedItem is System.Windows.Controls.TabItem selectedTab)
                        {
                            selectedTab.Focus();
                            await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            CapturePreview("backupmesh-menu-selection-preview.png");
                        }
                    }
                    catch (Exception error)
                    {
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "backupmesh-agents-check.txt"), error.ToString());
                    }
                }), DispatcherPriority.ApplicationIdle);
            };
        }
        _window.Closing += (_, args) => { args.Cancel = true; _window.Hide(); };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Localization.Text("Text_OpenBackupMesh_1E9B33"), null, (_, _) => ShowWindow());
        menu.Items.Add(Localization.Text("Text_Backupnow_02A284"), null, (_, _) => _window.ViewModel.QueueSelectedBackups());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Localization.Text("Text_Exit_D17D84"), null, (_, _) => ExitApplication());
        Localization.LanguageChanged += (_, _) =>
        {
            menu.Items[0].Text = Localization.Text("Text_OpenBackupMesh_1E9B33");
            menu.Items[1].Text = Localization.Text("Text_Backupnow_02A284");
            menu.Items[3].Text = Localization.Text("Text_Exit_D17D84");
            UpdateTrayText();
        };

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
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button != Forms.MouseButtons.Left || _flyout is null) return;
            _flyoutAutoHideTimer.Stop();
            _flyout.ShowNearTray();
        };
        flyoutViewModel.OpenMainWindowRequested += (_, _) => { _flyout?.Hide(); ShowWindow(); };
        // Start now and Skip this time update the Storage configuration through TrayFlyoutViewModel;
        // collection changes then drive visibility and auto-hide behavior below.
        _flyoutAutoHideTimer.Tick += (_, _) => { _flyoutAutoHideTimer.Stop(); _flyout?.Hide(); };
        _flyout.UserInteracted += (_, _) => _flyoutAutoHideTimer.Stop();
        _window.ViewModel.Jobs.CollectionChanged += (_, _) => ScheduleFlyoutStateUpdate();
        flyoutViewModel.PendingArrivals.CollectionChanged += (_, _) => ScheduleFlyoutStateUpdate();

        if (!preview) _window.ViewModel.StartDeviceMonitoring();
        ShowWindow();
    }

    // Fires on every job-list/pending-arrival refresh, not only on a real state transition, so the two
    // "_was..." fields gate auto-show to the actual edge - otherwise it would reappear on every poll tick
    // for the whole duration of a backup instead of once at the start.
    //
    // The popup is transient even when a pending-arrival card exists. The card remains in the ViewModel
    // and can be reopened with a single tray-icon click; keeping the window itself open for the whole
    // arrival delay made a 30-minute decision behave like a permanently pinned toast.
    private void UpdateFlyoutState()
    {
        if (_window is null || _flyout is null) return;
        var isBackingUp = _window.ViewModel.Jobs.Any(job => job.State == "RUNNING");
        var awaitingDecision = _flyout.ViewModel.HasPendingArrivals;
        var justStartedNeedingAttention = (awaitingDecision && !_wasAwaitingDecision) || (isBackingUp && !_wasBackingUp);

        if (justStartedNeedingAttention && _window.ViewModel.ShowFlyoutOnBackupStart && !IsFullScreenAppActive())
        {
            _flyout.ShowNearTray();
            _flyoutAutoHideTimer.Stop();
            _flyoutAutoHideTimer.Start();
        }
        else if (_wasAwaitingDecision && !awaitingDecision && !isBackingUp)
        {
            _flyoutAutoHideTimer.Stop();
            _flyout.Hide();
        }

        _wasBackingUp = isBackingUp;
        _wasAwaitingDecision = awaitingDecision;
        UpdateTrayText();
    }

    // Both projections refresh with Clear()+Add(), which raises several CollectionChanged events for
    // one logical state update. Coalescing them onto the dispatcher prevents a momentary empty
    // collection from hiding and immediately re-showing the flyout on every polling tick.
    private void ScheduleFlyoutStateUpdate()
    {
        if (_flyoutStateUpdateScheduled) return;
        _flyoutStateUpdateScheduled = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _flyoutStateUpdateScheduled = false;
            UpdateFlyoutState();
        }));
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

    protected override void OnExit(ExitEventArgs e)
    {
        _openRequestWait?.Unregister(null);
        _openRequest?.Dispose();
        if (_ownsInstance) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
