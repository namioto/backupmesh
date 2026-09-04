using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace BackupMesh.Storage.UiTests;

public sealed class BackupsTabTests : IClassFixture<StorageAppFixture>
{
    private readonly StorageAppFixture _fixture;

    public BackupsTabTests(StorageAppFixture fixture)
    {
        _fixture = fixture;
        _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("BackupsTab")).AsTabItem().Select();
    }

    private AutomationElement Find(string automationId)
    {
        var element = Retry.WhileNull(
            () => _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            TimeSpan.FromSeconds(10)).Result;
        Assert.True(element is not null, $"No element with AutomationId '{automationId}' was found.");
        return element!;
    }

    // AvailableDrives is Clear()'d and rebuilt on every 3-second RefreshDrives() tick (unlike Devices/
    // BackupSets, which are mutated in place), so a ComboBox bound to it can go stale between reading
    // Items and calling Select() - re-fetching the combo and retrying closes that race instead of
    // occasionally failing with FlaUI's ElementNotAvailableException.
    private static void SelectFirstItem(Func<FlaUI.Core.AutomationElements.ComboBox> findCombo)
    {
        Retry.WhileException(() =>
        {
            var combo = findCombo();
            if (combo.Items.Length == 0) throw new InvalidOperationException("No items yet.");
            combo.Select(0);
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void MappingsGrid_IsReachableByAutomationId()
    {
        Assert.NotNull(Find("MappingsGrid").AsDataGridView());
    }

    /// <summary>
    /// The per-row "Trigger devices" editor removed from this tab (a study found the default - start
    /// when the mapped device connects, no explicit choice - already covers the common case, and the
    /// editor itself was reachable-but-unused) doubled as a regression guard for the full
    /// register-device -> add-mapping -> grid-row pipeline. This replaces that coverage: P0-2 (this
    /// project's own history) was a control present in XAML but pushed off-screen, so a created backup
    /// must still produce a selectable grid row rather than silently failing partway through the flow.
    /// Also exercises the inline "New…" device-registration
    /// dialog next to the destination combo, which replaced the separate Devices tab.
    /// </summary>
    [Fact]
    public void MappingsGridShowsARowAfterRegisteringAndMappingADeviceInline()
    {
        Find("OpenRegisterDeviceDialogButton").AsButton().Invoke();
        var dialog = Retry.WhileNull(
            () => _fixture.GetAllTopLevelWindows().FirstOrDefault(window => window.AutomationId == "RegisterDeviceWindow"),
            TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(dialog);
        SelectFirstItem(() => dialog!.FindFirstDescendant(cf => cf.ByAutomationId("RegisterDeviceDialogDriveCombo")).AsComboBox());
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

    /// <summary>
    /// Regression test carried over from the removed Devices tab (AvailableDriveViewModel is a record, so
    /// without an explicit ToString() override the compiler-generated one becomes the UI Automation Name
    /// and leaks StableId and volume serials to screen readers - DisplayMemberPath does not affect the UIA
    /// Name). The drive picker itself moved into this dialog when the Devices tab was removed; this test
    /// was dropped at the same time and never re-created there until now.
    /// </summary>
    [Fact]
    public void RegisterDialogDrives_ExposeDisplayNameOnly_NotTheViewModelDump()
    {
        Find("OpenRegisterDeviceDialogButton").AsButton().Invoke();
        var dialog = Retry.WhileNull(
            () => _fixture.GetAllTopLevelWindows().FirstOrDefault(window => window.AutomationId == "RegisterDeviceWindow"),
            TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(dialog);

        try
        {
            var drives = dialog!.FindFirstDescendant(cf => cf.ByAutomationId("RegisterDeviceDialogDriveCombo")).AsComboBox();
            var items = Retry.WhileEmpty(() => drives.Items, TimeSpan.FromSeconds(10)).Result;

            foreach (var item in items)
            {
                var name = item.Name;
                Assert.False(string.IsNullOrWhiteSpace(name), "A drive entry exposed an empty accessibility name.");
                Assert.DoesNotContain("AvailableDriveViewModel", name, StringComparison.Ordinal);
                Assert.DoesNotContain("StableId", name, StringComparison.Ordinal);
            }
        }
        finally
        {
            dialog!.FindFirstDescendant(cf => cf.ByAutomationId("RegisterDeviceDialogCancelButton")).AsButton().Invoke();
        }
    }
}
