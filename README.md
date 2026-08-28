# BackupMesh

[한국어](README.ko.md) | **English**

**Plug in your backup storage. BackupMesh takes it from there.**

BackupMesh is a storage-aware backup orchestrator. It detects when trusted storage becomes available and automatically backs up data from registered source computers—even when the data and storage live on different machines.

For example, keep an external HDD safely disconnected most of the time. When you attach it to a Windows PC, BackupMesh verifies the drive and automatically backs up your Linux server. There is no backup command to remember and no network drive to mount by hand.

```text
External HDD connected
    ↓
Storage identified and verified
    ↓
Registered source and policy checked
    ↓
Backup started automatically
    ↓
Completion confirmed and drive safely ejected
```

## Why BackupMesh?

### Offline backups without the routine

A permanently connected backup drive can be exposed to ransomware, mistakes, and failures affecting the host. A manually disconnected drive is safer, but manual backup routines are easy to postpone or forget. BackupMesh combines the resilience of offline storage with the convenience of automatic backups.

### It recognizes the storage before it starts

BackupMesh does not start merely because a drive letter or directory exists. It verifies the registered storage identity, then checks readiness, free space, and policy before allowing a backup to run.

### Your source and storage can live on different computers

Connect an always-on home server or Linux machine to an external drive attached to your desktop. BackupMesh coordinates them as one backup workflow.

### Know exactly what your backup is doing

The Storage Agent shows progress, processed files and bytes, estimated completion time, and the latest successful backup. You do not have to guess whether the drive is safe to remove.

### Designed to protect existing recovery points

BackupMesh is designed to limit the Source Agent's normal permissions to creating backups, with deletion and maintenance privileges kept separate. Backup data is encrypted, and communication between Agents is mutually authenticated.

### Not locked to one backup engine

The first version builds on the proven Restic ecosystem. BackupMesh itself is an orchestration layer for storage availability, policy, execution, and observability—not a dependency on one repository format. Its architecture leaves room for additional Storage Providers and Backup Engines.

## First reference scenario

```text
Linux home server                    Windows PC
┌────────────────┐                 ┌────────────────────┐
│ Source Agent   │ ─── backup ───▶ │ Storage Agent      │
│ Photos & data  │                 │ External HDD       │
└────────────────┘                 └────────────────────┘
```

1. Install the Source Agent on a Linux server and register the paths to protect.
2. Install the Storage Agent on a Windows PC and register an external HDD.
3. Connect the HDD.
4. BackupMesh verifies the storage and runs the backup according to policy.
5. Confirm completion and safely eject the drive.

## Storage Agent for Windows

The Windows tray app keeps removable backup storage understandable without turning it into an always-on server. Register a physical device, review synchronized Source Agents and their Backup Sets, then map each Backup Set to a device and a relative repository path. The mapping model supports multiple Sources per device and multiple devices per Source.

To pair a Source, choose **Pair Source Agent** in the tray app and save the generated `backupmesh-pairing.json` bundle. Transfer it securely, then run `backupmesh-agent apply-pairing --config /etc/backupmesh/backupmesh.json --bundle backupmesh-pairing.json --output /etc/backupmesh/pairing`. This installs the identity-bound token, client certificate, private key, Storage Agent authority and remote Control API address, updates the Source ID and TLS paths, and protects the files with owner-only permissions on Linux. Delete the transfer bundle after applying it. Each Source receives an independent identity and credential.

![BackupMesh Storage Agent mapping multiple Sources to removable storage](docs/images/storage-agent-mappings.jpg)

### Try the current Windows build

From PowerShell in the repository, build a self-contained test package:

```powershell
pwsh -NoProfile -File scripts/build-windows-test-package.ps1
```

Then run `artifacts\BackupMesh-Storage-win-x64\Start-BackupMesh.ps1`. The launcher starts the local Storage Service, waits until it is ready, and opens the tray app. Closing the tray app also stops the test service. Configuration is kept under `%LOCALAPPDATA%\BackupMesh`.

For an always-on installation, open PowerShell as Administrator and run `Install-BackupMesh.ps1` from the built package. It installs the Storage Agent as an automatically restarting Windows service, starts it immediately, and registers the tray app for the current user's next sign-in. `Uninstall-BackupMesh.ps1` removes the service and startup entry while preserving configuration and repositories.

Build the self-contained Linux Source Agent package with `pwsh -NoProfile -File scripts/build-linux-source-package.ps1`. Copy `artifacts/BackupMesh-Source-linux-x64` to the source machine and run `sudo sh install.sh`. The package includes the pinned restic binary and systemd service/timer templates; the installer prints the validation and timer-enable commands after preserving or creating `/etc/backupmesh/backupmesh.json`.

## Project status

BackupMesh is in the early implementation stage. The first release focuses on:

- A Go Source Agent for Linux
- A .NET Storage Agent for Windows
- Encrypted backups through Restic and rest-server
- Connection detection and identity verification for a designated removable drive
- Delayed execution, progress reporting, and safe ejection
- An authenticated Control API between Agents

> BackupMesh is not yet ready to be the only backup solution for important data.

## License

BackupMesh is licensed under the [Apache License 2.0](LICENSE).

Distributions may bundle `restic` and `rest-server`, which remain independently licensed under the BSD 2-Clause License. See [Third-Party Notices](THIRD_PARTY_NOTICES.md) for details.
