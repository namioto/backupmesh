using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Service;

public sealed record RegisteredDevicePresence(Guid DeviceId, string StableId, string DisplayName, bool Connected, bool Ready, string? CurrentRoot, DateTimeOffset? ConnectedAt, DateTimeOffset? EligibleAt, string? Reason);

public sealed class StoragePresenceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _connectedSince = [];
    private IReadOnlyList<RegisteredDevicePresence> _devices = [];

    public IReadOnlyList<RegisteredDevicePresence> Refresh(StorageAgentConfiguration configuration, IReadOnlyList<StorageVolumeInfo> volumes, DateTimeOffset now)
    {
        lock (_gate)
        {
            var registeredIds = configuration.Devices.Select(device => device.Id).ToHashSet();
            foreach (var removed in _connectedSince.Keys.Where(id => !registeredIds.Contains(id)).ToArray()) _connectedSince.Remove(removed);

            var result = new List<RegisteredDevicePresence>();
            foreach (var device in configuration.Devices)
            {
                var volume = volumes.FirstOrDefault(item => string.Equals(item.StableId, device.StableId, StringComparison.OrdinalIgnoreCase));
                if (volume is null)
                {
                    _connectedSince.Remove(device.Id);
                    result.Add(new(device.Id, device.StableId, device.DisplayName, false, false, null, null, null, "Device is not connected."));
                    continue;
                }

                if (!_connectedSince.TryGetValue(device.Id, out var connectedAt))
                {
                    connectedAt = now;
                    _connectedSince[device.Id] = connectedAt;
                }
                var eligibleAt = connectedAt.AddMinutes(device.ArrivalDelayMinutes);
                var rootReady = Directory.Exists(volume.Root);
                var ready = rootReady && now >= eligibleAt;
                var reason = !rootReady ? "Volume root is unavailable." : ready ? null : "Waiting for the device arrival delay.";
                result.Add(new(device.Id, device.StableId, device.DisplayName, true, ready, volume.Root, connectedAt, eligibleAt, reason));
            }
            _devices = result;
            return result;
        }
    }

    public IReadOnlyList<RegisteredDevicePresence> List()
    {
        lock (_gate) return _devices.ToArray();
    }
}

public sealed class StorageMonitorService(IStorageVolumeInventory inventory, StorageConfigurationStore configuration, StoragePresenceStore presence, StorageStateMachine state, StorageOptions options, ILogger<StorageMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var devices = presence.Refresh(configuration.Get().Configuration, inventory.GetVolumes(), DateTimeOffset.UtcNow);
                UpdateAggregateState(state, devices);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Storage monitoring failed.");
                if (state.State == StorageState.Offline) state.TransitionTo(StorageState.Discovered, "Storage monitoring failed.");
                if (state.State != StorageState.Error && state.State != StorageState.Busy) state.TransitionTo(StorageState.Error, exception.Message);
            }

            try { await Task.Delay(options.PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private static void UpdateAggregateState(StorageStateMachine state, IReadOnlyList<RegisteredDevicePresence> devices)
    {
        if (state.State == StorageState.Busy) return;
        var desired = devices.Any(device => device.Ready) ? StorageState.Ready : devices.Any(device => device.Connected) ? StorageState.Waiting : StorageState.Offline;
        if (state.State == desired) return;
        if (desired == StorageState.Offline)
        {
            state.TransitionTo(StorageState.Offline, "No registered storage is connected.");
            return;
        }
        if (state.State is StorageState.Ready or StorageState.Error) state.TransitionTo(StorageState.Offline, "Storage presence changed.");
        if (state.State == StorageState.Offline)
        {
            state.TransitionTo(StorageState.Discovered, "Registered storage connected.");
            state.TransitionTo(StorageState.Verifying);
        }
        state.TransitionTo(desired, desired == StorageState.Waiting ? "Waiting for a device arrival delay." : "Registered storage is ready.");
    }
}
