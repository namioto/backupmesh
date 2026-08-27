using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.App;

public sealed record StorageConfigurationDocumentDto(long Revision, DateTimeOffset UpdatedAt, StorageAgentConfiguration Configuration);
public sealed record StorageConfigurationUpdateDto(long ExpectedRevision, StorageAgentConfiguration Configuration);

public interface IStorageConfigurationClient
{
    Task<StorageConfigurationDocumentDto> GetAsync(CancellationToken cancellationToken);
    Task<StorageConfigurationDocumentDto> UpdateAsync(long expectedRevision, StorageAgentConfiguration configuration, CancellationToken cancellationToken);
}

public sealed class StorageConfigurationConflictException : Exception
{
    public StorageConfigurationConflictException() : base("The Storage Agent configuration changed in another process.") { }
}

public sealed class StorageConfigurationClient : IStorageConfigurationClient, IDisposable
{
    private readonly HttpClient _client;

    public StorageConfigurationClient(string endpoint = "http://127.0.0.1:7444/api/v1/")
    {
        _client = new() { BaseAddress = new(endpoint, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<StorageConfigurationDocumentDto> GetAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("storage/configuration", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StorageConfigurationDocumentDto>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Storage Service returned an empty configuration response.");
    }

    public async Task<StorageConfigurationDocumentDto> UpdateAsync(long expectedRevision, StorageAgentConfiguration configuration, CancellationToken cancellationToken)
    {
        using var response = await _client.PutAsJsonAsync("storage/configuration", new StorageConfigurationUpdateDto(expectedRevision, configuration), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new StorageConfigurationConflictException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StorageConfigurationDocumentDto>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Storage Service returned an empty configuration response.");
    }

    public void Dispose() => _client.Dispose();
}
