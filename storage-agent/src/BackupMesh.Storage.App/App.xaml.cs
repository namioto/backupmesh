using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace BackupMesh.Storage.App;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _window = new MainWindow();
        _window.Closing += (_, args) => { args.Cancel = true; _window.Hide(); };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open BackupMesh", null, (_, _) => ShowWindow());
        menu.Items.Add("Back up now", null, (_, _) => _window.ViewModel.QueueSelectedBackups());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Pause automation", null, (_, _) => _window.ViewModel.TogglePause());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "backupmesh-tray.ico");
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = new Icon(iconPath),
            Text = "BackupMesh Storage Agent — starting",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
        _window.ViewModel.NotificationRequested += OnNotificationRequested;
        _window.ViewModel.StatusChanged += (_, status) => _trayIcon.Text = status.Length > 63 ? status[..63] : status;
        _window.ViewModel.StartDeviceMonitoring();
        ShowWindow();
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
        _window?.ViewModel.Dispose();
        Shutdown();
    }
}
