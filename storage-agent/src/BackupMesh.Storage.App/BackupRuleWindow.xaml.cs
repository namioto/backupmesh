using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace BackupMesh.Storage.App;

public partial class BackupRuleWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly MappingViewModel? _existing;
    private bool _initializing = true;

    public BackupRuleWindow(MainWindowViewModel viewModel, MappingViewModel? existing = null)
    {
        _viewModel = viewModel;
        _existing = existing;
        InitializeComponent();
        DataContext = viewModel;

        BackupSetCombo.SelectedItem = existing?.BackupSet ?? viewModel.SelectedBackupSet ?? viewModel.BackupSets.FirstOrDefault();
        TargetDeviceCombo.SelectedItem = existing is null
            ? viewModel.BackupDestinations.FirstOrDefault()
            : viewModel.BackupDestinations.FirstOrDefault(option => option.Device?.Id == existing.Device.Id);
        DestinationInput.Text = existing?.DestinationFolder ?? string.Empty;
        EnabledCheckBox.IsChecked = existing?.Enabled ?? true;
        if (existing is not null)
        {
            Title = "Edit backup rule";
            HeadingText.Text = "Edit backup rule";
            SaveButton.Content = "Save changes";
        }
        _initializing = false;
        if (existing is null) OnTargetChanged(TargetDeviceCombo, null!);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        ValidationText.Text = string.Empty;
        var error = await _viewModel.SaveMappingAsync(
            _existing,
            BackupSetCombo.SelectedItem as BackupSetViewModel,
            TargetDeviceCombo.SelectedItem as BackupDestinationOptionViewModel,
            DestinationInput.Text,
            EnabledCheckBox.IsChecked == true);
        SaveButton.IsEnabled = true;
        if (error is null) DialogResult = true;
        else ValidationText.Text = error;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (TargetDeviceCombo.SelectedItem is not BackupDestinationOptionViewModel option)
        {
            ValidationText.Text = "Choose a target storage device first.";
            return;
        }
        var root = option.Root;
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

    private void OnChooseStorageFolderClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a drive or folder where backups will be stored.",
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        TargetDeviceCombo.SelectedItem = _viewModel.AddFolderDestination(dialog.SelectedPath);
    }

    private void OnBackupSetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        SourcePathsText.Text = (BackupSetCombo.SelectedItem as BackupSetViewModel)?.SourcePathsDisplay ?? string.Empty;

    private void OnTargetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || TargetDeviceCombo.SelectedItem is not BackupDestinationOptionViewModel option || string.IsNullOrWhiteSpace(option.Root)) return;
        var repositoryPath = _existing?.RepositoryPath ?? Path.Combine("BackupMesh", BackupSetCombo.SelectedItem is BackupSetViewModel set ? set.Model.Name : "Backup");
        DestinationInput.Text = Path.Combine(option.Root, repositoryPath);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
