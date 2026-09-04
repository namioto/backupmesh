# Changelog

All notable changes to BackupMesh are documented in this file.

## [Unreleased]

## [0.2.0] - 2026-08-31

### Upgrading from 0.1.1

- Backup history created before this release has no destination-mapping identifier. It remains in the job list but is ignored by mapping-specific status and safe-removal checks.
- Finished job history is capped at the 20 most recent jobs per destination mapping; running jobs are always retained.
- Per-device arrival delays are replaced by one global default. Saving configuration updates existing devices to that value.
- Pairing, credentials, and certificate handling are unchanged.

### Added

- Local backups from the Storage PC without a separate Source Agent.
- Explicit trigger devices and any/all availability policies for Backup Sets.
- Source Agent certificate renewal, re-pairing, rename, forget, revoke, and restore-access actions.
- Storage identity rotation for recovery from key compromise.
- A non-administrator Windows Source Agent installer and guided Linux Source setup.
- A tray status flyout with backup progress, cancellation, Start now, and Skip this time actions.
- A cross-tab banner that distinguishes backing up, completed, and incomplete safe-removal states.

### Changed

- The tray uses four task-oriented tabs: Overview, Backups, Source Agents, and Settings.
- Device registration is available inline while creating a backup; connected storage status and removal actions appear on Overview.
- The Backups grid shows Source, Source folder, Target device, Target folder, Last backup, and Enabled state.
- Arrival delay is configured globally in Settings.
- Source connection status uses relative last-seen times and actionable recovery messages.
- Job history persists across service restarts and is pruned per destination mapping.

### Security

- Pairing codes can only mint a new identity or reissue credentials for the Source explicitly selected by Re-pair.
- Pairing failure tracking is bounded and retains lockout state across counting-window rollover.
- Malformed revoked-agent records are skipped with a warning instead of preventing API startup.

### Fixed

- Source state files are no longer rewritten when their contents have not changed.
- Timed-out Revoke and Restore access actions report an error instead of disappearing silently.
- Disabled mappings are restored correctly after Skip this time, including across tray restarts.
- Tray flyout actions now invoke the Storage Service, pending decisions remain visible, and progress-only notifications do not steal focus.
- Long grid values use ellipsis and tooltips instead of clipping mid-character.
- Drive-picker accessibility names no longer expose internal identifiers.
- Storage configuration validation rejects ambiguous or invalid device mappings before persistence.

## [0.1.1] - 2026-08-30

### Added

- Strict YAML Source Agent configuration with multiple source paths, alongside the existing JSON format.
- Storage-owned source-volume arrival detection so an external source drive can automatically back up to ready local or removable destinations without moving policy into the Source Agent.
- Ten-minute, single-use Source pairing codes with explicit Storage certificate fingerprint pinning and mTLS credential issuance; the tray no longer exports a private-key bundle for new pairings.
- Automatic persistent Agent and Backup Set identities, removing UUIDs from user-authored YAML.
- Automatic, DPAPI-protected repository password generation on pairing; existing manually created password files continue to work.
- Source Agent connection management APIs and tray actions for listing, revoking, and restoring access.
- A bilingual Windows installer for the service, tray app, bundled tools, firewall rules, startup, shortcuts, and uninstaller.
- Reproducible Windows package builds with package and version validation.

### Security

- `/pairing/exchange` locks out an address for ten minutes after five invalid, expired, or reused codes and logs outcomes without secrets.
- Pairing details are copied only through an explicit action with a clipboard-exposure warning.

### Deprecated

- File-bundle pairing (`backupmesh-agent apply-pairing` and `/pairing/credential`) is retained for one migration release. Use `backupmesh-agent pair` and the tray pairing dialog.

### Fixed

- Windows package builds clean their exact artifacts directory before publishing.
- Pairing and validation work before Storage connection fields are populated.
- Ambiguous Backup Set identity changes are rejected instead of guessed.
- Screen readers receive display names rather than internal volume identifiers.
- Windows service installation uses `New-Service`, and LocalMachine DPAPI permits LocalSystem startup.
- Upgrade and uninstall stop the tray before replacing or removing locked files.
- Remote POSIX Source paths are not treated as local Windows device arrivals.

## [0.1.0] - 2026-08-29

First end-to-end MVP release.

### Added

- Linux Source Agent with catalog synchronization, command watching, progress reporting, cancellation, and concurrent multi-target restic backups.
- Windows Storage Agent service and tray app with fixed, removable, and folder-backed device registration.
- Many-to-many Source Agent, Backup Set, device, and destination-folder mappings.
- Per-device arrival delay, notifications, job monitoring, and safe-eject coordination.
- Self-contained Windows and Linux packages with pinned restic 0.19.1 and rest-server 0.14.0 binaries.
- English and Korean installation, pairing, backup, restoration, and troubleshooting guides.

### Security

- Identity-bound Source pairing with a private CA, mutual TLS, pinned Storage certificates, and independent bearer credentials.
- TLS-protected rest-server transport, encrypted restic repositories, append-only repository access, and protected ephemeral key handling.
- Restricted Windows service-data permissions and owner-only Linux credential files.

### Verified

- Real-file backup to two targets followed by restoration and SHA-256 comparison.
- Windows and Linux package builds, .NET and Go tests, protocol validation, and systemd unit validation.

[0.1.0]: https://github.com/namioto/backupmesh/releases/tag/v0.1.0
[0.1.1]: https://github.com/namioto/backupmesh/compare/v0.1.0...v0.1.1
[0.2.0]: https://github.com/namioto/backupmesh/compare/v0.1.1...v0.2.0
[Unreleased]: https://github.com/namioto/backupmesh/compare/v0.2.0...HEAD
