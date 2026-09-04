using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json.Serialization;

namespace BackupMesh.Storage.App;

public sealed record SourceConnectionDto(
    [property: JsonPropertyName("agent_id")] Guid AgentId,
    [property: JsonPropertyName("agent_name")] string AgentName,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("backup_set_count")] int BackupSetCount,
    [property: JsonPropertyName("revoked")] bool Revoked);

public interface ISourceConnectionsClient
{
    Task<IReadOnlyList<SourceConnectionDto>> ListAsync(CancellationToken cancellationToken);
    Task RevokeAsync(Guid agentId, CancellationToken cancellationToken);
    Task UnrevokeAsync(Guid agentId, CancellationToken cancellationToken);
}

public sealed class SourceConnectionsClient : ISourceConnectionsClient, IDisposable
{
    private readonly HttpClient _client;

    public SourceConnectionsClient(string endpoint = "http://127.0.0.1:7444/api/v1/")
    {
        _client = new() { BaseAddress = new(endpoint, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<IReadOnlyList<SourceConnectionDto>> ListAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("sources", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SourceConnectionDto[]>(cancellationToken: cancellationToken) ?? [];
    }

    public async Task RevokeAsync(Guid agentId, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync($"sources/{agentId}/revoke", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UnrevokeAsync(Guid agentId, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync($"sources/{agentId}/unrevoke", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}
