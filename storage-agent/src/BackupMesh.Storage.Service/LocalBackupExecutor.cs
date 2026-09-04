using System.Diagnostics;
using System.Text.Json;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Service;

public sealed record LocalBackupProgress(long BytesDone, long? BytesTotal, long FilesDone, long? FilesTotal);
public sealed record LocalBackupResult(string SnapshotId, long BytesAdded);

// Mirrors source-agent/internal/restic/restic.go's contract exactly - the same restic CLI flags and the
// same JSON message schema - so a local Backup Set's snapshots, and the progress/results reported for
// it, are indistinguishable from a paired Source Agent's own restic invocation.
public sealed class LocalResticRunner(string executablePath, string cacheDirectory)
{
    public async Task EnsureRepositoryAsync(string repository, string passwordFile, CancellationToken cancellationToken)
    {
        if (await RunAsync(repository, passwordFile, cancellationToken, "snapshots", "--json") == 0) return;
        if (await RunAsync(repository, passwordFile, cancellationToken, "init", "--repository-version", "2") == 0) return;
        // Another local Backup Set's mapping may have initialized this same repository path concurrently.
        if (await RunAsync(repository, passwordFile, cancellationToken, "snapshots", "--json") != 0)
            throw new InvalidOperationException("Could not initialize the local restic repository.");
    }

    public async Task<LocalBackupResult> BackupAsync(string repository, string passwordFile, IReadOnlyList<string> paths, Action<LocalBackupProgress> onProgress, CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(repository, passwordFile);
        startInfo.ArgumentList.Add("backup");
        startInfo.ArgumentList.Add("--json");
        foreach (var path in paths) startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start restic.");
        using var killRegistration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var result = await ParseJsonStreamAsync(process.StandardOutput, onProgress, cancellationToken);
        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var stderr = (await stderrTask).Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(stderr) ? $"restic backup exited with code {process.ExitCode}." : $"restic backup failed: {stderr}");
        }
        return result ?? throw new InvalidOperationException("restic output ended without a summary message.");
    }

    private async Task<int> RunAsync(string repository, string passwordFile, CancellationToken cancellationToken, params string[] args)
    {
        var startInfo = BuildStartInfo(repository, passwordFile);
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start restic.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private ProcessStartInfo BuildStartInfo(string repository, string passwordFile)
    {
        var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        startInfo.Environment["RESTIC_REPOSITORY"] = repository;
        startInfo.Environment["RESTIC_PASSWORD_FILE"] = passwordFile;
        startInfo.Environment["RESTIC_CACHE_DIR"] = cacheDirectory;
        return startInfo;
    }

    internal static async Task<LocalBackupResult?> ParseJsonStreamAsync(StreamReader stdout, Action<LocalBackupProgress> onProgress, CancellationToken cancellationToken)
    {
        LocalBackupResult? result = null;
        string? line;
        while ((line = await stdout.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var messageType = root.TryGetProperty("message_type", out var messageTypeElement) ? messageTypeElement.GetString() : null;
            if (messageType == "status")
            {
                onProgress(new LocalBackupProgress(
                    GetInt64OrDefault(root, "bytes_done"),
                    GetNullableInt64(root, "total_bytes"),
                    GetInt64OrDefault(root, "files_done"),
                    GetNullableInt64(root, "total_files")));
            }
            else if (messageType == "summary")
            {
                result = new LocalBackupResult(
                    root.TryGetProperty("snapshot_id", out var snapshotId) ? snapshotId.GetString() ?? string.Empty : string.Empty,
                    GetInt64OrDefault(root, "data_added"));
            }
        }
        return result;
    }

    private static long GetInt64OrDefault(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    private static long? GetNullableInt64(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
}

// Runs Backup Sets defined directly on this PC (LocalSourceIdentity) - no pairing, no mTLS, no
// rest-server: restic runs directly against the resolved target's local DestinationFolder. Behaves
// like a single always-connected "Source Agent" for that one well-known identity, claiming its own
// queued commands from BackupCommandQueue and admitting/reporting jobs through the same
// BackupJobStore a real Source Agent's HTTP calls would use, just via direct in-process calls.
public sealed class LocalBackupExecutorService(
    BackupCommandQueue commands,
    BackupJobStore jobs,
    BackupTargetResolver targets,
    StorageConfigurationStore configuration,
    LocalRepositoryPasswordStore passwords,
    LocalBackupOptions options,
    StorageStateMachine state,
    ILogger<LocalBackupExecutorService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Lease = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var command = commands.ClaimNext(LocalSourceIdentity.AgentId, DateTimeOffset.UtcNow, Lease);
                if (command is not null) await RunCommandAsync(command, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Local backup execution failed unexpectedly.");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    // Test seam: exercises one claimed command's full run without starting the BackgroundService loop.
    internal async Task RunCommandAsync(BackupCommand command, CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        commands.Acknowledge(LocalSourceIdentity.AgentId, command.CommandId, now);
        // Matches /backup/request's own precondition exactly: only proceed when Storage's aggregate
        // state is already Ready or Busy, rather than assuming it and letting TransitionTo throw.
        if (state.State is not StorageState.Ready and not StorageState.Busy)
        {
            commands.Complete(LocalSourceIdentity.AgentId, command.CommandId, "FAILED", DateTimeOffset.UtcNow, null, "Storage is not ready.");
            return;
        }
        var jobId = Guid.NewGuid();
        var request = new BackupRequest(jobId, LocalSourceIdentity.AgentId, command.BackupSetId, command.TargetMappingId, now, null);
        var resolution = targets.Resolve(request);
        if (resolution.Target is null)
        {
            commands.Complete(LocalSourceIdentity.AgentId, command.CommandId, "FAILED", DateTimeOffset.UtcNow, null, resolution.Message ?? "Target is unavailable.");
            return;
        }
        var target = resolution.Target;
        var backupSet = configuration.Get().Configuration.BackupSets.FirstOrDefault(set => set.Id == command.BackupSetId);
        if (backupSet is null || backupSet.SourcePaths.Count == 0)
        {
            commands.Complete(LocalSourceIdentity.AgentId, command.CommandId, "FAILED", DateTimeOffset.UtcNow, null, "The Backup Set has no source paths.");
            return;
        }
        var admission = jobs.Admit(request, $"local:{command.CommandId:N}", new Uri(target.DestinationFolder), target.DeviceId);
        if (admission.Outcome != StoreOutcome.Accepted || admission.Admission is null)
        {
            commands.Complete(LocalSourceIdentity.AgentId, command.CommandId, "FAILED", DateTimeOffset.UtcNow, null, "Could not admit the local backup job.");
            return;
        }
        state.TransitionTo(StorageState.Busy, jobId.ToString());
        long sequence = 0;
        try
        {
            var runner = new LocalResticRunner(options.ResticExecutablePath, ResolveCacheDirectory());
            using var passwordFile = passwords.GetOrCreatePlaintextPasswordFile(target.MappingId, out var passwordPath);
            await runner.EnsureRepositoryAsync(target.DestinationFolder, passwordPath, stoppingToken);
            var result = await runner.BackupAsync(target.DestinationFolder, passwordPath, backupSet.SourcePaths, progress =>
            {
                jobs.Progress(new BackupProgress(Guid.NewGuid(), jobId, ++sequence, DateTimeOffset.UtcNow, "UPLOADING", progress.BytesDone, progress.BytesTotal, progress.FilesDone, progress.FilesTotal, null));
            }, stoppingToken);
            jobs.Complete(new BackupResult(Guid.NewGuid(), jobId, ++sequence, DateTimeOffset.UtcNow, "SUCCEEDED", result.SnapshotId, result.BytesAdded, null, null));
            commands.Complete(LocalSourceIdentity.AgentId, command.CommandId, "SUCCEEDED", DateTimeOffset.UtcNow, jobId, null);
        }
        catch (OperationCanceledException)
        {
            jobs.Complete(new BackupResult(Guid.NewGuid(), jobId, ++sequence, DateTimeOffset.UtcNow, "CANCELLED", null, null, "CANCELLED", "backup was cancelled"));
            commands.Complete(LocalSourceIdentity.AgentId, command.CommandId, "CANCELLED", DateTimeOffset.UtcNow, jobId, "backup was cancelled");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            logger.LogError(exception, "Local backup failed for mapping {MappingId}.", target.MappingId);
            jobs.Complete(new BackupResult(Guid.NewGuid(), jobId, ++sequence, DateTimeOffset.UtcNow, "FAILED", null, null, "BACKUP_ENGINE_FAILED", "backup engine failed"));
            commands.Complete(LocalSourceIdentity.AgentId, command.CommandId, "FAILED", DateTimeOffset.UtcNow, jobId, exception.Message);
        }
        finally
        {
            if (!jobs.HasActiveJobs) state.TransitionTo(StorageState.Ready, "Local backup finished.");
        }
    }

    private string ResolveCacheDirectory()
    {
        var directory = !string.IsNullOrWhiteSpace(options.CacheDirectory)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.CacheDirectory))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "restic-cache");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
