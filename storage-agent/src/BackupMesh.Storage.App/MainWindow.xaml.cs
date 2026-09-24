using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Windows.Navigation;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace BackupMesh.Storage.App;

public partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }
    private string _ruleQuery = string.Empty;
    private string? _ruleStatus;
    private Guid? _ruleComputerId;
    private Guid? _ruleDeviceId;
    private bool _ruleRefreshPending;
    private readonly HashSet<MappingViewModel> _observedRules = [];
    private int _rulePageIndex;
    private int _rulePageSize = 25;
    private bool _updatingRuleSelectAll;
    public ObservableCollection<MappingViewModel> RulePage { get; } = [];
    private string _agentQuery = string.Empty;
    private int _agentStatusIndex;
    private readonly HashSet<RemoteAgentViewModel> _observedAgents = [];
    private bool _agentRefreshPending;
    private bool _selectingAllAgents;
    private bool _updatingAgentSelectAll;

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
        UpdateFooterLayout();
        System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.Mappings).Filter = MatchesRuleFilter;
        ApplyRuleSort();
        foreach (var mapping in ViewModel.Mappings) { _observedRules.Add(mapping); mapping.PropertyChanged += OnRuleChanged; }
        ViewModel.Mappings.CollectionChanged += OnRulesChanged;
        UpdateRulePage();
        System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.Sources).Filter = MatchesSourceAgent;
        System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.NearbyComputers).Filter = MatchesNearbyAgent;
        ViewModel.Sources.CollectionChanged += OnAgentsCollectionChanged;
        OnAgentsCollectionChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        Localization.LanguageChanged += (_, _) => OnRuleFilterChanged(this, new RoutedEventArgs());
    }

    private void OnMinimizeWindowClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeWindowClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindowClick(object sender, RoutedEventArgs e) => Close();

    // TabControl.SelectionChanged is the same routed event every descendant Selector (ListBox, ComboBox,
    // DataGrid) raises, and it bubbles - so this also fires for their selection changes. Only react when
    // the TabControl itself is the actual source, not just the routing ancestor.
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.TabControl) return;
        ViewModel.ClearFooterStatusOnTabChange();
        UpdateFooterLayout();
    }

    private void UpdateFooterLayout()
    {
        if (FooterRow is null || StatusFooter is null) return;
        var isDesignTab = BackupsTabItem?.IsSelected == true || ComputersTabItem?.IsSelected == true || SettingsTabItem?.IsSelected == true;
        FooterRow.Height = new GridLength(isDesignTab ? 0 : 34);
        StatusFooter.Visibility = isDesignTab ? Visibility.Collapsed : Visibility.Visible;
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

    private void OnRuleFilterChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel) return;
        _ruleQuery = RuleSearch.Text.Trim();
        _ruleStatus = RuleStatusFilter.SelectedIndex > 0 ? (RuleStatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() : null;
        _ruleComputerId = (RuleComputerFilter.SelectedItem as RuleComputerOption)?.Id;
        _ruleDeviceId = (RuleDeviceFilter.SelectedItem as DeviceViewModel)?.Id;
        System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.Mappings).Refresh();
        UpdateRulePage(resetPage: true);
    }

    private void OnRuleSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel) return;
        ApplyRuleSort();
    }

    private void ApplyRuleSort()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.Mappings);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(RuleSort.SelectedIndex == 1
            ? new SortDescription(nameof(MappingViewModel.RuleName), ListSortDirection.Ascending)
            : new SortDescription(nameof(MappingViewModel.LastBackupAt), ListSortDirection.Descending));
        UpdateRulePage(resetPage: true);
    }

    private void UpdateRulePage(bool resetPage = false)
    {
        var filtered = System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.Mappings).Cast<MappingViewModel>().ToArray();
        var pages = Math.Max(1, (filtered.Length + _rulePageSize - 1) / _rulePageSize);
        _rulePageIndex = resetPage ? 0 : Math.Min(_rulePageIndex, pages - 1);
        var selected = ViewModel.SelectedMapping;
        RulePage.Clear();
        foreach (var mapping in filtered.Skip(_rulePageIndex * _rulePageSize).Take(_rulePageSize)) RulePage.Add(mapping);
        ViewModel.SelectedMapping = selected is not null && RulePage.Contains(selected) ? selected : null;
        var first = filtered.Length == 0 ? 0 : _rulePageIndex * _rulePageSize + 1;
        var last = Math.Min(filtered.Length, first + RulePage.Count - 1);
        RuleCountText.Text = filtered.Length == 0 ? Localization.Text("RuleCountEmpty") : Localization.Format("RuleCountRange", ViewModel.Mappings.Count, first, last);
        RulePageButtons.Children.Clear();
        var visiblePages = Enumerable.Range(0, Math.Min(3, pages))
            .Concat(Enumerable.Range(Math.Max(0, _rulePageIndex - 1), Math.Min(3, pages - Math.Max(0, _rulePageIndex - 1))))
            .Append(pages - 1).Distinct().Order().ToArray();
        var previousPage = -1;
        foreach (var page in visiblePages)
        {
            if (previousPage >= 0 && page - previousPage > 1)
                RulePageButtons.Children.Add(new TextBlock { Text = "···", Foreground = Brushes.SlateGray, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var pageButton = new Button
            {
                Content = (page + 1).ToString(), Tag = page, MinWidth = 34, Height = 36,
                Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2, 0, 2, 0),
                BorderThickness = new Thickness(0), Background = page == _rulePageIndex ? new SolidColorBrush(Color.FromRgb(228, 248, 245)) : Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(23, 43, 77))
            };
            System.Windows.Automation.AutomationProperties.SetName(pageButton, Localization.Format("RulePageAccessible", page + 1));
            pageButton.Click += OnRulePageNumberClick;
            RulePageButtons.Children.Add(pageButton);
            previousPage = page;
        }
        PreviousRulePageButton.IsEnabled = _rulePageIndex > 0;
        NextRulePageButton.IsEnabled = _rulePageIndex + 1 < pages;
    }

    private void OnPreviousRulePageClick(object sender, RoutedEventArgs e) { _rulePageIndex--; UpdateRulePage(); }
    private void OnNextRulePageClick(object sender, RoutedEventArgs e) { _rulePageIndex++; UpdateRulePage(); }
    private void OnRulePageNumberClick(object sender, RoutedEventArgs e)
    {
        _rulePageIndex = (int)((Button)sender).Tag;
        UpdateRulePage();
    }
    private void OnRulePageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel) return;
        _rulePageSize = RulePageSize.SelectedIndex switch { 1 => 50, 2 => 100, _ => 25 };
        UpdateRulePage(resetPage: true);
    }

    private void OnRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var mapping in _observedRules.Where(mapping => !ViewModel.Mappings.Contains(mapping)).ToArray())
        {
            mapping.PropertyChanged -= OnRuleChanged;
            _observedRules.Remove(mapping);
        }
        foreach (var mapping in ViewModel.Mappings)
            if (_observedRules.Add(mapping)) mapping.PropertyChanged += OnRuleChanged;
        UpdateRulePage();
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MappingViewModel.StatusDisplay) or nameof(MappingViewModel.LastBackupAt)) || _ruleRefreshPending) return;
        _ruleRefreshPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _ruleRefreshPending = false;
            System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.Mappings).Refresh();
            UpdateRulePage();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    public async Task<string> VerifyPreviewRuleControlsAsync()
    {
        int Count() => RulePage.Count;
        if (BackupRuleWindow.CopyDestination(@"D:\Backup", [@"D:\Backup" + Localization.Text("RuleCopySuffix")])
            != @"D:\Backup" + Localization.Format("RuleCopySuffixNumber", 2))
            throw new InvalidOperationException("Duplicate destination failed.");
        if (Count() != 12 || ViewModel.SelectedMapping is null) throw new InvalidOperationException("Preview rows or selection missing.");
        RuleSearch.Text = Localization.Source.Culture.TwoLetterISOLanguageName == "en" ? "Family" : "가족";
        if (Count() != 1) throw new InvalidOperationException("Rule search failed.");
        RuleSearch.Clear();
        RuleStatusFilter.SelectedIndex = 2;
        if (Count() != 1) throw new InvalidOperationException("Status filter failed.");
        RuleStatusFilter.SelectedIndex = 0;
        RuleComputerFilter.SelectedItem = ViewModel.RuleComputerOptions.First(option => option.Id == BackupMesh.Storage.Core.LocalSourceIdentity.AgentId);
        if (Count() != 12) throw new InvalidOperationException("Computer filter failed.");
        RuleComputerFilter.SelectedItem = null;
        RuleDeviceFilter.SelectedItem = ViewModel.Devices.First();
        if (Count() != 12) throw new InvalidOperationException("Device filter failed.");
        RuleDeviceFilter.SelectedItem = null;
        RuleSort.SelectedIndex = 1;
        if (Count() != 12 || RulePage.First().RuleName != ViewModel.Mappings.MinBy(mapping => mapping.RuleName, StringComparer.CurrentCulture)?.RuleName)
            throw new InvalidOperationException("Name sort failed.");
        RuleSort.SelectedIndex = 0;
        RuleStatusFilter.SelectedIndex = 4;
        var paused = ViewModel.Mappings.Single(mapping => !mapping.Enabled);
        if (Count() != 1) throw new InvalidOperationException("Paused filter failed.");
        paused.SetEnabledWithoutSaving(true);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (Count() != 0) throw new InvalidOperationException("Live status filter failed.");
        paused.SetEnabledWithoutSaving(false);
        RuleStatusFilter.SelectedIndex = 0;
        var previousTime = paused.LastBackupAt;
        paused.LastBackupAt = DateTimeOffset.Now.AddDays(1);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (RulePage.First() != paused) throw new InvalidOperationException("Live backup sort failed.");
        paused.LastBackupAt = previousTime;
        var original = ViewModel.Mappings.First();
        ViewModel.SelectedMapping = original;
        var originalTime = original.LastBackupAt;
        original.LastBackupAt = DateTimeOffset.Now.AddHours(1);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (ViewModel.SelectedMapping != original) throw new InvalidOperationException("Selection lost during live update.");
        original.LastBackupAt = originalTime;
        var extra = Enumerable.Range(0, 25).Select(index => new MappingViewModel(
            new BackupMesh.Storage.Core.BackupTargetMapping(Guid.NewGuid(), original.BackupSet.Id, original.Device.Id, $"page-check-{index}"),
            original.BackupSet, original.Device)).ToArray();
        try
        {
            foreach (var mapping in extra) ViewModel.Mappings.Add(mapping);
            if (Count() != 25 || !NextRulePageButton.IsEnabled) throw new InvalidOperationException("First page failed.");
            ((Button)RulePageButtons.Children.OfType<Button>().Last()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (Count() != 12 || !PreviousRulePageButton.IsEnabled) throw new InvalidOperationException("Numbered page failed.");
            ((Button)RulePageButtons.Children.OfType<Button>().First()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            NextRulePageButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (Count() != 12 || !PreviousRulePageButton.IsEnabled) throw new InvalidOperationException("Next page failed.");
            RulePageSize.SelectedIndex = 1;
            if (Count() != 37 || NextRulePageButton.IsEnabled) throw new InvalidOperationException("Page size failed.");
            RulePageSize.SelectedIndex = 0;
        }
        finally
        {
            foreach (var mapping in extra) ViewModel.Mappings.Remove(mapping);
            ViewModel.SelectedMapping = original;
        }
        MappingsGrid.UpdateLayout();
        var otherRule = RulePage.Skip(1).First();
        var otherRow = MappingsGrid.ItemContainerGenerator.ContainerFromItem(otherRule) as DataGridRow;
        var rowMore = otherRow is null ? null : FindDescendant<Button>(otherRow, button => Equals(button.ToolTip, Localization.Text("UX_b4db392797")));
        if (rowMore is null) throw new InvalidOperationException("Rule row menu button missing.");
        rowMore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!rowMore.ContextMenu.IsOpen || ViewModel.SelectedMapping != otherRule) throw new InvalidOperationException("Rule row menu failed.");
        rowMore.ContextMenu.IsOpen = false;
        RuleSelectAll.IsChecked = true;
        if (MappingsGrid.SelectedItems.Count != RulePage.Count || RuleEditButton.IsEnabled || !RuleDeleteButton.IsEnabled || SelectedRuleCountText.Text != Localization.Format("RuleSelectedCount", RulePage.Count))
            throw new InvalidOperationException("Rule select all failed.");
        RuleSelectAll.IsChecked = false;
        if (MappingsGrid.SelectedItems.Count != 0 || RuleDeleteButton.IsEnabled) throw new InvalidOperationException("Rule clear selection failed.");
        ViewModel.SelectedMapping = original;
        RuleToolbarMoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!RuleToolbarMoreButton.ContextMenu.IsOpen || RuleToolbarMoreButton.ContextMenu.Items[3] is not MenuItem { IsEnabled: true })
            throw new InvalidOperationException("Rule toolbar menu failed.");
        RuleToolbarMoreButton.ContextMenu.IsOpen = false;
        return "Search, status, computer, device, sort, single and all selection, live updates, numbered pagination, row and toolbar menus, duplicate path: passed.";
    }

    private void OnRuleFilterKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Delete || sender is not System.Windows.Controls.ComboBox filter) return;
        filter.SelectedItem = null;
        e.Handled = true;
    }

    private void OnAgentFilterChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel) return;
        _agentQuery = AgentSearch.Text.Trim();
        _agentStatusIndex = AgentStatusFilter.SelectedIndex;
        System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.Sources).Refresh();
        System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.NearbyComputers).Refresh();
    }

    private void OnAgentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var agent in _observedAgents.Where(agent => !ViewModel.Sources.Contains(agent)).ToArray())
        {
            agent.PropertyChanged -= OnAgentStatusChanged;
            _observedAgents.Remove(agent);
        }
        foreach (var agent in ViewModel.Sources)
            if (_observedAgents.Add(agent)) agent.PropertyChanged += OnAgentStatusChanged;
    }

    private void OnAgentStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RemoteAgentViewModel.ListStatusDisplay) || _agentRefreshPending) return;
        _agentRefreshPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _agentRefreshPending = false;
            System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.Sources).Refresh();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool MatchesSourceAgent(object item)
    {
        if (item is not RemoteAgentViewModel agent) return false;
        if (_agentQuery.Length > 0 && !agent.DisplayName.Contains(_agentQuery, StringComparison.CurrentCultureIgnoreCase)
            && !agent.Id.ToString().Contains(_agentQuery, StringComparison.OrdinalIgnoreCase)) return false;
        return _agentStatusIndex switch
        {
            1 => agent.Connection?.IsOnline == true,
            2 => agent.Connection?.IsOnline != true,
            3 => false,
            _ => true
        };
    }

    private bool MatchesNearbyAgent(object item)
    {
        if (item is not NearbyComputerViewModel agent || IncludeNearbyAgents.IsChecked != true || _agentStatusIndex is 1 or 2) return false;
        return _agentQuery.Length == 0 || agent.DisplayName.Contains(_agentQuery, StringComparison.CurrentCultureIgnoreCase)
            || agent.AgentId.ToString().Contains(_agentQuery, StringComparison.OrdinalIgnoreCase);
    }

    private void OnConnectNewAgentClick(object sender, RoutedEventArgs e)
    {
        new ConnectAgentWindow { Owner = this }.ShowDialog();
    }

    private void OnSourceAgentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_selectingAllAgents && e.AddedItems.Count > 0 && NearbyAgentsGrid is not null) NearbyAgentsGrid.UnselectAll();
        UpdateAgentSelection();
    }

    private void OnNearbyAgentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_selectingAllAgents && e.AddedItems.Count > 0 && SourceConnectionsGrid is not null) SourceConnectionsGrid.UnselectAll();
        UpdateAgentSelection();
    }

    private void UpdateAgentSelection()
    {
        if (AgentRenameButton is null || SourceConnectionsGrid is null || NearbyAgentsGrid is null) return;
        var sourceCount = SourceConnectionsGrid.SelectedItems.Count;
        var total = sourceCount + NearbyAgentsGrid.SelectedItems.Count;
        var singleConnected = total == 1 && sourceCount == 1 && SourceConnectionsGrid.SelectedItems[0] is RemoteAgentViewModel { HasConnection: true };
        AgentRenameButton.IsEnabled = singleConnected;
        AgentForgetButton.IsEnabled = singleConnected;
        _updatingAgentSelectAll = true;
        AgentSelectAll.IsChecked = total > 0 && total == SourceConnectionsGrid.Items.Count + NearbyAgentsGrid.Items.Count;
        _updatingAgentSelectAll = false;
    }

    private void OnAgentSelectAllChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingAgentSelectAll || SourceConnectionsGrid is null || NearbyAgentsGrid is null) return;
        _selectingAllAgents = true;
        try
        {
            if (AgentSelectAll.IsChecked == true) { SourceConnectionsGrid.SelectAll(); NearbyAgentsGrid.SelectAll(); }
            else { SourceConnectionsGrid.UnselectAll(); NearbyAgentsGrid.UnselectAll(); }
        }
        finally { _selectingAllAgents = false; }
        UpdateAgentSelection();
    }

    private void OnNearbyAgentRequestClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NearbyComputerViewModel agent }) return;
        NearbyAgentsGrid.UnselectAll();
        NearbyAgentsGrid.SelectedItem = agent;
        ViewModel.SelectedNearbyComputer = agent;
        AgentHelpExpander.Visibility = Visibility.Visible;
        AgentHelpExpander.IsExpanded = true;
        if (ViewModel.RequestNearbyPairingCommand.CanExecute(null)) ViewModel.RequestNearbyPairingCommand.Execute(null);
    }

    private void OnAgentMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RemoteAgentViewModel agent } button) return;
        SourceConnectionsGrid.UnselectAll();
        SourceConnectionsGrid.SelectedItem = agent;
        ViewModel.SelectedRemoteAgent = agent;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> matches) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && matches(match)) return match;
            if (FindDescendant(child, matches) is { } nested) return nested;
        }
        return null;
    }

    private void OnRenameAgentMenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HasSelectedSourceConnection) ViewModel.RenameSourceCommand.Execute(null);
    }

    private void OnForgetAgentMenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HasSelectedSourceConnection) ViewModel.ForgetSourceCommand.Execute(null);
    }

    private async void OnRefreshAgentStatusClick(object sender, RoutedEventArgs e) => await ViewModel.RefreshAgentConnectionsAsync();

    public async Task<string> VerifyPreviewAgentControlsAsync()
    {
        int SourceCount() => System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.Sources).Cast<object>().Count();
        int NearbyCount() => System.Windows.Data.CollectionViewSource.GetDefaultView(ViewModel.NearbyComputers).Cast<object>().Count();
        if (SourceCount() != 4 || NearbyCount() != 2) throw new InvalidOperationException("Preview agents missing.");
        AgentSearch.Text = Localization.Source.Culture.TwoLetterISOLanguageName == "en" ? "Family" : "가족";
        if (SourceCount() != 1 || NearbyCount() != 0) throw new InvalidOperationException("Agent search failed.");
        AgentSearch.Clear();
        AgentStatusFilter.SelectedIndex = 1;
        if (SourceCount() != 1 || NearbyCount() != 0) throw new InvalidOperationException("Online filter failed.");
        var online = ViewModel.Sources.First();
        var previousConnection = online.Connection;
        online.Connection = new SourceConnectionViewModel(new SourceConnectionDto(online.Id, online.DisplayName, online.DisplayName,
            DateTimeOffset.UtcNow.AddHours(-1), null, online.BackupSets.Count, false, DateTimeOffset.UtcNow.AddDays(90)));
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (SourceCount() != 0) throw new InvalidOperationException("Live agent status filter failed.");
        online.Connection = previousConnection;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (SourceCount() != 1) throw new InvalidOperationException("Agent status restore failed.");
        AgentStatusFilter.SelectedIndex = 3;
        if (SourceCount() != 0 || NearbyCount() != 2) throw new InvalidOperationException("Discovery filter failed.");
        AgentStatusFilter.SelectedIndex = 0;
        IncludeNearbyAgents.IsChecked = false;
        if (NearbyCount() != 0) throw new InvalidOperationException("Nearby toggle failed.");
        IncludeNearbyAgents.IsChecked = true;
        NearbyAgentsGrid.SelectedItem = ViewModel.NearbyComputers.First();
        if (ViewModel.SelectedNearbyComputer is null || ViewModel.SelectedRemoteAgent is not null) throw new InvalidOperationException("Nearby selection failed.");
        SourceConnectionsGrid.SelectedItem = ViewModel.Sources.First();
        if (ViewModel.SelectedRemoteAgent is null || ViewModel.SelectedNearbyComputer is not null) throw new InvalidOperationException("Source selection failed.");
        AgentSelectAll.IsChecked = true;
        if (SourceConnectionsGrid.SelectedItems.Count != 4 || NearbyAgentsGrid.SelectedItems.Count != 2 || AgentRenameButton.IsEnabled || AgentForgetButton.IsEnabled)
            throw new InvalidOperationException("Agent select all failed.");
        AgentSelectAll.IsChecked = false;
        if (SourceConnectionsGrid.SelectedItems.Count != 0 || NearbyAgentsGrid.SelectedItems.Count != 0) throw new InvalidOperationException("Agent clear selection failed.");
        SourceConnectionsGrid.SelectedItem = ViewModel.Sources.First();
        if (!AgentRenameButton.IsEnabled || !AgentForgetButton.IsEnabled) throw new InvalidOperationException("Single agent actions failed.");
        SourceConnectionsGrid.UpdateLayout();
        foreach (var agent in new[] { ViewModel.Sources.First(), ViewModel.Sources.Last() })
        {
            var row = SourceConnectionsGrid.ItemContainerGenerator.ContainerFromItem(agent) as DataGridRow;
            var more = row is null ? null : FindDescendant<Button>(row, button => Equals(button.ToolTip, Localization.Text("UX_bddb76c84d")));
            if (more is null) throw new InvalidOperationException("Agent row menu button missing.");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (!more.ContextMenu.IsOpen || more.ContextMenu.Items[2] is not MenuItem rename || rename.IsEnabled != agent.HasConnection)
                throw new InvalidOperationException("Agent row menu state failed.");
            more.ContextMenu.IsOpen = false;
        }
        IncludeNearbyAgents.IsChecked = false;
        var dialog = new ConnectAgentWindow { Owner = this };
        dialog.Show();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        foreach (var number in new[] { 1, 2, 3 })
            if (FindDescendant<TextBlock>(dialog, step =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(step) == $"ConnectAgentStep{number}") is null)
                throw new InvalidOperationException($"New agent guide step {number} missing.");
        if (IncludeNearbyAgents.IsChecked != false) throw new InvalidOperationException("New agent guide changed discovery filter.");
        dialog.Close();
        IncludeNearbyAgents.IsChecked = true;
        SourceConnectionsGrid.SelectedItem = ViewModel.Sources.First();
        HistoryTabItem.IsSelected = true;
        if (MainTabControl.SelectedContent != HistoryTabItem.Content) throw new InvalidOperationException("History navigation failed.");
        ComputersTabItem.IsSelected = true;
        return "Agent search, live status, discovery toggle, single and all selection, three-step setup guide, row menus and history navigation: passed.";
    }

    private bool MatchesRuleFilter(object item)
    {
        if (item is not MappingViewModel mapping) return false;
        if (_ruleQuery.Length > 0 &&
            !new[] { mapping.RuleName, mapping.SourcePathsDisplay, mapping.SourceAgentName, mapping.DeviceName }
                .Any(value => value.Contains(_ruleQuery, StringComparison.CurrentCultureIgnoreCase))) return false;
        if (_ruleStatus is not null && mapping.StatusDisplay != _ruleStatus) return false;
        if (_ruleComputerId is { } computerId && mapping.BackupSet.Model.SourceAgentId != computerId) return false;
        if (_ruleDeviceId is { } deviceId && mapping.Device.Id != deviceId) return false;
        return true;
    }

    private void OnGoToComputersClick(object sender, RoutedEventArgs e) => ComputersTabItem.IsSelected = true;

    private void OnGoToBackupsClick(object sender, RoutedEventArgs e) => BackupsTabItem.IsSelected = true;

    public void ShowBackupRules() => BackupsTabItem.IsSelected = true;

    public void ShowRemoteAgents() => ComputersTabItem.IsSelected = true;

    public void ShowSettings() => SettingsTabItem.IsSelected = true;

    public async Task<string> VerifyPreviewSettingsControlsAsync()
    {
        T Control<T>(string id) where T : DependencyObject => FindDescendant<T>((DependencyObject)SettingsTabItem.Content,
            control => System.Windows.Automation.AutomationProperties.GetAutomationId(control) == id)
            ?? throw new InvalidOperationException($"Settings control missing: {id}.");
        void CheckToggle(string id, Func<bool> value)
        {
            var toggle = Control<System.Windows.Controls.CheckBox>(id);
            var before = value();
            try
            {
                toggle.IsChecked = !before;
                if (value() == before) throw new InvalidOperationException($"Settings toggle binding failed: {id}.");
            }
            finally { toggle.IsChecked = before; }
        }

        if (Control<System.Windows.Controls.ComboBox>("LanguageCombo").SelectedValue?.ToString() != ViewModel.Language)
            throw new InvalidOperationException("Language selection binding failed.");
        var originalCulture = Localization.Source.Culture.Name;
        try
        {
            foreach (var language in new[] { "en", "ko" })
            {
                Localization.Initialize(language);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (RuleStatusFilter.Items[0] is not ComboBoxItem status ||
                    status.Content?.ToString() != Localization.Text("UX_aad0345e13") ||
                    RuleCountText.Text != Localization.Format("RuleCountRange", ViewModel.Mappings.Count, 1, RulePage.Count))
                    throw new InvalidOperationException($"Language switch did not update rule labels: {language}; status={(RuleStatusFilter.Items[0] as ComboBoxItem)?.Content}; count={RuleCountText.Text}; expected={Localization.Format("RuleCountRange", ViewModel.Mappings.Count, 1, RulePage.Count)}.");
            }
        }
        finally { Localization.Initialize(originalCulture); }
        CheckToggle("StartWithWindowsCheckBox", () => ViewModel.StartWithWindows);
        CheckToggle("NotifyOnDeviceArrivalCheckBox", () => ViewModel.NotifyOnDeviceArrival);
        CheckToggle("AutomaticBackupsCheckBox", () => ViewModel.AutomaticBackups);
        CheckToggle("ShowFlyoutOnBackupStartCheckBox", () => ViewModel.ShowFlyoutOnBackupStart);
        var delay = Control<System.Windows.Controls.TextBox>("DefaultArrivalDelayInput");
        var previousDelay = ViewModel.DefaultArrivalDelayMinutes;
        try
        {
            delay.Text = "12";
            delay.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            if (ViewModel.DefaultArrivalDelayMinutes != 12) throw new InvalidOperationException("Arrival delay binding failed.");
        }
        finally
        {
            delay.Text = previousDelay.ToString();
            delay.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        }
        if (Control<Button>("SaveSettingsButton").Command != ViewModel.SaveCommand ||
            Control<Button>("RotateStorageIdentityButton").Command != ViewModel.RotateStorageIdentityCommand ||
            StatusFooter.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Settings commands or footer layout failed.");
        return "Language, four toggles, arrival delay, save and recovery commands, footer layout: passed.";
    }

    private void OnGoToHistoryClick(object sender, RoutedEventArgs e) => HistoryTabItem.IsSelected = true;

    private void OnEditMappingClick(object sender, RoutedEventArgs e) => OpenBackupRule(ViewModel.SelectedMapping);

    private void OnDuplicateMappingClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedMapping is { } mapping)
            OpenBackupRule(mapping, copy: true);
    }

    private void OnRuleMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: MappingViewModel mapping } button) return;
        MappingsGrid.UnselectAll();
        MappingsGrid.SelectedItem = mapping;
        ViewModel.SelectedMapping = mapping;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private MappingViewModel[] SelectedRules() => MappingsGrid.SelectedItems.Cast<MappingViewModel>().ToArray();

    private void OnRuleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != MappingsGrid || RuleStartButton is null) return;
        var selected = SelectedRules();
        SelectedRuleCountBadge.Visibility = selected.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SelectedRuleCountText.Text = Localization.Format("RuleSelectedCount", selected.Length);
        RuleStartButton.IsEnabled = selected.Any(mapping => mapping.Enabled && mapping.Device.IsConnected);
        RuleEditButton.IsEnabled = selected.Length == 1;
        RuleDuplicateButton.IsEnabled = selected.Length == 1;
        RuleDeleteButton.IsEnabled = selected.Length > 0;
        _updatingRuleSelectAll = true;
        RuleSelectAll.IsChecked = RulePage.Count > 0 && selected.Length == RulePage.Count;
        _updatingRuleSelectAll = false;
    }

    private void OnRuleSelectAllChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingRuleSelectAll || MappingsGrid is null) return;
        if (RuleSelectAll.IsChecked == true) MappingsGrid.SelectAll();
        else MappingsGrid.UnselectAll();
    }

    private async void OnStartSelectedRulesClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRules();
        if (selected.Length > 0) await ViewModel.QueueMappingsAsync(selected);
    }

    private async void OnDeleteSelectedRulesClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRules();
        if (selected.Length > 0) await ViewModel.RemoveMappingsAsync(selected);
    }

    private void OnRuleToolbarMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var items = button.ContextMenu.Items;
        ((MenuItem)items[2]).IsEnabled = RuleStartButton.IsEnabled;
        ((MenuItem)items[3]).IsEnabled = RuleEditButton.IsEnabled;
        ((MenuItem)items[4]).IsEnabled = RuleDuplicateButton.IsEnabled;
        ((MenuItem)items[5]).IsEnabled = RuleDeleteButton.IsEnabled;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void OnDeleteMappingClick(object sender, RoutedEventArgs e) => ViewModel.RemoveMappingCommand.Execute(null);

    private void OnMappingDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindAncestor<DataGridRow>(source) is null) return;
        OpenBackupRule(ViewModel.SelectedMapping);
        e.Handled = true;
    }

    internal BackupRuleWindow? RuleEditor => RuleEditorHost.Children.OfType<BackupRuleWindow>().FirstOrDefault();

    internal void OpenBackupRule(MappingViewModel? mapping, bool copy = false)
    {
        BackupsTabItem.IsSelected = true;
        var editor = new BackupRuleWindow(ViewModel, mapping, copy);
        editor.CloseRequested += CloseRuleEditor;
        editor.KeyDown += OnRuleEditorKeyDown;
        RuleEditorHost.Children.Clear();
        RuleEditorHost.Children.Add(editor);
        RuleEditorHost.Visibility = Visibility.Visible;
    }

    private void OnRuleEditorKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseRuleEditor();
        e.Handled = true;
    }

    internal void CloseRuleEditor()
    {
        RuleEditorHost.Visibility = Visibility.Collapsed;
        RuleEditorHost.Children.Clear();
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
