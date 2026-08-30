using System.Windows;
using System.Windows.Controls;

namespace BackupMesh.Storage.App;

public partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow(bool demoMode = false, string? serviceEndpoint = null)
    {
        ViewModel = new MainWindowViewModel(
            demoMode,
            serviceEndpoint is null ? null : new SourceCatalogClient(serviceEndpoint),
            configurationClient: serviceEndpoint is null ? null : new StorageConfigurationClient(serviceEndpoint),
            jobClient: serviceEndpoint is null ? null : new BackupJobClient(serviceEndpoint));
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
}
