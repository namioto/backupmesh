using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.App;

public partial class MainWindow : Window
{
    private bool _syncingTriggerSelection;

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
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncTriggerDevicesSelection();
    }

    private void OnSourceSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is BackupSetViewModel backupSet) ViewModel.SelectedBackupSet = backupSet;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedBackupSet) or nameof(MainWindowViewModel.Devices))
            SyncTriggerDevicesSelection();
    }

    // Reflects the selected Backup Set's already-saved trigger devices/policy into the list box and
    // checkbox without treating that as a user edit - selection-changed handling below is suppressed
    // for the duration.
    private void SyncTriggerDevicesSelection()
    {
        _syncingTriggerSelection = true;
        try
        {
            TriggerDevicesListBox.SelectedItems.Clear();
            var selectedSet = ViewModel.SelectedBackupSet;
            if (selectedSet is not null)
            {
                foreach (DeviceViewModel device in TriggerDevicesListBox.Items)
                    if (selectedSet.Model.TriggerDeviceIds.Contains(device.Id)) TriggerDevicesListBox.SelectedItems.Add(device);
                RequireAllTriggerDevicesCheckBox.IsChecked = selectedSet.Model.TriggerPolicy == BackupSetTriggerPolicy.AllAvailable;
            }
            else
            {
                RequireAllTriggerDevicesCheckBox.IsChecked = false;
            }
        }
        finally { _syncingTriggerSelection = false; }
    }

    private void OnTriggerDevicesSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingTriggerSelection || ViewModel.SelectedBackupSet is not { } selectedSet) return;
        var deviceIds = TriggerDevicesListBox.SelectedItems.Cast<DeviceViewModel>().Select(device => device.Id).ToArray();
        var policy = RequireAllTriggerDevicesCheckBox.IsChecked == true ? BackupSetTriggerPolicy.AllAvailable : BackupSetTriggerPolicy.AnyAvailable;
        ViewModel.UpdateBackupSetTriggers(selectedSet, deviceIds, policy);
    }
}
