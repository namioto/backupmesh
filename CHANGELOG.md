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
- Backup Sets can now name explicit **trigger devices** and an availability policy (any one, or all at once) instead of Storage silently inferring a source arrival from path containment. The Devices tab shows each device's role (Target / Source trigger / both / unassigned) so this is never left for the user to guess, and a device used only as a trigger no longer needs a backup target mapping of its own. Backup Sets without an explicit trigger keep the previous inferred behavior unchanged.
- Storage can now back up local paths on its own PC directly, with no Source Agent, pairing, or network hop at all: **This PC** always appears as a Source in the tray, and **Add local Backup Set…** maps a folder straight to any registered target device. A well-known "This PC" Source identity runs the bundled `restic` binary in-process against the resolved local target folder as soon as the mapped device is ready, reusing the existing job/command tracking so progress, cancellation, and history work identically to a remote Source's backups. Verified with a real backup and restore of real file content through the bundled restic binary.
- A standalone Windows Source Agent installer (`build-windows-source-installer.ps1`, wrapping `Install-BackupMeshSource.ps1`) for a Windows PC that is a Source only, with no Storage Agent of its own - for example, a laptop backing up to a Storage PC elsewhere on the network. It installs entirely under `%LOCALAPPDATA%` with a per-user Scheduled Task and needs no administrator rights. Not needed, and not used, for backing up the Storage Agent's own PC - see "This PC" above. Verified with a real pairing and catalog sync against a live Storage Service on this machine; the Scheduled Task registration itself has not been exercised through a full install run in this session.
- `install.sh` now prompts interactively for an Agent name and first Backup Set (writing a minimal YAML config) and offers to run `pair` immediately, instead of only leaving a generic template to edit by hand; unattended installs are unaffected.

### Changed

Tray UX pass based on a design review of real screenshots:

- The Settings tab no longer clips **Rotate Storage identity** off the bottom of the window - its content now scrolls.
- Destructive/access-reducing actions (**Revoke**, **Forget** a Source, **Remove local Backup Set**, **Forget** a device, **Rotate Storage identity**) are now visually distinct (red) from safe actions, which consistently use the accent color as their one primary action per group.
- The Connections panel's **Revoke/Restore access/Re-pair/Rename/Forget** buttons are disabled with nothing selected, and selecting a Source in the tree above (including "This PC", which has no certificate or connection to act on) now clears a stale Connections-grid selection instead of leaving its buttons looking active for an unrelated row.
- Empty grids (Devices, Backup jobs, Backup targets, Connections) show a one-line hint instead of just a blank grid.
- Consistent "Backup Set" capitalization throughout the tray; the Trigger devices explainer is shorter, with the technical caveat moved into a tooltip.

Terminology pass based on a blind first-click usability study (tab names and `AutomationId`s kept unchanged - measured to already be as good as several redesigned alternatives; the wording *inside* each tab is what evaluators actually struggled with):

- "Source Agent" is now called a **computer** throughout the tray (buttons, dialogs, notifications, footer messages); the Connections grid's **Revoke**/**Forget** actions are now **Block access**/**Remove computer**, each with a confirmation dialog (Block access previously had none) that states plainly that nothing is deleted and access can be restored or re-paired later.
- The Devices tab's **Forget selected device** is now **Stop using this drive**, with a new confirmation dialog stating that backup data already on the drive is not deleted.
- "Backup targets" is now **Backups**, its "Source / Backup Set" column is **What to back up**, and its Add/Remove actions read "Add backup"/"Remove" instead of "Add mapping"/"Remove selected".
- The Trigger devices group is retitled to name the Backup Set it's currently editing (e.g. "Start automatically for: Home Server / Photos") since evaluators had no way to tell what was selected; its explainer and the device Role column now talk about a "drive" "connecting" instead of a device "arriving".
- The header's connected-device count badge could show stale text for up to 3 seconds after forgetting the only registered device (it only refreshed on the next drive-poll tick); it now updates immediately with every action that changes device count.
- Each of the Sources & mappings and Devices tabs now opens with a one-line explanation of what it's for.

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
