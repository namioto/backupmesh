using System.Diagnostics;
using System.Net.Sockets;
using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Service;

public sealed record BackupTargetAvailability(Guid MappingId, Guid DeviceId, Guid BackupSetId, string DeviceName, string DestinationFolder, string State, string? Reason);
public sealed record ResolvedBackupTarget(Guid MappingId, Guid DeviceId, Guid BackupSetId, string DeviceName, string DeviceRoot, string RepositoryPath, string DestinationFolder);
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
        return new(new(mapping.Id, device.Id, backupSet.Id, device.DisplayName, status.CurrentRoot, mapping.RepositoryPath, destination));
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
    public string ListenHost { get; set; } = "127.0.0.1";
    public string PublicHost { get; set; } = "127.0.0.1";
    public int BasePort { get; set; } = 18000;
    public bool NoAuthentication { get; set; } = true;
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
        Session session;
        lock (_gate)
        {
            if (_sessions.TryGetValue(target.DeviceId, out session!) && !session.Process.HasExited)
                return Endpoint(session.Port, target.RepositoryPath);
            var port = options.BasePort + _sessions.Count;
            var startInfo = new ProcessStartInfo
            {
                FileName = options.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--path");
            startInfo.ArgumentList.Add(target.DeviceRoot);
            startInfo.ArgumentList.Add("--listen");
            startInfo.ArgumentList.Add($"{options.ListenHost}:{port}");
            if (options.NoAuthentication) startInfo.ArgumentList.Add("--no-auth");
            session = new(port, target.DeviceRoot, processFactory.Start(startInfo));
            _sessions[target.DeviceId] = session;
        }
        await WaitUntilListeningAsync(session, cancellationToken);
        return Endpoint(session.Port, target.RepositoryPath);
    }

    private Uri Endpoint(int port, string repositoryPath)
        => BuildEndpoint(options.PublicHost, port, repositoryPath);

    internal static Uri BuildEndpoint(string publicHost, int port, string repositoryPath)
    {
        var path = repositoryPath == "." ? "/" : "/" + string.Join('/', repositoryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + "/";
        var endpoint = new UriBuilder(Uri.UriSchemeHttp, publicHost, port, path).Uri;
        // restic distinguishes its REST backend from an ordinary HTTP URL with
        // the `rest:` transport prefix.
        return new Uri("rest:" + endpoint.AbsoluteUri);
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
        }
    }

    private sealed record Session(int Port, string Root, IManagedProcess Process);
}
