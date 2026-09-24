using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace BackupMesh.Storage.App;

public partial class ConnectAgentWindow : Window
{
    public ConnectAgentWindow() => InitializeComponent();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnInstallationGuideClick(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri.Scheme != Uri.UriSchemeHttps || e.Uri.Host != "github.com") return;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show("설치 안내를 열 수 없습니다.\n" + e.Uri.AbsoluteUri, "BackupMesh");
        }
    }
}
