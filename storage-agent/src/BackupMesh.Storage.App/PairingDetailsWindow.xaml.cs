using System.Runtime.InteropServices;
using System.Windows;

namespace BackupMesh.Storage.App;

public partial class PairingDetailsWindow : Window
{
    private readonly string _clipboardText;

    public PairingDetailsWindow(PairingSessionDto pairing, string? rebindAgentName = null)
    {
        InitializeComponent();
        IntentText.Text = rebindAgentName is null
            ? Localization.Text("Text_ThiscodewillpairanewSourceAgen_7A5CFC")
            : Localization.Format("Text_Thiscodewillonlyrepairtheexist_40CE33", rebindAgentName);
        EndpointText.Text = pairing.ControlEndpoint;
        CodeText.Text = pairing.Code;
        FingerprintText.Text = pairing.CertificateSha256;
        ExpiresText.Text = Localization.Format("PairingExpiryHint", pairing.ExpiresAt.LocalDateTime);
        _clipboardText = $"Storage: {pairing.ControlEndpoint}\nPairing code: {pairing.Code}\nCertificate SHA-256: {pairing.CertificateSha256}";
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyValue(_clipboardText);

    private void OnCopyFieldClick(object sender, RoutedEventArgs e) => CopyValue((string)((System.Windows.Controls.Button)sender).Tag);

    private void CopyValue(string value)
    {
        try
        {
            System.Windows.Clipboard.SetText(value);
            CopyStatus.Text = Localization.Text("Text_Copied_D24981");
        }
        catch (ExternalException)
        {
            CopyStatus.Text = Localization.Text("Text_Couldnotaccesstheclipboard_75C339");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
