using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupMesh.Storage.Tests;

public sealed class LocalBackupExecutorServiceTests
{
    /// <summary>
    /// End-to-end through the same real bundled restic binary as LocalResticRunnerTests: a local
    /// Backup Set (no Source Agent, no pairing) mapped to a ready target device is claimed from
    /// BackupCommandQueue, actually backed up, and ends with a SUCCEEDED job and command - the whole
    /// path a real "This PC" Backup Set takes once StorageMonitorService enqueues its arrival.
    /// </summary>
    [Fact]
    public async Task RunCommandAsyncBacksUpALocalBackupSetAndReportsSuccess()
    {
        var resticPath = Path.Combine(FindRepositoryRoot(), "artifacts", "tools", "windows-x64", "restic.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(resticPath)) return;

        var root = Path.Combine(Path.GetTempPath(), $"backupmesh-local-executor-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "source");
        var targetDirectory = Path.Combine(root, "target");
        var passwordDirectory = Path.Combine(root, "passwords");
        var cacheDirectory = Path.Combine(root, "cache");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(targetDirectory);
        var expectedContent = $"local executor content {Guid.NewGuid()}";
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "note.txt"), expectedContent);

        try
        {
            var deviceId = Guid.NewGuid();
            var backupSetId = Guid.NewGuid();
            var mappingId = Guid.NewGuid();
            var device = new RegisteredDevice(deviceId, FolderStorageIdentity.Create(targetDirectory), "Target", "Folder", targetDirectory, DateTimeOffset.UtcNow, null, 0);
            var backupSet = new SourceBackupSet(backupSetId, LocalSourceIdentity.AgentId, LocalSourceIdentity.DisplayName, "Notes", [sourceDirectory]);
            var mapping = new BackupTargetMapping(mappingId, backupSetId, deviceId, "repo");
            var topology = new StorageAgentConfiguration([device], [backupSet], [mapping]);

            var configuration = new StorageConfigurationStore(new StorageConfigurationOptions { PersistencePath = string.Empty });
            configuration.Update(new StorageConfigurationUpdate(0, topology));
            var presence = new StoragePresenceStore();
            presence.Refresh(topology, [], DateTimeOffset.UtcNow);

            var commands = new BackupCommandQueue();
            var jobs = new BackupJobStore();
            var targets = new BackupTargetResolver(configuration, presence);
            var passwords = new LocalRepositoryPasswordStore(new LocalBackupOptions { PasswordDirectory = passwordDirectory });
            var options = new LocalBackupOptions { ResticExecutablePath = resticPath, PasswordDirectory = passwordDirectory, CacheDirectory = cacheDirectory };
            var state = new StorageStateMachine();
            DriveToReady(state);
            var executor = new LocalBackupExecutorService(commands, jobs, targets, configuration, passwords, options, state, NullLogger<LocalBackupExecutorService>.Instance);

            commands.Enqueue("test", [new BackupCommandDraft(LocalSourceIdentity.AgentId, backupSetId, mappingId, "manual")], DateTimeOffset.UtcNow);
            var claimed = commands.ClaimNext(LocalSourceIdentity.AgentId, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            Assert.NotNull(claimed);

            await executor.RunCommandAsync(claimed!, CancellationToken.None);

            var job = Assert.Single(jobs.List());
            Assert.Equal("SUCCEEDED", job.State);
            Assert.False(string.IsNullOrWhiteSpace(job.Result?.SnapshotId));

            var completedCommand = Assert.Single(commands.List());
            Assert.Equal("SUCCEEDED", completedCommand.State);

            Assert.True(Directory.Exists(Path.Combine(targetDirectory, "repo", "config")) || Directory.Exists(Path.Combine(targetDirectory, "repo", "data")),
                "restic repository was not created under the mapped destination folder.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RunCommandAsyncFailsTheCommandWhenStorageIsNotReady()
    {
        // A fresh StorageStateMachine starts Offline; TransitionTo(Busy) is only reachable from Ready,
        // matching /backup/request's own precondition - this must fail the command cleanly rather than
        // let StorageStateMachine.TransitionTo throw.
        var commands = new BackupCommandQueue();
        var jobs = new BackupJobStore();
        var configuration = new StorageConfigurationStore(new StorageConfigurationOptions { PersistencePath = string.Empty });
        var presence = new StoragePresenceStore();
        var targets = new BackupTargetResolver(configuration, presence);
        var passwords = new LocalRepositoryPasswordStore(new LocalBackupOptions { PasswordDirectory = Path.Combine(Path.GetTempPath(), $"backupmesh-local-executor-pw-{Guid.NewGuid():N}") });
        var options = new LocalBackupOptions();
        var state = new StorageStateMachine();
        var executor = new LocalBackupExecutorService(commands, jobs, targets, configuration, passwords, options, state, NullLogger<LocalBackupExecutorService>.Instance);

        var backupSetId = Guid.NewGuid();
        var mappingId = Guid.NewGuid();
        commands.Enqueue("test", [new BackupCommandDraft(LocalSourceIdentity.AgentId, backupSetId, mappingId, "manual")], DateTimeOffset.UtcNow);
        var claimed = commands.ClaimNext(LocalSourceIdentity.AgentId, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        await executor.RunCommandAsync(claimed!, CancellationToken.None);

        Assert.Empty(jobs.List());
        var completedCommand = Assert.Single(commands.List());
        Assert.Equal("FAILED", completedCommand.State);
    }

    [Fact]
    public async Task RunCommandAsyncFailsTheCommandWhenTheTargetIsNotFound()
    {
        var commands = new BackupCommandQueue();
        var jobs = new BackupJobStore();
        var configuration = new StorageConfigurationStore(new StorageConfigurationOptions { PersistencePath = string.Empty });
        var presence = new StoragePresenceStore();
        var targets = new BackupTargetResolver(configuration, presence);
        var passwords = new LocalRepositoryPasswordStore(new LocalBackupOptions { PasswordDirectory = Path.Combine(Path.GetTempPath(), $"backupmesh-local-executor-pw-{Guid.NewGuid():N}") });
        var options = new LocalBackupOptions();
        var state = new StorageStateMachine();
        DriveToReady(state);
        var executor = new LocalBackupExecutorService(commands, jobs, targets, configuration, passwords, options, state, NullLogger<LocalBackupExecutorService>.Instance);

        var backupSetId = Guid.NewGuid();
        var mappingId = Guid.NewGuid();
        commands.Enqueue("test", [new BackupCommandDraft(LocalSourceIdentity.AgentId, backupSetId, mappingId, "manual")], DateTimeOffset.UtcNow);
        var claimed = commands.ClaimNext(LocalSourceIdentity.AgentId, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        await executor.RunCommandAsync(claimed!, CancellationToken.None);

        Assert.Empty(jobs.List());
        var completedCommand = Assert.Single(commands.List());
        Assert.Equal("FAILED", completedCommand.State);
    }

    private static void DriveToReady(StorageStateMachine state)
    {
        state.TransitionTo(StorageState.Discovered);
        state.TransitionTo(StorageState.Verifying);
        state.TransitionTo(StorageState.Ready);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
               && !File.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
