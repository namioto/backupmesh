using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class BackupJobStoreTests
{
    [Fact]
    public void ControlApiTokenComparisonRejectsMissingShortAndDifferentTokens()
    {
        const string token = "0123456789abcdef0123456789abcdef";
        Assert.True(ControlApiAuthenticationFilter.TokenMatches(token, token));
        Assert.False(ControlApiAuthenticationFilter.TokenMatches(token, token + "x"));
        Assert.False(ControlApiAuthenticationFilter.TokenMatches("short", "short"));
        Assert.False(ControlApiAuthenticationFilter.TokenMatches(null, token));
    }

    [Fact]
    public void Admission_IsPerMappingAndIdempotent()
    {
        var store = new BackupJobStore(); var request = Request(Guid.NewGuid()); var endpoint = new Uri("https://localhost/repo");
        Assert.Equal(StoreOutcome.Accepted, store.Admit(request, "abcdefghijklmnop", endpoint).Outcome);
        Assert.Equal(StoreOutcome.Replayed, store.Admit(request, "abcdefghijklmnop", endpoint).Outcome);
        Assert.Equal(StoreOutcome.Conflict, store.Admit(Request(Guid.NewGuid(), request.TargetMappingId), "different-key-1234", endpoint).Outcome);
        Assert.Equal(StoreOutcome.Accepted, store.Admit(Request(Guid.NewGuid()), "another-target-12", endpoint).Outcome);
    }
    [Fact]
    public void ProgressIsMonotonicAndResultIsTerminal()
    {
        var store = new BackupJobStore(); var id = Guid.NewGuid(); store.Admit(Request(id), "abcdefghijklmnop", new Uri("https://localhost/repo"));
        var progress = new BackupProgress(Guid.NewGuid(), id, 1, DateTimeOffset.UtcNow, "UPLOADING", 10, 20, 1, 2, null);
        Assert.Equal(StoreOutcome.Accepted, store.Progress(progress)); Assert.Equal(StoreOutcome.InvalidSequence, store.Progress(progress with { EventId = Guid.NewGuid() }));
        var result = new BackupResult(Guid.NewGuid(), id, 2, DateTimeOffset.UtcNow, "SUCCEEDED", "snapshot", 10, null, null);
        Assert.Equal(StoreOutcome.Accepted, store.Complete(result)); Assert.Equal("SUCCEEDED", store.Get(id)!.State); Assert.Null(store.ActiveJobId);
        Assert.Equal(StoreOutcome.Terminal, store.Progress(progress with { EventId = Guid.NewGuid(), Sequence = 3 }));
    }
    private static BackupRequest Request(Guid id, Guid? mappingId = null) => new(id, Guid.NewGuid(), Guid.NewGuid(), mappingId ?? Guid.NewGuid(), DateTimeOffset.UtcNow, ["daily"]);
}
