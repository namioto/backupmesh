using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<RestServerOptions>(builder.Configuration.GetSection("RestServer"));
builder.Services.Configure<ControlApiOptions>(builder.Configuration.GetSection("ControlApi"));
builder.Services.Configure<SourceCatalogOptions>(builder.Configuration.GetSection("SourceCatalog"));
builder.Services.Configure<StorageConfigurationOptions>(builder.Configuration.GetSection("StorageConfiguration"));
builder.Services.Configure<RepositoryServerOptions>(builder.Configuration.GetSection("RepositoryServer"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RestServerOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ControlApiOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SourceCatalogOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageConfigurationOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RepositoryServerOptions>>().Value);
builder.Services.AddSingleton<StorageStateMachine>();
builder.Services.AddSingleton<BackupJobStore>();
builder.Services.AddSingleton<SourceCatalogStore>();
builder.Services.AddSingleton<StorageConfigurationStore>();
builder.Services.AddSingleton<StoragePresenceStore>();
builder.Services.AddSingleton<BackupTargetResolver>();
builder.Services.AddSingleton<RepositoryServerManager>();
builder.Services.AddSingleton<IRepositoryEndpointProvider>(sp => sp.GetRequiredService<RepositoryServerManager>());
builder.Services.AddSingleton<RequiredControlHeadersFilter>();
builder.Services.AddSingleton<IStorageVolumeInventory, WindowsStorageVolumeInventory>();
builder.Services.AddSingleton<IProcessFactory, SystemProcessFactory>();
builder.Services.AddSingleton<IRestServerLifecycle, RestServerLifecycle>();
builder.Services.AddHostedService<StorageMonitorService>();

var app = builder.Build();
app.MapControlApi();
app.Run();

public partial class Program;
