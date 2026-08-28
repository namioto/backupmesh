using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;

namespace BackupMesh.Storage.Service;

public sealed class PairingCertificateOptions { public string? ProtectedAuthorityPath { get; set; } public string? ProtectedServerCertificatePath { get; set; } }
public sealed record SourceCertificateBundle(string CertificatePem, string PrivateKeyPem, string AuthorityPem, DateTimeOffset ExpiresAt);

public sealed class PairingCertificateAuthority(PairingCertificateOptions options)
{
    private readonly object _gate = new();
    private readonly string _path = ResolvePath(options.ProtectedAuthorityPath);
    private X509Certificate2? _authority;

    public SourceCertificateBundle Issue(Guid sourceAgentId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pairing certificate issuance requires Windows DPAPI.");
        lock (_gate)
        {
            var authority = _authority ??= LoadOrCreateAuthority();
            using var key = RSA.Create(3072);
            var request = new CertificateRequest($"CN={sourceAgentId:D}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var expires = DateTimeOffset.UtcNow.AddYears(1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), expires);
            return new(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), authority.ExportCertificatePem(), expires);
        }
    }

    public X509Certificate2 GetAuthorityCertificate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pairing certificate issuance requires Windows DPAPI.");
        lock (_gate) return X509CertificateLoader.LoadCertificate((_authority ??= LoadOrCreateAuthority()).RawData);
    }

    public X509Certificate2 IssueServerCertificate(IEnumerable<string>? configuredNames = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pairing certificate issuance requires Windows DPAPI.");
        lock (_gate)
        {
            var serverPath = !string.IsNullOrWhiteSpace(options.ProtectedServerCertificatePath)
                ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.ProtectedServerCertificatePath))
                : _path + ".server.dpapi";
            if (File.Exists(serverPath))
            {
                var protectedPfx = File.ReadAllBytes(serverPath);
                var storedPfx = ProtectedData.Unprotect(protectedPfx, null, DataProtectionScope.CurrentUser);
                return X509CertificateLoader.LoadPkcs12(storedPfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", Environment.MachineName };
            if (configuredNames is not null)
                foreach (var name in configuredNames.Where(value => !string.IsNullOrWhiteSpace(value))) names.Add(name.Trim());
            using var key = RSA.Create(3072);
            var request = new CertificateRequest($"CN={names.First()}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
            foreach (var name in names)
                if (System.Net.IPAddress.TryParse(name, out var address)) san.AddIpAddress(address); else san.AddDnsName(name);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
            var pfx = certificate.Export(X509ContentType.Pfx);
            var protectedBytes = ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(serverPath) ?? throw new InvalidOperationException("Server certificate path must include a directory."));
            var temporary = $"{serverPath}.{Guid.NewGuid():N}.tmp";
            try { File.WriteAllBytes(temporary, protectedBytes); File.Move(temporary, serverPath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
    }

    [SupportedOSPlatform("windows")]
    private X509Certificate2 LoadOrCreateAuthority()
    {
        if (File.Exists(_path))
        {
            var storedBytes = File.ReadAllBytes(_path);
            var loadedPfx = ProtectedData.Unprotect(storedBytes, null, DataProtectionScope.CurrentUser);
            return X509CertificateLoader.LoadPkcs12(loadedPfx, null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        using var key = RSA.Create(4096);
        var request = new CertificateRequest("CN=BackupMesh Source Pairing CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        var pfx = created.Export(X509ContentType.Pfx);
        var protectedBytes = ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Pairing authority path must include a directory."));
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try { File.WriteAllBytes(temporary, protectedBytes); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }

    private static string ResolvePath(string? path) => !string.IsNullOrWhiteSpace(path)
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "pairing-authority.dpapi");

    private static byte[] CreatePositiveSerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        if (serial.All(value => value == 0)) serial[^1] = 1;
        return serial;
    }
}
