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

    /// <summary>
    /// Saves a screenshot of the main window. Capture can fail transiently while the window is
    /// still being composed, so failures are swallowed - a missing screenshot must not fail a test.
    /// </summary>
    public void TrySaveScreenshot(string fileName)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(directory);
            MainWindow.CaptureToFile(Path.Combine(directory, fileName));
        }
        catch (Exception)
        {
            // Screenshots are diagnostic only.
        }
    }

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
