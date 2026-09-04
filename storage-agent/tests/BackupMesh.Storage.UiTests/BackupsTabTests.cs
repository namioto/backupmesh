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

    [Fact]
    public void MappingsGrid_IsReachableByAutomationId()
    {
        Assert.NotNull(Find("MappingsGrid").AsDataGridView());
    }

    [Fact]
    public void BackupRuleActionsAndExpandedGridAreReachable()
    {
        var grid = Find("MappingsGrid");
        Assert.True(grid.BoundingRectangle.Height >= 250, $"Backup rule list is only {grid.BoundingRectangle.Height}px high.");
        Assert.True(Find("AddMappingButton").AsButton().IsEnabled);
        Assert.NotNull(Find("EditMappingButton").AsButton());
    }

    [Fact]
    public void AddBackupButtonHasAUserFacingName()
    {
        Assert.Contains("Add backup", Find("AddMappingButton").AsButton().Name, StringComparison.OrdinalIgnoreCase);
    }
}
