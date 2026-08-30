# BackupMesh 사용자 가이드

**한국어** | [English](USER_GUIDE.md)

이 문서는 현재 MVP인 Windows Storage Agent와 하나 이상의 Linux Source Agent를 설치하고 사용하는 방법을 설명합니다. 실제 환경에서 복원을 검증하기 전까지는 중요한 데이터의 다른 독립 사본을 유지하세요.

## 1. 패키지 빌드

저장소 루트의 PowerShell에서 실행합니다.

```powershell
pwsh -NoProfile -File scripts/build-windows-test-package.ps1
pwsh -NoProfile -File scripts/build-windows-installer.ps1
pwsh -NoProfile -File scripts/build-linux-source-package.ps1
pwsh -NoProfile -File scripts/build-windows-source-package.ps1
```

자체 포함 패키지는 다음 위치에 생성됩니다.

- `artifacts\installer\BackupMesh-Storage-0.1.1-win-x64-Setup.exe`
- `artifacts\BackupMesh-Storage-win-x64` (개발·시험용 패키지)
- `artifacts\BackupMesh-Source-linux-x64`
- `artifacts\BackupMesh-Source-win-x64` (같은 PC를 백업하기 위한 Source Agent)

고정 버전 `restic`과 `rest-server`가 포함되므로 대상 장비에 .NET이나 Go를 별도로 설치할 필요가 없습니다.

## 2. Windows Storage Agent 설치

일반 사용자는 `BackupMesh-Storage-0.1.1-win-x64-Setup.exe`를 실행해 라이선스에 동의하고 **설치**를 선택합니다. 마법사가 Windows 서비스를 설치·시작하고, 로그인 시 트레이 앱 실행과 로컬 서브넷 방화벽 규칙 및 제거 프로그램을 등록합니다. 업그레이드할 때 기존 설정을 보존하며 완료 후 BackupMesh를 실행합니다.

설치 프로그램은 아직 Authenticode 코드 서명이 없어 실행 전 Windows에 **알 수 없는 게시자**로 표시되고 SmartScreen 경고가 뜰 수 있습니다 — 정상적인 현상이며 변조의 증거가 아닙니다. `build-windows-installer.ps1`이 설치 프로그램 옆에 `.sha256` 파일을 함께 생성하니, 설치를 승인하기 전에 `Get-FileHash BackupMesh-Storage-0.1.1-win-x64-Setup.exe -Algorithm SHA256` 결과를 이 파일과 비교해 확인하세요.

개발 중 임시 평가에는 `Start-BackupMesh.ps1`을 실행합니다. 문제 해결을 위한 PowerShell 설치 방식도 유지됩니다.

```powershell
Set-Location artifacts\BackupMesh-Storage-win-x64
.\Install-BackupMesh.ps1
```

설치 프로그램은 자동 재시작되는 `BackupMeshStorageAgent` Windows 서비스를 만들고, Private·Domain 네트워크의 로컬 서브넷에 인증된 Control/repository 포트를 허용하며, 현재 사용자의 다음 로그인부터 트레이 앱을 실행합니다. 서비스 데이터는 `%ProgramData%\BackupMesh` 아래에 보호됩니다.

## 3. 저장장치 등록

저장장치 등록은 더 이상 독립된 단계가 아닙니다. **Backups** 탭에서 백업을 추가할 때 Target device 콤보 옆의 **New…** 버튼을 누르면 그 자리에서 등록할 수 있습니다.

- 열리는 대화상자에서 감지된 고정식·이동식 드라이브를 선택해 **Register drive**를 누르거나, **Register folder instead…**로 로컬·네트워크 폴더를 논리 장치로 등록합니다.
- 등록한 장치는 대화상자가 닫히면 Target device 콤보에 자동으로 선택됩니다.
- repository는 볼륨 루트가 아닌 안전한 하위 폴더에 저장해야 합니다 — 실제 대상 폴더는 6장에서 매핑을 추가할 때 지정합니다.
- 장치별 arrival delay는 더 이상 없습니다. Windows와 느린 디스크가 마운트를 끝낼 시간을 확보하는 지연은 이제 **Settings** 탭의 전역 기본값("Wait before starting, after any device connects")으로 등록된 모든 장치에 함께 적용됩니다.

등록된 장치 목록과 여유 공간, 안전 제거는 **Overview** 탭의 "Connected storage"에서 확인합니다. 폴더 장치는 USB로 표시되지 않는 저장소를 사용할 때와 일반 폴더만으로 다중 대상 동작을 시험할 때 유용합니다.

## 3b. 이 PC 자체의 파일 백업하기 (Source Agent 불필요)

**This PC**는 페어링도, 별도 설치 프로그램도, 켜는 절차도 없이 **Source Agents** 탭의 목록에 "This PC (no agent needed)"로 항상 표시됩니다. **Back up a folder on this PC…**를 선택해 폴더를 고르면, 페어링된 Source Agent의 Backup Set과 똑같이 등록된 대상 장치에 매핑할 수 있는 Backup Set으로 나타납니다. 매핑된 대상이 준비되면 Storage가 번들된 `restic`을 로컬 폴더에 직접 실행합니다 — 네트워크도, 인증서도, 관리해야 할 repository 암호도 없습니다.

이 방식으로 폴더 백업을 그만두려면 같은 탭의 **Remove folder**를 사용하세요, 매핑도 함께 제거됩니다. 이건 아래에서 설명하는 독립 실행형 Windows Source Agent와는 무관합니다 — 그건 Storage Agent가 없는 **다른** PC를 위한 것입니다.

## 4. Linux Source Agent 설치와 설정

`BackupMesh-Source-linux-x64`를 Linux 장비로 복사한 뒤 실행합니다.

```sh
sudo sh install.sh
sudoedit /etc/backupmesh/backupmesh.json
```

각 Backup Set에는 표시 이름, 원본 경로, 필요한 include/exclude 패턴만 설정합니다. Source Agent가 Agent와 Backup Set의 고정 UUID를 자동 생성하고 설정 파일 옆의 소유자 전용 `*.state.json` 파일에 보존합니다. 사용자가 ID를 편집하거나 Source 사이에 복사하면 안 됩니다. 설정 파일을 검증합니다.

Source Agent는 엄격한 JSON(`.json`)과 YAML(`.yaml`, `.yml`)을 지원합니다. Backup Set의 `paths` 목록에는 파일과 디렉터리를 원하는 만큼 지정할 수 있습니다. 다중 경로 예시는 `source-agent/example.config.yaml`을 참고하세요. YAML과 JSON 모두 알 수 없는 필드를 거부하므로 오타가 조용히 무시되지 않습니다.

```sh
sudo /opt/backupmesh/backupmesh-agent validate \
  -config /etc/backupmesh/backupmesh.json
```

설치 프로그램은 소유자 전용 권한의 `/etc/backupmesh/restic-password`를 만듭니다. 이 암호를 잃으면 암호화된 snapshot을 복구할 수 없으므로 보호된 복구 사본을 별도로 보관하세요.

대화형 터미널에서 `install.sh`를 실행하면(스크립트로 자동 실행하는 대신) 손으로 편집할 일반 템플릿 대신 Agent 이름과 첫 Backup Set을 직접 물어보고, 완료 후 바로 `pair`를 실행할지도 제안합니다.

## 4b. 다른 PC에 Windows Source Agent 설치하기

이건 Storage Agent가 없는 **다른** Windows PC가 네트워크의 다른 곳에 있는 Storage Agent에 백업해야 할 때 씁니다 — 예를 들어 다른 방에 있는 Storage PC에 노트북을 백업하는 경우입니다. Storage Agent 자체의 PC를 백업하려면 대신 트레이의 **This PC**(3b 항목)를 쓰세요 — 설치 프로그램이 아예 필요 없습니다.

그 PC에서 `BackupMesh-Source-0.1.1-win-x64-Setup.exe`를 실행하세요. Storage 설치 프로그램과 달리 관리자 권한을 전혀 요구하지 않습니다 — 사용자 프로필 아래에 설치되고, 파일 복사 직후 콘솔 창이 열려 Agent 이름과 첫 Backup Set 경로를 물어본 뒤 최소한의 `backupmesh.yaml`을 작성합니다(`backupSets` 항목은 이후 직접 추가 가능). 또한 Source Agent를 백그라운드에서 계속 감시 상태로 유지하는 사용자별 예약 작업을 등록하고, 설정·페어링된 신원·repository 암호는 유지한 채 예약 작업과 바이너리만 제거하는 제거 프로그램도 포함합니다.

스크립트 기반 설치나 문제 해결이 필요하면 패키지와 설치 스크립트를 직접 사용할 수 있습니다.

```powershell
pwsh -NoProfile -File scripts/build-windows-source-package.ps1
Set-Location artifacts\BackupMesh-Source-win-x64
.\Install-BackupMeshSource.ps1
```

트레이의 **Pair a Source Agent** 대화상자가 보여주는 코드·주소·지문으로 Linux Source와 동일하게 페어링하면 됩니다.

```powershell
& "$env:LOCALAPPDATA\BackupMesh\Source\backupmesh-agent.exe" pair `
  -config "$env:LOCALAPPDATA\BackupMesh\Source\backupmesh.yaml" `
  -storage https://STORAGE-PC:7443 `
  -code TRAY에_표시된_코드 `
  -fingerprint TRAY에_표시된_64자리_16진수_지문
```

`Uninstall-BackupMeshSource.ps1`은 예약 작업과 바이너리만 제거하고, `%LOCALAPPDATA%\BackupMesh\Source` 아래의 설정·페어링된 신원·repository 암호는 그대로 유지합니다.

## 5. Source 페어링

Windows 트레이 앱에서 **Pair a Source Agent**를 선택합니다. Storage 주소, 1회용 코드, 인증서 SHA-256 지문이 표시됩니다. 코드는 10분 후 만료되고 한 번만 사용할 수 있습니다. Source에서 다음 명령을 실행합니다.

```sh
sudo /opt/backupmesh/backupmesh-agent pair \
  -config /etc/backupmesh/backupmesh.json \
  -storage https://STORAGE-PC:7443 \
  -code TRAY에_표시된_코드 \
  -fingerprint TRAY에_표시된_64자리_16진수_지문 \
  -output /etc/backupmesh/pairing
```

Source는 코드를 보내기 전에 표시된 인증서 지문을 고정 검증하고, 이후 Source에 결속된 토큰, 클라이언트 인증서와 개인 키, 고정된 Storage 인증서를 소유자 전용 권한으로 설치합니다. 개인 키가 전송 파일에 기록되지 않으며 운영체제 전역 신뢰 저장소도 변경하지 않습니다.

Source Agent의 개인 키나 인증서를 잃어버렸다면(예: `pairing` 디렉터리를 삭제한 경우) **Pair a Source Agent** 대신 **Source Agents** 탭의 목록에서 해당 Source를 선택하고 **Re-pair**를 사용하세요. 이 코드는 그 특정 Source의 자격 증명만 재발급할 수 있으며, 새 Source를 만들거나 다른 Source의 신원을 대신 차지할 수 없습니다.

명령 감시 서비스를 시작합니다.

```sh
sudo systemctl enable --now backupmesh-source-watch.service
sudo systemctl status backupmesh-source-watch.service
```

## 6. Backup Set과 저장 위치 매핑

Source가 동기화되면 트레이 앱의 **Backups** 탭을 엽니다.

1. **What to back up**에서 Backup Set을 선택합니다 — Source Agents 탭에서 페어링된 컴퓨터와 This PC가 제공하는 Backup Set 목록입니다.
2. **Target device**에서 등록된 저장장치를 선택합니다. 아직 등록한 장치가 없다면 옆의 **New…**로 그 자리에서 등록합니다(3장 참고).
3. **Target folder**에 장치 안의 저장 하위 폴더를 입력하거나 **Browse…**로 선택합니다.
4. **Add backup**을 누릅니다 — 즉시 저장되며 Settings 탭에서 따로 저장할 필요가 없습니다.

매핑은 다대다입니다. 하나의 장치에 여러 Source를 각기 다른 폴더 또는 공통 상위 폴더 아래 저장할 수 있고, 하나의 Backup Set을 여러 장치에 동시에 백업할 수도 있습니다. 의도적으로 공유하는 경우가 아니라면 독립된 Backup Set마다 별도 repository 하위 폴더를 사용하세요. 표의 **Enabled** 확인란으로 특정 백업만 삭제하지 않고 일시 중단할 수 있습니다.

Source Agent와 Storage Agent는 Storage Agent의 로컬 HTTPS 주소를 사용해 같은 PC에서 실행할 수 있습니다. USB뿐 아니라 로컬 고정 드라이브와 등록 폴더도 대상 장치로 사용할 수 있으므로 로컬 데이터→외장 저장장치와 외장 원본→로컬 저장장치 구성을 모두 만들 수 있습니다. 후자의 경우 외장 원본 볼륨을 Storage 장치로 등록합니다. Storage가 도착을 감지하고 그 볼륨 안에 원본 경로가 있는 Backup Set을 찾아 준비된 모든 대상 매핑의 명령을 보냅니다. Source Agent는 Storage가 승인한 명령만 실행하며 장치 감지나 정책을 소유하지 않습니다.

## 7. 백업 실행과 확인

등록된 장치를 연결하고 Settings 탭에서 설정한 arrival delay가 끝날 때까지 기다립니다. BackupMesh가 매핑된 Source에 자동으로 백업을 요청합니다. 트레이 앱에서 대기·실행 상태, 처리 파일과 바이트, 진행률, 결과, 최근 성공 시각을 확인할 수 있습니다. 실행 중인 작업을 취소하면 Source가 restic을 종료하고 `CANCELLED` 결과를 보고합니다.

백업이 시작되면 트레이 아이콘 옆에 진행률과 취소 버튼을 보여주는 작은 팝업(트레이 플라이아웃)이 뜹니다. Settings 탭의 "Show a status window when a backup starts" 토글(기본 켜짐)로 켜고 끌 수 있습니다.

Source에서 직접 실행하려면 다음 명령을 사용합니다.

```sh
sudo /opt/backupmesh/backupmesh-agent backup \
  -config /etc/backupmesh/backupmesh.json \
  -set documents \
  -restic /opt/backupmesh/restic
```

어느 탭을 열어 두었든 상단에는 연결된 장치의 백업이 모두 끝나 제거해도 되는지 알려주는 배너가 표시되며, 준비되면 배너의 **Remove safely** 버튼으로 바로 제거할 수 있습니다. 직접 선택해서 제거하려면 **Overview** 탭의 "Connected storage"에서 **Safely remove selected device**를 사용하세요. 해당 장치를 사용하는 모든 작업이 멈춘 뒤에만 제거하십시오 — BackupMesh는 Windows에 제거를 요청하기 전에 해당 repository listener를 닫습니다.

## 8. 복원 시험

실제 복원과 대표 파일 확인 전에는 백업이 검증됐다고 판단하면 안 됩니다. 저장장치를 Windows 장비에 직접 연결한 비상 복구 상황에서는 번들된 restic과 Source에서 별도로 보관한 repository 암호를 사용합니다.

```powershell
$env:RESTIC_PASSWORD_FILE = 'C:\secure\restic-password'
artifacts\BackupMesh-Storage-win-x64\Service\restic.exe `
  -r 'E:\BackupMesh\documents' snapshots
artifacts\BackupMesh-Storage-win-x64\Service\restic.exe `
  -r 'E:\BackupMesh\documents' restore latest `
  --target 'C:\BackupMesh-Restore-Test'
```

비어 있는 시험 폴더에 복원하고 파일 해시를 비교하거나 대표 파일을 직접 열어본 뒤 repository를 신뢰하세요.

## 문제 해결

- **Source가 보이지 않음:** `systemctl status backupmesh-source-watch.service`를 확인하고 TCP 7443 연결과 Storage 인증서의 호스트명/IP를 점검한 뒤 필요하면 다시 페어링합니다.
- **준비된 대상이 없음:** 장치 연결, 저장된 매핑, Source catalog 동기화, arrival delay 경과 여부를 확인합니다.
- **인증서 오류:** Storage Agent가 광고하는 호스트명을 수정한 뒤 다시 페어링합니다. BackupMesh 사설 CA를 Windows 시스템 신뢰 저장소에 설치하지 마세요.
- **공간 부족:** 공간을 확보하거나 다른 매핑 장치를 선택합니다. 한 대상의 실패가 준비된 다른 대상의 시도까지 막지는 않습니다.
- **중단된 실행:** 장치를 다시 연결하고 재시도합니다. 서비스 복구 후 오래된 작업 상태가 해제되며 restic은 이미 저장한 데이터를 안전하게 재사용합니다.
- **제거:** 관리자 권한으로 `Uninstall-BackupMesh.ps1`을 실행합니다. 설정과 repository는 의도적으로 보존됩니다.

## 현재 검증 범위

저장소의 테스트는 실제 파일을 사용해 인증된 페어링, TLS repository 전송, 다중 대상 백업, 복원, SHA-256 일치를 검증합니다. 실제 운영 전에는 사용할 Windows·Linux 장비와 저장장치에서 설치, 백업, 취소, 연결 단절, 복원을 다시 시험하세요.
