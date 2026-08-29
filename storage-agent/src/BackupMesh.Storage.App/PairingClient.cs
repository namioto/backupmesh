using System.Net.Http.Json;
using System.Net.Http;
using System.IO;
using System.Text.Json.Serialization;

namespace BackupMesh.Storage.App;

public sealed record PairingSessionDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("control_endpoint")] string ControlEndpoint,
    [property: JsonPropertyName("certificate_sha256")] string CertificateSha256,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
public interface IPairingClient { Task<PairingSessionDto> CreateSessionAsync(CancellationToken cancellationToken); }
public sealed class PairingClient : IPairingClient, IDisposable
{
    private readonly HttpClient _client;
    public PairingClient(string endpoint = "http://127.0.0.1:7444/api/v1/") => _client = new() { BaseAddress = new(endpoint), Timeout = TimeSpan.FromSeconds(5) };
    public async Task<PairingSessionDto> CreateSessionAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync("pairing/sessions", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PairingSessionDto>(cancellationToken) ?? throw new InvalidDataException("Pairing response was empty.");
    }
    public void Dispose() => _client.Dispose();
}
