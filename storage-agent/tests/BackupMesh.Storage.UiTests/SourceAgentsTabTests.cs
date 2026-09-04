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
        var grid = Find("SourceConnectionsGrid");
        Assert.Contains(grid.FindAllDescendants(), element => element.Name == "This PC (no agent needed)");
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
