using System.Diagnostics;
using System.Text;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class LocalResticRunnerTests
{
    [Fact]
    public async Task ParseJsonStreamAsyncReportsEachStatusLineAndReturnsTheSummary()
    {
        var json = string.Join('\n',
            """{"message_type":"status","bytes_done":100,"total_bytes":400,"files_done":1,"total_files":4,"percent_done":0.25}""",
            """{"message_type":"status","bytes_done":400,"total_bytes":400,"files_done":4,"total_files":4,"percent_done":1.0}""",
            """{"message_type":"summary","snapshot_id":"abc123","data_added":400,"files_new":4}""");
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        var progressReports = new List<LocalBackupProgress>();

        var result = await LocalResticRunner.ParseJsonStreamAsync(reader, progressReports.Add, CancellationToken.None);

        Assert.Equal(2, progressReports.Count);
        Assert.Equal(100, progressReports[0].BytesDone);
        Assert.Equal(400, progressReports[1].BytesDone);
        Assert.Equal(4, progressReports[1].FilesTotal);
        Assert.NotNull(result);
        Assert.Equal("abc123", result!.SnapshotId);
        Assert.Equal(400, result.BytesAdded);
    }

    [Fact]
    public async Task ParseJsonStreamAsyncReturnsNullWhenNoSummaryIsPresent()
    {
        var json = """{"message_type":"status","bytes_done":1,"total_bytes":1,"files_done":1,"total_files":1,"percent_done":1.0}""";
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        var result = await LocalResticRunner.ParseJsonStreamAsync(reader, _ => { }, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ParseJsonStreamAsyncIgnoresUnknownMessageTypes()
    {
        var json = string.Join('\n',
            """{"message_type":"verbose_status"}""",
            """{"message_type":"summary","snapshot_id":"xyz","data_added":0}""");
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        var progressReports = new List<LocalBackupProgress>();

        var result = await LocalResticRunner.ParseJsonStreamAsync(reader, progressReports.Add, CancellationToken.None);

        Assert.Empty(progressReports);
        Assert.Equal("xyz", result!.SnapshotId);
    }

    /// <summary>
    /// The one test in this file that actually shells out to the real bundled restic binary
    /// (artifacts/tools/windows-x64/restic.exe, fetched by scripts/fetch-third-party-tools.ps1) and
    /// verifies a genuine backup -&gt; restore round trip, matching the project's rule that "success"
    /// means real file content survives, not just that a process exited 0. Silently skipped if that
    /// binary has not been fetched in this checkout.
    /// </summary>
    [Fact]
    public async Task ARealLocalBackupProducesASnapshotThatRestoresTheOriginalFileContent()
    {
        var resticPath = Path.Combine(FindRepositoryRoot(), "artifacts", "tools", "windows-x64", "restic.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(resticPath)) return;

        var root = Path.Combine(Path.GetTempPath(), $"backupmesh-local-restic-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "source");
        var repositoryDirectory = Path.Combine(root, "repo");
        var restoreDirectory = Path.Combine(root, "restore");
        var cacheDirectory = Path.Combine(root, "cache");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(repositoryDirectory);
        var sourceFile = Path.Combine(sourceDirectory, "hello.txt");
        var expectedContent = $"hello from a real local backup {Guid.NewGuid()}";
        await File.WriteAllTextAsync(sourceFile, expectedContent);

        try
        {
            var passwordFile = Path.Combine(root, "password.txt");
            await File.WriteAllTextAsync(passwordFile, "correct horse battery staple");

            var runner = new LocalResticRunner(resticPath, cacheDirectory);
            await runner.EnsureRepositoryAsync(repositoryDirectory, passwordFile, CancellationToken.None);
            var progressReports = new List<LocalBackupProgress>();

            var result = await runner.BackupAsync(repositoryDirectory, passwordFile, [sourceDirectory], progressReports.Add, CancellationToken.None);

            Assert.False(string.IsNullOrWhiteSpace(result.SnapshotId));

            var restoreInfo = new ProcessStartInfo(resticPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            restoreInfo.Environment["RESTIC_REPOSITORY"] = repositoryDirectory;
            restoreInfo.Environment["RESTIC_PASSWORD_FILE"] = passwordFile;
            restoreInfo.Environment["RESTIC_CACHE_DIR"] = cacheDirectory;
            restoreInfo.ArgumentList.Add("restore");
            restoreInfo.ArgumentList.Add("latest");
            restoreInfo.ArgumentList.Add("--target");
            restoreInfo.ArgumentList.Add(restoreDirectory);
            using var restoreProcess = Process.Start(restoreInfo) ?? throw new InvalidOperationException("Could not start restic restore.");
            var restoreStderr = await restoreProcess.StandardError.ReadToEndAsync();
            await restoreProcess.WaitForExitAsync();
            // Not asserting ExitCode == 0 here: restoring an absolute Windows path whose ancestor chain
            // passes through a protected special folder (here, the test source lives under
            // %TEMP% = ...\AppData\Local\Temp, itself under C:\Users) makes restic exit 1 while it
            // still restores the actual file correctly - restic itself logs this as "ignoring error"
            // for the reconstructed ancestor directory's timestamp, not the file's content. What
            // actually matters for this test is verified below: the real file content survives.
            var restoredFile = Directory.GetFiles(restoreDirectory, "hello.txt", SearchOption.AllDirectories).SingleOrDefault();
            Assert.True(restoredFile is not null, $"restic restore produced no hello.txt (exit {restoreProcess.ExitCode}): {restoreStderr}");
            Assert.Equal(expectedContent, await File.ReadAllTextAsync(restoredFile!));
        }
        finally { TryDeleteRecursively(root); }
    }

    // restic restore recreates the source's absolute path structure and can carry over NTFS attributes
    // (e.g. read-only) that block a plain recursive delete of the restore directory; clearing them
    // first is cleanup robustness, not something the test itself is verifying.
    private static void TryDeleteRecursively(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
