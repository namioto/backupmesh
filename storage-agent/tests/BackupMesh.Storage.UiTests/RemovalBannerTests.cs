using FlaUI.Core.AutomationElements;
using Xunit;

namespace BackupMesh.Storage.UiTests;

/// <summary>
/// The removal banner sits outside the TabControl so backup status and safe-removal actions remain
/// visible regardless of the active tab.
/// </summary>
public sealed class RemovalBannerTests : IClassFixture<StorageAppFixture>
{
    private readonly StorageAppFixture _fixture;

    public RemovalBannerTests(StorageAppFixture fixture) => _fixture = fixture;

    private AutomationElement Find(string automationId)
    {
        var element = FlaUI.Core.Tools.Retry.WhileNull(
            () => _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            TimeSpan.FromSeconds(10)).Result;
        Assert.True(element is not null, $"No element with AutomationId '{automationId}' was found.");
        return element!;
    }

    /// <summary>
    /// The demo fixture starts with no registered devices and no job history, so nothing qualifies for a
    /// banner - this confirms the empty state renders as genuinely empty (no rows, no stray "Remove
    /// safely" button) rather than a visible-but-blank strip, and that the container itself is reachable
    /// regardless. The three populated states (backing up / safe / safe-but-incomplete) are covered by
    /// MainWindowViewModel unit tests instead: driving a real job through this black-box fixture would
    /// need a live Storage Service, which demo mode deliberately does not provide.
    /// </summary>
    [Fact]
    public void RemovalBannersAreReachableAndEmptyByDefault()
    {
        var container = Find("RemovalBanners");
        Assert.Empty(container.FindAllChildren());
        Assert.Null(_fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("RemoveSafelyButton")));
    }
}
