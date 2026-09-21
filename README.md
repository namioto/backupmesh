# BackupMesh

[한국어](README.ko.md) | **English** | [User guide](docs/USER_GUIDE.md)

**Plug in your backup storage. BackupMesh takes it from there.**

Current version: **0.3.4** — the Storage app supports Korean and English, displays project information and GitHub links, and offers optional settings cleanup during uninstall. See the [changelog](CHANGELOG.md).

BackupMesh is a storage-aware backup orchestrator. It detects when trusted storage becomes available and automatically backs up data from registered source computers—even when the data and storage live on different machines.

For example, keep an external HDD safely disconnected most of the time. When you attach it to a Windows PC, BackupMesh verifies the drive and automatically backs up your Linux server. There is no backup command to remember and no network drive to mount by hand.

![BackupMesh workflow: connect storage, verify it, check the Remote Agent policy, back up automatically, and safely eject](docs/images/backup-workflow.en.png)

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

BackupMesh is designed to limit the Remote Agent's normal permissions to creating backups, with deletion and maintenance privileges kept separate. Backup data is encrypted, and communication between Agents is mutually authenticated.

### Not locked to one backup engine

The first version builds on the proven Restic ecosystem. BackupMesh itself is an orchestration layer for storage availability, policy, execution, and observability—not a dependency on one repository format. Its architecture leaves room for additional Storage Providers and Backup Engines.

## First reference scenario

![BackupMesh reference scenario: a Linux Remote Agent sends an authenticated encrypted backup to a Windows Storage Agent and external HDD](docs/images/reference-scenario.en.png)

1. Install the Remote Agent on a Linux server and register the paths to protect.
2. Install the Storage Agent on a Windows PC and register an external HDD.
3. Connect the HDD.
4. BackupMesh verifies the storage and runs the backup according to policy.
5. Confirm completion and safely eject the drive.

## Storage Agent for Windows

The Windows tray app keeps backup storage understandable without turning it into an always-on server. On the **Backups** tab, choose what to back up and where it goes: register a physical device or an ordinary local/network folder as a logical storage device inline with **New…** next to the target-device picker, then map each Backup Set to that device and a relative repository path. The **Remote Agents** tab lists paired Remote Agents and the Backup Sets they offer; **Overview** shows connected storage, free space, and when it is safe to remove a device. The mapping model supports multiple Remote Agents per device and multiple devices per Remote Agent.

To pair a Remote Agent, choose **Pair a Remote Agent** on the **Remote Agents** tab. Copy the connection invitation, run `backupmesh-agent pair` with your configuration path, and paste the invitation when prompted. The Remote Agent pins the Storage certificate before transmitting the code and installs its identity-bound token, client certificate, private key, and Storage trust material with owner-only permissions. New pairing never places a private key in a transfer bundle or modifies the operating-system trust store.

The Linux installer creates `/etc/backupmesh/restic-password` for repository encryption. Store a protected recovery copy: BackupMesh cannot restore snapshots if this password is lost.

## Installation and contributing

See the [user guide](docs/USER_GUIDE.md) for installation and setup. Development prerequisites, build commands, tests, and contribution guidelines are in [CONTRIBUTING.md](CONTRIBUTING.md).

## Project status

The end-to-end MVP is implemented. The repository test flow performs a real authenticated backup to two folder-backed targets, restores both snapshots, and verifies SHA-256 equality. The current release includes:

- A Go Remote Agent for Linux
- A .NET Storage Agent for Windows
- Encrypted backups through Restic and rest-server
- Fixed, removable, and folder-backed storage registration with stable identity
- Delayed execution, progress reporting, and safe ejection
- An authenticated Control API between Agents

See the [user guide](docs/USER_GUIDE.md) for installation, pairing, mapping, restore testing, and troubleshooting. Before production use, repeat the acceptance tests on your actual Windows and Linux machines. BackupMesh should not be the only copy of important data until you have verified restoration yourself.

## License

BackupMesh is licensed under the [Apache License 2.0](LICENSE).

Distributions may bundle `restic` and `rest-server`, which remain independently licensed under the BSD 2-Clause License. See [Third-Party Notices](THIRD_PARTY_NOTICES.md) for details.
