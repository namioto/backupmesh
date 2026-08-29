using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class LocalRepositoryPasswordStoreTests
{
    [Fact]
    public void TheSamePasswordFileContentsAreReturnedOnEachCall()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-local-repo-password-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalRepositoryPasswordStore(new LocalBackupOptions { PasswordDirectory = directory });
            var mappingId = Guid.NewGuid();

            string firstPath, secondPath;
            byte[] first, second;
            using (store.GetOrCreatePlaintextPasswordFile(mappingId, out firstPath)) { first = File.ReadAllBytes(firstPath); }
            using (store.GetOrCreatePlaintextPasswordFile(mappingId, out secondPath)) { second = File.ReadAllBytes(secondPath); }

            Assert.Equal(first, second);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void DifferentMappingsGetDifferentPasswords()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-local-repo-password-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalRepositoryPasswordStore(new LocalBackupOptions { PasswordDirectory = directory });

            using var a = store.GetOrCreatePlaintextPasswordFile(Guid.NewGuid(), out var pathA);
            using var b = store.GetOrCreatePlaintextPasswordFile(Guid.NewGuid(), out var pathB);

            Assert.NotEqual(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ThePlaintextFileIsDeletedWhenDisposed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-local-repo-password-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalRepositoryPasswordStore(new LocalBackupOptions { PasswordDirectory = directory });
            var handle = store.GetOrCreatePlaintextPasswordFile(Guid.NewGuid(), out var path);

            Assert.True(File.Exists(path));
            handle.Dispose();

            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void PasswordSurvivesAcrossStoreInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-local-repo-password-{Guid.NewGuid():N}");
        var mappingId = Guid.NewGuid();
        try
        {
            var options = new LocalBackupOptions { PasswordDirectory = directory };
            byte[] first;
            using (new LocalRepositoryPasswordStore(options).GetOrCreatePlaintextPasswordFile(mappingId, out var path)) { first = File.ReadAllBytes(path); }

            byte[] second;
            using (new LocalRepositoryPasswordStore(options).GetOrCreatePlaintextPasswordFile(mappingId, out var path)) { second = File.ReadAllBytes(path); }

            Assert.Equal(first, second);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
