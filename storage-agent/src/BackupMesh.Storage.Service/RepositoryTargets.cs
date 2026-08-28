using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Service;

public sealed record BackupTargetAvailability(Guid MappingId, Guid DeviceId, Guid BackupSetId, string DeviceName, string DestinationFolder, string State, string? Reason);
public sealed record ResolvedBackupTarget(Guid MappingId, Guid DeviceId, Guid BackupSetId, Guid SourceAgentId, string DeviceName, string DeviceRoot, string RepositoryPath, string DestinationFolder);
public sealed record TargetResolution(ResolvedBackupTarget? Target, string? ErrorCode = null, string? Message = null);

public sealed class BackupTargetResolver(StorageConfigurationStore configuration, StoragePresenceStore presence)
{
    public IReadOnlyList<BackupTargetAvailability> List(Guid sourceAgentId, Guid backupSetId)
    {
        var topology = configuration.Get().Configuration;
        var backupSet = topology.BackupSets.FirstOrDefault(set => set.Id == backupSetId && set.SourceAgentId == sourceAgentId);
        if (backupSet is null) return [];
        var presenceByDevice = presence.List().ToDictionary(item => item.DeviceId);
        return topology.Mappings.Where(mapping => mapping.Enabled && mapping.BackupSetId == backupSetId)
            .Select(mapping =>
            {
                var device = topology.Devices.Single(item => item.Id == mapping.DeviceId);
                presenceByDevice.TryGetValue(device.Id, out var status);
                var state = status?.Ready == true ? "READY" : status?.Connected == true ? "WAITING" : "OFFLINE";
                return new BackupTargetAvailability(mapping.Id, device.Id, backupSetId, device.DisplayName,
                    Destination(status?.CurrentRoot ?? device.LastKnownRoot, mapping.RepositoryPath), state, status?.Reason);
            }).ToArray();
    }

    public TargetResolution Resolve(BackupRequest request)
    {
        var topology = configuration.Get().Configuration;
        var backupSet = topology.BackupSets.FirstOrDefault(set => set.Id == request.BackupSetId);
        if (backupSet is null || backupSet.SourceAgentId != request.SourceAgentId)
            return new(null, "TARGET_NOT_FOUND", "The Backup Set does not belong to this Source Agent.");
        var mapping = topology.Mappings.FirstOrDefault(item => item.Id == request.TargetMappingId && item.BackupSetId == request.BackupSetId && item.Enabled);
        if (mapping is null) return new(null, "TARGET_NOT_FOUND", "The enabled target mapping was not found.");
        var device = topology.Devices.FirstOrDefault(item => item.Id == mapping.DeviceId);
        var status = presence.List().FirstOrDefault(item => item.DeviceId == mapping.DeviceId);
        if (device is null || status is null || !status.Ready || string.IsNullOrWhiteSpace(status.CurrentRoot))
            return new(null, "TARGET_NOT_READY", status?.Reason ?? "The mapped device is not ready.");
        var destination = Destination(status.CurrentRoot, mapping.RepositoryPath);
        if (!IsWithinRoot(status.CurrentRoot, destination)) return new(null, "INVALID_CONFIGURATION", "The repository destination is outside the registered device.");
        return new(new(mapping.Id, device.Id, backupSet.Id, backupSet.SourceAgentId, device.DisplayName, status.CurrentRoot, mapping.RepositoryPath, destination));
    }

    public IReadOnlyList<ResolvedBackupTarget> ListReady(Guid[]? mappingIds)
    {
        var selectedMappings = mappingIds?.ToHashSet() ?? [];
        var topology = configuration.Get().Configuration;
        var presenceByDevice = presence.List().ToDictionary(item => item.DeviceId);
        var targets = new List<ResolvedBackupTarget>();
        foreach (var mapping in topology.Mappings.Where(item => item.Enabled && (selectedMappings.Count == 0 || selectedMappings.Contains(item.Id))))
        {
            var backupSet = topology.BackupSets.FirstOrDefault(item => item.Id == mapping.BackupSetId);
            var device = topology.Devices.FirstOrDefault(item => item.Id == mapping.DeviceId);
            if (backupSet is null || device is null || !presenceByDevice.TryGetValue(mapping.DeviceId, out var status) || !status.Ready || string.IsNullOrWhiteSpace(status.CurrentRoot)) continue;
            var destination = Destination(status.CurrentRoot, mapping.RepositoryPath);
            if (!IsWithinRoot(status.CurrentRoot, destination)) continue;
            targets.Add(new(mapping.Id, device.Id, backupSet.Id, backupSet.SourceAgentId, device.DisplayName, status.CurrentRoot, mapping.RepositoryPath, destination));
        }
        return targets;
    }

    private static string Destination(string? root, string repositoryPath) =>
        string.IsNullOrWhiteSpace(root) ? repositoryPath : Path.GetFullPath(Path.Combine(root, repositoryPath));

    private static bool IsWithinRoot(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RepositoryServerOptions
{
    public string ExecutablePath { get; set; } = "rest-server.exe";
    public string ListenHost { get; set; } = "0.0.0.0";
    public string PublicHost { get; set; } = string.Empty;
    public int BasePort { get; set; } = 18000;
    public bool NoAuthentication { get; set; }
    public string? CredentialDirectory { get; set; }
}

public interface IRepositoryEndpointProvider
{
    Task<Uri> GetEndpointAsync(ResolvedBackupTarget target, CancellationToken cancellationToken);
}

public sealed class RepositoryServerManager(RepositoryServerOptions options, IProcessFactory processFactory) : IRepositoryEndpointProvider, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Session> _sessions = [];

    public async Task<Uri> GetEndpointAsync(ResolvedBackupTarget target, CancellationToken cancellationToken)
    {
        await PrepareDirectoryAsync(target.DestinationFolder, cancellationToken);
        Session session;
        lock (_gate)
        {
            if (_sessions.TryGetValue(target.MappingId, out session!) && !session.Process.HasExited)
                return Endpoint(session);
            var port = options.BasePort + _sessions.Count;
            var credential = options.NoAuthentication ? null : CreateCredential(target.MappingId, options.CredentialDirectory);
            var startInfo = new ProcessStartInfo
            {
                FileName = options.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--path");
            startInfo.ArgumentList.Add(target.DestinationFolder);
            startInfo.ArgumentList.Add("--listen");
            startInfo.ArgumentList.Add($"{options.ListenHost}:{port}");
            if (options.NoAuthentication) startInfo.ArgumentList.Add("--no-auth");
            else { startInfo.ArgumentList.Add("--htpasswd-file"); startInfo.ArgumentList.Add(credential!.FilePath); }
            IManagedProcess process;
            try { process = processFactory.Start(startInfo); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                throw new IOException($"Could not start rest-server for repository '{target.DestinationFolder}': {exception.Message}", exception);
            }
            session = new(port, target.DestinationFolder, process, credential);
            _sessions[target.MappingId] = session;
        }
        await WaitUntilListeningAsync(session, cancellationToken);
        return Endpoint(session);
    }

    private static async Task PrepareDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await EnsureDirectoryAsync(path, cancellationToken);
                foreach (var child in new[] { "data", "index", "keys", "locks", "snapshots" })
                    await EnsureDirectoryAsync(Path.Combine(path, child), cancellationToken);
                for (var shard = 0; shard <= byte.MaxValue; shard++)
                    await EnsureDirectoryAsync(Path.Combine(path, "data", shard.ToString("x2")), cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = exception;
                if (attempt < 4) await Task.Delay(200, cancellationToken);
            }
        }
        throw new IOException($"Could not prepare repository directory '{path}': {failure?.Message}", failure);
    }

    private static async Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(path);
                if (Directory.Exists(path)) return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failure = exception; }
            await Task.Delay(200, cancellationToken);
        }
        throw new IOException(failure?.Message ?? $"Directory '{path}' did not become visible after creation.", failure);
    }

    private Uri Endpoint(Session session)
        => BuildEndpoint(ResolvePublicHost(options.PublicHost), session.Port, ".", session.Credential?.Username, session.Credential?.Password);

    internal static string ResolvePublicHost(string? configuredHost)
        => string.IsNullOrWhiteSpace(configuredHost) ? Environment.MachineName : configuredHost.Trim();

    internal static Uri BuildEndpoint(string publicHost, int port, string repositoryPath, string? username = null, string? password = null)
    {
        var path = repositoryPath == "." ? "/" : "/" + string.Join('/', repositoryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + "/";
        var endpoint = new UriBuilder(Uri.UriSchemeHttp, publicHost, port, path).Uri;
        var builder = new UriBuilder(endpoint) { UserName = username ?? string.Empty, Password = password ?? string.Empty };
        // restic distinguishes its REST backend from an ordinary HTTP URL with
        // the `rest:` transport prefix.
        return new Uri("rest:" + builder.Uri.AbsoluteUri);
    }

    internal static RepositoryCredential CreateCredential(Guid deviceId, string? configuredDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMesh", "credentials")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredDirectory));
        Directory.CreateDirectory(directory);
        var username = "backupmesh";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var digest = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        var path = Path.Combine(directory, $"{deviceId:N}.htpasswd");
        File.WriteAllText(path, $"{username}:{{SHA}}{digest}{Environment.NewLine}");
        return new(username, password, path);
    }

    private static async Task WaitUntilListeningAsync(Session session, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Process.HasExited) throw new InvalidOperationException("rest-server exited before it became ready.");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", session.Port, cancellationToken);
                return;
            }
            catch (SocketException) { await Task.Delay(100, cancellationToken); }
        }
        throw new TimeoutException("rest-server did not start listening within five seconds.");
    }

    public async ValueTask DisposeAsync()
    {
        Session[] sessions;
        lock (_gate) { sessions = _sessions.Values.ToArray(); _sessions.Clear(); }
        foreach (var session in sessions)
        {
            if (!session.Process.HasExited) session.Process.Kill();
            await session.Process.WaitForExitAsync(CancellationToken.None);
            await session.Process.DisposeAsync();
            if (session.Credential is not null && File.Exists(session.Credential.FilePath)) File.Delete(session.Credential.FilePath);
        }
    }

    internal sealed record RepositoryCredential(string Username, string Password, string FilePath);
    private sealed record Session(int Port, string Root, IManagedProcess Process, RepositoryCredential? Credential);
}
