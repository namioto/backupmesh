# Changelog

All notable changes to BackupMesh are documented in this file.

## [Unreleased]

### Security

- `/pairing/exchange` no longer accepts an arbitrary caller-supplied `agent_id`. A one-time code either mints a brand new Source identity, or - only when the tray's new **Re-pair** action created it - reissues credentials for one specific, already-paired Source. Previously any valid code could claim an unrelated Source's `agent_id` (visible in the tray's Connections list, not a secret) and take over its identity and catalog.
- `/pairing/exchange`'s per-address failure table is now pruned as it grows, so a remote attacker sending one invalid code from each address in a large IPv6 range can no longer grow it without bound; an address's lockout now survives its counting window rolling over.
- A malformed line in the revoked-agents file is now skipped with a logged warning instead of crashing the control API's dependency injection and returning 500 to every request, including the tray's own.

### Fixed

- The Source Agent no longer rewrites `<config>.state.json` on every load when nothing changed. Combined with the systemd units' `ProtectSystem=strict`, this previously made `watch` and `backup` fail outright once `/etc` became read-only.
- The tray's **Revoke**/**Restore access** actions no longer disappear silently on a timeout; a timed-out request now reports an error on the footer status like other tray actions.

### Added

- A **Re-pair** action in the tray's Connections list issues a one-time code scoped to reissuing credentials for that specific Source only - for recovering a Source that lost its private key or certificate, without exposing every other paired Source to identity takeover.
- The Source Agent renews its own mTLS client certificate about 30 days before it expires, authenticated with its current certificate and token - no one-time code, tray interaction, or unbounded key reuse required. Checked at the start of every `backup` run and once a day while `watch` is running.
- The tray's Connections list shows each Source's certificate expiry, and lets you **Rename** its display name (cosmetic only - the Source keeps reporting its own name) or **Forget** it (revokes it and removes it from the list; its Backup Set mappings are kept but stop being reported until it is re-paired, rather than being deleted).
- A **Rotate Storage identity** action (Settings tab) regenerates the Storage pairing CA and server certificate for recovery from a suspected key compromise. It only takes effect after a service restart, after which every paired Source must be re-paired - existing agent_id, catalog, and revocation state are preserved.

## [0.1.1] - 2026-08-30

### Added

- Strict YAML Source Agent configuration with multiple source paths, alongside the existing JSON format.
- Storage-owned source-volume arrival detection so an external source drive can automatically back up to ready local or removable destinations without moving policy into the Source Agent.
- Ten-minute, single-use Source pairing codes with explicit Storage certificate fingerprint pinning and mTLS credential issuance; the tray no longer exports a private-key bundle for new pairings.
- Automatic persistent Agent and Backup Set identities, removing UUIDs from user-authored YAML.
- Automatic, DPAPI-protected repository password generation on pairing, so a user is never asked to create or manage a restic password themselves on Windows; existing manually created password files keep working unchanged.
- A Source Agent connection management API (list, revoke, unrevoke) and a tray Connections view, so a lost or decommissioned Source can be cut off immediately instead of waiting for its certificate to expire or the CA to rotate.

- A bilingual Inno Setup wizard that installs the Windows service, tray app, bundled tools, firewall rules, automatic startup, Start menu shortcuts, and uninstaller from one executable.
- A reproducible `build-windows-installer.ps1` entry point with package and version validation.

### Security

- `/pairing/exchange` now locks out a remote address for ten minutes after five invalid, expired, or reused pairing codes, and every pairing attempt's outcome (remote address, agent id/name; never the code, bearer credential, or private key) is logged.
- The tray no longer copies pairing code/endpoint/fingerprint details to the clipboard automatically; a dedicated "Pair Source Agent" dialog shows them read-only behind an explicit "Copy to clipboard" button with a clipboard-exposure warning.

### Deprecated

- The file-bundle Source pairing path (`backupmesh-agent apply-pairing` and the Storage `/pairing/credential` API) is superseded by the one-time-code pairing above. It is kept for one release as a migration-only path and will be removed after 0.1.1 — switch to `backupmesh-agent pair` and the tray's pairing dialog.

### Fixed

- Windows package builds now clean their exact artifacts directory before publishing, preventing stale files and nested license directories from leaking into later installers.
- `backupmesh-agent pair`/`apply-pairing`/`validate` no longer require a config's Storage connection fields (endpoint, repository password) to already be filled in, so a freshly authored config can be paired and validated before those fields exist.
- Identity resolution no longer guesses when a Backup Set is renamed and has its paths changed at the same time in a way that matches two different previously known backup sets; it now reports the ambiguity instead of silently picking one.
- The tray's drive and device pickers no longer expose internal identifiers (volume serials, StableId) to screen readers as the accessibility name; only the display name shown on screen is read aloud.
- **The Windows installer could not create or start the service on a real machine at all.** `sc.exe create`'s quoted binPath argument was mangled by PowerShell's native-command quoting (exit 1639); replaced with `New-Service`. The service then crashed on first start with a DPAPI `CryptographicException` because it ran as LocalSystem while using `DataProtectionScope.CurrentUser`; switched to `DataProtectionScope.LocalMachine`. Found and verified by an actual UAC-approved install.
- Uninstalling (or upgrading over) a running install left the tray app's own files behind, because the app hides to the tray instead of exiting on a close request and so never released its file locks; uninstall and upgrade now force it to exit first.
- Storage no longer treats a remote (e.g. Linux) Source's POSIX backup-set paths as a local device arrival; only paths that already look like Windows paths are matched.

## [0.1.0] - 2026-08-29

First end-to-end MVP release.

### Added

- Linux Source Agent with Backup Set catalog synchronization, Storage command watching, progress reporting, cancellation, and concurrent multi-target restic backups.
- Windows Storage Agent service and tray app with fixed, removable, and folder-backed device registration.
- Many-to-many Source Agent, Backup Set, device, and destination-folder mappings.
- Per-device arrival delay, device notifications, job monitoring, and safe-eject coordination.
- Self-contained Windows and Linux packages with pinned restic 0.19.1 and rest-server 0.14.0 binaries.
- English and Korean README and user guides covering installation, pairing, backup, restoration, and troubleshooting.

### Security

- Identity-bound Source pairing with a private CA, mutual TLS, pinned Storage certificates, and independent bearer credentials.
- TLS-protected rest-server transport, encrypted restic repositories, append-only repository access, and protected ephemeral key handling.
- Restricted Windows service-data permissions and owner-only Linux credential files.

### Verified

- Real-file backup to two folder-backed targets followed by restoration from both repositories and SHA-256 content comparison.
- Windows package build, Linux package build and installation under Ubuntu, .NET tests, Go tests and vet, protocol validation, and systemd unit validation.

[0.1.0]: https://github.com/namioto/backupmesh/releases/tag/v0.1.0
[Unreleased]: https://github.com/namioto/backupmesh/compare/v0.1.0...HEAD
