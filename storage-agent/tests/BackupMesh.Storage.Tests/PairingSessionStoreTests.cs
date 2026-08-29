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

    [Fact]
    public void ThrottleForgetsAddressesOnceTheirWindowAndLockoutHavePassed()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new PairingAttemptThrottle(clock);
        for (var i = 0; i < 50; i++) throttle.RecordFailure(System.Net.IPAddress.Parse($"2001:db8::{i:x}"));
        Assert.Equal(50, throttle.TrackedAddressCount);

        clock.Advance(TimeSpan.FromMinutes(21));
        throttle.RecordFailure(System.Net.IPAddress.Parse("2001:db8::ffff"));

        Assert.Equal(1, throttle.TrackedAddressCount);
    }

    [Fact]
    public void ThrottleKeepsALockedOutAddressEvenAfterItsWindowRollsOver()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new PairingAttemptThrottle(clock);
        var attacker = System.Net.IPAddress.Parse("203.0.113.7");
        clock.Advance(TimeSpan.FromMinutes(9));
        for (var i = 0; i < 5; i++) throttle.RecordFailure(attacker);
        Assert.True(throttle.IsLockedOut(attacker));

        // The 10-minute counting window has now rolled over while the lockout is still running.
        clock.Advance(TimeSpan.FromMinutes(2));
        throttle.RecordFailure(attacker);

        Assert.True(throttle.IsLockedOut(attacker));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
