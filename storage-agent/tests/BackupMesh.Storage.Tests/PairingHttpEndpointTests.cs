using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BackupMesh.Storage.Service;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Tests;

public sealed class PairingHttpEndpointTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"backupmesh-pairing-http-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly PairingSessionStore _sessions;
    private readonly PairingCredentialStore _credentials = new();
    private readonly RevokedSourceStore _revocations = new();
    private readonly SourceCatalogStore _catalogs = new(new() { PersistencePath = string.Empty });
    private readonly PairingCertificateAuthority _certificateAuthority;
    private readonly IssuedCertificateStore _issuedCertificates = new();
    private readonly SourceDisplayNameStore _displayNames = new();
    private readonly IHost _host;
    private readonly TestServer _server;

    public PairingHttpEndpointTests()
    {
        _sessions = new PairingSessionStore(_clock);
        _certificateAuthority = new PairingCertificateAuthority(new() { ProtectedAuthorityPath = Path.Combine(_directory, "authority.dpapi") });
        using var serverCertificate = _certificateAuthority.IssueServerCertificate(["test-storage"]);
        var mutualTls = new MutualTlsOptions { ServerNames = ["test-storage"], Port = 7443, ServerTrustPem = serverCertificate.ExportCertificatePem() };

        _host = new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services =>
            {
                // MapControlApi() registers every route as one endpoint group, so the matcher builds
                // request-delegate metadata for all of them up front, not just the ones a test calls.
                // Every service type referenced anywhere in ControlApi.cs must resolve here, even though
                // these pairing tests only ever invoke the pairing and service/shutdown endpoints.
                services.AddRouting();
                services.AddLogging();
                services.AddSingleton(mutualTls);
                services.AddSingleton(_sessions);
                services.AddSingleton(new PairingAttemptThrottle(_clock));
                services.AddSingleton(_credentials);
                services.AddSingleton(_revocations);
                services.AddSingleton(_certificateAuthority);
                services.AddSingleton(_issuedCertificates);
                services.AddSingleton(_displayNames);
                services.AddSingleton<ControlApiAuthenticationFilter>();
                services.AddSingleton(new ControlApiOptions());
                services.AddSingleton(new StorageStateMachine());
                services.AddSingleton(new StoragePresenceStore());
                services.AddSingleton(new BackupJobStore(new() { PersistencePath = string.Empty }));
                services.AddSingleton(new BackupCommandQueue(new() { PersistencePath = string.Empty }));
                services.AddSingleton(new BackupCommandOptions());
                services.AddSingleton(new AutomationSettingsStore(new() { PersistencePath = string.Empty }));
                services.AddSingleton(_catalogs);
                services.AddSingleton(new StorageConfigurationStore(new() { PersistencePath = string.Empty }));
                services.AddSingleton<IStorageVolumeInventory>(new StubVolumeInventory());
                services.AddSingleton<IStorageDeviceEjector>(new StubDeviceEjector());
                services.AddSingleton<IRepositoryEndpointProvider>(new StubRepositoryEndpoints());
                services.AddSingleton<IRepositorySessionController>(new StubRepositorySessions());
                services.AddSingleton(sp => new BackupTargetResolver(sp.GetRequiredService<StorageConfigurationStore>(), sp.GetRequiredService<StoragePresenceStore>()));
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapControlApi());
            }))
            .Start();
        _server = _host.GetTestServer();
    }

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task SessionCreationIsRejectedFromNonLoopbackCallers()
    {
        var context = await PostAsync("/api/v1/pairing/sessions", IPAddress.Parse("203.0.113.10"));
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task SessionCreationSucceedsFromLoopback()
    {
        var context = await PostAsync("/api/v1/pairing/sessions", IPAddress.Loopback);
        Assert.Equal(200, context.Response.StatusCode);
        var payload = await ReadJsonAsync(context);
        Assert.True(payload.TryGetProperty("code", out var code) && code.GetString()!.Length >= 20);
        Assert.Equal("https://test-storage:7443", payload.GetProperty("control_endpoint").GetString());
    }

    [Fact]
    public async Task ExchangeRejectsAnUnknownCode()
    {
        var response = await ExchangeAsync(new string('a', 27), Guid.NewGuid(), "source-1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExchangeRejectsAnExpiredCode()
    {
        var session = _sessions.Create();
        _clock.Advance(TimeSpan.FromMinutes(11));

        var response = await ExchangeAsync(session.Code, Guid.NewGuid(), "source-1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExchangeConsumesTheCodeExactlyOnce()
    {
        var session = _sessions.Create();
        var agentId = Guid.NewGuid();

        var first = await ExchangeAsync(session.Code, agentId, "source-1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await ExchangeAsync(session.Code, agentId, "source-1");
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task ExchangeRejectsAMissingAgentId()
    {
        var session = _sessions.Create();
        var response = await ExchangeAsync(session.Code, Guid.Empty, "source-1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExchangeRejectsAMissingAgentName()
    {
        var session = _sessions.Create();
        var response = await ExchangeAsync(session.Code, Guid.NewGuid(), "  ");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RepeatedInvalidCodesLockOutFurtherExchangeAttempts()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failure = await ExchangeAsync(new string('a', 27), Guid.NewGuid(), "source-1");
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        // Even a fresh, valid code is throttled once the failure threshold is hit for this caller.
        var session = _sessions.Create();
        var lockedOut = await ExchangeAsync(session.Code, Guid.NewGuid(), "source-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedOut.StatusCode);

        // A different caller is unaffected: the throttle is keyed per remote address, not global.
        var otherCallerSession = _sessions.Create();
        var otherCaller = await ExchangeAsync(otherCallerSession.Code, Guid.NewGuid(), "source-2", IPAddress.Parse("198.51.100.20"));
        Assert.Equal(HttpStatusCode.OK, otherCaller.StatusCode);
    }

    [Fact]
    public async Task ExchangeRejectsAnAgentIdThatIsAlreadyPairedWhenTheSessionDoesNotRebindIt()
    {
        // agent_id is shown in the tray's Connections list, so it is not a secret. A code issued for a
        // brand new Source must not let anyone claim an unrelated, already-paired Source's identity.
        var (existingAgentId, existingCertificate, _) = IssuePairedIdentity();
        using var _cert = existingCertificate;

        var session = _sessions.Create();
        var response = await ExchangeAsync(session.Code, existingAgentId, "attacker-claimed-name");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ARebindingSessionRejectsAnyAgentIdOtherThanTheOneItIsBoundTo()
    {
        var (existingAgentId, existingCertificate, _) = IssuePairedIdentity();
        using var _cert = existingCertificate;

        var session = await CreateSessionAsync(existingAgentId);
        var response = await ExchangeAsync(session, Guid.NewGuid(), "source-1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ARebindingSessionSucceedsForTheAgentIdItIsBoundTo()
    {
        var (existingAgentId, existingCertificate, _) = IssuePairedIdentity();
        using var _cert = existingCertificate;

        var session = await CreateSessionAsync(existingAgentId);
        var response = await ExchangeAsync(session, existingAgentId, "source-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SessionCreationEchoesTheRebindAgentIdItWasBoundTo()
    {
        var existingAgentId = Guid.NewGuid();
        using var handler = _server.CreateHandler(ctx => ctx.Connection.RemoteIpAddress = IPAddress.Loopback);
        using var client = new HttpClient(handler) { BaseAddress = _server.BaseAddress };

        using var response = await client.PostAsJsonAsync("/api/v1/pairing/sessions", new { rebind_agent_id = existingAgentId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(existingAgentId, payload.GetProperty("rebind_agent_id").GetGuid());
    }

    [Fact]
    public async Task RemoteCallersWithoutPairingCannotReachGeneralControlApi()
    {
        // No client certificate, no bearer token, no loopback: the general control API must stay closed
        // even though Kestrel's ClientCertificateMode is now AllowCertificate rather than RequireCertificate.
        var context = await PostAsync("/api/v1/service/shutdown", IPAddress.Parse("203.0.113.10"));
        Assert.True(context.Response.StatusCode is 401 or 403);
    }

    [Fact]
    public async Task PairingExchangeIsTheOnlyControlApiCallAllowedWithoutAClientCertificate()
    {
        // A remote caller presenting a valid bearer token and agent header but no client certificate must
        // still be rejected by the general control API filter, unlike /pairing/exchange which needs none.
        var context = await _server.SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Post;
            ctx.Request.Path = "/api/v1/service/shutdown";
            ctx.Request.Headers["X-BackupMesh-Agent-ID"] = Guid.NewGuid().ToString();
            ctx.Request.Headers.Authorization = "Bearer not-a-real-token-not-a-real-token";
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
            ctx.Connection.ClientCertificate = null;
        });
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task RevokedSourceCannotReachTheGeneralControlApiEvenWithAValidCertificateAndToken()
    {
        var (agentId, certificate, token) = IssuePairedIdentity();
        using var _ = certificate;
        _revocations.Revoke(agentId);

        var context = await SendAsPairedAgentAsync(agentId, certificate, token);

        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnrevokingASourceRestoresGeneralControlApiAccess()
    {
        var (agentId, certificate, token) = IssuePairedIdentity();
        using var _ = certificate;
        _revocations.Revoke(agentId);
        _revocations.Unrevoke(agentId);

        var context = await SendAsPairedAgentAsync(agentId, certificate, token);

        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task NonRevokedSourceReachesTheGeneralControlApiNormally()
    {
        var (agentId, certificate, token) = IssuePairedIdentity();
        using var _ = certificate;

        var context = await SendAsPairedAgentAsync(agentId, certificate, token);

        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task ListSourcesIsRejectedFromNonLoopbackCallers()
    {
        // Rejected either by ControlApiAuthenticationFilter (no agent identity header: 401) or by this
        // endpoint's own loopback check if it gets that far (403); either way it must not succeed.
        var context = await PostOrGetAsync("GET", "/api/v1/sources", IPAddress.Parse("203.0.113.10"));
        Assert.True(context.Response.StatusCode is 401 or 403);
    }

    [Fact]
    public async Task ListSourcesFromLoopbackReportsRevocationStatus()
    {
        var agentId = Guid.NewGuid();
        _catalogs.Upsert(new SourceCatalog(agentId, "source-1", DateTimeOffset.UtcNow, []));
        _revocations.Revoke(agentId);

        var context = await PostOrGetAsync("GET", "/api/v1/sources", IPAddress.Loopback);
        var payload = await ReadJsonAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        var entry = payload.EnumerateArray().Single(item => item.GetProperty("agent_id").GetGuid() == agentId);
        Assert.True(entry.GetProperty("revoked").GetBoolean());
    }

    [Fact]
    public async Task ListSourcesReportsTheRenamedDisplayNameAndTheOriginalReportedName()
    {
        var agentId = Guid.NewGuid();
        _catalogs.Upsert(new SourceCatalog(agentId, "reported-name", DateTimeOffset.UtcNow, []));
        _displayNames.Set(agentId, "My renamed source");

        var context = await PostOrGetAsync("GET", "/api/v1/sources", IPAddress.Loopback);
        var payload = await ReadJsonAsync(context);
        var entry = payload.EnumerateArray().Single(item => item.GetProperty("agent_id").GetGuid() == agentId);

        Assert.Equal("My renamed source", entry.GetProperty("agent_name").GetString());
        Assert.Equal("reported-name", entry.GetProperty("reported_agent_name").GetString());
    }

    [Fact]
    public async Task ListSourcesReportsTheCertificateExpiryRecordedAtExchange()
    {
        var session = _sessions.Create();
        var agentId = Guid.NewGuid();
        var exchange = await ExchangeAsync(session.Code, agentId, "source-1");
        exchange.EnsureSuccessStatusCode();
        var exchangePayload = await exchange.Content.ReadFromJsonAsync<JsonElement>();
        _catalogs.Upsert(new SourceCatalog(agentId, "source-1", DateTimeOffset.UtcNow, []));

        var context = await PostOrGetAsync("GET", "/api/v1/sources", IPAddress.Loopback);
        var payload = await ReadJsonAsync(context);
        var entry = payload.EnumerateArray().Single(item => item.GetProperty("agent_id").GetGuid() == agentId);

        Assert.Equal(exchangePayload.GetProperty("expires_at").GetDateTimeOffset(), entry.GetProperty("certificate_expires_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task RenamingASourceIsLoopbackOnly()
    {
        var agentId = Guid.NewGuid();
        var remote = await PutJsonAsync($"/api/v1/sources/{agentId}/name", new { display_name = "New name" }, IPAddress.Parse("203.0.113.10"));
        Assert.True(remote.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.Null(_displayNames.Get(agentId));

        var loopback = await PutJsonAsync($"/api/v1/sources/{agentId}/name", new { display_name = "New name" }, IPAddress.Loopback);
        Assert.Equal(HttpStatusCode.OK, loopback.StatusCode);
        Assert.Equal("New name", _displayNames.Get(agentId));
    }

    [Fact]
    public async Task ForgettingASourceRevokesItAndRemovesItsCatalogButNotAnythingElse()
    {
        var agentId = Guid.NewGuid();
        _catalogs.Upsert(new SourceCatalog(agentId, "source-1", DateTimeOffset.UtcNow, []));

        var context = await PostOrGetAsync("POST", $"/api/v1/sources/{agentId}/forget", IPAddress.Loopback);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(_revocations.IsRevoked(agentId));
        Assert.DoesNotContain(_catalogs.List(), catalog => catalog.SourceAgentId == agentId);
    }

    [Fact]
    public async Task ForgetEndpointIsLoopbackOnly()
    {
        var agentId = Guid.NewGuid();
        _catalogs.Upsert(new SourceCatalog(agentId, "source-1", DateTimeOffset.UtcNow, []));

        var remote = await PostOrGetAsync("POST", $"/api/v1/sources/{agentId}/forget", IPAddress.Parse("203.0.113.10"));

        Assert.True(remote.Response.StatusCode is 401 or 403);
        Assert.Contains(_catalogs.List(), catalog => catalog.SourceAgentId == agentId);
    }

    [Fact]
    public async Task RotateAuthorityEndpointIsLoopbackOnly()
    {
        var remote = await PostOrGetAsync("POST", "/api/v1/pairing/rotate-authority", IPAddress.Parse("203.0.113.10"));
        Assert.Equal(403, remote.Response.StatusCode);
    }

    [Fact]
    public async Task RotatingTheAuthorityMakesPreviouslyIssuedClientCertificatesFailChainValidation()
    {
        var (agentId, certificate, token) = IssuePairedIdentity();
        using var _cert = certificate;

        var rotate = await PostOrGetAsync("POST", "/api/v1/pairing/rotate-authority", IPAddress.Loopback);
        Assert.Equal(200, rotate.Response.StatusCode);

        using var newAuthority = _certificateAuthority.GetAuthorityCertificate();
        Assert.False(MutualTlsCertificateValidator.Validate(certificate, newAuthority));
    }

    [Fact]
    public async Task CertificateRenewalIssuesAFreshCertificateForTheAuthenticatedAgent()
    {
        var (agentId, certificate, token) = IssuePairedIdentity();
        using var _cert = certificate;

        var context = await _server.SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Post;
            ctx.Request.Path = "/api/v1/certificate/renew";
            ctx.Request.Headers["X-BackupMesh-Agent-ID"] = agentId.ToString();
            ctx.Request.Headers.Authorization = $"Bearer {token}";
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
            ctx.Connection.ClientCertificate = certificate;
        });

        Assert.Equal(200, context.Response.StatusCode);
        var payload = await ReadJsonAsync(context);
        using var renewed = X509Certificate2.CreateFromPem(payload.GetProperty("certificate_pem").GetString());
        Assert.NotEqual(certificate.Thumbprint, renewed.Thumbprint);
        Assert.True(MutualTlsCertificateValidator.Validate(renewed, _certificateAuthority.GetAuthorityCertificate()));
    }

    [Fact]
    public async Task RevokeAndUnrevokeEndpointsAreLoopbackOnly()
    {
        var agentId = Guid.NewGuid();
        var revoke = await PostOrGetAsync("POST", $"/api/v1/sources/{agentId}/revoke", IPAddress.Parse("203.0.113.10"));
        Assert.True(revoke.Response.StatusCode is 401 or 403);
        Assert.False(_revocations.IsRevoked(agentId));

        var revokeFromLoopback = await PostOrGetAsync("POST", $"/api/v1/sources/{agentId}/revoke", IPAddress.Loopback);
        Assert.Equal(200, revokeFromLoopback.Response.StatusCode);
        Assert.True(_revocations.IsRevoked(agentId));
    }

    private (Guid AgentId, X509Certificate2 Certificate, string Token) IssuePairedIdentity()
    {
        var agentId = Guid.NewGuid();
        var bundle = _certificateAuthority.Issue(agentId);
        var certificate = X509Certificate2.CreateFromPem(bundle.CertificatePem);
        var token = _credentials.Issue(agentId);
        return (agentId, certificate, token);
    }

    private async Task<HttpContext> SendAsPairedAgentAsync(Guid agentId, X509Certificate2 certificate, string token) => await _server.SendAsync(ctx =>
    {
        ctx.Request.Method = HttpMethods.Get;
        ctx.Request.Path = "/api/v1/storage/status";
        ctx.Request.Headers["X-BackupMesh-Agent-ID"] = agentId.ToString();
        ctx.Request.Headers.Authorization = $"Bearer {token}";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        ctx.Connection.ClientCertificate = certificate;
    });

    private async Task<HttpContext> PostOrGetAsync(string method, string path, IPAddress remoteAddress) => await _server.SendAsync(ctx =>
    {
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = remoteAddress;
    });

    private async Task<HttpResponseMessage> PutJsonAsync(string path, object body, IPAddress remoteAddress)
    {
        using var handler = _server.CreateHandler(ctx => ctx.Connection.RemoteIpAddress = remoteAddress);
        using var client = new HttpClient(handler) { BaseAddress = _server.BaseAddress };
        return await client.PutAsJsonAsync(path, body);
    }

    private async Task<string> CreateSessionAsync(Guid rebindAgentId)
    {
        using var handler = _server.CreateHandler(ctx => ctx.Connection.RemoteIpAddress = IPAddress.Loopback);
        using var client = new HttpClient(handler) { BaseAddress = _server.BaseAddress };
        using var response = await client.PostAsJsonAsync("/api/v1/pairing/sessions", new { rebind_agent_id = rebindAgentId });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("code").GetString()!;
    }

    private Task<HttpResponseMessage> ExchangeAsync(string code, Guid agentId, string agentName) => ExchangeAsync(code, agentId, agentName, IPAddress.Parse("203.0.113.10"));

    private async Task<HttpResponseMessage> ExchangeAsync(string code, Guid agentId, string agentName, IPAddress remoteAddress)
    {
        using var handler = _server.CreateHandler(ctx => ctx.Connection.RemoteIpAddress = remoteAddress);
        using var client = new HttpClient(handler) { BaseAddress = _server.BaseAddress };
        return await client.PostAsJsonAsync("/api/v1/pairing/exchange", new { code, agent_id = agentId, agent_name = agentName });
    }

    private async Task<HttpContext> PostAsync(string path, IPAddress remoteAddress) => await _server.SendAsync(ctx =>
    {
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = remoteAddress;
    });

    private static async Task<JsonElement> ReadJsonAsync(HttpContext context)
    {
        // TestServer.SendAsync always wraps the response body in a forward-only reader stream,
        // even though we set ctx.Response.Body ourselves in PostAsync, so this must not seek.
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    // Unused-route dependencies: never exercised by these pairing tests, only needed so MapControlApi()
    // can build request-delegate metadata for the routes this fixture does not call.
    private sealed class StubVolumeInventory : IStorageVolumeInventory { public IReadOnlyList<StorageVolumeInfo> GetVolumes() => []; }
    private sealed class StubDeviceEjector : IStorageDeviceEjector { public StorageEjectResult Eject(StorageVolumeInfo volume) => throw new NotSupportedException(); }
    private sealed class StubRepositoryEndpoints : IRepositoryEndpointProvider { public Task<Uri> GetEndpointAsync(ResolvedBackupTarget target, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class StubRepositorySessions : IRepositorySessionController { public Task StopDeviceAsync(Guid deviceId, CancellationToken cancellationToken) => throw new NotSupportedException(); }
}
