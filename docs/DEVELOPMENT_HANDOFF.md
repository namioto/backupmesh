# BackupMesh 개발 인수인계

> **[아카이브됨 — 2026-08-29 시점 스냅샷]** 이 문서가 작성된 이후 `feat/secure-pairing-redesign` 브랜치에서
> 아래 P0/P1 목록의 pairing 보안 항목, connection 관리 UI, trigger-device 모델이 모두 구현·검증되었고
> (`v0.1.1` 릴리스 이후), 이어서 블라인드 사용성 연구 기반의 트레이 UI 전면 재설계(탭 구조 개편, Devices 탭
> 제거, 트레이 상태 팝업 등, `v0.2.0`)까지 완료되었습니다. 현재 상태의 단일 진실 공급원은
> [`CHANGELOG.md`](../CHANGELOG.md)입니다 — 아래 본문은 그 이전 시점의 계획 기록으로만 참고하세요.
>
> 기준 브랜치: `feat/secure-pairing-redesign`  
> 작성일: 2026-08-29  
> 현재 공개 릴리스: `v0.1.0`  
> 다음 목표(당시): 사용자 편의와 보안을 갖춘 `v0.1.1` end-to-end MVP

## 1. 제품 목표와 변경할 수 없는 원칙

BackupMesh는 평소 분리해 두는 외장 저장장치가 연결되면 적절한 Source의 데이터를 자동으로 암호화 백업하는 오케스트레이터다. Source Agent는 파일을 읽고 백업 엔진을 실행하며, Windows Storage Agent는 장치 감지, 정책, 대상 매핑, repository 제공, 작업 상태와 취소를 소유한다.

다음 원칙은 이후 구현에서도 유지해야 한다.

- **장치 감지와 자동 실행 정책은 Storage Agent의 책임이다.** Source Agent가 자체적으로 매체를 감지하거나 백업 시점을 결정하게 만들지 않는다.
- **Source Agent는 Storage가 인증·승인한 명령만 실행한다.** 임의의 저장 위치나 다른 Source의 작업을 선택할 수 없어야 한다.
- **매핑은 다대다다.** 한 Backup Set을 여러 장치에 동시에 백업할 수 있고, 여러 Source/Backup Set을 한 장치의 서로 다른 하위 폴더에 저장할 수 있어야 한다.
- **USB에 한정하지 않는다.** 고정 드라이브, 이동식 볼륨, 등록 폴더를 모두 장치로 사용할 수 있다.
- **Source와 Storage는 같은 PC에서도 동작해야 한다.** 로컬 데이터→외장장치뿐 아니라 외장 원본→로컬 대상도 지원한다.
- **성공 표시는 실제 성공을 의미해야 한다.** 작은 실제 파일을 백업한 뒤 복원하고 SHA-256이 일치해야 E2E 성공으로 인정한다.
- **사용자 설정에 UUID나 비밀값을 요구하지 않는다.** 내부 ID, 토큰, 개인 키, CA와 repository 암호는 Agent가 생성·보호한다.
- **운영체제 전역 인증서 저장소를 변경하지 않는다.** BackupMesh 자체 신뢰 자료를 애플리케이션 내부에서 고정한다.
- `restic`과 `rest-server`는 고정 버전 바이너리를 제품에 포함하며 해당 BSD 고지를 유지한다. 프로젝트 자체 라이선스는 Apache-2.0이다.

## 2. 저장소와 릴리스 상태

- GitHub: `namioto/backupmesh`
- `main`: Windows installer PR #5까지 병합된 상태다.
- `v0.1.0`: 첫 공개 릴리스다.
- `v0.1.1`: 아직 발행하지 않았다. README와 CHANGELOG에서 Upcoming/Unreleased로 표시한다.
- Windows installer 산출물은 `artifacts/installer/BackupMesh-Storage-0.1.1-win-x64-Setup.exe`로 만들어졌지만, UAC 보안 데스크톱을 직접 승인할 수 없어 관리자 권한 설치·제거 실기 테스트는 남아 있다.
- 현재 작업 브랜치는 `feat/secure-pairing-redesign`이다. `35a656b`는 자동 내부 ID와 Storage 주도 원본 도착 감지를 담은 검증된 중간 커밋이다.
- `35a656b` 이후 pairing-code 관련 변경은 이 문서 작성 시점에 작업 트리에 남아 있다. 다른 변경을 시작하기 전에 `git status`, `git diff`, 테스트 결과를 확인하고 별도 커밋으로 보존한다.

## 3. 이미 구현되고 검증된 기능

### 기존 MVP

- Go Source Agent: catalog 동기화, 명령 watch, restic 백업, 진행률, 취소, 결과 보고, 동시 다중 대상 백업
- .NET Windows Storage Service: 고정/이동식/폴더 장치 감지와 안정 ID, 장치별 arrival delay, topology 보존
- WPF tray app: 장치 등록, 폴더 선택, Source/Backup Set/장치/repository 하위 경로 매핑, 작업 상태와 취소, 안전 제거
- mTLS Control API, Source별 bearer credential, repository TLS와 일시적인 rest-server credential
- Windows installer, Linux Source 패키지, 영문/한글 README와 사용자 가이드

### `35a656b`에서 완료한 내용

- Source 설정은 JSON 외에 strict YAML(`.yaml`, `.yml`)을 지원한다.
- YAML의 `paths`에 여러 파일/디렉터리를 지정할 수 있다.
- 알 수 없는 JSON/YAML 필드는 오류로 처리한다.
- 사용자가 Agent/Backup Set UUID를 입력하지 않아도 자동 생성한다.
- 자동 ID는 설정 옆의 소유자 전용 `*.state.json`에 보존한다. 이름이 바뀌더라도 동일 경로를 기준으로 기존 Backup Set ID를 재사용한다.
- Storage Agent가 새 장치 도착을 감지할 때 다음 두 경우를 모두 처리한다.
  - 새 장치가 목적지인 매핑
  - 새 장치 아래에 Source 경로가 존재하는 Backup Set의 준비된 목적지 매핑
- 준비되지 않은 목적지는 enqueue하지 않고, 동일 매핑 명령은 중복 생성하지 않는다.
- 외장 원본에 해당하는 폴더가 나중에 등장하는 상황을 Storage가 감지하여 같은 PC의 폴더 대상 두 곳에 백업하고, 두 repository를 복원해 SHA-256 일치를 확인했다.

검증 결과:

- Go `go test ./...`, `go vet ./...`, `staticcheck`: 통과
- .NET 테스트: 72개 통과(`35a656b` 기준)
- Storage 주도 source-arrival E2E: 2개 대상 백업/복원/SHA-256 일치
- OpenAPI 구조 검사: 당시 22 operations 통과

## 4. 현재 작업 트리의 pairing 재설계

`35a656b` 이후 다음을 구현했고, pairing-code 기반 E2E까지 한 차례 통과했다. 다만 아래 미완성 항목 때문에 아직 최종 완료로 간주하면 안 된다.

- Storage loopback API에서 160-bit 무작위 1회용 코드를 생성한다.
- 코드는 메모리에 SHA-256 hash만 보관하고 10분 후 만료되며 성공 시 원자적으로 삭제된다.
- Source가 `/pairing/exchange`에 접근하기 전에 tray가 표시한 Storage 서버 인증서 SHA-256 지문을 고정 검증한다.
- 교환 성공 후 Source 전용 bearer credential과 mTLS client certificate를 발급한다.
- 새 `backupmesh-agent pair` 명령을 추가했다.
- tray의 **Pair Source Agent**는 더 이상 개인 키 bundle을 저장하지 않고 endpoint/code/fingerprint를 표시한다.
- 원격 Kestrel endpoint는 pairing bootstrap을 위해 client certificate를 선택적으로 받지만, 일반 Control API는 endpoint filter가 계속 인증서와 Agent ID 일치를 강제한다.
- 기존 `apply-pairing`과 `/pairing/credential`은 마이그레이션 호환용으로 아직 남아 있다.
- 새 pairing 경로를 사용한 folder-target/source-arrival E2E에서 2개 repository 백업·복원·SHA-256 일치가 통과했다.
- .NET 테스트는 pairing session 단위 테스트가 추가되어 74개 통과했다.

## 5. 반드시 이어서 구현하거나 점검할 내용

우선순위 순서다.

### P0 — 현재 pairing 변경을 안전하게 완결

1. 현재 작업 트리에서 다음을 다시 실행한다.

   ```powershell
   cd source-agent
   gofmt -w cmd/backupmesh-agent/main.go internal/config/config.go internal/controlapi/*.go
   go test ./...
   go vet ./...
   go run honnef.co/go/tools/cmd/staticcheck@v0.6.1 ./...

   cd ..\storage-agent
   dotnet build BackupMesh.Storage.sln -c Release
   dotnet test BackupMesh.Storage.sln -c Release --no-build

   cd ..
   .\protocol\validate.ps1
   .\scripts\build-windows-test-package.ps1
   .\scripts\test-local-e2e.ps1 -FolderTargets -AutomaticOnly -SourceArrival
   ```

2. `/pairing/sessions`와 `/pairing/exchange`의 HTTP 통합 테스트를 추가한다. 최소한 다음을 검증한다.

   - session 생성은 loopback에서만 가능
   - 틀린/만료/재사용 code 거부
   - 잘못된 agent ID/name 거부
   - fingerprint 불일치 시 Source가 code를 보내기 전에 종료
   - pairing 없이 일반 원격 API 접근 불가
   - client certificate가 없는 pairing exchange만 예외이고 다른 API는 예외가 아님

3. `ClientCertificateMode.AllowCertificate` 변경으로 mTLS가 약화되지 않았는지 별도 회귀 테스트를 작성한다. 일반 endpoint의 certificate 필수 조건을 Kestrel과 filter 양쪽에서 확인한다.
4. code 입력 실패 rate limiting과 감사 로그를 추가한다. 로그에는 code, token, private key를 절대 기록하지 않는다.
5. 현재 tray가 pairing 정보를 Windows clipboard에 자동 복사한다. clipboard는 다른 프로세스가 읽을 수 있으므로 기본 자동 복사는 제거하고, 명시적인 **Copy** 버튼과 경고가 있는 전용 dialog/화면으로 바꾼다.
6. session 생성은 연결 의사를 사전 승인한 것으로 볼 수 있지만, 더 명확한 UX가 필요하면 Source 이름/OS/IP/fingerprint를 보여주는 pending request와 최종 **Approve/Reject** 단계를 추가한다.
7. 기존 file-bundle pairing은 한 릴리스 동안 migration 메뉴/명령으로만 남기고 일반 UI에서는 숨긴 뒤, 폐기 시점을 CHANGELOG에 기록한다.

### P0 — 사용자 설정과 보호 상태의 완전한 분리

현재 자동 UUID는 별도 state에 저장되지만, pairing 적용 후 config에는 인증 파일 경로와 Storage endpoint가 기록된다. 파일 내용 자체는 비밀이 아니지만 사용자 YAML을 더 단순화하려면 다음 구조로 마무리한다.

- 사용자 YAML: `agent.name`, Backup Set의 `name`, `paths`, include/exclude, 사용자 의도에 해당하는 hook만 허용
- 내부 identity state: Agent ID, Backup Set ID와 이름/경로 매칭 정보
- 보호된 connection state: Storage endpoint, token 경로, certificate/key/CA 경로, certificate expiry와 연결 ID
- repository password: 설치 시 생성하고 Windows DPAPI 또는 Linux root-only 파일에 보관; 사용자 YAML에 값이나 임의 경로를 요구하지 않음
- `pair`는 아직 연결되지 않은 최소 YAML도 읽을 수 있어야 한다. 현재 `config.Load` 검증이 Storage endpoint/password를 먼저 요구하는지 확인하고, 필요하면 `LoadUserConfig`와 `LoadRuntimeConfig`를 분리한다.
- YAML 이름 변경과 경로 변경이 동시에 발생했을 때 기존 ID를 확정할 수 없으면 임의 매칭하지 말고 사용자에게 새 Backup Set인지 묻거나 migration 명령을 제공한다.
- `*.state.json`은 UUID만 포함하므로 secret은 아니지만 권한 0600을 유지하고 atomic replacement를 테스트한다.

### P1 — 같은 PC 설치와 자동 검색 UX

- Windows installer가 Storage만 설치한다. “이 PC의 데이터도 백업”을 일반 기능으로 제공하려면 Windows Source Agent binary와 service/watch 등록을 installer 선택 항목으로 포함한다.
- 같은 PC에서는 사용자가 endpoint/code/fingerprint를 타이핑하지 않도록 loopback으로 Storage를 발견하고 tray에서 한 번 승인하는 흐름을 구현한다.
- 다른 PC에서는 LAN discovery가 endpoint와 표시 이름만 광고하게 한다. discovery 정보는 신뢰하지 말고 실제 신뢰는 code와 certificate fingerprint로 확립한다.
- Source 쪽에도 최소한의 setup UI 또는 guided CLI가 필요하다. 현재 tray는 Storage 전용이며 Source pairing 값을 받을 GUI가 없다.
- Linux installer는 최소 사용자 YAML 템플릿을 만들고 `pair` 명령을 대화형으로 안내해야 한다.

### P1 — 연결 관리와 인증서 수명주기

- tray에 연결된 Source 목록, 마지막 접속, certificate 만료, revoke, rename을 표시한다.
- Source별 credential/certificate 폐기 API와 persistent revocation 상태를 구현한다.
- 만료 전 자동 certificate rotation을 구현하되 기존 key를 무기한 재사용하지 않는다.
- Storage CA/서버 인증서 교체와 Source 재신뢰 절차를 설계한다.
- Source 삭제 시 topology의 Backup Set/mapping을 즉시 파괴하지 말고 unresolved 상태로 남기고 명시적으로 정리한다.

### P1 — 원본 장치 트리거 모델의 UI 명확화

현재 Storage는 등록 장치의 root 아래 Source path가 있으면 source-arrival로 판단한다. 이 동작을 UI에서 우연히 추론하게 두지 말고 Backup Set별 **Trigger device** 또는 “이 원본 장치가 연결될 때” 관계를 표시한다.

- 대상 장치와 원본 트리거 장치를 시각적으로 구분한다.
- 외장 원본 장치는 repository 대상 매핑 없이도 등록할 수 있어야 한다.
- 하나의 Backup Set에 여러 원본 경로가 여러 볼륨에 걸칠 때 실행 조건을 `all available` 또는 명시적 정책으로 정의한다.
- 네트워크/리눅스 원격 Source 경로는 Windows Storage가 로컬 path containment로 판단하면 안 된다. source-arrival 트리거는 같은 호스트로 확인된 Source에만 적용하거나 사용자가 명시적으로 장치와 Backup Set을 연결하게 한다.
- 드라이브 문자가 바뀌어도 volume stable ID와 현재 root를 사용한다.

### P1 — 설치·릴리스 완료

- 사람이 UAC를 승인할 수 있을 때 Windows installer의 install/upgrade/uninstall을 실제로 실행한다.
- 서비스 시작, tray 자동 시작, firewall, 설정 보존, 제거 후 사용자 데이터/repository 보존을 확인한다.
- installer code signing이 없으므로 Unknown publisher 경고와 SHA-256 검증 방법을 릴리스에 명확히 쓴다.
- 모든 검증 후 CHANGELOG의 `[Unreleased] - 0.1.1`을 날짜가 있는 `[0.1.1]`로 바꾸고 tag/release를 생성한다.
- 릴리스 전에 생성 cache, 실제 사용자 경로, pairing code/token/key, repository 데이터가 추적되지 않았는지 확인한다.

## 6. 체크리스트 최종 판단 기준

1. **사용하지 않는 함수:** Go compiler/vet/staticcheck와 .NET build/analyzer에서 미사용 코드가 없어야 한다. 단지 테스트 때문에만 남긴 production wrapper도 검토한다.
2. **불필요한 한 번짜리 함수:** 보안 경계, 단위 테스트 가능한 순수 로직, 두 곳 이상 호출되는 로직이 아니라면 의미 없는 forwarding method를 만들지 않는다.
3. **Source YAML 다중 경로:** ID 없이 작성한 strict YAML이 로드되고 여러 `paths`가 실제 restic 인자로 전달되어야 한다.
4. **한 Source → 다수 매체:** 하나의 Backup Set에 여러 mapping을 만들고 모든 repository에 snapshot이 생기며 각각 복원되어야 한다.
5. **Source와 Storage가 같은 PC:** loopback pairing/control과 로컬 source path가 실제 E2E로 동작해야 한다.
6. **외장 원본 → 로컬 자동 백업:** Storage가 원본 장치 도착을 감지해 명령을 보내야 하며 Source 자체 감지 코드는 없어야 한다. 로컬 대상 복원과 hash 일치까지 확인한다.

현재 3~6의 핵심 실행 경로는 구현·검증했지만, 5의 일반 사용자 설치 UX와 6의 명시적 trigger-device UI는 아직 P1 작업이다.

## 7. 위험 요소와 피해야 할 구현

- TLS 검증을 끈 채 pairing code만 보내지 않는다. code와 함께 Storage certificate fingerprint를 먼저 고정해야 한다.
- 짧고 추측 가능한 6자리 숫자 code를 단독 인증 수단으로 사용하지 않는다. 현재 code는 160-bit 무작위 값이다.
- bearer token이나 private key를 README, sample config, 로그, clipboard history, crash dump에 남기지 않는다.
- Source가 “경로가 나타났다”는 이유로 독자적으로 자동 백업하지 않는다.
- Storage가 원격 Linux 경로를 Windows 로컬 경로처럼 해석하지 않는다.
- repository를 볼륨 root에 만들거나 서로 다른 Backup Set이 의도치 않게 동일 repository path를 공유하게 하지 않는다.
- 실제 외장장치나 사용자 파일을 E2E 후 삭제하지 않는다. 테스트 cleanup은 `artifacts` 아래의 검증된 run directory 또는 명시적으로 만든 `BackupMesh-E2E-*` 경로에만 적용한다.
- 관리자 권한 설치 테스트를 했다고 가장하지 않는다. 현재 UAC 승인이 없어서 미검증이다.

## 8. 관련 문서와 진입점

- 사용자 가치와 전체 소개: `README.md`, `README.ko.md`
- 사용자 설치/운영: `docs/USER_GUIDE.md`, `docs/USER_GUIDE.ko.md`
- 통신 계약: `protocol/openapi.yaml`, `protocol/README.md`
- TLS 설계: `docs/mutual-tls.md`
- Source 설정: `source-agent/internal/config/config.go`, `source-agent/example.config.yaml`
- Source CLI/pairing/backup: `source-agent/cmd/backupmesh-agent/main.go`
- Storage API/pairing: `storage-agent/src/BackupMesh.Storage.Service/ControlApi.cs`, `PairingSessions.cs`, `PairingCertificates.cs`
- 장치 도착 정책: `storage-agent/src/BackupMesh.Storage.Service/StorageMonitor.cs`
- tray pairing과 mapping UI: `storage-agent/src/BackupMesh.Storage.App/MainWindowViewModel.cs`, `MainWindow.xaml`
- 실제 E2E: `scripts/test-local-e2e.ps1`
- Windows installer: `packaging/windows/BackupMesh.iss`, `scripts/build-windows-installer.ps1`

## 9. 다음 에이전트가 시작할 때 권장 순서

1. 이 문서와 `git status`, 최근 두 커밋, 전체 diff를 읽는다.
2. 현재 pairing 변경을 테스트하고 작은 독립 커밋으로 보존한다.
3. pairing HTTP 통합/보안 회귀 테스트와 clipboard UX부터 해결한다.
4. user config/runtime secret state 분리를 끝낸다.
5. 같은 PC Source 설치 옵션과 자동 pairing을 구현한다.
6. trigger-device UI와 원격 Source 경로 오판 방지를 구현한다.
7. 전체 build/unit/protocol/E2E/installer 수동 테스트를 통과시킨다.
8. 검증된 변경만 PR로 병합하고, 마지막에 `v0.1.1`을 발행한다.

