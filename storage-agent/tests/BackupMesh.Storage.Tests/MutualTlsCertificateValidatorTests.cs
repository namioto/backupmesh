using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class MutualTlsCertificateValidatorTests
{
    [Fact]
    public async Task IssuedCertificatesCompleteARealMutualTlsHandshake()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-handshake-test-{Guid.NewGuid():N}");
        try
        {
            var issuer = new PairingCertificateAuthority(new() { ProtectedAuthorityPath = Path.Combine(directory, "authority.dpapi") });
            using var authority = issuer.GetAuthorityCertificate();
            using var serverCertificate = issuer.IssueServerCertificate(["localhost"]);
            var bundle = issuer.Issue(Guid.NewGuid());
            using var ephemeralClientCertificate = X509Certificate2.CreateFromPem(bundle.CertificatePem, bundle.PrivateKeyPem);
            using var clientCertificate = X509CertificateLoader.LoadPkcs12(ephemeralClientCertificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync();
                using var stream = new SslStream(connection.GetStream(), false, (_, certificate, _, _) => ValidateClient(certificate, authority));
                var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
                policy.CustomTrustStore.Add(authority);
                policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = serverCertificate, ClientCertificateRequired = true, CertificateChainPolicy = policy, EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 }, timeout.Token);
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var clientStream = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate?.GetCertHashString() == serverCertificate.GetCertHashString());
            try
            {
                await clientStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", ClientCertificates = [clientCertificate], EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 }, timeout.Token);
            }
            catch
            {
                await server;
                throw;
            }
            await server;
            Assert.True(clientStream.IsAuthenticated && clientStream.IsMutuallyAuthenticated);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void PairingAuthorityPersistsProtectedKeyAndIssuesBoundClientCertificates()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-ca-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "authority.dpapi");
        try
        {
            var agentId = Guid.NewGuid();
            var first = new PairingCertificateAuthority(new() { ProtectedAuthorityPath = path }).Issue(agentId);
            using var certificate = X509Certificate2.CreateFromPem(first.CertificatePem);
            using var authority = X509Certificate2.CreateFromPem(first.AuthorityPem);
            Assert.Equal(agentId.ToString("D"), certificate.GetNameInfo(X509NameType.SimpleName, false));
            Assert.Equal(authority.Subject, certificate.Issuer);
            Assert.True(MutualTlsCertificateValidator.Validate(certificate, authority));
            Assert.DoesNotContain("PRIVATE KEY", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);

            var second = new PairingCertificateAuthority(new() { ProtectedAuthorityPath = path }).Issue(Guid.NewGuid());
            using var reloadedAuthority = X509Certificate2.CreateFromPem(second.AuthorityPem);
            Assert.Equal(authority.Thumbprint, reloadedAuthority.Thumbprint);

            var serverOptions = new PairingCertificateOptions { ProtectedAuthorityPath = path, ProtectedServerCertificatePath = Path.Combine(directory, "server.dpapi") };
            using var server = new PairingCertificateAuthority(serverOptions).IssueServerCertificate(["storage.example"]);
            Assert.True(server.HasPrivateKey);
            Assert.Contains(server.Extensions.OfType<X509Extension>(), extension => extension.Oid?.Value == "2.5.29.17" && extension.Format(false).Contains("storage.example", StringComparison.OrdinalIgnoreCase));
            using var reloadedServer = new PairingCertificateAuthority(serverOptions).IssueServerCertificate(["storage.example"]);
            Assert.Equal(server.Thumbprint, reloadedServer.Thumbprint);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    // Program.cs switched Kestrel from ClientCertificateMode.RequireCertificate to AllowCertificate so
    // /pairing/exchange can complete without a client certificate, using
    // `certificate is null || MutualTlsCertificateValidator.Validate(certificate, clientAuthority)` as the
    // validation callback. These three tests pin that exact behavior at the real TLS handshake level (not
    // just the application-level ControlApiAuthenticationFilter) so a future change cannot silently widen
    // it into accepting untrusted certificates.
    [Fact]
    public async Task AllowCertificateModeAcceptsAConnectionWithNoClientCertificate()
    {
        using var authority = CreateAuthority("CN=BackupMesh Test CA");
        Assert.True(await RunOptionalClientCertificateHandshakeAsync(authority, clientCertificate: null));
    }

    [Fact]
    public async Task AllowCertificateModeStillRejectsAnUntrustedClientCertificate()
    {
        using var authority = CreateAuthority("CN=BackupMesh Test CA");
        using var otherAuthority = CreateAuthority("CN=Other Test CA");
        using var untrustedClient = IssueWithPrivateKey(otherAuthority, "CN=source-untrusted", "1.3.6.1.5.5.7.3.2");
        await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() => RunOptionalClientCertificateHandshakeAsync(authority, untrustedClient));
    }

    [Fact]
    public async Task AllowCertificateModeAcceptsAValidClientCertificate()
    {
        using var authority = CreateAuthority("CN=BackupMesh Test CA");
        using var trustedClient = IssueWithPrivateKey(authority, "CN=source-1", "1.3.6.1.5.5.7.3.2");
        Assert.True(await RunOptionalClientCertificateHandshakeAsync(authority, trustedClient));
    }

    // Unlike Issue() below, this attaches and reloads the private key so the certificate can actually
    // authenticate a TLS client (Issue() only needs to be chain-buildable for MutualTlsCertificateValidator.Validate).
    private static X509Certificate2 IssueWithPrivateKey(X509Certificate2 authority, string subject, string eku)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(eku) }, true));
        using var withoutKey = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30), RandomNumberGenerator.GetBytes(16));
        using var withKey = withoutKey.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private static async Task<bool> RunOptionalClientCertificateHandshakeAsync(X509Certificate2 authority, X509Certificate2? clientCertificate)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=storage.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeralServerCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // SChannel needs the private key reloaded from a PFX rather than the ephemeral in-memory key CreateSelfSigned returns.
        using var serverCertificate = X509CertificateLoader.LoadPkcs12(ephemeralServerCertificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync();
            using var stream = new SslStream(connection.GetStream(), false, (_, certificate, _, _) => certificate is null || ValidateClient(certificate, authority));
            // Mirrors Kestrel's mapping of ClientCertificateMode.AllowCertificate: ClientCertificateRequired stays
            // true so the certificate is actually requested (needed for it to reach the callback at all under
            // TLS 1.3), while the callback itself tolerates a missing certificate for pairing bootstrap.
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = serverCertificate, ClientCertificateRequired = true, EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 }, timeout.Token);
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var clientStream = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate?.GetCertHashString() == serverCertificate.GetCertHashString());
        var clientOptions = new SslClientAuthenticationOptions { TargetHost = "storage.example", EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 };
        if (clientCertificate is not null) clientOptions.ClientCertificates = [clientCertificate];
        try
        {
            await clientStream.AuthenticateAsClientAsync(clientOptions, timeout.Token);
        }
        catch
        {
            try { await server; } catch { /* surface the client-side exception instead */ }
            throw;
        }
        await server;
        return clientStream.IsAuthenticated;
    }

    [Fact]
    public void AcceptsOnlyClientAuthCertificateFromConfiguredAuthority()
    {
        using var authority = CreateAuthority("CN=BackupMesh Test CA");
        using var otherAuthority = CreateAuthority("CN=Other Test CA");
        using var validClient = Issue(authority, "CN=source-1", "1.3.6.1.5.5.7.3.2");
        using var wrongAuthorityClient = Issue(otherAuthority, "CN=source-2", "1.3.6.1.5.5.7.3.2");
        using var serverOnly = Issue(authority, "CN=not-a-client", "1.3.6.1.5.5.7.3.1");

        Assert.True(MutualTlsCertificateValidator.Validate(validClient, authority));
        Assert.False(MutualTlsCertificateValidator.Validate(wrongAuthorityClient, authority));
        Assert.False(MutualTlsCertificateValidator.Validate(serverOnly, authority));
    }

    private static X509Certificate2 CreateAuthority(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }

    private static X509Certificate2 Issue(X509Certificate2 authority, string subject, string eku)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(eku) }, true));
        var serial = RandomNumberGenerator.GetBytes(16);
        return request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30), serial);
    }

    private static bool ValidateChain(X509Certificate? certificate, X509Certificate2 authority)
    {
        if (certificate is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var candidate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return chain.Build(candidate);
    }

    private static bool ValidateClient(X509Certificate? certificate, X509Certificate2 authority)
    {
        if (certificate is null) return false;
        using var candidate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return MutualTlsCertificateValidator.Validate(candidate, authority);
    }
}
