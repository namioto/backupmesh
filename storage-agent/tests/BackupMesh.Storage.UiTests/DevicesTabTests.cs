using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace BackupMesh.Storage.UiTests;

public sealed class DevicesTabTests : IClassFixture<StorageAppFixture>
{
    private readonly StorageAppFixture _fixture;

    public DevicesTabTests(StorageAppFixture fixture)
    {
        _fixture = fixture;
        _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("DevicesTab")).AsTabItem().Select();
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
    public void RefreshDrives_ListsAtLeastOneAvailableDrive()
    {
        Find("RefreshDrivesButton").AsButton().Invoke();

        var combo = Find("AvailableDrivesCombo").AsComboBox();
        var items = Retry.WhileEmpty(() => combo.Items, TimeSpan.FromSeconds(10)).Result;

        Assert.NotEmpty(items);
    }

    /// <summary>
    /// Regression test: AvailableDriveViewModel is a record, so without an explicit ToString()
    /// override the compiler-generated one becomes the UI Automation Name and leaks StableId and
    /// volume serials to screen readers. DisplayMemberPath does not affect the UIA Name.
    /// </summary>
    [Fact]
    public void AvailableDrives_ExposeDisplayNameOnly_NotTheViewModelDump()
    {
        Find("RefreshDrivesButton").AsButton().Invoke();

        var combo = Find("AvailableDrivesCombo").AsComboBox();
        var items = Retry.WhileEmpty(() => combo.Items, TimeSpan.FromSeconds(10)).Result;

        foreach (var item in items)
        {
            var name = item.Name;
            Assert.False(string.IsNullOrWhiteSpace(name), "A drive entry exposed an empty accessibility name.");
            Assert.DoesNotContain("AvailableDriveViewModel", name, StringComparison.Ordinal);
            Assert.DoesNotContain("StableId", name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DeviceActionButtons_ArePresentAndEnabled()
    {
        string[] buttonIds =
        [
            "RefreshDrivesButton",
            "RegisterDeviceButton",
            "RegisterFolderButton",
            "ForgetDeviceButton",
            "EjectDeviceButton"
        ];

        foreach (var id in buttonIds)
        {
            var button = Find(id).AsButton();
            Assert.True(button.IsEnabled, $"Button '{id}' was disabled.");
        }
    }

    [Fact]
    public void RegisteredDevicesGrid_IsReachableByAutomationId()
    {
        Assert.NotNull(Find("RegisteredDevicesGrid").AsDataGridView());
    }
}
