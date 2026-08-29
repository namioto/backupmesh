using BackupMesh.Storage.Service;

namespace BackupMesh.Storage.Tests;

public sealed class PairingSessionStoreTests
{
    [Fact]
    public void CodeIsSingleUse()
    {
        var store = new PairingSessionStore();
        var session = store.Create();

        Assert.True(store.Consume(session.Code));
        Assert.False(store.Consume(session.Code));
    }

    [Fact]
    public void ExpiredCodeIsRejected()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new PairingSessionStore(clock);
        var session = store.Create();
        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.False(store.Consume(session.Code));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
