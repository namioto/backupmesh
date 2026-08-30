using System.Linq;
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
    /// The per-row "Trigger devices" editor removed in this commit (a study found the default - start
    /// when the mapped device connects, no explicit choice - already covers the common case, and the
    /// editor itself was reachable-but-unused) doubled as a regression guard for the full
    /// register-device -> add-mapping -> grid-row pipeline. This replaces that coverage: P0-2 (this
    /// project's own history) was a control present in XAML but pushed off-screen and found only by a
    /// human looking at a screenshot, so a created backup must still produce a selectable grid row rather
    /// than silently failing partway through the flow. Also exercises the inline "New…" device-registration
    /// dialog next to the destination combo, added in the same commit, instead of the Devices tab.
    /// </summary>
    [Fact]
    public void MappingsGridShowsARowAfterRegisteringAndMappingADeviceInline()
    {
        Find("OpenRegisterDeviceDialogButton").AsButton().Invoke();
        var dialog = Retry.WhileNull(
            () => _fixture.GetAllTopLevelWindows().FirstOrDefault(window => window.AutomationId == "RegisterDeviceWindow"),
            TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(dialog);
        var drives = dialog!.FindFirstDescendant(cf => cf.ByAutomationId("RegisterDeviceDialogDriveCombo")).AsComboBox();
        Retry.WhileEmpty(() => drives.Items, TimeSpan.FromSeconds(10));
        drives.Select(0);
        dialog.FindFirstDescendant(cf => cf.ByAutomationId("RegisterDeviceDialogRegisterButton")).AsButton().Invoke();

        var backupSets = Find("BackupSetCombo").AsComboBox();
        Retry.WhileEmpty(() => backupSets.Items, TimeSpan.FromSeconds(10));
        backupSets.Select(0);
        var devices = Find("MappingDeviceCombo").AsComboBox();
        Retry.WhileEmpty(() => devices.Items, TimeSpan.FromSeconds(10));
        devices.Select(0);
        Find("DestinationFolderInput").AsTextBox().Text = "BackupMesh\\UiTestInline";
        Find("AddMappingButton").AsButton().Invoke();

        var grid = Find("MappingsGrid");
        var row = Retry.WhileNull(() => grid.FindFirstChild(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)), TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(row);
        row!.Patterns.SelectionItem.Pattern.Select();

        Find("RemoveMappingButton").AsButton().Invoke();
        Retry.WhileNotNull(() => grid.FindFirstChild(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)), TimeSpan.FromSeconds(10));
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
