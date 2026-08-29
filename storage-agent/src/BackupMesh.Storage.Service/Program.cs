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
RepositoryServerManager.DeleteStaleTlsFiles(repositoryServer.CredentialDirectory);
if (mutualTls.Enabled)
{
    var serverCertificate = string.IsNullOrWhiteSpace(mutualTls.ServerCertificatePath)
        ? pairingCertificateAuthority.IssueServerCertificate(mutualTls.ServerNames)
        : File.Exists(mutualTls.ServerCertificatePath)
            ? X509CertificateLoader.LoadPkcs12FromFile(mutualTls.ServerCertificatePath, mutualTls.ServerCertificatePassword)
            : throw new InvalidOperationException("MutualTls server certificate path must reference an existing file.");
    var clientAuthority = pairingCertificateAuthority.GetAuthorityCertificate();
    mutualTls.ServerTrustPem = serverCertificate.ExportCertificatePem();
    repositoryServer.UseTls = true;
    repositoryServer.TlsCertificatePem = mutualTls.ServerTrustPem;
    using (var repositoryKey = serverCertificate.GetRSAPrivateKey())
        repositoryServer.TlsPrivateKeyPem = repositoryKey?.ExportPkcs8PrivateKeyPem() ?? throw new InvalidOperationException("The Storage server certificate must have an RSA private key.");
    builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(mutualTls.Port, listen => listen.UseHttps(https =>
    {
        https.ServerCertificate = serverCertificate;
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.OnAuthenticate = (_, authentication) =>
        {
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                VerificationFlags = X509VerificationFlags.NoFlag
            };
            policy.CustomTrustStore.Add(clientAuthority);
            policy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2"));
            authentication.CertificateChainPolicy = policy;
        };
        https.ClientCertificateValidation = (certificate, _, _) => certificate is null ||
            MutualTlsCertificateValidator.Validate(certificate, clientAuthority);
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
builder.Services.Configure<BackupCommandOptions>(builder.Configuration.GetSection("BackupCommand"));
builder.Services.Configure<AutomationSettingsOptions>(builder.Configuration.GetSection("AutomationSettings"));
builder.Services.Configure<PairingOptions>(builder.Configuration.GetSection("Pairing"));
builder.Services.Configure<PairingCertificateOptions>(builder.Configuration.GetSection("PairingCertificate"));
builder.Services.Configure<IssuedCertificateOptions>(builder.Configuration.GetSection("IssuedCertificate"));
builder.Services.Configure<SourceDisplayNameOptions>(builder.Configuration.GetSection("SourceDisplayName"));
builder.Services.Configure<LocalBackupOptions>(builder.Configuration.GetSection("LocalBackup"));
builder.Services.AddSingleton(mutualTls);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RestServerOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ControlApiOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SourceCatalogOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageConfigurationOptions>>().Value);
builder.Services.AddSingleton(repositoryServer);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackupJobOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackupCommandOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AutomationSettingsOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PairingOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PairingCertificateOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IssuedCertificateOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SourceDisplayNameOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalBackupOptions>>().Value);
builder.Services.AddSingleton<StorageStateMachine>();
builder.Services.AddSingleton<BackupJobStore>();
builder.Services.AddSingleton<BackupCommandQueue>();
builder.Services.AddSingleton<AutomationSettingsStore>();
builder.Services.AddSingleton<PairingCredentialStore>();
builder.Services.AddSingleton<RevokedSourceStore>();
builder.Services.AddSingleton<PairingSessionStore>();
builder.Services.AddSingleton<PairingAttemptThrottle>();
builder.Services.AddSingleton(pairingCertificateAuthority);
builder.Services.AddSingleton<IssuedCertificateStore>();
builder.Services.AddSingleton<SourceDisplayNameStore>();
builder.Services.AddSingleton<LocalRepositoryPasswordStore>();
builder.Services.AddSingleton<SourceCatalogStore>();
builder.Services.AddSingleton<StorageConfigurationStore>();
builder.Services.AddSingleton<StoragePresenceStore>();
builder.Services.AddSingleton<BackupTargetResolver>();
builder.Services.AddSingleton<RepositoryServerManager>();
builder.Services.AddSingleton<IRepositoryEndpointProvider>(sp => sp.GetRequiredService<RepositoryServerManager>());
builder.Services.AddSingleton<IRepositorySessionController>(sp => sp.GetRequiredService<RepositoryServerManager>());
builder.Services.AddSingleton<RequiredControlHeadersFilter>();
builder.Services.AddSingleton<ControlApiAuthenticationFilter>();
builder.Services.AddSingleton<IStorageVolumeInventory, WindowsStorageVolumeInventory>();
builder.Services.AddSingleton<IStorageDeviceEjector, WindowsStorageDeviceEjector>();
builder.Services.AddSingleton<IProcessFactory, SystemProcessFactory>();
builder.Services.AddSingleton<IRestServerLifecycle, RestServerLifecycle>();
builder.Services.AddHostedService<StorageMonitorService>();
builder.Services.AddHostedService<LocalBackupExecutorService>();

var app = builder.Build();
app.MapControlApi();
app.Run();

public partial class Program;
