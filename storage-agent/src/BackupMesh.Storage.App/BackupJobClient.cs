using System.Net.Http.Json;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupMesh.Storage.App;

public sealed record BackupProgressDto(
    [property: JsonPropertyName("bytes_done")] long BytesDone,
    [property: JsonPropertyName("bytes_total")] long? BytesTotal,
    [property: JsonPropertyName("files_done")] long FilesDone,
    [property: JsonPropertyName("files_total")] long? FilesTotal);

public sealed record BackupResultDto(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("snapshot_id")] string? SnapshotId,
    [property: JsonPropertyName("message")] string? Message);

public sealed record BackupJobDto(
    [property: JsonPropertyName("job_id")] Guid JobId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("progress")] BackupProgressDto? Progress,
    [property: JsonPropertyName("result")] BackupResultDto? Result,
    [property: JsonPropertyName("target_mapping_id")] Guid? TargetMappingId = null,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt = null);

public sealed record BackupCommandEnqueueDto([property: JsonPropertyName("queued_count")] int QueuedCount);

public interface IBackupJobClient
{
    Task<IReadOnlyList<BackupJobDto>> ListAsync(CancellationToken cancellationToken);
    Task<int> EnqueueAsync(Guid[] mappingIds, string reason, CancellationToken cancellationToken);
    Task CancelAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed class BackupJobClient : IBackupJobClient, IDisposable
{
    private readonly HttpClient _client;
    public BackupJobClient(string endpoint = "http://127.0.0.1:7444/api/v1/") =>
        _client = new() { BaseAddress = new(endpoint, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(5) };

    public async Task<IReadOnlyList<BackupJobDto>> ListAsync(CancellationToken cancellationToken) =>
        await _client.GetFromJsonAsync<BackupJobDto[]>("backup/jobs", cancellationToken) ?? [];

    public async Task<int> EnqueueAsync(Guid[] mappingIds, string reason, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "backup/commands/enqueue")
        {
            Content = JsonContent.Create(new { mapping_ids = mappingIds, reason })
        };
        AddControlHeaders(request);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BackupCommandEnqueueDto>(cancellationToken) ?? new(0);
        return result.QueuedCount;
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "backup/cancel")
        {
            Content = JsonContent.Create(new { job_id = jobId, requested_at = DateTimeOffset.UtcNow, reason = "Cancelled from Storage Agent UI" })
        };
        AddControlHeaders(request);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static void AddControlHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-Request-ID", Guid.NewGuid().ToString());
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-BackupMesh-Sent-At", DateTimeOffset.UtcNow.ToString("O"));
    }

    public void Dispose() => _client.Dispose();
}

// Resolving TargetMappingId to display names here (rather than binding two separate columns in XAML to
// job.TargetMappingId and a converter) keeps the join against the currently-loaded Mappings collection a
// one-time snapshot taken when the job list is refreshed, matching how every other *Name property in
// this file already works.
public sealed class BackupJobViewModel(BackupJobDto model, MappingViewModel? mapping = null)
{
    public Guid JobId => model.JobId;
    public string State => model.State;
    public Guid? TargetMappingId => model.TargetMappingId;
    public DateTimeOffset? StartedAt => model.StartedAt;
    public DateTimeOffset UpdatedAt => model.UpdatedAt;
    public string Updated => model.UpdatedAt.LocalDateTime.ToString("g");
    public string Target => mapping is null ? "—" : $"{mapping.BackupSetName} → {mapping.DeviceName}";
    public string Progress => model.Progress is null ? "—" : model.Progress.BytesTotal is > 0
        ? $"{model.Progress.BytesDone * 100d / model.Progress.BytesTotal:0.0}% · {model.Progress.FilesDone}/{model.Progress.FilesTotal?.ToString() ?? "?"} files{EtaSuffix}"
        : $"{model.Progress.BytesDone:N0} bytes · {model.Progress.FilesDone} files";
    public string Result => model.Result?.SnapshotId is { Length: > 0 } snapshot ? $"{model.Result.Outcome} · {snapshot[..Math.Min(8, snapshot.Length)]}" : model.Result?.Outcome ?? "—";
    public bool CanCancel => State is "ACCEPTED" or "RUNNING";
    // Mirrors BackupJobStore.Terminal() server-side: CANCEL_REQUESTED is deliberately excluded - a
    // cancellation still in flight is not yet safe to treat as "this device is done".
    public bool IsTerminal => State is "SUCCEEDED" or "FAILED" or "CANCELLED";

    // Shared by the Progress column's compact "· ETA 4m" and the removal banner's descriptive
    // "(about 4 minutes left)" - null whenever there isn't enough information yet to estimate.
    public TimeSpan? EstimatedTimeRemaining
    {
        get
        {
            if (model.StartedAt is not { } startedAt || model.Progress is not { BytesTotal: > 0 } progress || progress.BytesDone <= 0) return null;
            var fraction = progress.BytesDone / (double)progress.BytesTotal;
            if (fraction is <= 0 or >= 1) return null;
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            return TimeSpan.FromTicks((long)(elapsed.Ticks * (1 - fraction) / fraction));
        }
    }

    private string EtaSuffix
    {
        get
        {
            if (EstimatedTimeRemaining is not { } remaining) return string.Empty;
            var eta = remaining.TotalHours >= 1 ? $"{remaining.TotalHours:0.0}h" : remaining.TotalMinutes >= 1 ? $"{remaining.TotalMinutes:0}m" : "<1m";
            return $" · ETA {eta}";
        }
    }
}

// The user's next move differs by code: STORAGE_BUSY means wait, EJECT_REFUSED means Windows itself
// refused (e.g. a file still open) and waiting alone won't fix it.
public sealed class StorageDeviceEjectRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record ProblemResponseDto(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("code")] string? Code);

public interface IStorageDeviceClient { Task EjectAsync(Guid deviceId, CancellationToken cancellationToken); }
public sealed class StorageDeviceClient : IStorageDeviceClient, IDisposable
{
    private readonly HttpClient _client;
    public StorageDeviceClient(string endpoint = "http://127.0.0.1:7444/api/v1/") => _client = new() { BaseAddress = new(endpoint), Timeout = TimeSpan.FromSeconds(10) };
    public async Task EjectAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"storage/devices/{deviceId}/eject");
        request.Headers.Add("X-Request-ID", Guid.NewGuid().ToString());
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-BackupMesh-Sent-At", DateTimeOffset.UtcNow.ToString("O"));
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        ProblemResponseDto? problem = null;
        try { problem = await response.Content.ReadFromJsonAsync<ProblemResponseDto>(cancellationToken: cancellationToken); }
        catch (JsonException) { }
        throw new StorageDeviceEjectRefusedException(problem?.Code ?? problem?.Title ?? "UNKNOWN", problem?.Detail ?? $"Request failed with status {(int)response.StatusCode}.");
    }
    public void Dispose() => _client.Dispose();
}
