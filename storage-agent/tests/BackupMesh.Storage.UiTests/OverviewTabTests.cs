using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace BackupMesh.Storage.UiTests;

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
    public void BackupJobsGrid_IsReachableByAutomationId()
    {
        Assert.NotNull(Find("BackupJobsGrid").AsDataGridView());
    }
}
