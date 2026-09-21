using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace BackupMesh.Storage.Service;

// Discovery provides an address hint only. Clients authenticate the saved server certificate
// before sending credentials or accepting the address. No pairing secrets travel over UDP.
public sealed class LanDiscoveryService(MutualTlsOptions options, ILogger<LanDiscoveryService> logger) : BackgroundService
{
    public const int Port = 7445;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ServerTrustPem)) return;
        using var certificate = X509Certificate2.CreateFromPem(options.ServerTrustPem);
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
                while (!stoppingToken.IsCancellationRequested)
                {
                    var packet = await socket.ReceiveAsync(stoppingToken);
                    if (packet.Buffer.Length > 512 || !StorageNetworkAddress.IsUsable(packet.RemoteEndPoint.Address)) continue;
                    var response = Reply(packet.Buffer, fingerprint, options.Port);
                    if (response is not null) await socket.SendAsync(response, packet.RemoteEndPoint, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (SocketException exception)
            {
                logger.LogWarning(exception, "LAN discovery unavailable; retrying in 10 seconds");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    internal static byte[]? Reply(byte[] packet, string fingerprint, int controlPort)
    {
        try
        {
            var query = JsonSerializer.Deserialize<DiscoveryMessage>(packet);
            if (query is not { protocol: "backupmesh-discovery-v1", port: 0 }
                || query.fingerprint != fingerprint || query.nonce is not { Length: 32 }
                || !query.nonce.All(char.IsAsciiHexDigit)) return null;
            return JsonSerializer.SerializeToUtf8Bytes(query with { port = controlPort });
        }
        catch (JsonException) { return null; }
    }

    private sealed record DiscoveryMessage(string protocol, string fingerprint, string nonce, int port);
}
