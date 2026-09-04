using System.Security.Cryptography.X509Certificates;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class PairingCertificateAuthorityTests
{
    [Fact]
    public void RotateAuthorityDeletesTheAuthorityAndServerCertificateFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-ca-rotate-{Guid.NewGuid():N}");
        var authorityPath = Path.Combine(directory, "authority.dpapi");
        try
        {
            var authority = new PairingCertificateAuthority(new PairingCertificateOptions { ProtectedAuthorityPath = authorityPath });
            using (authority.GetAuthorityCertificate()) { }
            using (authority.IssueServerCertificate(["test-storage"])) { }
            var serverPath = authorityPath + ".server.dpapi";
            Assert.True(File.Exists(authorityPath));
            Assert.True(File.Exists(serverPath));

            authority.RotateAuthority();

            Assert.False(File.Exists(authorityPath));
            Assert.False(File.Exists(serverPath));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ANewAuthorityIsGeneratedAfterRotation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-ca-rotate-{Guid.NewGuid():N}");
        var authorityPath = Path.Combine(directory, "authority.dpapi");
        try
        {
            var authority = new PairingCertificateAuthority(new PairingCertificateOptions { ProtectedAuthorityPath = authorityPath });
            using var before = authority.GetAuthorityCertificate();

            authority.RotateAuthority();
            using var after = authority.GetAuthorityCertificate();

            Assert.NotEqual(before.Thumbprint, after.Thumbprint);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// After rotation, every previously issued Source client certificate was signed by a CA that no
    /// longer exists anywhere - not just replaced in memory, deleted from disk - so it must fail chain
    /// validation against the new one exactly like Kestrel's own client-certificate policy would.
    /// </summary>
    [Fact]
    public void AClientCertificateIssuedBeforeRotationDoesNotChainToTheNewAuthority()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-ca-rotate-{Guid.NewGuid():N}");
        var authorityPath = Path.Combine(directory, "authority.dpapi");
        try
        {
            var authority = new PairingCertificateAuthority(new PairingCertificateOptions { ProtectedAuthorityPath = authorityPath });
            var bundle = authority.Issue(Guid.NewGuid());

            authority.RotateAuthority();
            using var newAuthority = authority.GetAuthorityCertificate();
            using var issuedCertificate = X509Certificate2.CreateFromPem(bundle.CertificatePem);

            Assert.False(MutualTlsCertificateValidator.Validate(issuedCertificate, newAuthority));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
