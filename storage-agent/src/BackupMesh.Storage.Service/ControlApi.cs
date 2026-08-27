using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Service;

public sealed class ControlApiOptions { public Guid AgentId { get; set; } = Guid.NewGuid(); public Uri RepositoryEndpoint { get; set; } = new("https://localhost:8000/repo"); }
public sealed record BackupRequest([property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("source_agent_id")] Guid SourceAgentId, [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt, [property: JsonPropertyName("repository"), Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]*$"), StringLength(128, MinimumLength = 1)] string Repository, [property: JsonPropertyName("snapshot_tags"), MaxLength(32)] string[]? SnapshotTags);
public sealed record BackupAdmission([property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("state")] string State, [property: JsonPropertyName("accepted_at")] DateTimeOffset AcceptedAt, [property: JsonPropertyName("repository_endpoint")] Uri RepositoryEndpoint);
public sealed record BackupProgress([property: JsonPropertyName("event_id")] Guid EventId, [property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("sequence"), Range(1, long.MaxValue)] long Sequence, [property: JsonPropertyName("reported_at")] DateTimeOffset ReportedAt, [property: JsonPropertyName("phase"), Required, RegularExpression("^(SCANNING|UPLOADING|FINALIZING)$")] string Phase, [property: JsonPropertyName("bytes_done"), Range(0, long.MaxValue)] long BytesDone, [property: JsonPropertyName("bytes_total"), Range(0, long.MaxValue)] long? BytesTotal, [property: JsonPropertyName("files_done"), Range(0, long.MaxValue)] long FilesDone, [property: JsonPropertyName("files_total"), Range(0, long.MaxValue)] long? FilesTotal, [property: JsonPropertyName("message"), StringLength(512)] string? Message);
public sealed record BackupResult([property: JsonPropertyName("event_id")] Guid EventId, [property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("sequence"), Range(1, long.MaxValue)] long Sequence, [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt, [property: JsonPropertyName("outcome"), Required, RegularExpression("^(SUCCEEDED|FAILED|CANCELLED)$")] string Outcome, [property: JsonPropertyName("snapshot_id"), StringLength(128, MinimumLength = 1)] string? SnapshotId, [property: JsonPropertyName("bytes_added"), Range(0, long.MaxValue)] long? BytesAdded, [property: JsonPropertyName("error_code"), RegularExpression("^[A-Z][A-Z0-9_]*$"), StringLength(64)] string? ErrorCode, [property: JsonPropertyName("message"), StringLength(2048)] string? Message);
public sealed record CancelRequest([property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt, [property: JsonPropertyName("reason"), StringLength(512)] string? Reason);
public sealed record JobStatus([property: JsonPropertyName("job_id")] Guid JobId, [property: JsonPropertyName("state")] string State, [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt, [property: JsonPropertyName("last_sequence")] long LastSequence, [property: JsonPropertyName("progress")] BackupProgress? Progress, [property: JsonPropertyName("result")] BackupResult? Result);
public sealed record SourceCatalogBackupSet([property: JsonPropertyName("backup_set_id")] Guid BackupSetId, [property: JsonPropertyName("name"), Required, StringLength(128, MinimumLength = 1)] string Name, [property: JsonPropertyName("source_paths"), MinLength(1), MaxLength(4096)] string[] SourcePaths);
public sealed record SourceCatalog([property: JsonPropertyName("source_agent_id")] Guid SourceAgentId, [property: JsonPropertyName("source_agent_name"), Required, StringLength(128, MinimumLength = 1)] string SourceAgentName, [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt, [property: JsonPropertyName("backup_sets"), MaxLength(1024)] SourceCatalogBackupSet[] BackupSets);
public enum StoreOutcome { Accepted, Replayed, NotFound, Conflict, InvalidSequence, Terminal }

public sealed class BackupJobStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, JobStatus> _jobs = [];
    private readonly Dictionary<string, (string Signature, BackupAdmission Admission)> _admissions = [];
    private readonly Dictionary<Guid, object> _events = [];
    public Guid? ActiveJobId { get; private set; }
    public (StoreOutcome Outcome, BackupAdmission? Admission) Admit(BackupRequest request, string key, Uri endpoint)
    {
        lock (_gate)
        {
            var signature = $"{request.JobId:N}|{request.SourceAgentId:N}|{request.RequestedAt:O}|{request.Repository}|{string.Join(',', request.SnapshotTags ?? [])}";
            if (_admissions.TryGetValue(key, out var prior)) return prior.Signature == signature ? (StoreOutcome.Replayed, prior.Admission) : (StoreOutcome.Conflict, null);
            if (ActiveJobId is not null || _jobs.ContainsKey(request.JobId)) return (StoreOutcome.Conflict, null);
            var now = DateTimeOffset.UtcNow; var admission = new BackupAdmission(request.JobId, "ACCEPTED", now, endpoint);
            _jobs[request.JobId] = new(request.JobId, "ACCEPTED", now, 0, null, null); _admissions[key] = (signature, admission); ActiveJobId = request.JobId;
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
            _events[progress.EventId] = progress; _jobs[progress.JobId] = job with { State = "RUNNING", UpdatedAt = DateTimeOffset.UtcNow, LastSequence = progress.Sequence, Progress = progress }; return StoreOutcome.Accepted;
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
            _events[result.EventId] = result; _jobs[result.JobId] = job with { State = result.Outcome, UpdatedAt = DateTimeOffset.UtcNow, LastSequence = result.Sequence, Result = result }; ActiveJobId = null; return StoreOutcome.Accepted;
        }
    }
    public (StoreOutcome Outcome, JobStatus? Status) Cancel(CancelRequest request)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(request.JobId, out var job)) return (StoreOutcome.NotFound, null);
            if (Terminal(job.State)) return (StoreOutcome.Terminal, job);
            job = job with { State = "CANCEL_REQUESTED", UpdatedAt = DateTimeOffset.UtcNow }; _jobs[request.JobId] = job; return (StoreOutcome.Accepted, job);
        }
    }
    public JobStatus? Get(Guid id) { lock (_gate) return _jobs.GetValueOrDefault(id); }
    private static bool Terminal(string state) => state is "CANCELLED" or "SUCCEEDED" or "FAILED";
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

public static class ControlApi
{
    public static IEndpointRouteBuilder MapControlApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");
        api.MapGet("/storage/status", (StorageStateMachine state, BackupJobStore jobs, ControlApiOptions options, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return Results.Ok(new { agent_id = options.AgentId, state = state.State.ToString().ToLowerInvariant(), observed_at = DateTimeOffset.UtcNow, storage = (object?)null, active_job_id = jobs.ActiveJobId, message = state.Detail }); });
        api.MapPost("/backup/request", (BackupRequest request, HttpContext http, StorageStateMachine state, BackupJobStore jobs, ControlApiOptions options, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested(); var invalid = Validate(request); if (invalid is not null) return invalid;
            if (state.State != StorageState.Ready) return Problem(409, "STORAGE_BUSY", "Storage is not ready.");
            var result = jobs.Admit(request, http.Request.Headers["Idempotency-Key"].ToString(), options.RepositoryEndpoint);
            if (result.Outcome == StoreOutcome.Conflict) return Problem(409, "JOB_CONFLICT", "Another backup is active or the idempotency key conflicts.");
            if (result.Outcome == StoreOutcome.Accepted) state.TransitionTo(StorageState.Busy, request.JobId.ToString());
            http.Response.Headers["Idempotency-Replayed"] = (result.Outcome == StoreOutcome.Replayed).ToString().ToLowerInvariant(); return Results.Accepted(value: result.Admission);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapPost("/backup/progress", (BackupProgress progress, HttpContext http, BackupJobStore jobs, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); var invalid = Validate(progress); return invalid ?? EventResult(jobs.Progress(progress), http); }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapPost("/backup/result", (BackupResult result, HttpContext http, StorageStateMachine state, BackupJobStore jobs, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested(); var invalid = Validate(result); if (invalid is not null) return invalid; var outcome = jobs.Complete(result);
            if (outcome == StoreOutcome.Accepted && state.State == StorageState.Busy) state.TransitionTo(StorageState.Ready, result.Message);
            return EventResult(outcome, http);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapPost("/backup/cancel", (CancelRequest request, HttpContext http, BackupJobStore jobs, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested(); var invalid = Validate(request); if (invalid is not null) return invalid; var result = jobs.Cancel(request);
            if (result.Outcome == StoreOutcome.NotFound) return Problem(404, "NOT_FOUND", "Backup job not found."); if (result.Outcome == StoreOutcome.Terminal) return Problem(409, "JOB_CONFLICT", "Backup job is terminal.");
            http.Response.Headers["Idempotency-Replayed"] = "false"; return Results.Accepted(value: result.Status);
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapGet("/backup/status/{job_id:guid}", (Guid job_id, BackupJobStore jobs, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); var status = jobs.Get(job_id); return status is null ? Problem(404, "NOT_FOUND", "Backup job not found.") : Results.Ok(status); });
        api.MapPost("/source/catalog", (SourceCatalog catalog, HttpContext http, SourceCatalogStore catalogs, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var invalid = Validate(catalog);
            if (invalid is not null) return invalid;
            if (catalog.SourceAgentId == Guid.Empty || catalog.UpdatedAt == default || catalog.BackupSets.Any(set => set.BackupSetId == Guid.Empty || set.SourcePaths.Any(string.IsNullOrWhiteSpace)) || catalog.BackupSets.Select(set => set.BackupSetId).Distinct().Count() != catalog.BackupSets.Length)
                return Problem(400, "INVALID_REQUEST", "Catalog IDs, timestamps, Backup Set IDs, and source paths must be valid and unique.");
            catalogs.Upsert(catalog);
            http.Response.Headers["Idempotency-Replayed"] = "false";
            return Results.NoContent();
        }).AddEndpointFilter<RequiredControlHeadersFilter>();
        api.MapGet("/source/catalogs", (SourceCatalogStore catalogs, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return Results.Ok(catalogs.List()); });
        return endpoints;
    }
    private static IResult EventResult(StoreOutcome outcome, HttpContext http)
    {
        if (outcome == StoreOutcome.NotFound) return Problem(404, "NOT_FOUND", "Backup job not found.");
        if (outcome is StoreOutcome.Conflict or StoreOutcome.InvalidSequence or StoreOutcome.Terminal) return Problem(409, "REPLAY_CONFLICT", "Event conflicts with job state or sequence.");
        http.Response.Headers["Idempotency-Replayed"] = (outcome == StoreOutcome.Replayed).ToString().ToLowerInvariant(); return Results.NoContent();
    }
    private static IResult? Validate<T>(T request)
    {
        var errors = new List<ValidationResult>(); if (Validator.TryValidateObject(request!, new ValidationContext(request!), errors, true)) return null;
        return Results.ValidationProblem(errors.SelectMany(e => e.MemberNames.DefaultIfEmpty(string.Empty), (e, member) => new { member, e.ErrorMessage }).GroupBy(x => x.member).ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid value.").ToArray()));
    }
    private static IResult Problem(int status, string code, string detail) => Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code, ["occurred_at"] = DateTimeOffset.UtcNow, ["retryable"] = status >= 500 });
}
