using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupMesh.Storage.Service;

public sealed class BackupCommandOptions
{
    public string? PersistencePath { get; set; }
    public int LeaseSeconds { get; set; } = 3600;
}

public sealed record BackupCommandDraft(Guid SourceAgentId, Guid BackupSetId, Guid TargetMappingId, string Reason);

public sealed record BackupCommand(
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("source_agent_id")] Guid SourceAgentId,
    [property: JsonPropertyName("backup_set_id")] Guid BackupSetId,
    [property: JsonPropertyName("target_mapping_id")] Guid TargetMappingId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("claimed_at")] DateTimeOffset? ClaimedAt,
    [property: JsonPropertyName("lease_expires_at")] DateTimeOffset? LeaseExpiresAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("job_id")] Guid? JobId,
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("message")] string? Message);

public sealed record BackupCommandEnqueueResult(
    [property: JsonPropertyName("queued_count")] int QueuedCount,
    [property: JsonPropertyName("command_ids")] Guid[] CommandIds,
    [property: JsonPropertyName("skipped_mapping_ids")] Guid[] SkippedMappingIds);

public sealed class BackupCommandQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly Dictionary<Guid, BackupCommand> _commands = [];
    private readonly Dictionary<string, (string Signature, Guid[] CommandIds, Guid[] SkippedMappingIds)> _enqueueKeys = [];
    private readonly string? _persistencePath;

    public BackupCommandQueue(BackupCommandOptions? options = null)
    {
        _persistencePath = options is null ? null : ResolvePath(options.PersistencePath);
        foreach (var command in Load(_persistencePath)) _commands[command.CommandId] = command;
    }

    public (StoreOutcome Outcome, BackupCommandEnqueueResult Result) Enqueue(string idempotencyKey, IReadOnlyList<BackupCommandDraft> drafts, DateTimeOffset now)
    {
        lock (_gate)
        {
            var signature = Signature(drafts);
            if (_enqueueKeys.TryGetValue(idempotencyKey, out var prior))
            {
                var result = new BackupCommandEnqueueResult(prior.CommandIds.Length, prior.CommandIds, prior.SkippedMappingIds);
                return prior.Signature == signature ? (StoreOutcome.Replayed, result) : (StoreOutcome.Conflict, result);
            }

            var commandIds = new List<Guid>();
            var skipped = new List<Guid>();
            foreach (var draft in drafts)
            {
                if (HasOpenCommand(draft.TargetMappingId))
                {
                    skipped.Add(draft.TargetMappingId);
                    continue;
                }

                var command = new BackupCommand(Guid.NewGuid(), "BACKUP_SET", draft.SourceAgentId, draft.BackupSetId, draft.TargetMappingId,
                    draft.Reason, now, "PENDING", null, null, null, null, null, null);
                _commands[command.CommandId] = command;
                commandIds.Add(command.CommandId);
            }

            var created = commandIds.ToArray();
            var skippedIds = skipped.ToArray();
            _enqueueKeys[idempotencyKey] = (signature, created, skippedIds);
            Persist();
            return (StoreOutcome.Accepted, new(created.Length, created, skippedIds));
        }
    }

    public BackupCommand? ClaimNext(Guid sourceAgentId, DateTimeOffset now, TimeSpan lease)
    {
        lock (_gate)
        {
            var command = _commands.Values
                .Where(item => item.SourceAgentId == sourceAgentId && IsClaimable(item, now))
                .OrderBy(item => item.RequestedAt)
                .FirstOrDefault();
            if (command is null) return null;
            command = command with { State = "CLAIMED", ClaimedAt = now, LeaseExpiresAt = now.Add(lease) };
            _commands[command.CommandId] = command;
            Persist();
            return command;
        }
    }

    public StoreOutcome Acknowledge(Guid sourceAgentId, Guid commandId, DateTimeOffset claimedAt)
    {
        lock (_gate)
        {
            if (!_commands.TryGetValue(commandId, out var command)) return StoreOutcome.NotFound;
            if (command.SourceAgentId != sourceAgentId) return StoreOutcome.Conflict;
            if (IsTerminal(command.State)) return StoreOutcome.Terminal;
            _commands[commandId] = command with { State = "RUNNING", ClaimedAt = claimedAt };
            Persist();
            return StoreOutcome.Accepted;
        }
    }

    public StoreOutcome Complete(Guid sourceAgentId, Guid commandId, string outcome, DateTimeOffset completedAt, Guid? jobId, string? message)
    {
        lock (_gate)
        {
            if (!_commands.TryGetValue(commandId, out var command)) return StoreOutcome.NotFound;
            if (command.SourceAgentId != sourceAgentId) return StoreOutcome.Conflict;
            if (IsTerminal(command.State)) return StoreOutcome.Terminal;
            _commands[commandId] = command with { State = outcome, CompletedAt = completedAt, JobId = jobId, Outcome = outcome, Message = message, LeaseExpiresAt = null };
            Persist();
            return StoreOutcome.Accepted;
        }
    }

    public IReadOnlyList<BackupCommand> List()
    {
        lock (_gate) return _commands.Values.OrderByDescending(command => command.RequestedAt).ToArray();
    }

    private bool HasOpenCommand(Guid mappingId) => _commands.Values.Any(command => command.TargetMappingId == mappingId && !IsTerminal(command.State));
    private static bool IsClaimable(BackupCommand command, DateTimeOffset now) => command.State == "PENDING" || command.State == "CLAIMED" && command.LeaseExpiresAt <= now;
    private static bool IsTerminal(string state) => state is "SUCCEEDED" or "FAILED" or "CANCELLED";
    private static string Signature(IEnumerable<BackupCommandDraft> drafts) => string.Join('|', drafts
        .OrderBy(item => item.TargetMappingId)
        .Select(item => $"{item.SourceAgentId:N}:{item.BackupSetId:N}:{item.TargetMappingId:N}:{item.Reason}"));

    private void Persist()
    {
        if (_persistencePath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_persistencePath) ?? throw new InvalidOperationException("The command persistence path must include a directory."));
        var temporary = $"{_persistencePath}.{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(_commands.Values.ToArray(), JsonOptions)); File.Move(temporary, _persistencePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static BackupCommand[] Load(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<BackupCommand[]>(File.ReadAllText(path), JsonOptions) ?? []; }
        catch (Exception exception) when (exception is IOException or JsonException) { throw new InvalidDataException($"Could not load backup commands from '{path}'.", exception); }
    }

    private static string? ResolvePath(string? path) => path == string.Empty ? null : !string.IsNullOrWhiteSpace(path) ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "backup-commands.json");
}
