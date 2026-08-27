using System.Windows;
using System.Windows.Controls;

namespace BackupMesh.Storage.App;

public partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow(bool demoMode = false)
    {
        ViewModel = new MainWindowViewModel(demoMode);
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void OnSourceSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is BackupSetViewModel backupSet) ViewModel.SelectedBackupSet = backupSet;
    }
}
