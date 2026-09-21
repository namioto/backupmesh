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
    [property: JsonPropertyName("rebind_agent_id")] Guid? RebindAgentId)
{
    [JsonIgnore]
    public string Invitation => "backupmesh:v1:" + Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        new { endpoint = ControlEndpoint, code = Code, fingerprint = CertificateSha256 }))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
public sealed record PairingSessionRequestDto([property: JsonPropertyName("rebind_agent_id")] Guid? RebindAgentId);
public sealed class PairingSetupRequiredException() : HttpRequestException(Localization.Text("PairingAddressCertificateMismatch"));
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
        if (response.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.ServiceUnavailable)
        {
            using var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var title = problem.RootElement.TryGetProperty("title", out var value) ? value.GetString() : null;
            if (title == "PAIRING_ADDRESS_CERTIFICATE_MISMATCH") throw new PairingSetupRequiredException();
            if (title == "NO_NETWORK_ADDRESS") throw new HttpRequestException(Localization.Text("PairingNoNetworkAddress"));
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PairingSessionDto>(cancellationToken: cancellationToken) ?? throw new InvalidDataException(Localization.Text("Text_Pairingresponsewasempty_536774"));
    }
    public async Task RotateAuthorityAsync(CancellationToken cancellationToken)
    {
        // Obtain elevation before changing trust material. Cancelling UAC must leave it intact.
        var endpoint = _client.BaseAddress!;
        if (!endpoint.IsLoopback || endpoint.Port != 7444)
            throw new HttpRequestException(Localization.Text("IdentityRestartFailed"));
        var servicePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Service", "BackupMesh.Storage.Service.exe"));
        var script = """
            $ErrorActionPreference = 'Stop'
            try {
                $service = Get-CimInstance Win32_Service -Filter "Name='BackupMeshStorageAgent'"
                if (!$service -or !$service.PathName.StartsWith('"' + '__SERVICE__' + '"', [StringComparison]::OrdinalIgnoreCase)) { exit 3 }
                Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:7444/api/v1/pairing/rotate-authority' -TimeoutSec 10 | Out-Null
                Restart-Service -Name BackupMeshStorageAgent -ErrorAction Stop
                (Get-Service BackupMeshStorageAgent).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
                exit 0
            } catch { exit 1 }
            """.Replace("__SERVICE__", servicePath.Replace("'", "''"));
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        try
        {
            using var process = System.Diagnostics.Process.Start(start) ?? throw new HttpRequestException(Localization.Text("IdentityRestartFailed"));
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new HttpRequestException(Localization.Text("IdentityRestartFailed"));
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new HttpRequestException(Localization.Text(error.NativeErrorCode == 1223 ? "IdentityApprovalCancelled" : "IdentityRestartFailed"), error);
        }
        // Running service status alone does not mean HTTPS is ready. Verify pairing preflight too.
        for (var attempt = 0; ; attempt++)
        {
            try { await CreateSessionAsync(null, cancellationToken); break; }
            catch (HttpRequestException) when (attempt < 9) { await Task.Delay(1000, cancellationToken); }
        }
    }
    public void Dispose() => _client.Dispose();
}
