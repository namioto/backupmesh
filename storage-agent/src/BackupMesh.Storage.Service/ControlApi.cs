using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography.X509Certificates;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Service;

public sealed class ControlApiOptions { public Guid AgentId { get; set; } = Guid.NewGuid(); public Uri RepositoryEndpoint { get; set; } = new("https://localhost:8000/repo"); public string? AuthenticationToken { get; set; } }
public sealed class PairingOptions { public string? CredentialHashPath { get; set; } }
public sealed class MutualTlsOptions { public bool Enabled { get; set; } = true; public int Port { get; set; } = 7443; public string[] ServerNames { get; set; } = []; public string ServerCertificatePath { get; set; } = string.Empty; public string? ServerCertificatePassword { get; set; } public string ClientCertificateAuthorityPath { get; set; } = string.Empty; public string ServerTrustPem { get; set; } = string.Empty; }
public static class MutualTlsCertificateValidator
{
    public static bool Validate(X509Certificate2 certificate, X509Certificate2 authority)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2"));
        return chain.Build(certificate);
    }
}
public sealed record BackupRequest([property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("source_agent_id")] Guid SourceAgentId, [property: JsonPropertyName("backup_set_id")] Guid BackupSetId, [property: JsonPropertyName("target_mapping_id")] Guid TargetMappingId, [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt, [property: JsonPropertyName("snapshot_tags"), MaxLength(32)] string[]? SnapshotTags);
public sealed record BackupAdmission([property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("target_mapping_id")] Guid TargetMappingId, [property: JsonPropertyName("device_id")] Guid DeviceId, [property: JsonPropertyName("state")] string State, [property: JsonPropertyName("accepted_at")] DateTimeOffset AcceptedAt, [property: JsonPropertyName("repository_endpoint")] Uri RepositoryEndpoint);
public sealed record BackupProgress([property: JsonPropertyName("event_id")] Guid EventId, [property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("sequence"), Range(1, long.MaxValue)] long Sequence, [property: JsonPropertyName("reported_at")] DateTimeOffset ReportedAt, [property: JsonPropertyName("phase"), Required, RegularExpression("^(SCANNING|UPLOADING|FINALIZING)$")] string Phase, [property: JsonPropertyName("bytes_done"), Range(0, long.MaxValue)] long BytesDone, [property: JsonPropertyName("bytes_total"), Range(0, long.MaxValue)] long? BytesTotal, [property: JsonPropertyName("files_done"), Range(0, long.MaxValue)] long FilesDone, [property: JsonPropertyName("files_total"), Range(0, long.MaxValue)] long? FilesTotal, [property: JsonPropertyName("message"), StringLength(512)] string? Message);
public sealed record BackupResult([property: JsonPropertyName("event_id")] Guid EventId, [property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("sequence"), Range(1, long.MaxValue)] long Sequence, [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt, [property: JsonPropertyName("outcome"), Required, RegularExpression("^(SUCCEEDED|FAILED|CANCELLED)$")] string Outcome, [property: JsonPropertyName("snapshot_id"), StringLength(128, MinimumLength = 1)] string? SnapshotId, [property: JsonPropertyName("bytes_added"), Range(0, long.MaxValue)] long? BytesAdded, [property: JsonPropertyName("error_code"), RegularExpression("^[A-Z][A-Z0-9_]*$"), StringLength(64)] string? ErrorCode, [property: JsonPropertyName("message"), StringLength(2048)] string? Message);
public sealed record CancelRequest([property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt, [property: JsonPropertyName("reason"), StringLength(512)] string? Reason);
public sealed record JobStatus([property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("state")] string State, [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt, [property: JsonPropertyName("last_sequence")] long LastSequence, [property: JsonPropertyName("progress")] BackupProgress? Progress, [property: JsonPropertyName("result")] BackupResult? Result);
public sealed record BackupCommandEnqueueRequest([property: JsonPropertyName("mapping_ids")] Guid[]? MappingIds, [property: JsonPropertyName("reason"), StringLength(64)] string? Reason);
public sealed record BackupCommandClaimResponse([property: JsonPropertyName("command")] BackupCommand? Command);
public sealed record BackupCommandAcknowledgementRequest([property: JsonPropertyName("command_id")] Guid CommandId, [property: JsonPropertyName("source_agent_id")] Guid SourceAgentId, [property: JsonPropertyName("state"), Required, RegularExpression("^(RUNNING|CLAIMED)$")] string State, [property: JsonPropertyName("claimed_at")] DateTimeOffset ClaimedAt);
public sealed record BackupCommandResultRequest([property: JsonPropertyName("command_id")] Guid CommandId, [property: JsonPropertyName("source_agent_id")] Guid SourceAgentId, [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt, [property: JsonPropertyName("outcome"), Required, RegularExpression("^(SUCCEEDED|FAILED|CANCELLED)$")] string Outcome, [property: JsonPropertyName("job_id")] Guid? JobId, [property: JsonPropertyName("message"), StringLength(2048)] string? Message);
public sealed record BackupCommandCompletionRequest([property: JsonPropertyName("command_id")] Guid CommandId, [property: JsonPropertyName("source_agent_id")] Guid SourceAgentId, [property: JsonPropertyName("state"), Required, RegularExpression("^(SUCCEEDED|FAILED|CANCELLED)$")] string State, [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt, [property: JsonPropertyName("job_id")] Guid? JobId, [property: JsonPropertyName("message"), StringLength(2048)] string? Message);
public sealed record SourceCatalogBackupSet([property: JsonPropertyName("backup_set_id")] Guid BackupSetId, [property: JsonPropertyName("name"), Required, StringLength(128, MinimumLength = 1)] string Name, [property: JsonPropertyName("source_paths"), MinLength(1), MaxLength(4096)] string[] SourcePaths);
public sealed record SourceCatalog([property: JsonPropertyName("source_agent_id")] Guid SourceAgentId, [property: JsonPropertyName("source_agent_name"), Required, StringLength(128, MinimumLength = 1)] string SourceAgentName, [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt, [property: JsonPropertyName("backup_sets"), MaxLength(1024)] SourceCatalogBackupSet[] BackupSets);
public enum StoreOutcome { Accepted, Replayed, NotFound, Conflict, InvalidSequence, Terminal }
public sealed class BackupJobOptions { public string? PersistencePath { get; set; } }

public sealed class BackupJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly Dictionary<Guid, JobStatus> _jobs = [];
    private readonly Dictionary<string, (string Signature, BackupAdmission Admission)> _admissions = [];
    private readonly Dictionary<Guid, object> _events = [];
    private readonly Dictionary<Guid, Guid> _jobMappings = [];
    private readonly Dictionary<Guid, Guid> _activeMappings = [];
    private readonly Dictionary<Guid, Guid> _jobSources = [];
    private readonly string? _persistencePath;
    public Guid? ActiveJobId { get; private set; }
    public bool HasActiveJobs { get { lock (_gate) return _activeMappings.Count > 0; } }
    public BackupJobStore(BackupJobOptions? options = null)
    {
        _persistencePath = options is null ? null : ResolvePath(options.PersistencePath);
        foreach (var entry in Load(_persistencePath))
        {
            _jobs[entry.Status.JobId] = entry.Status;
            _jobSources[entry.Status.JobId] = entry.SourceAgentId;
            if (!Terminal(entry.Status.State))
            {
                _jobMappings[entry.Status.JobId] = entry.MappingId;
                _activeMappings[entry.MappingId] = entry.Status.JobId;
            }
        }
        ActiveJobId = _activeMappings.Values.Cast<Guid?>().FirstOrDefault();
    }
    public (StoreOutcome Outcome, BackupAdmission? Admission) Admit(BackupRequest request, string key, Uri endpoint, Guid deviceId = default)
    {
        lock (_gate)
        {
            var signature = $"{request.JobId:N}|{request.SourceAgentId:N}|{request.BackupSetId:N}|{request.TargetMappingId:N}|{request.RequestedAt:O}|{string.Join(',', request.SnapshotTags ?? [])}";
            if (_admissions.TryGetValue(key, out var prior)) return prior.Signature == signature ? (StoreOutcome.Replayed, prior.Admission) : (StoreOutcome.Conflict, null);
            if (_activeMappings.ContainsKey(request.TargetMappingId) || _jobs.ContainsKey(request.JobId)) return (StoreOutcome.Conflict, null);
            var now = DateTimeOffset.UtcNow; var admission = new BackupAdmission(request.JobId, request.TargetMappingId, deviceId, "ACCEPTED", now, endpoint);
            _jobs[request.JobId] = new(request.JobId, "ACCEPTED", now, 0, null, null); _admissions[key] = (signature, admission); _jobMappings[request.JobId] = request.TargetMappingId; _activeMappings[request.TargetMappingId] = request.JobId; ActiveJobId ??= request.JobId;
            _jobSources[request.JobId] = request.SourceAgentId;
            Persist();
            return (StoreOutcome.Accepted, admission);
        }
    }
    public StoreOutcome Progress(BackupProgress progress)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(progress.JobId, out var job)) return StoreOutcome.NotFound;
            if (Terminal(job.State)) return StoreOutcome.Terminal;
            if (_events.TryGetValue(progress.EventId, out var prior)) return Equals(prior, progress) ? StoreOutcome.Replayed : StoreOutcome.Conflict;
            if (progress.Sequence <= job.LastSequence) return StoreOutcome.InvalidSequence;
            _events[progress.EventId] = progress; _jobs[progress.JobId] = job with { State = "RUNNING", UpdatedAt = DateTimeOffset.UtcNow, LastSequence = progress.Sequence, Progress = progress }; Persist(); return StoreOutcome.Accepted;
        }
    }
    public StoreOutcome Complete(BackupResult result)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(result.JobId, out var job)) return StoreOutcome.NotFound;
            if (_events.TryGetValue(result.EventId, out var prior)) return Equals(prior, result) ? StoreOutcome.Replayed : StoreOutcome.Conflict;
            if (Terminal(job.State)) return StoreOutcome.Terminal;
            if (result.Sequence <= job.LastSequence) return StoreOutcome.InvalidSequence;
            _events[result.EventId] = result; _jobs[result.JobId] = job with { State = result.Outcome, UpdatedAt = DateTimeOffset.UtcNow, LastSequence = result.Sequence, Result = result };
            if (_jobMappings.Remove(result.JobId, out var mappingId)) _activeMappings.Remove(mappingId);
            ActiveJobId = _activeMappings.Values.Cast<Guid?>().FirstOrDefault(); Persist(); return StoreOutcome.Accepted;
        }
    }
    public (StoreOutcome Outcome, JobStatus? Status) Cancel(CancelRequest request)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(request.JobId, out var job)) return (StoreOutcome.NotFound, null);
            if (Terminal(job.State)) return (StoreOutcome.Terminal, job);
            job = job with { State = "CANCEL_REQUESTED", UpdatedAt = DateTimeOffset.UtcNow }; _jobs[request.JobId] = job; Persist(); return (StoreOutcome.Accepted, job);
        }
    }
    public JobStatus? Get(Guid id) { lock (_gate) return _jobs.GetValueOrDefault(id); }
    public IReadOnlyList<JobStatus> List() { lock (_gate) return _jobs.Values.OrderByDescending(job => job.UpdatedAt).ToArray(); }
    public bool IsOwnedBy(Guid jobId, Guid sourceAgentId) { lock (_gate) return _jobSources.GetValueOrDefault(jobId) == sourceAgentId; }
    private static bool Terminal(string state) => state is "CANCELLED" or "SUCCEEDED" or "FAILED";
    private void Persist()
    {
        if (_persistencePath is null) return;
        var directory = Path.GetDirectoryName(_persistencePath) ?? throw new InvalidOperationException("The backup job persistence path must include a directory.");
        Directory.CreateDirectory(directory);
        var entries = _jobs.Values.Select(status => new PersistedJob(status, _jobMappings.GetValueOrDefault(status.JobId), _jobSources.GetValueOrDefault(status.JobId))).ToArray();
        var temporary = $"{_persistencePath}.{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions)); File.Move(temporary, _persistencePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static PersistedJob[] Load(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<PersistedJob[]>(File.ReadAllText(path), JsonOptions) ?? []; }
        catch (Exception exception) when (exception is IOException or JsonException) { throw new InvalidDataException($"Could not load backup jobs from '{path}'.", exception); }
    }
    private static string? ResolvePath(string? path) => path == string.Empty ? null : !string.IsNullOrWhiteSpace(path) ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "backup-jobs.json");
    private sealed record PersistedJob(JobStatus Status, Guid MappingId, Guid SourceAgentId);
}

public sealed class RequiredControlHeadersFilter : IEndpointFilter
{
    private static readonly Regex Key = new("^[A-Za-z0-9._~-]{16,128}$", RegexOptions.CultureInvariant);
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Request.Headers;
        if (!Guid.TryParse(headers["X-Request-ID"], out _) || !Key.IsMatch(headers["Idempotency-Key"].ToString())) return Results.Problem(statusCode: 400, title: "INVALID_REQUEST", detail: "Required request headers are missing or invalid.");
        if (!DateTimeOffset.TryParse(headers["X-BackupMesh-Sent-At"], out var sentAt) || Math.Abs((DateTimeOffset.UtcNow - sentAt).TotalMinutes) > 5) return Results.Problem(statusCode: 400, title: "STALE_REQUEST", detail: "Request timestamp is outside the five-minute window.");
        context.HttpContext.Response.Headers["X-Request-ID"] = headers["X-Request-ID"].ToString(); return await next(context);
    }
}

public sealed class PairingCredentialStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly List<CredentialEntry> _credentials = [];
    public PairingCredentialStore(PairingOptions? pairing = null, ControlApiOptions? control = null)
    {
        _path = pairing is null ? null : ResolvePath(pairing.CredentialHashPath);
        if (_path is not null && File.Exists(_path))
            _credentials.AddRange(File.ReadAllLines(_path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(Parse));
        else if (!string.IsNullOrWhiteSpace(control?.AuthenticationToken) && control.AuthenticationToken.Length >= 32)
            _credentials.Add(new(SHA256.HashData(Encoding.UTF8.GetBytes(control.AuthenticationToken)), null));
    }
    public string Issue(Guid agentId)
    {
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate) { _credentials.Add(new(SHA256.HashData(Encoding.UTF8.GetBytes(credential)), agentId)); Persist(); }
        return credential;
    }
    public bool Authorize(string supplied, Guid agentId)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        lock (_gate)
        {
            CredentialEntry? matched = null;
            foreach (var entry in _credentials) if (CryptographicOperations.FixedTimeEquals(entry.Hash, candidate)) matched = entry;
            if (matched is null || (matched.AgentId is not null && matched.AgentId != agentId)) return false;
            if (matched.AgentId is null) { matched.AgentId = agentId; Persist(); }
            return true;
        }
    }
    private void Persist()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Pairing credential path must include a directory."));
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try { File.WriteAllLines(temporary, _credentials.Select(entry => $"{Convert.ToHexString(entry.Hash)}|{entry.AgentId}")); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string? ResolvePath(string? path) => path == string.Empty ? null : !string.IsNullOrWhiteSpace(path) ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "pairing-credential.sha256");
    private static CredentialEntry Parse(string line)
    {
        var parts = line.Trim().Split('|', 2);
        return new(Convert.FromHexString(parts[0]), parts.Length == 2 && Guid.TryParse(parts[1], out var id) ? id : null);
    }
    private sealed class CredentialEntry(byte[] hash, Guid? agentId) { public byte[] Hash { get; } = hash; public Guid? AgentId { get; set; } = agentId; }
}

public sealed class ControlApiAuthenticationFilter(PairingCredentialStore credentials) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.Connection.RemoteIpAddress is { } remote && System.Net.IPAddress.IsLoopback(remote))
            return await next(context);
        if (!Guid.TryParse(context.HttpContext.Request.Headers["X-BackupMesh-Agent-ID"], out var agentId))
            return Results.Problem(statusCode: 401, title: "UNAUTHORIZED", detail: "A valid Source Agent identity header is required.");
        if (context.HttpContext.Connection.ClientCertificate is { } certificate)
        {
            if (!Guid.TryParse(certificate.GetNameInfo(X509NameType.SimpleName, false), out var certificateAgentId) || certificateAgentId != agentId)
                return Results.Problem(statusCode: 403, title: "FORBIDDEN", detail: "The client certificate identity does not match the Source Agent.");
        }
        var authorization = context.HttpContext.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : string.Empty;
        if (!credentials.Authorize(supplied, agentId))
            return Results.Problem(statusCode: 401, title: "UNAUTHORIZED", detail: "A valid BackupMesh authentication token is required.");
        context.HttpContext.Items["BackupMesh.AgentId"] = agentId;
        return await next(context);
    }

    internal static bool TokenMatches(string? expected, string supplied)
    {
        if (string.IsNullOrEmpty(expected) || expected.Length < 32 || string.IsNullOrEmpty(supplied)) return false;
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(supplied);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public static class ControlApi
{
    public static IEndpointRouteBuilder MapControlApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter<ControlApiAuthenticationFilter>();
        api.MapPost("/pairing/credential", (HttpContext http, PairingCredentialStore credentials, PairingCertificateAuthority certificates, MutualTlsOptions mutualTls, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (http.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote)) return Problem(403, "FORBIDDEN", "Pairing credentials can only be issued from the local tray app.");
            var agentId = Guid.NewGuid();
            var certificate = certificates.Issue(agentId);
            var host = mutualTls.ServerNames.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && !name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) ?? Environment.MachineName;
            var endpoint = new UriBuilder(Uri.UriSchemeHttps, host, mutualTls.Port).Uri.GetLeftPart(UriPartial.Authority);
            return Results.Ok(new
            {
                agent_id = agentId,
                control_endpoint = endpoint,
                credential = credentials.Issue(agentId),
                certificate_pem = certificate.CertificatePem,
                private_key_pem = certificate.PrivateKeyPem,
                authority_pem = mutualTls.ServerTrustPem,
                expires_at = certificate.ExpiresAt,
                issued_at = DateTimeOffset.UtcNow
            });
        });
        api.MapPost("/service/shutdown", (HttpContext http, IHostApplicationLifetime lifetime, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (http.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote)) return Problem(403, "FORBIDDEN", "Service shutdown is available only from the local tray app.");
            _ = Task.Run(async () => { await Task.Delay(100); lifetime.StopApplication(); }, CancellationToken.None);
            return Results.Accepted();
        });
        api.MapGet("/storage/status", (StorageStateMachine state, StoragePresenceStore presence, BackupJobStore jobs, ControlApiOptions options, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return Results.Ok(new { agent_id = options.AgentId, state = state.State.ToString().ToLowerInvariant(), observed_at = DateTimeOffset.UtcNow, storage = presence.List(), active_job_id = jobs.ActiveJobId, message = state.Detail }); });
        api.MapGet("/storage/devices/status", (StoragePresenceStore presence, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return Results.Ok(presence.List()); });
        api.MapGet("/storage/volumes", (IStorageVolumeInventory inventory, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return Results.Ok(inventory.GetVolumes()); });
        api.MapPost("/storage/devices/{device_id:guid}/eject", (Guid device_id, StorageConfigurationStore configuration, IStorageVolumeInventory inventory, IStorageDeviceEjector ejector, BackupJobStore jobs, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (jobs.HasActiveJobs) return Problem(409, "STORAGE_BUSY", "Safe removal is blocked while a backup job is active.");
            var device = configuration.Get().Configuration.Devices.FirstOrDefault(item => item.Id == device_id);
            if (device is null) return Problem(404, "NOT_FOUND", "Registered storage device not found.");
            var volume = inventory.GetVolumes().FirstOrDefault(item => string.Equals(item.StableId, device.StableId, StringComparison.OrdinalIgnoreCase));
            if (volume is null) return Problem(409, "TARGET_NOT_READY", "The registered storage device is not connected.");
            var result = ejector.Eject(volume);
            return result.Succeeded ? Results.Accepted(value: result) : Problem(409, "EJECT_REFUSED", result.Message);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapGet("/backup/targets/{source_agent_id:guid}/{backup_set_id:guid}", (Guid source_agent_id, Guid backup_set_id, HttpContext http, BackupTargetResolver targets, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!AgentMatches(http, source_agent_id)) return Problem(403, "FORBIDDEN", "The authenticated Source Agent cannot access another Source.");
            return Results.Ok(targets.List(source_agent_id, backup_set_id));
        });
        api.MapPost("/backup/request", async (BackupRequest request, HttpContext http, StorageStateMachine state, BackupJobStore jobs, BackupTargetResolver targets, IRepositoryEndpointProvider repositories, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested(); var invalid = Validate(request); if (invalid is not null) return invalid;
            if (!AgentMatches(http, request.SourceAgentId)) return Problem(403, "FORBIDDEN", "The authenticated Source Agent cannot submit a backup for another Source.");
            if (request.JobId == Guid.Empty || request.SourceAgentId == Guid.Empty || request.BackupSetId == Guid.Empty || request.TargetMappingId == Guid.Empty || request.RequestedAt == default)
                return Problem(400, "INVALID_REQUEST", "Backup request IDs and timestamp must be valid.");
            if (state.State is not StorageState.Ready and not StorageState.Busy) return Problem(409, "STORAGE_BUSY", "Storage is not ready.");
            var resolution = targets.Resolve(request);
            if (resolution.Target is null) return Problem(resolution.ErrorCode == "TARGET_NOT_FOUND" ? 404 : 409, resolution.ErrorCode ?? "TARGET_NOT_READY", resolution.Message ?? "Target is unavailable.");
            Uri endpoint;
            try { endpoint = await repositories.GetEndpointAsync(resolution.Target, ct); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
            {
                return Problem(503, "REPOSITORY_SERVER_FAILED", exception.Message);
            }
            var result = jobs.Admit(request, http.Request.Headers["Idempotency-Key"].ToString(), endpoint, resolution.Target.DeviceId);
            if (result.Outcome == StoreOutcome.Conflict) return Problem(409, "JOB_CONFLICT", "Another backup is active or the idempotency key conflicts.");
            if (result.Outcome == StoreOutcome.Accepted) state.TransitionTo(StorageState.Busy, request.JobId.ToString());
            http.Response.Headers["Idempotency-Replayed"] = (result.Outcome == StoreOutcome.Replayed).ToString().ToLowerInvariant(); return Results.Accepted(value: result.Admission);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapPost("/backup/progress", (BackupProgress progress, HttpContext http, BackupJobStore jobs, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); if (!AgentCanAccessJob(http, jobs, progress.JobId)) return Problem(403, "FORBIDDEN", "The backup job belongs to another Source Agent."); var invalid = Validate(progress); return invalid ?? EventResult(jobs.Progress(progress), http); }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapPost("/backup/result", (BackupResult result, HttpContext http, StorageStateMachine state, BackupJobStore jobs, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested(); if (!AgentCanAccessJob(http, jobs, result.JobId)) return Problem(403, "FORBIDDEN", "The backup job belongs to another Source Agent."); var invalid = Validate(result); if (invalid is not null) return invalid; var outcome = jobs.Complete(result);
            if (outcome == StoreOutcome.Accepted && state.State == StorageState.Busy && !jobs.HasActiveJobs) state.TransitionTo(StorageState.Ready, result.Message);
            return EventResult(outcome, http);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapPost("/backup/cancel", (CancelRequest request, HttpContext http, BackupJobStore jobs, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested(); if (!AgentCanAccessJob(http, jobs, request.JobId)) return Problem(403, "FORBIDDEN", "The backup job belongs to another Source Agent."); var invalid = Validate(request); if (invalid is not null) return invalid; var result = jobs.Cancel(request);
            if (result.Outcome == StoreOutcome.NotFound) return Problem(404, "NOT_FOUND", "Backup job not found."); if (result.Outcome == StoreOutcome.Terminal) return Problem(409, "JOB_CONFLICT", "Backup job is terminal.");
            http.Response.Headers["Idempotency-Replayed"] = "false"; return Results.Accepted(value: result.Status);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapGet("/backup/status/{job_id:guid}", (Guid job_id, HttpContext http, BackupJobStore jobs, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); if (!AgentCanAccessJob(http, jobs, job_id)) return Problem(403, "FORBIDDEN", "The backup job belongs to another Source Agent."); var status = jobs.Get(job_id); return status is null ? Problem(404, "NOT_FOUND", "Backup job not found.") : Results.Ok(status); });
        api.MapGet("/backup/jobs", (HttpContext http, BackupJobStore jobs, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); var list = jobs.List(); return Results.Ok(AuthenticatedAgent(http) is { } agentId ? list.Where(job => jobs.IsOwnedBy(job.JobId, agentId)) : list); });
        api.MapPost("/backup/commands/enqueue", (BackupCommandEnqueueRequest request, HttpContext http, BackupTargetResolver targets, BackupCommandQueue commands, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (http.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote)) return Problem(403, "FORBIDDEN", "Backup commands can only be queued from the local tray app.");
            var invalid = Validate(request);
            if (invalid is not null) return invalid;
            var readyTargets = targets.ListReady(request.MappingIds);
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? "manual" : request.Reason.Trim();
            var drafts = readyTargets.Select(target => new BackupCommandDraft(target.SourceAgentId, target.BackupSetId, target.MappingId, reason)).ToArray();
            var result = commands.Enqueue(http.Request.Headers["Idempotency-Key"].ToString(), drafts, DateTimeOffset.UtcNow);
            if (result.Outcome == StoreOutcome.Conflict) return Problem(409, "COMMAND_CONFLICT", "The idempotency key conflicts with a different enqueue request.");
            http.Response.Headers["Idempotency-Replayed"] = (result.Outcome == StoreOutcome.Replayed).ToString().ToLowerInvariant();
            return Results.Accepted(value: result.Result);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapPost("/backup/commands/claim/{source_agent_id:guid}", (Guid source_agent_id, HttpContext http, BackupCommandQueue commands, BackupCommandOptions options, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!AgentMatches(http, source_agent_id)) return Problem(403, "FORBIDDEN", "The authenticated Source Agent cannot claim another Source's commands.");
            return Results.Ok(new BackupCommandClaimResponse(commands.ClaimNext(source_agent_id, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(Math.Max(60, options.LeaseSeconds)))));
        });
        api.MapPost("/backup/commands/result", (BackupCommandResultRequest request, HttpContext http, BackupCommandQueue commands, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var invalid = Validate(request);
            if (invalid is not null) return invalid;
            if (!AgentMatches(http, request.SourceAgentId)) return Problem(403, "FORBIDDEN", "The authenticated Source Agent cannot complete another Source's command.");
            if (request.CommandId == Guid.Empty || request.SourceAgentId == Guid.Empty || request.CompletedAt == default) return Problem(400, "INVALID_REQUEST", "Command result IDs and timestamp must be valid.");
            var outcome = commands.Complete(request.SourceAgentId, request.CommandId, request.Outcome, request.CompletedAt, request.JobId, request.Message);
            if (outcome == StoreOutcome.NotFound) return Problem(404, "NOT_FOUND", "Backup command not found.");
            if (outcome == StoreOutcome.Conflict) return Problem(403, "FORBIDDEN", "The backup command belongs to another Source Agent.");
            return EventResult(outcome, http);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapGet("/source/commands/{source_agent_id:guid}/next", (Guid source_agent_id, HttpContext http, BackupCommandQueue commands, BackupCommandOptions options, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!AgentMatches(http, source_agent_id)) return Problem(403, "FORBIDDEN", "The authenticated Source Agent cannot claim another Source's commands.");
            return Results.Ok(new BackupCommandClaimResponse(commands.ClaimNext(source_agent_id, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(Math.Max(60, options.LeaseSeconds)))));
        });
        api.MapPost("/source/commands/{command_id:guid}/ack", (Guid command_id, BackupCommandAcknowledgementRequest request, HttpContext http, BackupCommandQueue commands, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var invalid = Validate(request);
            if (invalid is not null) return invalid;
            if (request.CommandId != command_id || request.CommandId == Guid.Empty || request.SourceAgentId == Guid.Empty || request.ClaimedAt == default) return Problem(400, "INVALID_REQUEST", "Command acknowledgement IDs and timestamp must be valid.");
            if (!AgentMatches(http, request.SourceAgentId)) return Problem(403, "FORBIDDEN", "The authenticated Source Agent cannot acknowledge another Source's command.");
            var outcome = commands.Acknowledge(request.SourceAgentId, request.CommandId, request.ClaimedAt);
            if (outcome == StoreOutcome.NotFound) return Problem(404, "NOT_FOUND", "Backup command not found.");
            if (outcome == StoreOutcome.Conflict) return Problem(403, "FORBIDDEN", "The backup command belongs to another Source Agent.");
            return EventResult(outcome, http);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapPost("/source/commands/{command_id:guid}/complete", (Guid command_id, BackupCommandCompletionRequest request, HttpContext http, BackupCommandQueue commands, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var invalid = Validate(request);
            if (invalid is not null) return invalid;
            if (request.CommandId != command_id || request.CommandId == Guid.Empty || request.SourceAgentId == Guid.Empty || request.CompletedAt == default) return Problem(400, "INVALID_REQUEST", "Command completion IDs and timestamp must be valid.");
            if (!AgentMatches(http, request.SourceAgentId)) return Problem(403, "FORBIDDEN", "The authenticated Source Agent cannot complete another Source's command.");
            var outcome = commands.Complete(request.SourceAgentId, request.CommandId, request.State, request.CompletedAt, request.JobId, request.Message);
            if (outcome == StoreOutcome.NotFound) return Problem(404, "NOT_FOUND", "Backup command not found.");
            if (outcome == StoreOutcome.Conflict) return Problem(403, "FORBIDDEN", "The backup command belongs to another Source Agent.");
            return EventResult(outcome, http);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapPost("/source/catalog", (SourceCatalog catalog, HttpContext http, SourceCatalogStore catalogs, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var invalid = Validate(catalog);
            if (invalid is not null) return invalid;
            if (!AgentMatches(http, catalog.SourceAgentId)) return Problem(403, "FORBIDDEN", "The authenticated Source Agent cannot publish another Source catalog.");
            if (catalog.SourceAgentId == Guid.Empty || catalog.UpdatedAt == default || catalog.BackupSets.Any(set => set.BackupSetId == Guid.Empty || set.SourcePaths.Any(string.IsNullOrWhiteSpace)) || catalog.BackupSets.Select(set => set.BackupSetId).Distinct().Count() != catalog.BackupSets.Length)
                return Problem(400, "INVALID_REQUEST", "Catalog IDs, timestamps, Backup Set IDs, and source paths must be valid and unique.");
            var outcome = catalogs.Upsert(catalog);
            if (outcome == StoreOutcome.InvalidSequence)
                return Problem(409, "STALE_CATALOG", "A newer catalog from this Source Agent is already stored.");
            if (outcome == StoreOutcome.Conflict)
                return Problem(409, "REPLAY_CONFLICT", "The catalog timestamp was reused with different content.");
            http.Response.Headers["Idempotency-Replayed"] = (outcome == StoreOutcome.Replayed).ToString().ToLowerInvariant();
            return Results.NoContent();
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapGet("/source/catalogs", (SourceCatalogStore catalogs, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return Results.Ok(catalogs.List()); });
        api.MapGet("/storage/configuration", (StorageConfigurationStore configuration, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Results.Ok(configuration.Get());
        });
        api.MapPut("/storage/configuration", (StorageConfigurationUpdate update, StorageConfigurationStore configuration, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var errors = BackupTopologyValidator.Validate(update.Configuration);
            if (errors.Count > 0) return Problem(400, "INVALID_CONFIGURATION", errors[0]);
            var result = configuration.Update(update);
            return result.Outcome == StoreOutcome.Conflict
                ? Problem(409, "CONFIGURATION_CONFLICT", $"Configuration revision {result.Document.Revision} is current; reload before saving.")
                : Results.Ok(result.Document);
        });
        return endpoints;
    }
    private static IResult EventResult(StoreOutcome outcome, HttpContext http)
    {
        if (outcome == StoreOutcome.NotFound) return Problem(404, "NOT_FOUND", "Backup job not found.");
        if (outcome is StoreOutcome.Conflict or StoreOutcome.InvalidSequence or StoreOutcome.Terminal) return Problem(409, "REPLAY_CONFLICT", "Event conflicts with job state or sequence.");
        http.Response.Headers["Idempotency-Replayed"] = (outcome == StoreOutcome.Replayed).ToString().ToLowerInvariant(); return Results.NoContent();
    }
    private static bool AgentMatches(HttpContext http, Guid claimedAgentId) => !http.Items.TryGetValue("BackupMesh.AgentId", out var authenticated) || authenticated is Guid id && id == claimedAgentId;
    private static Guid? AuthenticatedAgent(HttpContext http) => http.Items.TryGetValue("BackupMesh.AgentId", out var authenticated) && authenticated is Guid id ? id : null;
    private static bool AgentCanAccessJob(HttpContext http, BackupJobStore jobs, Guid jobId) => AuthenticatedAgent(http) is not { } agentId || jobs.IsOwnedBy(jobId, agentId);
    private static IResult? Validate<T>(T request)
    {
        var errors = new List<ValidationResult>(); if (Validator.TryValidateObject(request!, new ValidationContext(request!), errors, true)) return null;
        return Results.ValidationProblem(errors.SelectMany(e => e.MemberNames.DefaultIfEmpty(string.Empty), (e, member) => new { member, e.ErrorMessage }).GroupBy(x => x.member).ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid value.").ToArray()));
    }
    private static IResult Problem(int status, string code, string detail) => Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code, ["occurred_at"] = DateTimeOffset.UtcNow, ["retryable"] = status >= 500 });
}
