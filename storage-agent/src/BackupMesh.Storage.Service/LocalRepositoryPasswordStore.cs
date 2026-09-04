using System.Security.Cryptography;

namespace BackupMesh.Storage.Service;

public sealed class LocalBackupOptions
{
    public string ResticExecutablePath { get; set; } = "restic.exe";
    public string? PasswordDirectory { get; set; }
    public string? CacheDirectory { get; set; }
}

// Repository passwords for Backup Sets defined directly on this PC (see LocalSourceIdentity) - restic
// itself has no concept of DPAPI, so unlike a real Source Agent's protected password file, this store
// decrypts to a plaintext temporary file only for the duration of a single restic invocation and
// deletes it immediately after, the same lifecycle main.go's resolveRepositoryPasswordFile gives a
// paired Source's protected password.
public sealed class LocalRepositoryPasswordStore(LocalBackupOptions? options = null)
{
    private readonly object _gate = new();
    private readonly string _directory = ResolveDirectory(options?.PasswordDirectory);

    public IDisposable GetOrCreatePlaintextPasswordFile(Guid mappingId, out string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local backup passwords require Windows DPAPI.");
        lock (_gate)
        {
            var protectedPath = Path.Combine(_directory, $"{mappingId:N}.password.dpapi");
            byte[] plaintext;
            if (File.Exists(protectedPath))
            {
                plaintext = ProtectedData.Unprotect(File.ReadAllBytes(protectedPath), null, DataProtectionScope.LocalMachine);
            }
            else
            {
                plaintext = RandomNumberGenerator.GetBytes(32);
                var encoded = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
                Directory.CreateDirectory(_directory);
                var protectedBytes = ProtectedData.Protect(encoded, null, DataProtectionScope.LocalMachine);
                var temporary = $"{protectedPath}.{Guid.NewGuid():N}.tmp";
                File.WriteAllBytes(temporary, protectedBytes);
                File.Move(temporary, protectedPath, true);
                plaintext = encoded;
            }
            var temporaryPasswordFile = Path.Combine(Path.GetTempPath(), $"backupmesh-local-repo-password-{Guid.NewGuid():N}");
            File.WriteAllBytes(temporaryPasswordFile, plaintext);
            path = temporaryPasswordFile;
            return new TemporaryFile(temporaryPasswordFile);
        }
    }

    private static string ResolveDirectory(string? configured) => !string.IsNullOrWhiteSpace(configured)
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "local-repository-passwords");

    private sealed class TemporaryFile(string path) : IDisposable
    {
        public void Dispose() { try { File.Delete(path); } catch (IOException) { } }
    }
}
