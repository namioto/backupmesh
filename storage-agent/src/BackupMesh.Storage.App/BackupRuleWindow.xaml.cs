using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace BackupMesh.Storage.App;

public partial class BackupRuleWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly MappingViewModel? _existing;

    public BackupRuleWindow(MainWindowViewModel viewModel, MappingViewModel? existing = null)
    {
        _viewModel = viewModel;
        _existing = existing;
        InitializeComponent();
        DataContext = viewModel;

        BackupSetCombo.SelectedItem = existing?.BackupSet ?? viewModel.SelectedBackupSet ?? viewModel.BackupSets.FirstOrDefault();
        TargetDeviceCombo.SelectedItem = existing?.Device ?? viewModel.SelectedDevice ?? viewModel.Devices.FirstOrDefault();
        DestinationInput.Text = existing?.RepositoryPath ?? string.Empty;
        EnabledCheckBox.IsChecked = existing?.Enabled ?? true;
        if (existing is not null)
        {
            Title = "Edit backup rule";
            HeadingText.Text = "Edit backup rule";
            SaveButton.Content = "Save changes";
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        ValidationText.Text = string.Empty;
        var error = await _viewModel.SaveMappingAsync(
            _existing,
            BackupSetCombo.SelectedItem as BackupSetViewModel,
            TargetDeviceCombo.SelectedItem as DeviceViewModel,
            DestinationInput.Text,
            EnabledCheckBox.IsChecked == true);
        SaveButton.IsEnabled = true;
        if (error is null) DialogResult = true;
        else ValidationText.Text = error;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (TargetDeviceCombo.SelectedItem is not DeviceViewModel device)
        {
            ValidationText.Text = "Choose a target storage device first.";
            return;
        }
        var root = device.CurrentRoot ?? device.LastKnownRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            ValidationText.Text = "The selected storage device is not currently available.";
            return;
        }
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose or create the folder that will contain this backup repository.",
            InitialDirectory = root,
            SelectedPath = root,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) DestinationInput.Text = dialog.SelectedPath;
    }

    private void OnRegisterStorageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new RegisterDeviceWindow(_viewModel) { Owner = this };
        Dispatcher.BeginInvoke(new Action(() =>
        {
            dialog.ShowDialog();
            TargetDeviceCombo.SelectedItem = _viewModel.SelectedDevice ?? TargetDeviceCombo.SelectedItem;
        }));
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
