# BackupMesh 기여 가이드

**한국어** | [English](CONTRIBUTING.md) | [README](README.ko.md)

설치, 페어링, 복구 방법은 [사용자 가이드](docs/USER_GUIDE.ko.md)를 참고하세요.

## 개발 환경

WPF 스토리지 에이전트와 Windows 설치 프로그램은 Windows에서 빌드합니다. Git, PowerShell 7, .NET 9 SDK, Go 1.23 이상이 필요합니다. Windows 설치 프로그램을 만들려면 Inno Setup 6도 설치하세요. 패키지 스크립트가 고정 버전의 외부 도구를 내려받으므로 의존성 복원과 최초 빌드에는 네트워크 연결이 필요합니다.

별도 안내가 없으면 저장소 루트에서 명령을 실행하세요.

## 패키지 빌드

스토리지 에이전트 앱과 서비스를 포함한 자체 포함 설치 프로그램을 만듭니다.

```powershell
pwsh -NoProfile -File scripts/build-windows-installer.ps1
```

설치 프로그램과 SHA-256 체크섬은 `artifacts/installer`에 생성됩니다. 파일명에는 `VERSION`의 버전이 포함됩니다. 설치 대상 장비에는 .NET SDK가 필요하지 않습니다.

개발용 패키지만 만들려면 다음 명령을 실행합니다.

```powershell
pwsh -NoProfile -File scripts/build-windows-test-package.ps1
```

임시 실행에는 `artifacts/BackupMesh-Storage-win-x64/Start-BackupMesh.ps1`을 사용합니다. 런처는 서비스 준비를 기다린 뒤 트레이 앱을 열고, 앱을 닫으면 시험용 서비스도 종료합니다. 앱 설정은 `%LOCALAPPDATA%/BackupMesh`에 저장됩니다. 설치된 서비스와 충돌하지 않도록 시험 환경을 사용하세요.

필요한 원격 에이전트 패키지를 빌드합니다.

```powershell
pwsh -NoProfile -File scripts/build-linux-source-package.ps1
pwsh -NoProfile -File scripts/build-windows-source-package.ps1
pwsh -NoProfile -File scripts/build-windows-source-installer.ps1
```

패키지 디렉터리는 `artifacts/BackupMesh-Source-linux-x64`, `artifacts/BackupMesh-Source-win-x64`이며 Windows 설치 프로그램은 `artifacts/installer`에 생성됩니다. 기존 `source-agent` 경로와 `BackupMesh-Source` 산출물 이름은 원격 에이전트를 가리킵니다.

## 테스트

스토리지 에이전트 테스트는 Windows에서 실행합니다.

```powershell
dotnet test storage-agent/tests/BackupMesh.Storage.Tests/BackupMesh.Storage.Tests.csproj
pwsh -NoProfile -File scripts/test-settings-cleanup.ps1
```

원격 에이전트 테스트는 해당 모듈 디렉터리에서 실행합니다.

```powershell
Push-Location source-agent
go test ./...
Pop-Location
```

Windows 개발용 패키지를 만든 뒤 폴더 저장 대상을 사용해 실제 백업과 복원을 검증합니다.

```powershell
pwsh -NoProfile -File scripts/test-local-e2e.ps1 -FolderTargets
```

통합 테스트는 포트 7444가 비어 있어야 하며 `artifacts` 아래의 시험 데이터를 사용합니다. 하드웨어에 의존하는 동작은 실제 대상 장치에서 별도로 검증하세요.

## 변경 기여

변경 범위를 좁게 유지하고 PR에 문제, 변경 후 동작, 검증 결과를 설명하세요. 동작 변경에는 회귀 테스트를 추가하고 실행하지 못한 검사도 명시하세요. 영어·한국어 문서와 UI 리소스를 함께 갱신하세요. [다국어 관리 문서](docs/localization.md)를 참고할 수 있습니다.

기존 설정과 프로토콜 필드의 호환성을 유지하거나 명시적인 마이그레이션을 제공하세요. 자격 증명, 페어링 자료, 개인 키, 실제 백업 데이터는 커밋하지 마세요.

릴리스 시 `VERSION`, .NET 버전 속성, 원격 에이전트 버전, 설치 프로그램 기본 버전, 변경 이력을 함께 갱신하세요. 패키지를 다시 빌드하고 포함된 버전과 체크섬을 확인한 뒤 배포하세요.
