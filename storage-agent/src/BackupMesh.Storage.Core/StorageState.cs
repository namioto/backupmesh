namespace BackupMesh.Storage.Core;

public enum StorageState
{
    Offline,
    Discovered,
    Verifying,
    Waiting,
    Ready,
    Busy,
    Error
}
