using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Service;

public sealed class PollingDriveDiscovery : IStorageDiscovery
{
    public Task<StorageIdentity?> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<StorageIdentity?>(null); // Windows volume discovery plugs in here.
    }
}

public sealed class BasicStorageIdentityVerifier : IStorageIdentityVerifier
{
    public Task<StorageReadiness> VerifyAsync(StorageIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var valid = !string.IsNullOrWhiteSpace(identity.StableId) && Directory.Exists(identity.RootPath);
        return Task.FromResult(new StorageReadiness(valid, valid ? null : "Storage identity or path is invalid."));
    }
}

public sealed class StorageMonitorService(
    IStorageDiscovery discovery,
    IStorageIdentityVerifier verifier,
    StorageStateMachine state,
    StorageOptions options,
    IRestServerLifecycle restServer,
    ILogger<StorageMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = await discovery.DiscoverAsync(stoppingToken);
                if (identity is null)
                {
                    if (state.State != StorageState.Offline)
                    {
                        await restServer.StopAsync(stoppingToken);
                        state.TransitionTo(StorageState.Offline, "Storage removed.");
                    }
                }
                else if (state.State == StorageState.Offline)
                {
                    state.TransitionTo(StorageState.Discovered, identity.StableId);
                    state.TransitionTo(StorageState.Verifying);
                    var readiness = await verifier.VerifyAsync(identity, stoppingToken);
                    if (!readiness.IsReady) state.TransitionTo(StorageState.Error, readiness.Reason);
                    else
                    {
                        state.TransitionTo(StorageState.Waiting, "Grace period.");
                        await Task.Delay(options.GracePeriod, stoppingToken);
                        await restServer.StartAsync(stoppingToken);
                        state.TransitionTo(StorageState.Ready);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Storage monitoring failed.");
                if (state.State != StorageState.Error) state.TransitionTo(StorageState.Error, exception.Message);
            }
            await Task.Delay(options.PollInterval, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await restServer.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
