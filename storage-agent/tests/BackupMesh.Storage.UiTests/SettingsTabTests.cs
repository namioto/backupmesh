using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace BackupMesh.Storage.UiTests;

public sealed class SettingsTabTests : IClassFixture<StorageAppFixture>
{
    private readonly StorageAppFixture _fixture;

    public SettingsTabTests(StorageAppFixture fixture)
    {
        _fixture = fixture;
        _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SettingsTab")).AsTabItem().Select();
    }

    private AutomationElement Find(string automationId)
    {
        var element = Retry.WhileNull(
            () => _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            TimeSpan.FromSeconds(10)).Result;
        Assert.True(element is not null, $"No element with AutomationId '{automationId}' was found.");
        return element!;
    }

    /// <summary>
    /// Only checks presence: invoking it opens a WPF MessageBox confirmation that would block this
    /// automated test process with no user available to click it.
    /// </summary>
    [Fact]
    public void RotateStorageIdentityButton_IsPresent()
    {
        Assert.NotNull(Find("RotateStorageIdentityButton").AsButton());
    }

    // Replaces the removed Devices tab's per-device arrival-delay editor with one global default.
    [Fact]
    public void DefaultArrivalDelayInput_IsPresent()
    {
        Assert.NotNull(Find("DefaultArrivalDelayInput").AsTextBox());
    }
}
