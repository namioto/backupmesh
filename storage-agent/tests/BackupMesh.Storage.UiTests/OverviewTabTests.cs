using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace BackupMesh.Storage.UiTests;

// Replaces DevicesTabTests: the Devices tab was removed (peer review) - registration now happens inline
// from the Backups tab's "New…" dialog (see BackupsTabTests), and what's left - the registered-device
// list, safe-removal, and "stop using this device" - moved to a "Connected storage" group on Overview.
public sealed class OverviewTabTests : IClassFixture<StorageAppFixture>
{
    private readonly StorageAppFixture _fixture;

    public OverviewTabTests(StorageAppFixture fixture)
    {
        _fixture = fixture;
        _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("OverviewTab")).AsTabItem().Select();
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
    public void RegisteredDevicesGrid_IsReachableByAutomationId()
    {
        Assert.NotNull(Find("RegisteredDevicesGrid").AsDataGridView());
    }

    [Fact]
    public void DeviceActionButtons_ArePresentAndEnabled()
    {
        string[] buttonIds = ["ForgetDeviceButton", "EjectDeviceButton"];

        foreach (var id in buttonIds)
        {
            var button = Find(id).AsButton();
            Assert.True(button.IsEnabled, $"Button '{id}' was disabled.");
        }
    }

    [Fact]
    public void BackupJobsGrid_IsReachableByAutomationId()
    {
        Assert.NotNull(Find("BackupJobsGrid").AsDataGridView());
    }
}
