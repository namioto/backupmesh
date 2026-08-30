# BackupMesh user guide

[한국어](USER_GUIDE.ko.md) | **English**

This guide covers the current MVP: a Windows Storage Agent and one or more Linux Source Agents. Keep another independent copy of important data until you have tested restoration on your own machines.

## 1. Build the packages

From PowerShell at the repository root:

```powershell
pwsh -NoProfile -File scripts/build-windows-test-package.ps1
pwsh -NoProfile -File scripts/build-windows-installer.ps1
pwsh -NoProfile -File scripts/build-linux-source-package.ps1
pwsh -NoProfile -File scripts/build-windows-source-installer.ps1
```

The resulting self-contained packages are written to:

- `artifacts\installer\BackupMesh-Storage-0.1.1-win-x64-Setup.exe`
- `artifacts\BackupMesh-Storage-win-x64` (developer/test package)
- `artifacts\BackupMesh-Source-linux-x64`
- `artifacts\installer\BackupMesh-Source-0.1.1-win-x64-Setup.exe` (Source Agent for backing up this same PC)

The packages include pinned versions of `restic` and `rest-server`; a separate .NET or Go installation is not required.

## 2. Install the Windows Storage Agent

For normal use, run `BackupMesh-Storage-0.1.1-win-x64-Setup.exe`, accept the license, and choose **Install**. The wizard installs and starts the Windows service, registers the tray app for sign-in, creates local-subnet firewall rules, and adds an uninstaller. It preserves existing settings during upgrades and launches BackupMesh when setup finishes.

The installer is not yet Authenticode-signed, so Windows will show **Unknown publisher** (and SmartScreen may warn) before you can run it — this is expected, not a sign of tampering. `build-windows-installer.ps1` writes a matching `.sha256` file next to the installer; verify with `Get-FileHash BackupMesh-Storage-0.1.1-win-x64-Setup.exe -Algorithm SHA256` and compare the result against that file before approving installation.

For a temporary developer evaluation, run `Start-BackupMesh.ps1`. The PowerShell installation path remains available for troubleshooting:

```powershell
Set-Location artifacts\BackupMesh-Storage-win-x64
.\Install-BackupMesh.ps1
```

The installer creates the automatically restarting `BackupMeshStorageAgent` Windows service, opens the authenticated Control and repository ports on Private and Domain networks for the local subnet, and starts the tray app at the current user's next sign-in. Service data is protected under `%ProgramData%\BackupMesh`.

## 3. Register storage

Registering a device is not a separate step or tab anymore — it happens inline, on the **Backups** tab, right where you use it:

- Open BackupMesh from the system tray and go to **Backups**.
- Next to the **Target device** picker, choose **New…** to open the registration dialog.
- Pick a connected drive from the list and choose **Register drive**, or choose **Register folder instead…** to use a local or network folder as a logical device.
- The device is named automatically from its volume label (or folder name) — there is no separate naming step — and it is selected automatically as the target device for the backup you are creating (see step 6).
- A repository must be stored in a safe subfolder, not at the root of a volume; choose that subfolder in the **Target folder** field once the device is registered.

Folder devices are useful for evaluation and for storage that is not exposed as a removable USB volume. They also allow multi-target behavior to be tested with ordinary folders.

Once registered, a device's connection status, free space, and safe-removal readiness are shown on the **Overview** tab's **Connected storage** group. How long BackupMesh waits after any device connects before starting a backup is now a single global default on the **Settings** tab ("Wait before starting, after any device connects"), not a per-device setting.

## 3b. Back up this PC's own files (no Source Agent needed)

**This PC** always appears at the top of the **Source Agents** tab's list, with no pairing, no separate installer, and no enable step. Choose **Back up a folder on this PC…**, pick a folder, and it appears as a Backup Set you can map to any registered target device exactly like a paired Source's Backup Set. Storage runs the bundled `restic` directly against the local folder when the mapped target becomes ready - no network hop, no certificates, no repository password to manage.

Use **Remove folder** to stop backing up a folder this way; its mappings are removed with it. This is unrelated to the standalone Windows Source Agent described below, which is for a *different* PC with no Storage Agent of its own.

## 4. Install and configure a Linux Source Agent

Copy `BackupMesh-Source-linux-x64` to the Linux machine and run:

```sh
sudo sh install.sh
sudoedit /etc/backupmesh/backupmesh.json
```

Define each Backup Set with a user-facing name, source paths, and optional include/exclude patterns. The Source Agent generates stable Agent and Backup Set UUIDs automatically and preserves them in an owner-only `*.state.json` file next to the configuration. Do not edit or copy IDs between Sources. Validate the file:

The Source Agent accepts strict JSON (`.json`) and YAML (`.yaml` or `.yml`). A Backup Set's `paths` list may contain any number of files or directories; see `source-agent/example.config.yaml` for a multi-path example. Unknown YAML and JSON fields are rejected so spelling mistakes cannot silently disable a setting.

```sh
sudo /opt/backupmesh/backupmesh-agent validate \
  -config /etc/backupmesh/backupmesh.json
```

The installer creates `/etc/backupmesh/restic-password` with owner-only permissions. Make a protected recovery copy. Losing this password makes the encrypted snapshots unrecoverable.

Running `install.sh` from an interactive terminal (rather than a script) prompts for an Agent name and a first Backup Set instead of leaving a generic template to edit by hand, and offers to run `pair` immediately afterward.

## 4b. Install a Windows Source Agent on a different PC

Use this when a *separate* Windows PC (with no Storage Agent of its own) should back up to a Storage Agent running elsewhere on the network — for example, a laptop backing up to a Storage PC in another room. To back up the Storage Agent's own PC, use **This PC** in the tray instead (section 3b) — no installer needed at all.

Run `BackupMesh-Source-0.1.1-win-x64-Setup.exe` on that PC. Unlike the Storage installer, it never asks for administrator rights: it installs under your own user profile and, right after copying files, opens a console window asking for an Agent name and a first Backup Set path to write a minimal `backupmesh.yaml` (add more `backupSets` entries by hand any time). It also registers a per-user Scheduled Task that keeps the Source Agent watching in the background, and an uninstaller that removes the task and binaries while keeping your configuration, paired identity, and repository password.

For scripted or troubleshooting use, the underlying package and installer script remain available directly:

```powershell
pwsh -NoProfile -File scripts/build-windows-source-package.ps1
Set-Location artifacts\BackupMesh-Source-win-x64
.\Install-BackupMeshSource.ps1
```

Pair it the same way as a Linux Source, using the code, endpoint, and fingerprint the **Pair a Source Agent** dialog shows:

```powershell
& "$env:LOCALAPPDATA\BackupMesh\Source\backupmesh-agent.exe" pair `
  -config "$env:LOCALAPPDATA\BackupMesh\Source\backupmesh.yaml" `
  -storage https://STORAGE-PC:7443 `
  -code CODE-FROM-TRAY `
  -fingerprint 64_HEX_CHARACTERS_FROM_TRAY
```

`Uninstall-BackupMeshSource.ps1` removes the scheduled task and binaries while keeping the configuration, paired identity, and repository password under `%LOCALAPPDATA%\BackupMesh\Source`.

## 5. Pair the Source

On the **Source Agents** tab, choose **Pair a Source Agent**. It displays a Storage address, one-time code, and certificate SHA-256 fingerprint; the code expires after ten minutes and can be used once. On the Source run:

```sh
sudo /opt/backupmesh/backupmesh-agent pair \
  -config /etc/backupmesh/backupmesh.json \
  -storage https://STORAGE-PC:7443 \
  -code CODE-FROM-TRAY \
  -fingerprint 64_HEX_CHARACTERS_FROM_TRAY \
  -output /etc/backupmesh/pairing
```

The Source verifies the pinned fingerprint before sending the code, then installs an identity-bound token, client certificate, private key, and pinned Storage certificate with owner-only permissions. No private key is placed in a transfer file and no certificate is added to the operating-system trust store.

If a Source Agent loses its private key or certificate (for example, its `pairing` directory was deleted), select it in the **Source Agents** tab's list and choose **Re-pair** instead of **Pair a Source Agent**. That code can only reissue credentials for that specific, already-known Source — it cannot be used to create a new one or claim a different Source's identity.

Start the Source command watcher:

```sh
sudo systemctl enable --now backupmesh-source-watch.service
sudo systemctl status backupmesh-source-watch.service
```

## 6. Map Backup Sets to destinations

After the Source synchronizes, open **Backups** in the tray app.

1. Under **What to back up**, select a Backup Set — synced from a paired Source Agent, or a local folder added from **This PC** on the **Source Agents** tab (section 3b).
2. Under **Target device**, select a registered device, or choose **New…** to register one inline if it is not registered yet (see step 3).
3. Under **Target folder**, browse to or type a destination subfolder on that device.
4. Choose **Add backup**. The entry appears immediately in the grid above and is saved right away — there is no separate save step.

Mappings are many-to-many. Multiple Sources can use separate folders or a shared parent on one device, and one Backup Set can be copied to multiple devices. Use a distinct repository subfolder for each independent Backup Set unless intentional repository sharing has been tested.

Source and Storage Agents may run on the same computer by using the Storage Agent's local HTTPS endpoint. Local fixed drives and registered folders are valid destination devices, not only USB media. This supports both local-data-to-external-storage and external-source-to-local-storage layouts. For the latter, register the external source volume with Storage as a device. Storage detects its arrival, finds Backup Sets whose source paths are inside that volume, and sends commands for every ready mapped destination. The Source Agent only executes Storage-authorized commands; it does not own device detection or policy.

## 7. Run and monitor a backup

Connect the registered device and wait for the arrival delay set on the **Settings** tab. BackupMesh requests the mapped Source backup automatically. The tray app shows queued/running state, files and bytes processed, progress, result, and the latest successful run on the **Overview** tab. A small status window can also pop up near the tray icon when a backup starts, with progress and a Cancel button — this is on by default and can be turned off in **Settings**. A running job can be cancelled from either place; the Source terminates restic and reports `CANCELLED`.

For a manual Source-side run:

```sh
sudo /opt/backupmesh/backupmesh-agent backup \
  -config /etc/backupmesh/backupmesh.json \
  -set documents \
  -restic /opt/backupmesh/restic
```

A banner above the tabs — visible no matter which one is open — tells you once a connected device has finished all its backups and is safe to remove, with a **Remove safely** button right there; it also warns while a backup to that device is still running, so you never have to guess. Use that banner, or **Safely remove selected device** in the **Overview** tab's **Connected storage** group, only after all jobs targeting the device have stopped. BackupMesh closes its repository listeners before asking Windows to eject the volume.

## 8. Test restoration

Do not treat a backup as verified until a restore has succeeded and representative files match. For emergency recovery with the storage attached directly to a Windows machine, use the bundled restic executable and a protected copy of the Source repository password:

```powershell
$env:RESTIC_PASSWORD_FILE = 'C:\secure\restic-password'
artifacts\BackupMesh-Storage-win-x64\Service\restic.exe `
  -r 'E:\BackupMesh\documents' snapshots
artifacts\BackupMesh-Storage-win-x64\Service\restic.exe `
  -r 'E:\BackupMesh\documents' restore latest `
  --target 'C:\BackupMesh-Restore-Test'
```

Restore into an empty test directory and compare file hashes or open representative files before relying on the repository.

## Troubleshooting

- **Source does not appear:** check `systemctl status backupmesh-source-watch.service`, confirm TCP 7443 is reachable, and verify the Storage hostname/IP is included in its certificate names before issuing a new pairing bundle.
- **No target is ready:** confirm the device is connected, the backup has been added on the **Backups** tab, the Source has synchronized its catalog, and the arrival delay set in **Settings** has elapsed.
- **Certificate error:** re-pair after correcting the Storage Agent's advertised hostname. Do not install the private BackupMesh CA into the Windows system trust store.
- **Insufficient space:** free space or choose another mapped device. A failed target does not prevent another ready target from being attempted.
- **Interrupted run:** reconnect the device and retry. BackupMesh releases stale jobs after service recovery; restic safely reuses already stored content.
- **Uninstall:** run `Uninstall-BackupMesh.ps1` as Administrator. Configuration and repositories are intentionally preserved.

## Current acceptance boundary

The repository test suite verifies authenticated pairing, TLS repository transport, multi-target backup, restore, and SHA-256 equality using real files. Before production use, repeat installation, backup, cancellation, disconnection, and restore tests across your actual Windows and Linux machines and storage devices.
