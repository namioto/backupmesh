using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace BackupMesh.Storage.UiTests;

public sealed class SourceAgentsTabTests : IClassFixture<StorageAppFixture>
{
    private readonly StorageAppFixture _fixture;

    public SourceAgentsTabTests(StorageAppFixture fixture)
    {
        _fixture = fixture;
        _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SourceAgentsTab")).AsTabItem().Select();
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
    /// Demo mode uses an isolated, unreachable service endpoint. Invoking pairing must fail gracefully
    /// in the footer instead of contacting an installed Storage Service on the test machine.
    /// </summary>
    [Fact]
    public void PairSourceButton_ReportsAFailureWhenNoStorageServiceIsRunning()
    {
        Find("PairSourceButton").AsButton().Invoke();

        var footer = Find("FooterStatusText");
        Retry.WhileFalse(() => footer.Name.Contains("Pairing session could not be created", StringComparison.Ordinal), TimeSpan.FromSeconds(10));

        Assert.Contains("Pairing session could not be created", footer.Name, StringComparison.Ordinal);
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

    [Fact]
    public void AddAndRemoveLocalBackupSetButtons_ArePresent()
    {
        Assert.NotNull(Find("AddLocalBackupSetButton").AsButton());
        Assert.NotNull(Find("RemoveLocalBackupSetButton").AsButton());
    }

    /// <summary>
    /// "This PC" is always shown, even with no local Backup Sets configured. The label explains that a
    /// Source Agent is not required for local backups.
    /// </summary>
    [Fact]
    public void ThisPCIsAlwaysListedInTheGridWithAnExplanation()
    {
        var grid = Find("SourceConnectionsGrid").AsDataGridView();
        var rows = Retry.WhileEmpty(() => grid.Rows, TimeSpan.FromSeconds(10)).Result;
        Assert.Contains(rows.SelectMany(row => row.Cells), cell => cell.Value == "This PC (no agent needed)");
    }

    [Fact]
    public void AddressAndOffersColumnsArePresent()
    {
        var grid = Find("SourceConnectionsGrid");
        var headers = grid.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.HeaderItem)).Select(header => header.Name).ToArray();
        Assert.Contains("Address", headers);
        Assert.Contains("Offers", headers);
    }
}
