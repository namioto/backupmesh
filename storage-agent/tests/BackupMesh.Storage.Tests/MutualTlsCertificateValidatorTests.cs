using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class MutualTlsCertificateValidatorTests
{
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
}
