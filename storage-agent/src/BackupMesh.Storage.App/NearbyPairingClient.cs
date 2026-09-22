using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BackupMesh.Storage.App;

public sealed record NearbyComputerDto(
    [property: JsonPropertyName("agent_id")] Guid AgentId,
    [property: JsonPropertyName("agent_name")] string AgentName,
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt);

public sealed record NearbyPairingRequestDto(
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("agent_id")] Guid AgentId,
    [property: JsonPropertyName("agent_name")] string AgentName,
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("comparison_code")] string ComparisonCode,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("status")] string Status);

public interface INearbyPairingClient
{
    Task<IReadOnlyList<NearbyComputerDto>> ListAsync(CancellationToken cancellationToken);
    Task<NearbyPairingRequestDto> RequestAsync(Guid agentId, string identity, CancellationToken cancellationToken);
    Task<NearbyPairingRequestDto> GetAsync(Guid requestId, CancellationToken cancellationToken);
    Task CancelAsync(Guid requestId, CancellationToken cancellationToken);
}

public sealed class NearbyPairingClient : INearbyPairingClient, IDisposable
{
    private readonly HttpClient _client;

    public NearbyPairingClient(string endpoint = "http://127.0.0.1:7444/api/v1/") =>
        _client = new() { BaseAddress = new(endpoint, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(5) };

    public async Task<IReadOnlyList<NearbyComputerDto>> ListAsync(CancellationToken cancellationToken) =>
        await GetValueAsync<NearbyComputerDto[]>("pairing/nearby", cancellationToken) ?? [];

    public async Task<NearbyPairingRequestDto> RequestAsync(Guid agentId, string identity, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync("pairing/requests", new { agent_id = agentId, identity }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<NearbyPairingRequestDto>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Storage Service returned an empty pairing request.");
    }

    public async Task<NearbyPairingRequestDto> GetAsync(Guid requestId, CancellationToken cancellationToken) =>
        await GetValueAsync<NearbyPairingRequestDto>($"pairing/requests/{requestId}", cancellationToken)
            ?? throw new InvalidDataException("Storage Service returned an empty pairing status.");

    public async Task CancelAsync(Guid requestId, CancellationToken cancellationToken)
    {
        using var response = await _client.DeleteAsync($"pairing/requests/{requestId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T?> GetValueAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    public void Dispose() => _client.Dispose();
}
