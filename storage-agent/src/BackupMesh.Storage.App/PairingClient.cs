using System.Net.Http.Json;
using System.Net.Http;
using System.IO;
using System.Text.Json.Serialization;

namespace BackupMesh.Storage.App;

public sealed record PairingSessionDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("control_endpoint")] string ControlEndpoint,
    [property: JsonPropertyName("certificate_sha256")] string CertificateSha256,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("rebind_agent_id")] Guid? RebindAgentId);
public sealed record PairingSessionRequestDto([property: JsonPropertyName("rebind_agent_id")] Guid? RebindAgentId);
public interface IPairingClient
{
    Task<PairingSessionDto> CreateSessionAsync(Guid? rebindAgentId, CancellationToken cancellationToken);
    Task RotateAuthorityAsync(CancellationToken cancellationToken);
}
public sealed class PairingClient : IPairingClient, IDisposable
{
    private readonly HttpClient _client;
    public PairingClient(string endpoint = "http://127.0.0.1:7444/api/v1/") => _client = new() { BaseAddress = new(endpoint), Timeout = TimeSpan.FromSeconds(5) };
    public async Task<PairingSessionDto> CreateSessionAsync(Guid? rebindAgentId, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync("pairing/sessions", new PairingSessionRequestDto(rebindAgentId), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PairingSessionDto>(cancellationToken: cancellationToken) ?? throw new InvalidDataException("Pairing response was empty.");
    }
    public async Task RotateAuthorityAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync("pairing/rotate-authority", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
    public void Dispose() => _client.Dispose();
}
