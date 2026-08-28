using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class RestServerLifecycleTests
{
    [Fact]
    public async Task BundledRestServerRequiresGeneratedCredentials()
    {
        var repositoryRoot = FindRepositoryRoot();
        var executable = Path.Combine(repositoryRoot, "artifacts", "tools", "windows-x64", "rest-server.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(executable)) return;
        var temporary = Path.Combine(Path.GetTempPath(), $"backupmesh-auth-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        var credential = RepositoryServerManager.CreateCredential(Guid.NewGuid(), temporary);
        var port = FreeTcpPort();
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--path", temporary, "--listen", $"127.0.0.1:{port}", "--htpasswd-file", credential.FilePath }
        }) ?? throw new InvalidOperationException("Could not start bundled rest-server.");
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(2) };
            HttpResponseMessage? unauthenticated = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { unauthenticated = await client.GetAsync(""); break; }
                catch (HttpRequestException) { await Task.Delay(100); }
            }
            Assert.NotNull(unauthenticated);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{credential.Username}:{credential.Password}")));
            using var authenticated = await client.GetAsync("");
            Assert.NotEqual(HttpStatusCode.Unauthorized, authenticated.StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            Directory.Delete(temporary, true);
        }
    }

    [Fact]
    public void RepositoryEndpoint_UsesResticRestBackendPrefixAndEscapesPath()
    {
        var endpoint = RepositoryServerManager.BuildEndpoint("storage.local", 18000, "team files\\documents");

        Assert.Equal("rest:http://storage.local:18000/team%20files/documents/", endpoint.OriginalString);
    }

    [Fact]
    public void RepositoryEndpoint_EmbedsEphemeralBasicAuthentication()
    {
        var endpoint = RepositoryServerManager.BuildEndpoint("storage.local", 18000, "repo", "backupmesh", "secret");
        Assert.Equal("rest:http://backupmesh:secret@storage.local:18000/repo/", endpoint.OriginalString);
    }

    [Fact]
    public void RepositoryPublicHostDefaultsToTheWindowsComputerName()
    {
        Assert.Equal(Environment.MachineName, RepositoryServerManager.ResolvePublicHost(null));
        Assert.Equal("storage.example", RepositoryServerManager.ResolvePublicHost(" storage.example "));
    }

    [Fact]
    public void RepositoryCredentialCreatesShaPasswordFileWithoutPlaintextSecret()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-credential-test-{Guid.NewGuid():N}");
        try
        {
            var credential = RepositoryServerManager.CreateCredential(Guid.NewGuid(), directory);
            var contents = File.ReadAllText(credential.FilePath);
            Assert.StartsWith("backupmesh:{SHA}", contents);
            Assert.DoesNotContain(credential.Password, contents, StringComparison.Ordinal);
            Assert.Equal(32, credential.Password.Length);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Start_UsesArgumentListAndNoShell_ThenStops()
    {
        var factory = new FakeFactory();
        await using var lifecycle = new RestServerLifecycle(new RestServerOptions
        {
            ExecutablePath = "rest-server.exe",
            RepositoryPath = "C:\\backup data",
            ListenAddress = "localhost:9000",
            PasswordFile = "C:\\secret file"
        }, factory);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.True(lifecycle.IsRunning);
        Assert.False(factory.Info!.UseShellExecute);
        Assert.Equal(["--path", "C:\\backup data", "--listen", "localhost:9000", "--htpasswd-file", "C:\\secret file"], factory.Info.ArgumentList);

        await lifecycle.StopAsync(CancellationToken.None);
        Assert.True(factory.Process.Killed);
        Assert.False(lifecycle.IsRunning);
    }

    [Fact]
    public async Task Start_RequiresRepositoryPath()
    {
        await using var lifecycle = new RestServerLifecycle(new RestServerOptions(), new FakeFactory());
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(CancellationToken.None));
    }

    private sealed class FakeFactory : IProcessFactory
    {
        public ProcessStartInfo? Info { get; private set; }
        public FakeProcess Process { get; } = new();
        public IManagedProcess Start(ProcessStartInfo startInfo) { Info = startInfo; return Process; }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class FakeProcess : IManagedProcess
    {
        public bool Killed { get; private set; }
        public bool HasExited => Killed;
        public void Kill() => Killed = true;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
