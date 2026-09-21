# Contributing to BackupMesh

[한국어](CONTRIBUTING.ko.md) | **English** | [README](README.md)

For installation, pairing, and recovery instructions, see the [user guide](docs/USER_GUIDE.md).

## Development environment

Use Windows for the WPF Storage Agent and installer builds. Install Git, PowerShell 7, the .NET 9 SDK, and Go 1.23 or later. Windows installer builds also require Inno Setup 6. Package scripts download pinned third-party tools; dependency restoration and initial builds require network access.

Run the following commands from the repository root unless otherwise indicated.

## Build packages

Build the Storage Agent installer, including its self-contained app and service:

```powershell
pwsh -NoProfile -File scripts/build-windows-installer.ps1
```

The installer and SHA-256 checksum are written to `artifacts/installer`. The filename includes the version from `VERSION`. Target machines do not need the .NET SDK.

To build only the development package:

```powershell
pwsh -NoProfile -File scripts/build-windows-test-package.ps1
```

Run `artifacts/BackupMesh-Storage-win-x64/Start-BackupMesh.ps1` for a temporary local session. The launcher waits for the service, opens the tray app, and stops the test service when the app closes. App settings are kept under `%LOCALAPPDATA%/BackupMesh`. Use a test environment to avoid conflicts with an installed service.

Build Remote Agent packages as needed:

```powershell
pwsh -NoProfile -File scripts/build-linux-source-package.ps1
pwsh -NoProfile -File scripts/build-windows-source-package.ps1
pwsh -NoProfile -File scripts/build-windows-source-installer.ps1
```

Package directories are `artifacts/BackupMesh-Source-linux-x64` and `artifacts/BackupMesh-Source-win-x64`; Windows installers are under `artifacts/installer`. Existing `source-agent` paths and `BackupMesh-Source` artifact names refer to the Remote Agent.

## Tests

Prioritize backup and restore correctness, data preservation, authentication, and meaningful regression coverage. Avoid tests that only repeat getters, exact UI wording, control presence, or cosmetic dimensions. Remove obsolete tests with the behavior they covered.

Build scripts do not run tests automatically. For routine Storage Agent changes, run regression tests on Windows without external tools or hardware enumeration:

```powershell
dotnet test storage-agent/tests/BackupMesh.Storage.Tests/BackupMesh.Storage.Tests.csproj --filter "Category!=Integration"
```

Run Remote Agent tests from its module directory:

```powershell
Push-Location source-agent
go test ./...
Pop-Location
```

Run integration tests when changing backup execution, repository authentication, Windows volume detection, or bundled tools, and before a release. They require Windows and fetched tools; missing prerequisites fail instead of being reported as a pass:

```powershell
pwsh -NoProfile -File scripts/fetch-third-party-tools.ps1
dotnet test storage-agent/tests/BackupMesh.Storage.Tests/BackupMesh.Storage.Tests.csproj --filter "Category=Integration"
```

Run `scripts/test-settings-cleanup.ps1` when changing installer/settings cleanup and before a release. After building the Windows development package, verify the complete backup/restore flow when changing cross-agent behavior and before a release:

```powershell
pwsh -NoProfile -File scripts/test-local-e2e.ps1 -FolderTargets
```

The end-to-end test requires port 7444 to be available and uses test data under `artifacts`. Validate hardware-specific behavior separately on the intended devices.

Omitting `--filter` runs every .NET test. Documentation-only changes need link checks, not a full test run. A test remains useful after implementation when it detects meaningful regressions; do not remove such tests simply because they already passed once.

## Submit a change

Keep changes focused and explain the problem, resulting behavior, and validation in the pull request. Add regression coverage for behavior changes and report any checks that could not run. Keep English and Korean documentation and UI resources aligned; see [localization](docs/localization.md).

Preserve compatibility with existing settings and protocol fields, or provide an explicit migration. Never commit credentials, pairing material, private keys, or real backup data.

For a release, update `VERSION`, .NET version properties, Remote Agent version, installer defaults, and the changelog together. Rebuild the packages and check their embedded versions and generated checksums before distributing them.
