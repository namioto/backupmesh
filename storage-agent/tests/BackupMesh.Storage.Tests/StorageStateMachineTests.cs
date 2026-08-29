using BackupMesh.Storage.Core;

namespace BackupMesh.Storage.Tests;

public sealed class StorageStateMachineTests
{
    [Fact]
    public void HappyPath_ReachesBusyAndReturnsReady()
    {
        var machine = new StorageStateMachine();
        machine.TransitionTo(StorageState.Discovered);
        machine.TransitionTo(StorageState.Verifying);
        machine.TransitionTo(StorageState.Waiting);
        machine.TransitionTo(StorageState.Ready);
        machine.TransitionTo(StorageState.Busy);
        machine.TransitionTo(StorageState.Ready);
        Assert.Equal(StorageState.Ready, machine.State);
    }

    [Fact]
    public void InvalidTransition_IsRejected()
    {
        var machine = new StorageStateMachine();
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(StorageState.Busy));
    }
}
