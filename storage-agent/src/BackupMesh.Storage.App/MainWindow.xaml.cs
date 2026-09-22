using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Navigation;

namespace BackupMesh.Storage.App;

public partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow(bool demoMode = false, string? serviceEndpoint = null)
    {
        var effectiveServiceEndpoint = serviceEndpoint ?? (demoMode ? "http://127.0.0.1:1/api/v1/" : null);
        ViewModel = new MainWindowViewModel(
            demoMode,
            effectiveServiceEndpoint is null ? null : new SourceCatalogClient(effectiveServiceEndpoint),
            loadLocalState: !demoMode,
            configurationClient: effectiveServiceEndpoint is null ? null : new StorageConfigurationClient(effectiveServiceEndpoint),
            jobClient: effectiveServiceEndpoint is null ? null : new BackupJobClient(effectiveServiceEndpoint),
            pairingClient: effectiveServiceEndpoint is null ? null : new PairingClient(effectiveServiceEndpoint),
            connectionsClient: effectiveServiceEndpoint is null ? null : new SourceConnectionsClient(effectiveServiceEndpoint),
            nearbyPairingClient: effectiveServiceEndpoint is null ? null : new NearbyPairingClient(effectiveServiceEndpoint));
        InitializeComponent();
        DataContext = ViewModel;
    }

    // TabControl.SelectionChanged is the same routed event every descendant Selector (ListBox, ComboBox,
    // DataGrid) raises, and it bubbles - so this also fires for their selection changes. Only react when
    // the TabControl itself is the actual source, not just the routing ancestor.
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TabControl) ViewModel.ClearFooterStatusOnTabChange();
    }

    private void OnProjectLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri.Scheme != "https" || e.Uri.Host != "github.com") return;
        var address = e.Uri.AbsoluteUri;
        if (Localization.Source.Culture.TwoLetterISOLanguageName == "ko")
            address = address.Replace("/docs/USER_GUIDE.md", "/docs/USER_GUIDE.ko.md");
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(Localization.Text("LinkOpenFailed") + "\n" + address, "BackupMesh");
        }
    }

    private void OnAddMappingClick(object sender, RoutedEventArgs e) => OpenBackupRule(null);

    private void OnGoToComputersClick(object sender, RoutedEventArgs e) => ComputersTabItem.IsSelected = true;

    private void OnGoToBackupsClick(object sender, RoutedEventArgs e) => BackupsTabItem.IsSelected = true;

    private void OnEditMappingClick(object sender, RoutedEventArgs e) => OpenBackupRule(ViewModel.SelectedMapping);

    private void OnMappingDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindAncestor<DataGridRow>(source) is null) return;
        OpenBackupRule(ViewModel.SelectedMapping);
        e.Handled = true;
    }

    private void OpenBackupRule(MappingViewModel? mapping)
    {
        var dialog = new BackupRuleWindow(ViewModel, mapping) { Owner = this };
        dialog.ShowDialog();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
