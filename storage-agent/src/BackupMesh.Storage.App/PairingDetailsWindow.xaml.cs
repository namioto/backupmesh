using System.Runtime.InteropServices;
using System.Windows;

namespace BackupMesh.Storage.App;

public partial class PairingDetailsWindow : Window
{
    private readonly string _clipboardText;

    public PairingDetailsWindow(PairingSessionDto pairing)
    {
        InitializeComponent();
        EndpointText.Text = pairing.ControlEndpoint;
        CodeText.Text = pairing.Code;
        FingerprintText.Text = pairing.CertificateSha256;
        ExpiresText.Text = pairing.ExpiresAt.LocalDateTime.ToString("g");
        _clipboardText = $"Storage: {pairing.ControlEndpoint}\nPairing code: {pairing.Code}\nCertificate SHA-256: {pairing.CertificateSha256}\nExpires: {pairing.ExpiresAt.LocalDateTime:g}";
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

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
