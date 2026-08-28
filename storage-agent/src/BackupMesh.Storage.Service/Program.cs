using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);
var mutualTls = builder.Configuration.GetSection("MutualTls").Get<MutualTlsOptions>() ?? new();
if (mutualTls.Enabled)
{
    if (!File.Exists(mutualTls.ServerCertificatePath) || !File.Exists(mutualTls.ClientCertificateAuthorityPath))
        throw new InvalidOperationException("MutualTls certificate paths must reference existing files.");
    var serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(mutualTls.ServerCertificatePath, mutualTls.ServerCertificatePassword);
    var clientAuthority = X509CertificateLoader.LoadCertificateFromFile(mutualTls.ClientCertificateAuthorityPath);
    builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(mutualTls.Port, listen => listen.UseHttps(https =>
    {
        https.ServerCertificate = serverCertificate;
        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        https.ClientCertificateValidation = (certificate, _, _) => MutualTlsCertificateValidator.Validate(certificate, clientAuthority);
        https.SslProtocols = System.Security.Authentication.SslProtocols.Tls13;
    })));
}
builder.Services.AddWindowsService(options => options.ServiceName = "BackupMesh Storage Agent");
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<RestServerOptions>(builder.Configuration.GetSection("RestServer"));
builder.Services.Configure<ControlApiOptions>(builder.Configuration.GetSection("ControlApi"));
builder.Services.Configure<SourceCatalogOptions>(builder.Configuration.GetSection("SourceCatalog"));
builder.Services.Configure<StorageConfigurationOptions>(builder.Configuration.GetSection("StorageConfiguration"));
builder.Services.Configure<RepositoryServerOptions>(builder.Configuration.GetSection("RepositoryServer"));
builder.Services.Configure<BackupJobOptions>(builder.Configuration.GetSection("BackupJob"));
builder.Services.Configure<PairingOptions>(builder.Configuration.GetSection("Pairing"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RestServerOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ControlApiOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SourceCatalogOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageConfigurationOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RepositoryServerOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackupJobOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PairingOptions>>().Value);
builder.Services.AddSingleton<StorageStateMachine>();
builder.Services.AddSingleton<BackupJobStore>();
builder.Services.AddSingleton<PairingCredentialStore>();
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
