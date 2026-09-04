using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace BackupMesh.Storage.UiTests;

/// <summary>
/// Launches the Storage Agent UI once per test class and tears it down afterwards.
/// The app runs with --demo so it never contacts the Windows service on 127.0.0.1:7444,
/// which keeps the tests independent of whatever else is running on the machine.
/// </summary>
public sealed class StorageAppFixture : IDisposable
{
    private readonly Application _app;

    public StorageAppFixture()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "BackupMesh.Storage.App.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                $"Storage Agent executable not found at '{executable}'. Build BackupMesh.Storage.App first.",
                executable);
        }

        Automation = new UIA3Automation();
        _app = Application.Launch(new ProcessStartInfo(executable, "--demo"));
        MainWindow = _app.GetMainWindow(Automation, TimeSpan.FromSeconds(30))
                     ?? throw new InvalidOperationException("The Storage Agent main window did not appear within 30 seconds.");
    }

    public UIA3Automation Automation { get; }

    public Window MainWindow { get; }

    // Dialogs opened via Window.ShowDialog() (e.g. RegisterDeviceWindow) surface as their own top-level
    // window in the automation tree, not as a descendant of MainWindow - this is how a test finds one.
    public Window[] GetAllTopLevelWindows() => _app.GetAllTopLevelWindows(Automation);

    public void Dispose()
    {
        // Closing the window is cancelled by the app and only hides it to the tray, so kill outright.
        try
        {
            _app.Kill();
        }
        catch (Exception)
        {
            // The process may already be gone.
        }

        _app.Dispose();
        Automation.Dispose();
    }
}
