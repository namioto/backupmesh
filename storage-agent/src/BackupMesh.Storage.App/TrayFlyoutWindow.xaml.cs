using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace BackupMesh.Storage.App;

// A small, non-activating popup anchored near the tray icon (Windows tray flyouts do not expose their own
// screen position via any public WPF/WinForms API without extra native calls - see ShowNearTray below for
// the approximation used here). Two things about "borderless, ShowActivated=False" popups turned out to
// need verification rather than assumption, both noted where they're handled:
//
//   1. ShowActivated="False" is necessary but not sufficient to guarantee the window never takes keyboard
//      focus - it only suppresses the *initial* activation on Show(). If the user clicks a control inside
//      the flyout (e.g. "Cancel"), Windows activates it like any other click target, which is desired (the
//      button needs focus to receive the click) but means the flyout can still become active later.
//   2. Because of (1), Deactivated only fires if the window was activated at some point - a user who
//      glances at the flyout and clicks elsewhere on the desktop without ever clicking inside it will not
//      trigger Deactivated at all. Deactivated is wired up here as a (harmless) secondary path, but the
//      primary dismiss-on-outside-click mechanism is a WH_MOUSE_LL low-level mouse hook, which sees every
//      mouse-down system-wide regardless of this window's activation state. Tradeoff: this requires
//      SetWindowsHookEx/UnhookWindowsHookEx native interop and a hook that must be installed only while
//      the flyout is visible and always removed when it is hidden/closed (done in IsVisibleChanged/Closed
//      below) to avoid leaking a systemwide hook.
//
// One corollary of never taking initial focus: Escape only dismisses the flyout once it already has
// keyboard focus (i.e. after the user has clicked something inside it) - a bare Escape press while some
// other app is still focused has nowhere in this window to land. This mirrors the same non-negotiable
// tradeoff Windows' own volume/network flyouts avoid only by actually taking focus on open, which this
// design explicitly does not do.
public partial class TrayFlyoutWindow : Window
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Kept as a field (not a local/lambda) so the delegate is never garbage-collected while the unmanaged
    // hook still holds a reference to it - a classic and otherwise-silent source of a crash deep inside
    // user32 the first time Windows calls back into a collected delegate.
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _mouseHook = IntPtr.Zero;

    public TrayFlyoutViewModel ViewModel { get; }
    public event EventHandler? UserInteracted;

    public TrayFlyoutWindow(TrayFlyoutViewModel viewModel)
    {
        ViewModel = viewModel;
        _mouseProc = MouseHookCallback;
        InitializeComponent();
        DataContext = ViewModel;

        IsVisibleChanged += (_, _) => { if (IsVisible) InstallMouseHook(); else RemoveMouseHook(); };
        Closed += (_, _) => RemoveMouseHook();
        Deactivated += (_, _) => Hide();
        PreviewMouseDown += (_, _) => UserInteracted?.Invoke(this, EventArgs.Empty);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    // Windows tray icons (NotifyIcon) don't publish a reliable on-screen rectangle through any public
    // WPF/WinForms API - the closest native answer is Shell_NotifyIconGetRect, which needs the icon's GUID
    // and involves enough extra COM/interop plumbing that it isn't worth it for a v1: this anchors to the
    // bottom-right corner of the primary screen's work area instead, which is where Windows' own tray
    // flyouts (volume, network, etc.) appear whether or not the specific icon they belong to is visible in
    // the overflow tray. If pixel-perfect per-icon anchoring is wanted later, Shell_NotifyIconGetRect is
    // the documented path.
    public void ShowNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - Height - 12;
        Show();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(currentModule?.ModuleName), 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN))
        {
            var point = Marshal.PtrToStructure<Point32>(lParam);
            var outsideWindow = point.X < Left || point.X > Left + Width || point.Y < Top || point.Y > Top + Height;
            // Dispatched rather than hidden inline: this callback runs on the hook's install thread inside
            // a low-level systemwide hook, and WPF API (Hide()) should only ever be touched from the
            // dispatcher it's already on for this app - BeginInvoke defers it back there safely.
            if (outsideWindow) Dispatcher.BeginInvoke(new Action(Hide));
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }
}
