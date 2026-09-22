# Install BackupMesh

BackupMesh uses a Windows PC for storage and one or more Windows or Ubuntu computers as Remote Agents.

## 1. Install Storage on Windows

Run `BackupMesh-Storage-0.3.7-win-x64-Setup.exe`, accept the license, and choose **Install**.

Open BackupMesh after installation. The installer starts the Storage service and configures Private and Domain network firewall rules for the local subnet.

## 2. Install a Remote Agent on Ubuntu

For releases containing the Ubuntu package (0.3.7+), run this after the matching release assets and bootstrap script are published:

```sh
curl -fsSL https://raw.githubusercontent.com/namioto/backupmesh/main/packaging/linux/get-backupmesh.sh | sh
```

The bootstrap resolves the latest GitHub release, downloads its versioned Linux archive and SHA-256 file, verifies the archive, and starts the existing setup wizard. It keeps terminal input available when invoked through `curl | sh`.

If UFW is installed and active, the installer permits only Storage discovery replies from private or link-local IPv4 networks: UDP source port 7445 to Remote Agent port 7446. Existing UFW policy stays in place.

If you enable UFW after installing BackupMesh, rerun the installer once to add these discovery rules.

Installation opens the setup wizard. Enter each folder you want to back up, using an absolute path such as `/home/alex/Documents`. Press Enter on an empty prompt when finished, then press Enter or type `y` when asked to connect.

In the Windows Storage app, open **Remote Agents**, select the Ubuntu computer under **Nearby computers**, and choose **Request connection**. Confirm that Ubuntu shows the same six-digit number, then enter `y` to approve.

Rerun setup later to add folders or finish pairing:

```sh
sudo backupmesh-setup
```

The command preserves existing settings and restarts the watcher after a successful change. Keep a protected recovery copy of `/etc/backupmesh/restic-password`; encrypted backups cannot be restored without it.

After pairing, open **Backups → Add backup** in Windows. Choose the Ubuntu folder, select a destination drive or folder, and save the rule. Pairing alone does not create a backup destination.

### Upgrade an Ubuntu Remote Agent

When upgrading from 0.3.7, run the 0.3.8 installer again. It preserves configuration, pairing files, and the repository password, adds the narrow UFW discovery rules when UFW is active, and restarts the watcher. Then run `sudo backupmesh-setup` to finish pairing or add folders.

## 3. Install a Remote Agent on Windows

On another Windows PC, run `BackupMesh-Source-0.3.7-win-x64-Setup.exe`. It installs for the current user, creates an empty JSON configuration, and starts a watcher at sign-in. It does not open an interactive setup console.

Request the connection from **Nearby computers** in the Storage app. Compare the six-digit number and approve on the Remote Agent PC. Add backup folders in its JSON configuration under `%LOCALAPPDATA%\BackupMesh\Source\backupmesh.json`.

## Advanced fallback

To install a downloaded archive manually:

```sh
tar -xzf BackupMesh-Source-0.3.8-linux-x64.tar.gz
cd BackupMesh-Source-linux-x64
sudo sh install.sh
```

If UDP 7445 broadcast is blocked, choose **Copy connection invitation** in the Storage app and run `backupmesh-agent pair`. Manual JSON/YAML configuration and command details are in the [user guide](USER_GUIDE.md).
