# BackupMesh user guide

[한국어](USER_GUIDE.ko.md) | **English**

This guide covers the current MVP: a Windows Storage Agent and one or more Linux Remote Agents. Keep another independent copy of important data until you have tested restoration on your own machines.

## App language and project information

In **Settings → Language**, choose System default, 한국어, or English. The preference saves and applies immediately, including the tray menu, without restarting or interrupting backups. System default uses Korean on Korean Windows and English otherwise. Dates and numbers keep your regional formatting. Existing activity messages retain the language used when recorded.

The footer always shows the app version and GitHub link. **Settings → About BackupMesh** contains the project address, user guide, issue tracker and license. Service and external-tool diagnostic details may remain in their original language.

## Pairing address and copied values

Choose **Copy connection invitation**, run the pairing command below on the Remote Agent, and paste the invitation when prompted. The address and certificate fingerprint are handled automatically. Individual fields remain under **Connection details (advanced)** for older agents. Expiration is a deadline, not an input.

Storage defaults to an active LAN IPv4 address, preferring an interface with a gateway. The remote computer must have a route to that address and the required firewall ports. Multiple networks or VPNs may require an explicit reachable name or address in the service's `MutualTls.ServerNames` configuration; set `RepositoryServer.PublicHost` consistently when overriding it. DNS names must resolve on the remote computer. Reserve the chosen IP in DHCP or use a managed DNS name for a stable endpoint.

If connection setup needs repair, the app offers **Repair connection setup**. Approve it to apply changes and restart automatically, then continue to the invitation dialog. Windows may request administrator approval. Previously connected computers must connect again; backup files are preserved. The same repair action is available in Settings.

## 1. Prepare installation packages

Obtain the installation packages listed below. To build them from source, follow the [contribution guide](../CONTRIBUTING.md).

The resulting self-contained packages are written to:

- `artifacts\installer\BackupMesh-Storage-0.3.3-win-x64-Setup.exe`
- `artifacts\BackupMesh-Storage-win-x64` (developer/test package)
- `artifacts\BackupMesh-Source-linux-x64`
- `artifacts\installer\BackupMesh-Source-0.3.3-win-x64-Setup.exe` (Remote Agent for backing up this same PC)

The packages include pinned versions of `restic` and `rest-server`; a separate .NET or Go installation is not required.

## 2. Install the Windows Storage Agent

For normal use, run `BackupMesh-Storage-0.3.3-win-x64-Setup.exe`, accept the license, and choose **Install**. The wizard installs and starts the Windows service, registers the tray app for sign-in, creates local-subnet firewall rules, and adds an uninstaller. It preserves existing settings during upgrades and launches BackupMesh when setup finishes.

The installer is not yet Authenticode-signed, so Windows will show **Unknown publisher** (and SmartScreen may warn) before you can run it — this is expected, not a sign of tampering. `build-windows-installer.ps1` writes a matching `.sha256` file next to the installer; verify with `Get-FileHash BackupMesh-Storage-0.3.3-win-x64-Setup.exe -Algorithm SHA256` and compare the result against that file before approving installation.

For a temporary developer evaluation, run `Start-BackupMesh.ps1`. The PowerShell installation path remains available for troubleshooting:

```powershell
Set-Location artifacts\BackupMesh-Storage-win-x64
.\Install-BackupMesh.ps1
```

The installer creates the automatically restarting `BackupMeshStorageAgent` Windows service, opens the authenticated Control and repository ports on Private and Domain networks for the local subnet, and starts the tray app at the current user's next sign-in. Service data is protected under `%ProgramData%\BackupMesh`.

Uninstall defaults to **No: keep settings**. Choose **Yes** to remove Storage backup rules, history, pairing information and the app settings of the Windows user running uninstall. Computers must be paired again. Silent uninstall preserves settings. For PowerShell uninstall, use `Uninstall-BackupMesh.ps1 -RemoveSettings` to request the same cleanup.

Actual backups, passwords in `local-repository-passwords`, and separate Remote Agent data are never removed. Files named `*.recovery-*` beside the original settings preserve destination paths and mapping IDs needed to identify password files; setup does not automatically reload these copies. Passwords use Windows DPAPI and cannot be recovered on another PC merely by copying these files. Other Windows users' UI settings are preserved. Setup's welcome page announces when previous settings will be reused.

## 3. Choose where backups are stored

Storage registration is internal. You only choose what to back up and where to store it in the backup-rule window:

- Open BackupMesh from the system tray and go to **Backups**.
- Choose **Add backup…**, select a connected target drive, and enter or browse to the full destination path.
- Choose **Choose folder…** to use a local or network folder instead of a detected drive.
- BackupMesh remembers the target identity automatically so drive-letter changes do not break the rule. It also removes unused internal device records when their last rule is deleted.
- A repository must be stored in a safe subfolder, not at the root of a volume.

Folder devices are useful for evaluation and for storage that is not exposed as a removable USB volume. They also allow multi-target behavior to be tested with ordinary folders.

How long BackupMesh waits after a target connects before starting a backup is a single global default on the **Settings** tab. Use Windows to eject removable storage after its backup job has stopped.

## 3b. Back up this PC's own files (no Remote Agent needed)

**This PC** always appears at the top of the **Remote Agents** tab's list, with no pairing, no separate installer, and no enable step. Choose **Back up a folder on this PC…**, pick a folder, and it appears as a Backup Set you can send to any target exactly like a paired Source's Backup Set. Storage runs the bundled `restic` directly against the local folder when the mapped target becomes ready - no network hop, no certificates, no repository password to manage.

Use **Remove folder** to stop backing up a folder this way; its mappings are removed with it. This is unrelated to the standalone Windows Remote Agent described below, which is for a *different* PC with no Storage Agent of its own.

## 4. Install and configure a Linux Remote Agent

Copy `BackupMesh-Source-linux-x64` to the Linux machine and run:

```sh
sudo sh install.sh
sudoedit /etc/backupmesh/backupmesh.json
```

Define each Backup Set with a user-facing name, source paths, and optional include/exclude patterns. The Remote Agent generates stable Agent and Backup Set UUIDs automatically and preserves them in an owner-only `*.state.json` file next to the configuration. Do not edit or copy IDs between Sources. Validate the file:

The Remote Agent accepts strict JSON (`.json`) and YAML (`.yaml` or `.yml`). A Backup Set's `paths` list may contain any number of files or directories; see `source-agent/example.config.yaml` for a multi-path example. Unknown YAML and JSON fields are rejected so spelling mistakes cannot silently disable a setting.

```sh
sudo /opt/backupmesh/backupmesh-agent validate \
  -config /etc/backupmesh/backupmesh.json
```

The installer creates `/etc/backupmesh/restic-password` with owner-only permissions. Make a protected recovery copy. Losing this password makes the encrypted snapshots unrecoverable.

Running `install.sh` from an interactive terminal (rather than a script) prompts for an Agent name and a first Backup Set instead of leaving a generic template to edit by hand, and offers to run `pair` immediately afterward.

## 4b. Install a Windows Remote Agent on a different PC

Use this when a *separate* Windows PC (with no Storage Agent of its own) should back up to a Storage Agent running elsewhere on the network — for example, a laptop backing up to a Storage PC in another room. To back up the Storage Agent's own PC, use **This PC** in the tray instead (section 3b) — no installer needed at all.

Run `BackupMesh-Source-0.3.3-win-x64-Setup.exe` on that PC. Unlike the Storage installer, it never asks for administrator rights: it installs under your own user profile and, right after copying files, opens a console window asking for an Agent name and a first Backup Set path to write a minimal `backupmesh.yaml` (add more `backupSets` entries by hand any time). It also registers a per-user Scheduled Task that keeps the Remote Agent watching in the background, and an uninstaller that removes the task and binaries while keeping your configuration, paired identity, and repository password.

For scripted or troubleshooting use, the underlying package and installer script remain available directly:

```powershell
Set-Location artifacts\BackupMesh-Source-win-x64
.\Install-BackupMeshSource.ps1
```

Choose **Copy connection invitation** in the Storage app, run this command, and paste the invitation when prompted:

```powershell
& "$env:LOCALAPPDATA\BackupMesh\Source\backupmesh-agent.exe" pair `
  -config "$env:LOCALAPPDATA\BackupMesh\Source\backupmesh.yaml"
```

`Uninstall-BackupMeshSource.ps1` removes the scheduled task and binaries while keeping the configuration, paired identity, and repository password under `%LOCALAPPDATA%\BackupMesh\Source`.

## 5. Pair the Source

On the **Remote Agents** tab, choose **Pair a Remote Agent**, then **Copy connection invitation**. Run the command below on the Remote Agent and paste the invitation when prompted. It expires after ten minutes and works once:

```sh
sudo /opt/backupmesh/backupmesh-agent pair \
  -config /etc/backupmesh/backupmesh.json \
  -output /etc/backupmesh/pairing
```

The Source verifies the pinned fingerprint before sending the code, then installs an identity-bound token, client certificate, private key, and pinned Storage certificate with owner-only permissions. No private key is placed in a transfer file and no certificate is added to the operating-system trust store.

If a Remote Agent loses its private key or certificate (for example, its `pairing` directory was deleted), select it in the **Remote Agents** tab's list and choose **Re-pair** instead of **Pair a Remote Agent**. That code can only reissue credentials for that specific, already-known Source — it cannot be used to create a new one or claim a different Source's identity.

Start the Source command watcher:

```sh
sudo systemctl enable --now backupmesh-source-watch.service
sudo systemctl status backupmesh-source-watch.service
```

## 6. Map Backup Sets to destinations

After the Source synchronizes, open **Backups** in the tray app.

1. Under **What to back up**, select a Backup Set — synced from a paired Remote Agent, or a local folder added from **This PC** on the **Remote Agents** tab (section 3b).
2. Under **Where to store it**, select a connected drive or choose **Choose folder…**.
3. Confirm or edit the complete path under **Full destination path**.
4. Choose **Add backup…**, complete the backup-rule window, and select **Add backup**. Double-click an existing row to edit the same settings later. BackupMesh rejects an identical source, target device, and target-folder combination instead of creating a duplicate rule.

Mappings are many-to-many. Multiple Sources can use separate folders or a shared parent on one device, and one Backup Set can be copied to multiple devices. Use a distinct repository subfolder for each independent Backup Set unless intentional repository sharing has been tested.

Source and Storage Agents may run on the same computer by using the Storage Agent's local HTTPS endpoint. Local fixed drives and registered folders are valid destination devices, not only USB media. This supports both local-data-to-external-storage and external-source-to-local-storage layouts. For the latter, register the external source volume with Storage as a device. Storage detects its arrival, finds Backup Sets whose source paths are inside that volume, and sends commands for every ready mapped destination. The Remote Agent only executes Storage-authorized commands; it does not own device detection or policy.

## 7. Run and monitor a backup

Connect the target device and wait for the arrival delay set on the **Settings** tab. BackupMesh requests the mapped Source backup automatically. The tray app shows queued/running state, files and bytes processed, progress, result, and the latest successful run on the **Overview** tab. A small status window can also pop up near the tray icon when a backup starts, with progress and a Cancel button — this is on by default and can be turned off in **Settings**. A running job can be cancelled from either place; the Source terminates restic and reports `CANCELLED`.

For a manual Source-side run:

```sh
sudo /opt/backupmesh/backupmesh-agent backup \
  -config /etc/backupmesh/backupmesh.json \
  -set documents \
  -restic /opt/backupmesh/restic
```

Before ejecting removable storage through Windows, confirm that no job targeting it is queued or running.

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
