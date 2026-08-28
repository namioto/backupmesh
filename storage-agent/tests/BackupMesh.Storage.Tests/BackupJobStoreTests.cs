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
    public void PairingCredentialPersistsOnlyHashAndSurvivesRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-pairing-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "credential.sha256");
        try
        {
            var options = new PairingOptions { CredentialHashPath = path };
            var agentId = Guid.NewGuid();
            var credential = new PairingCredentialStore(options).Issue(agentId);
            var persisted = File.ReadAllText(path);

            Assert.True(credential.Length >= 43);
            Assert.DoesNotContain(credential, persisted, StringComparison.Ordinal);
            Assert.True(new PairingCredentialStore(options).Authorize(credential, agentId));
            Assert.False(new PairingCredentialStore(options).Authorize(credential + "x", agentId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void EachSourceCanKeepItsOwnPairingCredential()
    {
        var store = new PairingCredentialStore(new PairingOptions { CredentialHashPath = string.Empty });
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = store.Issue(firstId);
        var second = store.Issue(secondId);
        Assert.True(store.Authorize(first, firstId));
        Assert.True(store.Authorize(second, secondId));
    }

    [Fact]
    public void PairingCredentialCannotImpersonateAnotherSourceAfterBinding()
    {
        var store = new PairingCredentialStore(new PairingOptions { CredentialHashPath = string.Empty });
        var owner = Guid.NewGuid();
        var credential = store.Issue(owner);
        Assert.True(store.Authorize(credential, owner));
        Assert.False(store.Authorize(credential, Guid.NewGuid()));
        Assert.True(store.Authorize(credential, owner));
    }

    [Fact]
    public void JobListReturnsNewestStatusFirst()
    {
        var store = new BackupJobStore();
        var first = Request(Guid.NewGuid());
        var second = Request(Guid.NewGuid()) with { TargetMappingId = Guid.NewGuid() };
        store.Admit(first, "first-job-key-0001", new Uri("rest:http://localhost/one"));
        Thread.Sleep(2);
        store.Admit(second, "second-job-key-001", new Uri("rest:http://localhost/two"));

        Assert.Equal([second.JobId, first.JobId], store.List().Select(job => job.JobId));
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
    public void JobOwnershipIsBoundToAdmittedSource()
    {
        var store = new BackupJobStore();
        var request = Request(Guid.NewGuid());
        store.Admit(request, "ownership-key-001", new Uri("https://localhost/repo"));
        Assert.True(store.IsOwnedBy(request.JobId, request.SourceAgentId));
        Assert.False(store.IsOwnedBy(request.JobId, Guid.NewGuid()));
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
    [Fact]
    public void ActiveJobSurvivesRestartAndCanComplete()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-job-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "jobs.json");
        try
        {
            var options = new BackupJobOptions { PersistencePath = path };
            var request = Request(Guid.NewGuid());
            var first = new BackupJobStore(options);
            Assert.Equal(StoreOutcome.Accepted, first.Admit(request, "persistent-key-01", new Uri("https://localhost/repo")).Outcome);
            Assert.Equal(StoreOutcome.Accepted, first.Progress(new(Guid.NewGuid(), request.JobId, 3, DateTimeOffset.UtcNow, "UPLOADING", 10, 20, 1, 2, null)));

            var restored = new BackupJobStore(options);
            Assert.True(restored.HasActiveJobs);
            Assert.Equal("RUNNING", restored.Get(request.JobId)!.State);
            Assert.Equal(StoreOutcome.Conflict, restored.Admit(Request(Guid.NewGuid(), request.TargetMappingId), "other-job-key-01", new Uri("https://localhost/repo")).Outcome);
            Assert.Equal(StoreOutcome.Accepted, restored.Complete(new(Guid.NewGuid(), request.JobId, 4, DateTimeOffset.UtcNow, "FAILED", null, null, "RESTART_TEST", "recovered")));
            Assert.False(restored.HasActiveJobs);
            Assert.Equal("FAILED", new BackupJobStore(options).Get(request.JobId)!.State);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static BackupRequest Request(Guid id, Guid? mappingId = null) => new(id, Guid.NewGuid(), Guid.NewGuid(), mappingId ?? Guid.NewGuid(), DateTimeOffset.UtcNow, ["daily"]);
}
