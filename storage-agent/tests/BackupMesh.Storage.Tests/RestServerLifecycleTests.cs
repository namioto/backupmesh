using System.Diagnostics;
using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class RestServerLifecycleTests
{
    [Fact]
    public void RepositoryEndpoint_UsesResticRestBackendPrefixAndEscapesPath()
    {
        var endpoint = RepositoryServerManager.BuildEndpoint("storage.local", 18000, "team files\\documents");

        Assert.Equal("rest:http://storage.local:18000/team%20files/documents/", endpoint.OriginalString);
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

    private sealed class FakeProcess : IManagedProcess
    {
        public bool Killed { get; private set; }
        public bool HasExited => Killed;
        public void Kill() => Killed = true;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
