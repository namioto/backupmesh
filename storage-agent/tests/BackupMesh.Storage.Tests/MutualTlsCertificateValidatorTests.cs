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
                var context = SslStreamCertificateContext.Create(serverCertificate, new X509Certificate2Collection(authority));
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = context, ClientCertificateRequired = true, EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 }, timeout.Token);
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var clientStream = new SslStream(client.GetStream(), false, (_, certificate, _, _) => ValidateChain(certificate, authority));
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
            Assert.True(MutualTlsCertificateValidator.Validate(certificate, authority));
            Assert.DoesNotContain("PRIVATE KEY", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);

            var second = new PairingCertificateAuthority(new() { ProtectedAuthorityPath = path }).Issue(Guid.NewGuid());
            using var reloadedAuthority = X509Certificate2.CreateFromPem(second.AuthorityPem);
            Assert.Equal(authority.Thumbprint, reloadedAuthority.Thumbprint);

            using var server = new PairingCertificateAuthority(new() { ProtectedAuthorityPath = path }).IssueServerCertificate(["storage.example"]);
            Assert.True(server.HasPrivateKey);
            Assert.Contains(server.Extensions.OfType<X509Extension>(), extension => extension.Oid?.Value == "2.5.29.17" && extension.Format(false).Contains("storage.example", StringComparison.OrdinalIgnoreCase));
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(authority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.True(chain.Build(server));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
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
