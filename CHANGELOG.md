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

The Overview tab's Backup jobs grid could not tell two concurrent jobs apart ("3 rows but no way to tell which is which"), found in the same study:

- Jobs now show which Backup Set is going to which device (e.g. `Home Server / Photos → Archive drive`), and an estimated time remaining once enough progress has been reported. `JobStatus` gained optional `target_mapping_id`, `source_agent_id`, and `started_at` fields (existing required fields unchanged) so this can be resolved without a second request.
- The Storage Service now keeps at most the 20 most recent finished jobs per destination mapping (every still-running job is always kept); job history previously grew without bound, and every progress update of any job rewrote the entire history to disk.

A follow-up round of the same study, re-run against the new wording, confirmed each replaced term measured as understood; it also surfaced a few strings this pass had missed:

- The Sources & mappings tab had two buttons labeled just "Remove", one deleting a local folder's backup and the other a device mapping, distinguishable only by position; they now read "Remove folder" and "Remove backup".
- "Device" and "drive" were used interchangeably for the same concept (Trigger devices' explainer used "drive", its own checkbox and the Devices tab's grid used "device"); everything about a *registered* device now consistently says "device" ("drive" is kept only where the tray is listing physically detected drives that aren't registered yet, e.g. "Refresh drives").
- The destination-device picker now shows "Registered in the Devices tab" beneath it while empty, until inline registration (tracked separately) removes the need to leave the tab at all.

A follow-up study (14 blind measurements across two proposed designs plus the original) found a task built entirely around it went unused by every evaluator (0/4), and that pairing a tree with a separate table for the same computers created its own ambiguity - a destructive action's target ("does this apply to the computer or the folder selected under it?") was unclear to both evaluators who saw it:

- The Sources & mappings tab's computer tree and its separate Connections table are now one grid. Selecting a Backup Set (never done through the tree in the study) works the same as before, through the "What to back up" list, which now says where its entries come from ("Folders shared by the computers on the left").
- "This PC" appears in the grid like any other computer, but its Status cell now reads "—" rather than "This PC" - a name is not a status, and evaluators read that placeholder as a broken value. Selecting "This PC" (or a computer that has never connected) now shows a plain-text reason its Block access/Re-pair/Rename/Remove computer buttons are disabled, not just a tooltip.
- The "Certificate expires" column read as unexplained technical noise to every evaluator who saw it (4/4: "why does connecting a computer involve a certificate?") even when no certificate needed attention. It's gone; a computer's Allowed/Revoked status now says so directly only when its certificate is actually expiring within 30 days (e.g. "Allowed — certificate expires in 5 days").
- A registered device's name now keeps the drive letter and free space it showed before registration (e.g. "Archive drive (E:\), 465.8 GB free") instead of reverting to a plain name evaluators couldn't confirm was the right physical drive; the Devices grid also gained its own "Free space" column.
- Demo mode (used by this study and by UiTests) no longer contradicts itself: paired demo computers now appear in Connections too, instead of the tree showing computers with Backup Sets while Connections insisted none were paired.

A second re-measurement against the merged grid's real wording found real improvement (device/computer confusion resolved, the core "back up this folder to that drive" task succeeded for both evaluators), and surfaced why "Certificate expires"/"Allowed" felt like unexplained noise: the Source Agent already renews its own certificate starting 30 days before expiry (checked at the start of every backup and once a day while watching), so a computer that connects at all essentially never needs a person's attention for this - the tray was showing an always-present, mostly-irrelevant countdown for something already being handled automatically:

- A computer's status is now "Connected" (not "Allowed") in the normal case, and only calls out its certificate when the computer hasn't been seen since its 30-day renewal window actually opened - meaning it has genuinely missed its own chance to renew - as "Offline — re-pair before {date}" (or "Expired — re-pair to reconnect" past that date). Both name the exact action (Re-pair) that resolves them, since evaluators could infer what Block access/Remove computer meant but not Re-pair.
- "Last seen" is relative ("4 minutes ago") instead of an absolute timestamp evaluators had to do the arithmetic on themselves to answer "is this connected right now?" - a question "Allowed"/"Revoked" (a permission, not a connection state) couldn't actually answer.
- Adding or removing a backup, or changing which devices trigger one, now saves immediately instead of leaving the user responsible for remembering the Settings tab's Save button afterward, or losing the change entirely if they navigate away first.
- "Require all selected devices at once" only appears once two or more devices are actually selected, instead of always being present with no devices selected, where the premise it assumes plainly doesn't hold.

A third pass over the same wording caught that "Connected" (the previous fix's replacement for "Allowed") wasn't actually true for a computer that has been off for months but isn't yet at risk of missing a certificate renewal - it just always showed "Connected" once the certificate checks passed. Status now reflects a computer's real connectivity (last seen within 2 minutes, matching the Source Agent's default 5-second watch poll interval with generous slack) as its own "Connected"/"Offline", independent of the certificate-renewal check:

- Registering a folder as a storage device now saves immediately, matching every other configuration change in this pass; it was the one path still requiring a separate trip to the Settings tab's Save button after the others no longer did.
- Singular/plural wording ("1 device connected" vs "2 devices connected", "1 minute ago" vs "4 minutes ago") no longer always uses the plural form regardless of count.
- Two more `(s)` strings the wording pass had missed ("Synchronized N computer(s)", "Queued N mapped backup target(s)") are fixed the same way.

The "Trigger devices" group (now "Start automatically for: ...") was still bound to the "what to back up" combo used to *create* a backup, sitting visually *below* the button that does the creating - a study found evaluators couldn't tell whether it configured something being created or something already added, and, once told it was the latter, couldn't tell whether it applied to just the selected row or every destination sharing that row's Backup Set:

- It's now bound to whichever row is selected in the Backups grid instead, with a placeholder ("Select a backup above...") when nothing is selected there.
- Selecting a row now lightly highlights every other row that shares its Backup Set, since trigger settings apply per Backup Set, not per destination - a second, visible cue for that fact alongside the existing explanatory sentence (shown only when there is another row to be confused about; verified both evaluators would have misread the scope without the sentence, so it stays as a second cue, not a replacement).
- A "Saves to {device}" line now precedes "choose which devices should start it automatically", so the destination and the trigger are named as two different things rather than presented back-to-back with no distinction.
- "Require all selected devices at once" (a checkbox that couldn't say what "unchecked" meant) is now two mutually exclusive radio buttons naming both states directly: "Start when any of these devices connects" / "Start only when all of them are connected".
- Caught and fixed a bug in the same-day-added "applies to every backup of X" notice before it shipped: the property returned that text whenever any row was selected, not only when the Backup Set actually had more than one destination - masked in the running app by the XAML visibility binding on the same condition, but wrong at the view-model level and would have leaked through anything binding to it directly.

Two follow-ups to that change: splitting "Home Server / Photos already saves to Archive drive…" into two shorter lines dropped the word "already", which measurably reintroduced the "is this configuring something new or something existing?" confusion that sentence had fixed - restored ("Already saves to {device}."). Separately, replacing a checkbox with two radio buttons removed the one reachability test covering that area of the screen without replacing it, leaving newly-added controls (the radio buttons, the list box only shown once a backup is selected) with no automated guard against ending up present in XAML but not actually reachable on screen - which is exactly how an earlier defect in this same tab went undetected until a person looked at a screenshot. Added a test that creates a real backup, selects it, and asserts all four trigger controls are reachable, cleaning up after itself so it leaves the shared test fixture in the state other tests expect regardless of run order.

Every measured tab arrangement (18 first-click measurements across 6 candidate layouts) failed the same task: confirm a backup finished, then safely remove the drive. The reason didn't depend on tab names or organization - confirmation and the removal action simply live in different places, so evaluators expected to cross tabs to do either. README already promises "you never need to guess when it's safe to remove a storage device"; the UI didn't keep that promise:

- A banner now appears above the tabs - visible regardless of which one is open - for any connected, ejectable device that has finished a backup since it was last connected: "{device} — all backups finished. Safe to remove now." with a **Remove safely** button that ejects it directly, without switching to the Devices tab.
- While a backup to that device is still running, the same line instead reads "{device} — backing up now. Do not remove." with no button - the wording swaps in place rather than the banner disappearing or a second warning banner appearing, so there's never a moment where the line is silent about an active job.
- If the most recent backup to a finished device failed or was cancelled, the banner says so instead of overclaiming: "{device} — backup did not finish. Safe to remove, but nothing new was saved." Removal is still offered, since nothing is actively writing to the device either way.
- Deliberately conservative: a device already connected when the tray started has no known connection time, so a finished-backup banner never appears for it even if a job completes during this session, and job history from a previous connection (persisted job history can span days) never counts toward "just finished" for a newly reconnected device - only a job that started at or after the *current* connection is considered. A quieter, occasionally-absent banner was judged the safer failure mode than a wrong "safe to remove" claim.
- The Devices tab's own "Safely remove selected device" button is unchanged; the banner is a discoverability aid, not a replacement.
- Multiple qualifying devices each get their own banner line.
- The tray's eject failure message now distinguishes *why* Storage refused: "a backup is still active, wait" (`STORAGE_BUSY`) reads differently from "Windows itself refused, e.g. a file is still open" (`EJECT_REFUSED`), since the two need different next actions from the user - previously both showed the same generic `HttpRequestException` text with no explanation at all.

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
