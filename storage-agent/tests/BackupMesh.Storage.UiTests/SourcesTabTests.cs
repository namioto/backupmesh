using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace BackupMesh.Storage.UiTests;

public sealed class SourcesTabTests : IClassFixture<StorageAppFixture>
{
    private readonly StorageAppFixture _fixture;

    public SourcesTabTests(StorageAppFixture fixture)
    {
        _fixture = fixture;
        _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SourcesMappingsTab")).AsTabItem().Select();
    }

    private AutomationElement Find(string automationId)
    {
        var element = Retry.WhileNull(
            () => _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            TimeSpan.FromSeconds(10)).Result;
        Assert.True(element is not null, $"No element with AutomationId '{automationId}' was found.");
        return element!;
    }

    [Fact]
    public void PairSourceButton_IsPresentAndEnabled()
    {
        var button = Find("PairSourceButton").AsButton();
        Assert.True(button.IsEnabled, "PairSourceButton was disabled.");
    }

    /// <summary>
    /// The app runs with --demo (see StorageAppFixture), so there is no Storage Service on
    /// 127.0.0.1:7444 for PairSourceCommand to reach. Invoking it must fail gracefully - reporting
    /// the error on the footer status - rather than hanging, crashing, or opening
    /// PairingDetailsWindow with no data. That dialog itself needs a live paired session to test
    /// and is out of reach of this fixture.
    /// </summary>
    [Fact]
    public void PairSourceButton_ReportsAFailureWhenNoStorageServiceIsRunning()
    {
        Find("PairSourceButton").AsButton().Invoke();

        var footer = Find("FooterStatusText");
        Retry.WhileFalse(() => footer.Name.Contains("Pairing session could not be created", StringComparison.Ordinal), TimeSpan.FromSeconds(10));

        _fixture.TrySaveScreenshot("sources-after-pair-attempt.png");
        Assert.Contains("Pairing session could not be created", footer.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingsGrid_IsReachableByAutomationId()
    {
        Assert.NotNull(Find("MappingsGrid").AsDataGridView());
    }

    [Fact]
    public void SourceConnectionsGrid_IsReachableByAutomationId()
    {
        Assert.NotNull(Find("SourceConnectionsGrid").AsDataGridView());
    }

    [Fact]
    public void RevokeAndRestoreAccessButtons_ArePresent()
    {
        Assert.NotNull(Find("RevokeSourceButton").AsButton());
        Assert.NotNull(Find("UnrevokeSourceButton").AsButton());
    }

    [Fact]
    public void RePairSourceButton_IsPresent()
    {
        Assert.NotNull(Find("RePairSourceButton").AsButton());
    }

    [Fact]
    public void RenameAndForgetSourceButtons_ArePresent()
    {
        Assert.NotNull(Find("RenameSourceButton").AsButton());
        Assert.NotNull(Find("ForgetSourceButton").AsButton());
    }

    /// <summary>
    /// Trigger devices are configured per selected row in the Backups grid, not the "what to back up"
    /// creation combo (a study found evaluators expected the former). With no row selected, the trigger
    /// controls are Collapsed and absent from the automation tree, and this placeholder shows instead.
    /// </summary>
    [Fact]
    public void TriggerControlsShowAPlaceholderWithNoBackupSelected()
    {
        Assert.Contains(_fixture.MainWindow.FindAllDescendants(), element => element.Name == "Select a backup above to choose which devices should start it automatically.");
        Assert.Null(_fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("TriggerDevicesListBox")));
    }

    /// <summary>
    /// The Collapsed-by-default assertion above only proves the controls are correctly absent when they
    /// should be - it says nothing about whether they're actually reachable once a backup *is* selected.
    /// P0-2 (this project's own history) was exactly a control present in XAML but pushed off-screen and
    /// found only by a human looking at a screenshot; this is the automated guard for that failure mode
    /// on the controls this commit introduced. Creates a real device and mapping (RegisterDevice needs no
    /// native dialog, unlike RegisterFolder) since the demo fixture starts with none, and removes the
    /// mapping afterward - which also resets SelectedMapping to null - so it leaves the shared fixture's
    /// state the way the placeholder test above expects to find it, regardless of run order.
    /// </summary>
    [Fact]
    public void TriggerControlsAreReachableWithABackupSelected()
    {
        _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("DevicesTab")).AsTabItem().Select();
        var drives = Find("AvailableDrivesCombo").AsComboBox();
        Retry.WhileEmpty(() => drives.Items, TimeSpan.FromSeconds(10));
        drives.Select(0);
        Find("RegisterDeviceButton").AsButton().Invoke();

        _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SourcesMappingsTab")).AsTabItem().Select();
        var backupSets = Find("BackupSetCombo").AsComboBox();
        Retry.WhileEmpty(() => backupSets.Items, TimeSpan.FromSeconds(10));
        backupSets.Select(0);
        var devices = Find("MappingDeviceCombo").AsComboBox();
        Retry.WhileEmpty(() => devices.Items, TimeSpan.FromSeconds(10));
        devices.Select(0);
        Find("DestinationFolderInput").AsTextBox().Text = "BackupMesh\\UiTestTrigger";
        Find("AddMappingButton").AsButton().Invoke();

        var grid = Find("MappingsGrid");
        var row = Retry.WhileNull(() => grid.FindFirstChild(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)), TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(row);
        row!.Patterns.SelectionItem.Pattern.Select();

        try
        {
            Assert.NotNull(Find("TriggerDevicesListBox").AsListBox());
            Assert.NotNull(Find("AnyDeviceTriggerRadio"));
            Assert.NotNull(Find("AllDevicesTriggerRadio"));
            Assert.NotNull(Find("TriggerSummaryText"));
        }
        finally
        {
            Find("RemoveMappingButton").AsButton().Invoke();
        }
    }

    [Fact]
    public void AddAndRemoveLocalBackupSetButtons_ArePresent()
    {
        Assert.NotNull(Find("AddLocalBackupSetButton").AsButton());
        Assert.NotNull(Find("RemoveLocalBackupSetButton").AsButton());
    }

    /// <summary>
    /// "This PC" is always shown as a computer, even with no local Backup Sets configured yet. The
    /// Computers grid merges what used to be a separate tree (unused by evaluators in a first-click
    /// study: 0/4) and a Connections grid that never listed "This PC" at all - so this now checks the
    /// one merged grid instead of a tree that no longer exists.
    /// </summary>
    [Fact]
    public void ThisPCIsAlwaysListedInTheComputersGrid()
    {
        var grid = Find("SourceConnectionsGrid");
        Assert.Contains(grid.FindAllDescendants(), element => element.Name == "This PC");
    }
}
