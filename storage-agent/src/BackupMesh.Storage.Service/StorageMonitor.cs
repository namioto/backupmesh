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
                if (volume is null && FolderStorageIdentity.TryGetPath(device.StableId, out var folderRoot) && Directory.Exists(folderRoot))
                    volume = new(device.StableId, folderRoot, device.VolumeLabel ?? "Folder", 0, 0, device.DisplayName, 1);
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

public sealed class StorageMonitorService(IStorageVolumeInventory inventory, StorageConfigurationStore configuration, StoragePresenceStore presence, StorageStateMachine state, StorageOptions options, AutomationSettingsStore automation, BackupCommandQueue commands, ILogger<StorageMonitorService> logger) : BackgroundService
{
    private readonly HashSet<Guid> _readyDevices = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var devices = presence.Refresh(configuration.Get().Configuration, inventory.GetVolumes(), DateTimeOffset.UtcNow);
                UpdateAggregateState(state, devices);
                EnqueueNewlyReadyDevices(configuration.Get().Configuration, devices);
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

    private void EnqueueNewlyReadyDevices(StorageAgentConfiguration topology, IReadOnlyList<RegisteredDevicePresence> devices)
    {
        var readyNow = devices.Where(device => device.Ready).Select(device => device.DeviceId).ToHashSet();
        if (automation.Get().Enabled)
        {
            foreach (var deviceId in readyNow.Except(_readyDevices))
            {
                var arrived = devices.First(item => item.DeviceId == deviceId);
                var drafts = BuildArrivalDrafts(topology, devices, arrived);
                commands.Enqueue($"arrival:{deviceId:N}:{devices.First(item => item.DeviceId == deviceId).ConnectedAt:O}", drafts.ToArray(), DateTimeOffset.UtcNow);
            }
        }
        _readyDevices.Clear();
        _readyDevices.UnionWith(readyNow);
    }

    internal static IReadOnlyList<BackupCommandDraft> BuildArrivalDrafts(StorageAgentConfiguration topology, IReadOnlyList<RegisteredDevicePresence> devices, RegisteredDevicePresence arrived)
    {
        var readyDeviceIds = devices.Where(device => device.Ready).Select(device => device.DeviceId).ToHashSet();
        var sourceSets = topology.BackupSets
            .Where(set => IsSourceArrival(set, arrived, readyDeviceIds))
            .Select(set => set.Id)
            .ToHashSet();
        return (from mapping in topology.Mappings
                where mapping.Enabled && readyDeviceIds.Contains(mapping.DeviceId)
                    && (mapping.DeviceId == arrived.DeviceId || sourceSets.Contains(mapping.BackupSetId))
                join backupSet in topology.BackupSets on mapping.BackupSetId equals backupSet.Id
                select new BackupCommandDraft(backupSet.SourceAgentId, backupSet.Id, mapping.Id,
                    mapping.DeviceId == arrived.DeviceId ? "destination-arrival" : "source-arrival"))
            .DistinctBy(draft => draft.TargetMappingId)
            .ToArray();
    }

    // A Backup Set with an explicit trigger device (set by the user in the tray, not inferred) only
    // fires for that device's own arrival - never for an unrelated device whose root happens to
    // contain a matching path - and, under AllAvailable, only once every trigger device for that
    // Backup Set is simultaneously ready. A Backup Set with no explicit trigger device keeps the
    // original path-containment inference so already-configured Backup Sets are unaffected.
    private static bool IsSourceArrival(SourceBackupSet set, RegisteredDevicePresence arrived, HashSet<Guid> readyDeviceIds)
    {
        if (set.TriggerDeviceIds.Count > 0)
        {
            if (!set.TriggerDeviceIds.Contains(arrived.DeviceId)) return false;
            return set.TriggerPolicy == BackupSetTriggerPolicy.AnyAvailable || set.TriggerDeviceIds.All(readyDeviceIds.Contains);
        }
        return !string.IsNullOrWhiteSpace(arrived.CurrentRoot) && set.SourcePaths.Any(path => IsWithin(arrived.CurrentRoot, path));
    }

    private static bool IsWithin(string root, string candidate)
    {
        // A Backup Set's source paths can belong to a remote (e.g. Linux) Source Agent and be POSIX
        // paths like "/home/user/Documents". Path.IsPathRooted treats a leading '/' as rooted on
        // Windows too, and Path.GetFullPath resolves it against the current drive's root (e.g. to
        // "C:\home\user\Documents"), which can coincidentally fall under a connected device's root.
        // Only a path that already looks like a real Windows path can be a same-host source arrival.
        if (!LooksLikeWindowsPath(candidate)) return false;
        try
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullCandidate = Path.GetFullPath(candidate);
            return fullCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || fullCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool LooksLikeWindowsPath(string candidate) =>
        (candidate.Length >= 3 && candidate[1] == ':' && (candidate[2] == '\\' || candidate[2] == '/') && char.IsAsciiLetter(candidate[0]))
        || (candidate.Length >= 2 && candidate[0] == '\\' && candidate[1] == '\\');

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
