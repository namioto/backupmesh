using System.IO;
using System.Windows;
using System.Windows.Data;
using BackupMesh.Storage.Core;
using Forms = System.Windows.Forms;

namespace BackupMesh.Storage.App;

public partial class BackupRuleWindow : System.Windows.Controls.UserControl
{
    public event Action? CloseRequested;
    private readonly MainWindowViewModel _viewModel;
    private readonly MappingViewModel? _existing;
    private bool _initializing = true;
    private bool _updatingPathSelection;
    private List<SourcePathOption> _sourcePaths = [];
    private ListCollectionView? _sourcePathView;

    internal IReadOnlyList<string> SelectedSourcePaths => _sourcePaths.Where(path => path.IsSelected).Select(path => path.Path).ToArray();

    public BackupRuleWindow(MainWindowViewModel viewModel, MappingViewModel? existing = null, bool copy = false)
    {
        _viewModel = viewModel;
        _existing = copy ? null : existing;
        InitializeComponent();
        DataContext = viewModel;

        viewModel.SelectedBackupSet = existing?.BackupSet ?? (viewModel.SelectedBackupSet is { } selected && viewModel.BackupSets.Contains(selected)
            ? selected : viewModel.BackupSets.FirstOrDefault());
        TargetDeviceCombo.SelectedItem = existing is null
            ? viewModel.BackupDestinations.FirstOrDefault()
            : viewModel.BackupDestinations.FirstOrDefault(option => option.Device?.Id == existing.Device.Id);
        DestinationInput.Text = existing is null ? string.Empty : copy
            ? CopyDestination(existing.DestinationFolder, viewModel.Mappings.Select(mapping => mapping.DestinationFolder))
            : existing.DestinationFolder;
        BackupIntervalInput.Text = (existing?.BackupIntervalMinutes ?? 30).ToString();
        DelayWhenBusyCheckBox.IsChecked = existing?.DelayWhenBusy ?? true;
        UploadLimitInput.Text = existing?.UploadLimitKiBps?.ToString() ?? string.Empty;
        if (copy)
        {
            HeadingText.Text = Localization.Text("RuleCopyHeading");
            SaveButton.Content = Localization.Text("RuleCopyAction");
        }
        else if (existing is not null)
        {
            HeadingText.Text = Localization.Text("Text_Editbackuprule_A40603");
            SaveButton.Content = Localization.Text("Text_Savechanges_DD0AE7");
        }
        _initializing = false;
        SelectSourcePaths(existing?.SelectedSourcePaths ?? viewModel.SelectedBackupSet?.Model.SourcePaths);
        UpdateUploadLimitVisibility();
        if (existing is null) OnTargetChanged(TargetDeviceCombo, null!);
    }

    internal static string CopyDestination(string destination, IEnumerable<string> existing)
    {
        var candidate = destination + Localization.Text("RuleCopySuffix");
        for (var number = 2; existing.Contains(candidate, StringComparer.OrdinalIgnoreCase); number++)
            candidate = destination + Localization.Format("RuleCopySuffixNumber", number);
        return candidate;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (!int.TryParse(BackupIntervalInput.Text, out var interval) || interval is < 5 or > 1440)
        {
            ValidationText.Text = Localization.Text("RuleIntervalInvalid");
            return;
        }
        var uploadLimit = -1;
        if (!string.IsNullOrWhiteSpace(UploadLimitInput.Text)
            && (!int.TryParse(UploadLimitInput.Text, out uploadLimit) || uploadLimit is < 0 or > 1_048_576))
        {
            ValidationText.Text = Localization.Text("RuleUploadInvalid");
            return;
        }
        SaveButton.IsEnabled = false;
        var error = await _viewModel.SaveMappingAsync(
            _existing,
            BackupSetCombo.SelectedItem as BackupSetViewModel,
            TargetDeviceCombo.SelectedItem as BackupDestinationOptionViewModel,
            DestinationInput.Text,
            _existing?.Enabled ?? true,
            SelectedSourcePaths,
            interval,
            DelayWhenBusyCheckBox.IsChecked == true,
            uploadLimit);
        SaveButton.IsEnabled = true;
        if (error is null) CloseRequested?.Invoke();
        else ValidationText.Text = error;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (TargetDeviceCombo.SelectedItem is not BackupDestinationOptionViewModel option)
        {
            ValidationText.Text = Localization.Text("Text_Chooseatargetstoragedevicefirs_95F977");
            return;
        }
        var root = option.Root;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            ValidationText.Text = Localization.Text("Text_Theselectedstoragedeviceisnotc_837EBD");
            return;
        }
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = Localization.Text("Text_Chooseorcreatethefolderthatwil_1EAC62"),
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
            Description = Localization.Text("Text_Chooseadriveorfolderwherebacku_9995EA"),
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        var destination = Path.GetFullPath(dialog.SelectedPath);
        var option = _viewModel.BackupDestinations
            .Where(candidate => MainWindowViewModel.RelativeDestinationPath(candidate.Root, destination) is not null)
            .OrderByDescending(candidate => candidate.Root.Length)
            .FirstOrDefault();
        if (option is null)
        {
            var parent = Directory.GetParent(destination)?.FullName;
            if (parent is null)
            {
                ValidationText.Text = Localization.Text("Text_Chooseadestinationfolderinside_ACC42F");
                return;
            }
            option = _viewModel.AddFolderDestination(parent);
        }
        _initializing = true;
        TargetDeviceCombo.SelectedItem = option;
        _initializing = false;
        DestinationInput.Text = destination;
    }

    private void OnBackupSetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        SelectSourcePaths((BackupSetCombo.SelectedItem as BackupSetViewModel)?.Model.SourcePaths);
        UpdateUploadLimitVisibility();
    }

    private void UpdateUploadLimitVisibility()
    {
        var remote = (BackupSetCombo.SelectedItem as BackupSetViewModel)?.Model.SourceAgentId != LocalSourceIdentity.AgentId;
        UploadLimitSection.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        UploadDividerColumn.Width = remote ? new GridLength(1) : new GridLength(0);
        UploadSettingsColumn.Width = remote ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private void SelectSourcePaths(IReadOnlyList<string>? selected)
    {
        SourcePathSearch.Clear();
        var selectedPaths = selected?.ToHashSet(StringComparer.Ordinal) ?? [];
        var availablePaths = (BackupSetCombo.SelectedItem as BackupSetViewModel)?.Model.SourcePaths ?? [];
        _sourcePaths = availablePaths.Select(path => new SourcePathOption(path, selectedPaths.Contains(path), UpdateSourcePathsSummary)).ToList();
        _sourcePathView = new ListCollectionView(_sourcePaths)
        {
            Filter = item => item is SourcePathOption path && path.Path.Contains(SourcePathSearch.Text, StringComparison.CurrentCultureIgnoreCase)
        };
        SourcePathsList.ItemsSource = _sourcePathView;
        UpdateSourcePathsSummary();
    }

    private void OnSourcePathSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _sourcePathView?.Refresh();
        UpdateSourcePathsSummary();
    }

    private void OnSelectVisibleSourcePathsClick(object sender, RoutedEventArgs e) => SetVisibleSourcePaths(true);

    private void OnClearVisibleSourcePathsClick(object sender, RoutedEventArgs e) => SetVisibleSourcePaths(false);

    private void SetVisibleSourcePaths(bool selected)
    {
        _updatingPathSelection = true;
        try
        {
            foreach (SourcePathOption path in SourcePathsList.Items) path.IsSelected = selected;
        }
        finally
        {
            _updatingPathSelection = false;
        }
        UpdateSourcePathsSummary();
    }

    private void UpdateSourcePathsSummary()
    {
        if (_updatingPathSelection || SourcePathsList is null || SourcePathsSummary is null) return;
        SourcePathSearchHint.Visibility = string.IsNullOrEmpty(SourcePathSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        SourcePathsSummary.Text = Localization.Format("RulePathsSelected", _sourcePaths.Count(path => path.IsSelected), _sourcePaths.Count);
        var visible = SourcePathsList.Items.Count;
        SourcePathsMatches.Text = Localization.Format(string.IsNullOrEmpty(SourcePathSearch.Text) ? "RulePathsAll" : "RulePathsMatches", visible);
        SourcePathsEmptyText.Text = Localization.Text(_sourcePaths.Count == 0 ? "RulePathsEmpty" : "RulePathsNoMatches");
        SourcePathsEmptyText.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectVisibleSourcePathsButton.IsEnabled = visible > 0;
        ClearVisibleSourcePathsButton.IsEnabled = visible > 0;
    }

    private sealed class SourcePathOption(string path, bool selected, Action onChanged) : ObservableObject
    {
        private bool _selected = selected;
        public string Path { get; } = path;
        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (Set(ref _selected, value)) onChanged();
            }
        }
    }

    private void OnTargetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || TargetDeviceCombo.SelectedItem is not BackupDestinationOptionViewModel option || string.IsNullOrWhiteSpace(option.Root)) return;
        var repositoryPath = _existing?.RepositoryPath ?? Path.Combine("BackupMesh", BackupSetCombo.SelectedItem is BackupSetViewModel set ? set.Model.Name : "Backup");
        DestinationInput.Text = Path.Combine(option.Root, repositoryPath);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
