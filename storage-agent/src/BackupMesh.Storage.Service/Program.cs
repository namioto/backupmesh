using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<RestServerOptions>(builder.Configuration.GetSection("RestServer"));
builder.Services.Configure<ControlApiOptions>(builder.Configuration.GetSection("ControlApi"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RestServerOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ControlApiOptions>>().Value);
builder.Services.AddSingleton<StorageStateMachine>();
builder.Services.AddSingleton<BackupJobStore>();
builder.Services.AddSingleton<SourceCatalogStore>();
builder.Services.AddSingleton<RequiredControlHeadersFilter>();
builder.Services.AddSingleton<IStorageDiscovery, PollingDriveDiscovery>();
builder.Services.AddSingleton<IStorageIdentityVerifier, BasicStorageIdentityVerifier>();
builder.Services.AddSingleton<IProcessFactory, SystemProcessFactory>();
builder.Services.AddSingleton<IRestServerLifecycle, RestServerLifecycle>();
builder.Services.AddHostedService<StorageMonitorService>();

var app = builder.Build();
app.MapControlApi();
app.Run();

public partial class Program;
