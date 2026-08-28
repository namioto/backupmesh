using System.Net.Http.Json;
using System.Net.Http;
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
    [property: JsonPropertyName("result")] BackupResultDto? Result);

public interface IBackupJobClient
{
    Task<IReadOnlyList<BackupJobDto>> ListAsync(CancellationToken cancellationToken);
    Task CancelAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed class BackupJobClient : IBackupJobClient, IDisposable
{
    private readonly HttpClient _client;
    public BackupJobClient(string endpoint = "http://127.0.0.1:7444/api/v1/") =>
        _client = new() { BaseAddress = new(endpoint, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(5) };

    public async Task<IReadOnlyList<BackupJobDto>> ListAsync(CancellationToken cancellationToken) =>
        await _client.GetFromJsonAsync<BackupJobDto[]>("backup/jobs", cancellationToken) ?? [];

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "backup/cancel")
        {
            Content = JsonContent.Create(new { job_id = jobId, requested_at = DateTimeOffset.UtcNow, reason = "Cancelled from Storage Agent UI" })
        };
        request.Headers.Add("X-Request-ID", Guid.NewGuid().ToString());
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-BackupMesh-Sent-At", DateTimeOffset.UtcNow.ToString("O"));
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}

public sealed class BackupJobViewModel(BackupJobDto model)
{
    public Guid JobId => model.JobId;
    public string State => model.State;
    public string Updated => model.UpdatedAt.LocalDateTime.ToString("g");
    public string Progress => model.Progress is null ? "—" : model.Progress.BytesTotal is > 0
        ? $"{model.Progress.BytesDone * 100d / model.Progress.BytesTotal:0.0}% · {model.Progress.FilesDone}/{model.Progress.FilesTotal?.ToString() ?? "?"} files"
        : $"{model.Progress.BytesDone:N0} bytes · {model.Progress.FilesDone} files";
    public string Result => model.Result?.SnapshotId is { Length: > 0 } snapshot ? $"{model.Result.Outcome} · {snapshot[..Math.Min(8, snapshot.Length)]}" : model.Result?.Outcome ?? "—";
    public bool CanCancel => State is "ACCEPTED" or "RUNNING";
}
