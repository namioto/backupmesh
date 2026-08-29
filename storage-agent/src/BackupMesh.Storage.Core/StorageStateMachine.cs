namespace BackupMesh.Storage.Core;

public sealed class StorageStateMachine
{
    private static readonly IReadOnlyDictionary<StorageState, HashSet<StorageState>> Allowed =
        new Dictionary<StorageState, HashSet<StorageState>>
        {
            [StorageState.Offline] = [StorageState.Discovered],
            [StorageState.Discovered] = [StorageState.Verifying, StorageState.Offline, StorageState.Error],
            [StorageState.Verifying] = [StorageState.Waiting, StorageState.Ready, StorageState.Offline, StorageState.Error],
            [StorageState.Waiting] = [StorageState.Ready, StorageState.Offline, StorageState.Error],
            [StorageState.Ready] = [StorageState.Busy, StorageState.Offline, StorageState.Error],
            [StorageState.Busy] = [StorageState.Ready, StorageState.Offline, StorageState.Error],
            [StorageState.Error] = [StorageState.Offline, StorageState.Discovered]
        };

    private readonly object _gate = new();
    public StorageState State { get; private set; } = StorageState.Offline;
    public DateTimeOffset ChangedAt { get; private set; } = DateTimeOffset.UtcNow;
    public string? Detail { get; private set; }

    public void TransitionTo(StorageState next, string? detail = null)
    {
        lock (_gate)
        {
            if (next == State)
            {
                Detail = detail;
                return;
            }

            if (!Allowed[State].Contains(next))
                throw new InvalidOperationException($"Storage cannot transition from {State} to {next}.");

            State = next;
            Detail = detail;
            ChangedAt = DateTimeOffset.UtcNow;
        }
    }
}
