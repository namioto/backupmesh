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
        https.ClientCertificateValidation = (certificate, _, _) => ValidateClientCertificate(certificate, clientAuthority);
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
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RestServerOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ControlApiOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SourceCatalogOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageConfigurationOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RepositoryServerOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackupJobOptions>>().Value);
builder.Services.AddSingleton<StorageStateMachine>();
builder.Services.AddSingleton<BackupJobStore>();
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

static bool ValidateClientCertificate(X509Certificate2 certificate, X509Certificate2 authority)
{
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(authority);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2"));
    return chain.Build(certificate);
}

public partial class Program;
