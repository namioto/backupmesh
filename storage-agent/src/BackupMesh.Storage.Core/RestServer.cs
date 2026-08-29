using System.Diagnostics;

namespace BackupMesh.Storage.Core;

public interface IManagedProcess : IAsyncDisposable
{
    bool HasExited { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IProcessFactory
{
    IManagedProcess Start(ProcessStartInfo startInfo);
}

public interface IRestServerLifecycle : IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class RestServerLifecycle(RestServerOptions options, IProcessFactory processFactory) : IRestServerLifecycle
{
    private IManagedProcess? _process;
    public bool IsRunning => _process is { HasExited: false };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRunning) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(options.RepositoryPath))
            throw new InvalidOperationException("The rest-server repository path is required.");

        var info = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("--path");
        info.ArgumentList.Add(options.RepositoryPath);
        info.ArgumentList.Add("--listen");
        info.ArgumentList.Add(options.ListenAddress);
        if (!string.IsNullOrWhiteSpace(options.PasswordFile))
        {
            info.ArgumentList.Add("--htpasswd-file");
            info.ArgumentList.Add(options.PasswordFile);
        }

        _process = processFactory.Start(info);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is null) return;
        if (!_process.HasExited) _process.Kill();
        await _process.WaitForExitAsync(cancellationToken);
        await _process.DisposeAsync();
        _process = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
}

public sealed class SystemProcessFactory : IProcessFactory
{
    public IManagedProcess Start(ProcessStartInfo startInfo) =>
        new SystemManagedProcess(Process.Start(startInfo) ?? throw new InvalidOperationException("rest-server failed to start."));
}

internal sealed class SystemManagedProcess(Process process) : IManagedProcess
{
    public bool HasExited => process.HasExited;
    public void Kill() => process.Kill(entireProcessTree: true);
    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
}
