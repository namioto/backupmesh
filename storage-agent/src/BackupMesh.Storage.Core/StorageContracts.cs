namespace BackupMesh.Storage.Core;

public sealed record StorageIdentity(string StableId, string RootPath, string? Label);

public sealed record StorageReadiness(bool IsReady, string? Reason = null);

public interface IStorageDiscovery
{
    Task<StorageIdentity?> DiscoverAsync(CancellationToken cancellationToken);
}

public interface IStorageIdentityVerifier
{
    Task<StorageReadiness> VerifyAsync(StorageIdentity identity, CancellationToken cancellationToken);
}

public sealed class StorageOptions
{
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}

public sealed class RestServerOptions
{
    public string ExecutablePath { get; set; } = "rest-server";
    public string RepositoryPath { get; set; } = string.Empty;
    public string ListenAddress { get; set; } = "127.0.0.1:8000";
    public string? PasswordFile { get; set; }
}
