using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace BackupMesh.Storage.App;

public partial class PairingDetailsWindow : Window
{
    private readonly string _clipboardText;
    private readonly PairingSessionDto _pairing;
    private readonly string? _localSourceAgentExe;
    private readonly string _localSourceConfigPath;

    public PairingDetailsWindow(PairingSessionDto pairing, string? rebindAgentName = null)
    {
        InitializeComponent();
        _pairing = pairing;
        IntentText.Text = rebindAgentName is null
            ? "This code will pair a new Source Agent."
            : $"This code will only re-pair the existing Source Agent \"{rebindAgentName}\" — no other Source can use it.";
        EndpointText.Text = pairing.ControlEndpoint;
        CodeText.Text = pairing.Code;
        FingerprintText.Text = pairing.CertificateSha256;
        ExpiresText.Text = pairing.ExpiresAt.LocalDateTime.ToString("g");
        _clipboardText = $"Storage: {pairing.ControlEndpoint}\nPairing code: {pairing.Code}\nCertificate SHA-256: {pairing.CertificateSha256}\nExpires: {pairing.ExpiresAt.LocalDateTime:g}";

        // Only offered for a brand-new Source pairing on this same PC (see
        // packaging/windows/Install-BackupMeshSource.ps1, which installs here with no admin rights
        // required); re-pairing an existing Source still goes through the manual command so its
        // existing config path is never guessed.
        var localSourceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupMesh", "Source");
        var candidateExe = Path.Combine(localSourceRoot, "backupmesh-agent.exe");
        _localSourceConfigPath = Path.Combine(localSourceRoot, "backupmesh.yaml");
        if (rebindAgentName is null && File.Exists(candidateExe) && File.Exists(_localSourceConfigPath))
        {
            _localSourceAgentExe = candidateExe;
            LocalPairPanel.Visibility = Visibility.Visible;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_clipboardText);
            CopyStatus.Text = "Copied.";
        }
        catch (ExternalException)
        {
            CopyStatus.Text = "Could not access the clipboard.";
        }
    }

    private async void OnPairLocalSourceClick(object sender, RoutedEventArgs e)
    {
        if (_localSourceAgentExe is null) return;
        PairLocalButton.IsEnabled = false;
        LocalPairStatus.Foreground = System.Windows.Media.Brushes.Black;
        LocalPairStatus.Text = "Pairing…";
        try
        {
            var startInfo = new ProcessStartInfo(_localSourceAgentExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("pair");
            startInfo.ArgumentList.Add("-config"); startInfo.ArgumentList.Add(_localSourceConfigPath);
            startInfo.ArgumentList.Add("-storage"); startInfo.ArgumentList.Add(_pairing.ControlEndpoint);
            startInfo.ArgumentList.Add("-code"); startInfo.ArgumentList.Add(_pairing.Code);
            startInfo.ArgumentList.Add("-fingerprint"); startInfo.ArgumentList.Add(_pairing.CertificateSha256);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the local Source Agent process.");
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                // Best-effort: the scheduled task's first run (before pairing existed) failed config
                // validation and gave up retrying, so it needs an explicit nudge to pick up the identity
                // `pair` just wrote. Not fatal if this fails - the task also restarts at next sign-in.
                try { Process.Start(new ProcessStartInfo("schtasks.exe", "/Run /TN \"BackupMesh Source Agent\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose(); } catch (Win32Exception) { }
                LocalPairStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
                LocalPairStatus.Text = "Paired and started.";
            }
            else
            {
                LocalPairStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
                LocalPairStatus.Text = $"Pairing failed: {(await stderrTask).Trim()}";
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            LocalPairStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
            LocalPairStatus.Text = $"Could not run the local Source Agent: {exception.Message}";
        }
        finally { PairLocalButton.IsEnabled = true; }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
