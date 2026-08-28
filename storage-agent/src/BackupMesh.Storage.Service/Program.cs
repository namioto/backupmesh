using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);
var pairingCertificateOptions = builder.Configuration.GetSection("PairingCertificate").Get<PairingCertificateOptions>() ?? new();
var pairingCertificateAuthority = new PairingCertificateAuthority(pairingCertificateOptions);
var mutualTls = builder.Configuration.GetSection("MutualTls").Get<MutualTlsOptions>() ?? new();
var repositoryServer = builder.Configuration.GetSection("RepositoryServer").Get<RepositoryServerOptions>() ?? new();
if (string.IsNullOrWhiteSpace(repositoryServer.PublicHost))
    repositoryServer.PublicHost = mutualTls.ServerNames.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && !name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) ?? Environment.MachineName;
if (mutualTls.Enabled)
{
    var serverCertificate = string.IsNullOrWhiteSpace(mutualTls.ServerCertificatePath)
        ? pairingCertificateAuthority.IssueServerCertificate(mutualTls.ServerNames)
        : File.Exists(mutualTls.ServerCertificatePath)
            ? X509CertificateLoader.LoadPkcs12FromFile(mutualTls.ServerCertificatePath, mutualTls.ServerCertificatePassword)
            : throw new InvalidOperationException("MutualTls server certificate path must reference an existing file.");
    var clientAuthority = pairingCertificateAuthority.GetAuthorityCertificate();
    builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(mutualTls.Port, listen => listen.UseHttps(https =>
    {
        https.ServerCertificate = serverCertificate;
        https.ServerCertificateChain = new X509Certificate2Collection(clientAuthority);
        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        https.ClientCertificateValidation = (certificate, _, errors) =>
        {
            var accepted = MutualTlsCertificateValidator.Validate(certificate, clientAuthority);
            if (!accepted) Console.Error.WriteLine($"Rejected Source client certificate {certificate.Thumbprint}; TLS errors: {errors}.");
            return accepted;
        };
        https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
    })));
}
builder.Services.AddWindowsService(options => options.ServiceName = "BackupMesh Storage Agent");
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<RestServerOptions>(builder.Configuration.GetSection("RestServer"));
builder.Services.Configure<ControlApiOptions>(builder.Configuration.GetSection("ControlApi"));
builder.Services.Configure<SourceCatalogOptions>(builder.Configuration.GetSection("SourceCatalog"));
builder.Services.Configure<StorageConfigurationOptions>(builder.Configuration.GetSection("StorageConfiguration"));
builder.Services.Configure<BackupJobOptions>(builder.Configuration.GetSection("BackupJob"));
builder.Services.Configure<PairingOptions>(builder.Configuration.GetSection("Pairing"));
builder.Services.Configure<PairingCertificateOptions>(builder.Configuration.GetSection("PairingCertificate"));
builder.Services.AddSingleton(mutualTls);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RestServerOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ControlApiOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SourceCatalogOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageConfigurationOptions>>().Value);
builder.Services.AddSingleton(repositoryServer);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackupJobOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PairingOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PairingCertificateOptions>>().Value);
builder.Services.AddSingleton<StorageStateMachine>();
builder.Services.AddSingleton<BackupJobStore>();
builder.Services.AddSingleton<PairingCredentialStore>();
builder.Services.AddSingleton(pairingCertificateAuthority);
builder.Services.AddSingleton<SourceCatalogStore>();
builder.Services.AddSingleton<StorageConfigurationStore>();
builder.Services.AddSingleton<StoragePresenceStore>();
builder.Services.AddSingleton<BackupTargetResolver>();
builder.Services.AddSingleton<RepositoryServerManager>();
builder.Services.AddSingleton<IRepositoryEndpointProvider>(sp => sp.GetRequiredService<RepositoryServerManager>());
builder.Services.AddSingleton<RequiredControlHeadersFilter>();
builder.Services.AddSingleton<ControlApiAuthenticationFilter>();
builder.Services.AddSingleton<IStorageVolumeInventory, WindowsStorageVolumeInventory>();
builder.Services.AddSingleton<IStorageDeviceEjector, WindowsStorageDeviceEjector>();
builder.Services.AddSingleton<IProcessFactory, SystemProcessFactory>();
builder.Services.AddSingleton<IRestServerLifecycle, RestServerLifecycle>();
builder.Services.AddHostedService<StorageMonitorService>();

var app = builder.Build();
app.MapControlApi();
app.Run();

public partial class Program;
