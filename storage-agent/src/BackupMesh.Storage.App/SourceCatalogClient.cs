using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json.Serialization;

namespace BackupMesh.Storage.App;

public sealed record SourceCatalogBackupSetDto(
    [property: JsonPropertyName("backup_set_id")] Guid BackupSetId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source_paths")] string[] SourcePaths);

public sealed record SourceCatalogDto(
    [property: JsonPropertyName("source_agent_id")] Guid SourceAgentId,
    [property: JsonPropertyName("source_agent_name")] string SourceAgentName,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("backup_sets")] SourceCatalogBackupSetDto[] BackupSets);

public interface ISourceCatalogClient
{
    Task<IReadOnlyList<SourceCatalogDto>> ListAsync(CancellationToken cancellationToken);
}

public sealed class SourceCatalogClient : ISourceCatalogClient, IDisposable
{
    private readonly HttpClient _client;

    public SourceCatalogClient(string endpoint = "http://127.0.0.1:7444/api/v1/")
    {
        _client = new() { BaseAddress = new(endpoint, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<IReadOnlyList<SourceCatalogDto>> ListAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "source/catalogs");
        request.Headers.Add("X-Request-ID", Guid.NewGuid().ToString());
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SourceCatalogDto[]>(cancellationToken: cancellationToken) ?? [];
    }

    public void Dispose() => _client.Dispose();
}
