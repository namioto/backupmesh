using BackupMesh.Storage.Core;
using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class StorageConfigurationStoreTests
{
    [Fact]
    public void ConfigurationSurvivesRestartAndRevisionAdvances()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"backupmesh-configuration-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "configuration.json");
        try
        {
            var options = new StorageConfigurationOptions { PersistencePath = path };
            var device = new RegisteredDevice(Guid.NewGuid(), "volume:test", "Test volume", "TEST", "X:\\", DateTimeOffset.UtcNow, null);
            var configuration = new StorageAgentConfiguration([device], [], []);

            var result = new StorageConfigurationStore(options).Update(new(0, configuration));
            var restored = new StorageConfigurationStore(options).Get();

            Assert.Equal(StoreOutcome.Accepted, result.Outcome);
            Assert.Equal(1, restored.Revision);
            Assert.Equal(device.Id, restored.Configuration.Devices.Single().Id);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void StaleRevisionCannotOverwriteConfiguration()
    {
        var store = new StorageConfigurationStore(new StorageConfigurationOptions { PersistencePath = string.Empty });

        Assert.Equal(StoreOutcome.Accepted, store.Update(new(0, StorageAgentConfiguration.Empty)).Outcome);
        Assert.Equal(StoreOutcome.Conflict, store.Update(new(0, StorageAgentConfiguration.Empty)).Outcome);
        Assert.Equal(1, store.Get().Revision);
    }
}
