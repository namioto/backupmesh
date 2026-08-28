using System.Net.Http.Json;
using System.Net.Http;
using System.IO;
using System.Text.Json.Serialization;

namespace BackupMesh.Storage.App;

public sealed record PairingCredentialDto([property: JsonPropertyName("credential")] string Credential, [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt);
public interface IPairingClient { Task<PairingCredentialDto> IssueAsync(CancellationToken cancellationToken); }
public sealed class PairingClient : IPairingClient, IDisposable
{
    private readonly HttpClient _client;
    public PairingClient(string endpoint = "http://127.0.0.1:7444/api/v1/") => _client = new() { BaseAddress = new(endpoint), Timeout = TimeSpan.FromSeconds(5) };
    public async Task<PairingCredentialDto> IssueAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync("pairing/credential", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PairingCredentialDto>(cancellationToken) ?? throw new InvalidDataException("Pairing response was empty.");
    }
    public void Dispose() => _client.Dispose();
}
