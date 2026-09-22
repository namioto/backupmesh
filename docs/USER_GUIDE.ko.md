# BackupMesh 사용자 가이드

**한국어** | [English](USER_GUIDE.md)

이 문서는 현재 MVP인 Windows Storage Agent와 하나 이상의 Linux Remote Agent를 설치하고 사용하는 방법을 설명합니다. 실제 환경에서 복원을 검증하기 전까지는 중요한 데이터의 다른 독립 사본을 유지하세요.

## 연결 주소와 복사 항목

같은 LAN에서는 원격 에이전트 감시 프로그램을 시작하세요. **근처 컴퓨터**에서 연결할 컴퓨터를 선택하고 **연결 요청**을 누릅니다. 양쪽 컴퓨터에 같은 6자리 번호가 표시되는지 확인한 뒤 원격 컴퓨터에서 승인하세요. 근처 이름은 연결 성공 전까지 확인되지 않은 정보입니다. 탐색이 차단되면 **연결 초대 복사**와 아래 명령을 사용하세요.

최초 연결에는 사용 중인 LAN IPv4 주소를 사용합니다. 양쪽 에이전트를 0.3.5 이상으로 업데이트하면 DHCP 주소 변경 후 스토리지를 자동으로 다시 찾고, 연결이 끊겨도 재접속을 시도합니다. 주소 변경 때문에 인증서를 교체하거나 다시 페어링할 필요가 없습니다. 자동 탐색은 UDP 7445 브로드캐스트를 사용하며, 인증정보 전송 전에 저장된 스토리지 인증서를 검증합니다. 설치 프로그램이 개인·도메인 네트워크의 로컬 서브넷에만 탐색 방화벽 규칙을 등록합니다. 두 PC가 켜져 있고 서로 통신할 수 있어야 하며, 게스트 Wi-Fi 격리·VLAN 분리·브로드캐스트 차단 환경에서는 자동 탐색이 제한됩니다. BackupMesh가 발급한 연결 인증서를 사용하는 IPv4 LAN이 대상입니다. 사용자 지정 CA나 서로 다른 서브넷에서는 접근 가능한 DNS 이름 또는 안정적인 주소가 필요합니다.

연결 준비에 문제가 있으면 앱에서 복구를 제안합니다. **연결 준비 복구**를 승인하면 필요한 변경과 재시작을 자동 처리한 뒤 초대 화면으로 이어집니다. Windows 관리자 승인이 나타날 수 있습니다. 기존에 연결한 컴퓨터는 다시 연결해야 하며 백업 파일은 보존됩니다. 설정에서도 같은 복구 기능을 실행할 수 있습니다.

## 1. 설치 패키지 준비

아래 설치 패키지를 준비하세요. 소스에서 직접 만드는 절차는 [기여 가이드](../CONTRIBUTING.ko.md)를 참고하세요.

자체 포함 패키지는 다음 위치에 생성됩니다.

- `artifacts\installer\BackupMesh-Storage-0.3.7-win-x64-Setup.exe`
- `artifacts\BackupMesh-Storage-win-x64` (개발·시험용 패키지)
- `artifacts\BackupMesh-Source-linux-x64`
- `artifacts\installer\BackupMesh-Source-0.3.7-win-x64-Setup.exe` (다른 Windows PC용 Remote Agent)

고정 버전 `restic`과 `rest-server`가 포함되므로 대상 장비에 .NET이나 Go를 별도로 설치할 필요가 없습니다.

## 2. Windows Storage Agent 설치

일반 사용자는 `BackupMesh-Storage-0.3.7-win-x64-Setup.exe`를 실행해 라이선스에 동의하고 **설치**를 선택합니다. 마법사가 Windows 서비스를 설치·시작하고, 로그인 시 트레이 앱 실행과 로컬 서브넷 방화벽 규칙 및 제거 프로그램을 등록합니다. 업그레이드할 때 기존 설정을 보존하며 완료 후 BackupMesh를 실행합니다.

설치 프로그램은 아직 Authenticode 코드 서명이 없어 실행 전 Windows에 **알 수 없는 게시자**로 표시되고 SmartScreen 경고가 뜰 수 있습니다. `build-windows-installer.ps1`이 설치 프로그램 옆에 `.sha256` 파일을 함께 생성하니, 설치를 승인하기 전에 `Get-FileHash BackupMesh-Storage-0.3.7-win-x64-Setup.exe -Algorithm SHA256` 결과를 이 파일과 비교해 확인하세요.

개발 중 임시 평가에는 `Start-BackupMesh.ps1`을 실행합니다. 문제 해결을 위한 PowerShell 설치 방식도 유지됩니다.

```powershell
Set-Location artifacts\BackupMesh-Storage-win-x64
.\Install-BackupMesh.ps1
```

설치 프로그램은 자동 재시작되는 `BackupMeshStorageAgent` Windows 서비스를 만들고, Private·Domain 네트워크의 로컬 서브넷에 인증된 Control/repository 포트를 허용하며, 현재 사용자의 다음 로그인부터 트레이 앱을 실행합니다. 서비스 데이터는 `%ProgramData%\BackupMesh` 아래에 보호됩니다.

제거할 때는 **설정 보존(아니요)**이 기본값입니다. **예**를 선택하면 Storage의 백업 규칙, 이력, 페어링 정보와 제거를 실행하는 Windows 사용자의 앱 설정을 삭제합니다. 연결된 컴퓨터는 다시 페어링해야 합니다. 무인 제거는 설정을 보존합니다. PowerShell 제거에서는 `Uninstall-BackupMesh.ps1 -RemoveSettings`로 같은 삭제를 요청할 수 있습니다.

실제 백업, `local-repository-passwords`의 암호, 별도 Remote Agent 데이터는 삭제하지 않습니다. 설정 파일 옆의 `*.recovery-*`에는 암호 파일의 mapping ID와 백업 경로를 찾기 위한 복구용 정보가 보존되며 재설치 시 자동으로 불러오지 않습니다. 이 암호는 Windows DPAPI로 보호되므로 다른 PC로 파일을 복사하는 것만으로 복구할 수 없습니다. 다른 Windows 사용자의 UI 설정도 보존합니다. 기존 설정이 남아 있으면 재설치의 시작 화면에서 재사용 사실을 안내합니다.

## 3. 백업 저장 위치 선택

저장장치 등록은 내부 동작입니다. 사용자는 백업 규칙 창에서 무엇을 백업하고 어디에 저장할지만 선택합니다.

- **Add backup…**을 누르고 연결된 대상 드라이브와 전체 저장 경로를 선택합니다.
- 감지된 드라이브 대신 로컬·네트워크 폴더를 사용하려면 **Choose folder…**를 누릅니다.
- BackupMesh는 드라이브 문자가 바뀌어도 규칙이 유지되도록 장치 식별 정보를 내부에서 관리하며, 마지막 규칙을 삭제하면 사용하지 않는 내부 장치 정보도 정리합니다.
- repository는 볼륨 루트가 아닌 안전한 하위 폴더에 저장해야 합니다.

연결 후 백업 시작까지의 대기 시간은 **Settings** 탭의 전역 기본값으로 적용됩니다. 이동식 저장장치 분리는 백업 작업이 끝난 뒤 Windows에서 수행합니다.

## 3b. 이 PC 자체의 파일 백업하기 (Remote Agent 불필요)

로컬 폴더는 페어링이나 별도 에이전트 없이 사용할 수 있습니다. **백업 → 백업 추가**에서 **이 PC의 폴더 백업…**을 선택하고, 저장 대상을 지정한 뒤 저장하세요. 로컬 백업은 스토리지 PC에서 직접 실행되며 **원격 에이전트** 목록에는 표시되지 않습니다.

로컬 폴더를 더 이상 사용하지 않으려면 백업 창에서 해당 폴더를 선택하고 **폴더 제거**를 누르세요. 해당 폴더의 백업 규칙도 제거되며 기존 백업 데이터는 보존됩니다.

## 4. Ubuntu Remote Agent 설치와 설정

Ubuntu 패키지가 포함된 릴리스(0.3.7 이상)의 일반 설치 명령과 게시 전제 조건은 [설치 가이드](INSTALLATION.ko.md)를 참고하세요. 부트스트랩이 호환되는 최신 릴리스를 내려받아 검증한 뒤 설정을 시작합니다.

압축 파일 직접 설치는 고급 대체 방법으로 계속 사용할 수 있습니다.

```sh
tar -xzf BackupMesh-Source-0.3.7-linux-x64.tar.gz
cd BackupMesh-Source-linux-x64
sudo sh install.sh
```

대화형 터미널에서 백업할 절대 폴더 경로를 하나씩 입력합니다. 폴더 입력을 끝내려면 빈 입력에서 Enter를 누릅니다. 연결 질문에는 Enter를 누르거나 `y`를 입력하고, 스토리지 앱의 **근처 컴퓨터**에서 연결을 요청하세요. 같은 6자리 비교 번호가 표시되는지 확인한 뒤 `y`로 승인합니다.

나중에 폴더를 추가하거나 페어링을 마치려면 다시 실행합니다.

```sh
sudo backupmesh-setup
```

기존 설정을 보존하며 설정에서 변경 사항을 저장하면 감시 서비스를 다시 시작합니다. `/etc/backupmesh/restic-password`의 보호된 사본을 보관하세요. 이 암호 없이는 암호화된 snapshot을 복원할 수 없습니다.

### 고급 수동 설정

Remote Agent는 엄격한 JSON(`.json`)과 YAML(`.yaml`, `.yml`)을 지원합니다. Backup Set의 `paths`에는 여러 파일과 디렉터리를 지정할 수 있습니다. `source-agent/example.config.yaml`을 참고하고 수동 변경 후 `sudo /opt/backupmesh/backupmesh-agent validate -config /etc/backupmesh/backupmesh.json`을 실행하세요.

## 4b. 다른 PC에 Windows Remote Agent 설치하기

이건 Storage Agent가 없는 **다른** Windows PC가 네트워크의 다른 곳에 있는 Storage Agent에 백업해야 할 때 씁니다 — 예를 들어 다른 방에 있는 Storage PC에 노트북을 백업하는 경우입니다. Storage Agent 자체의 PC를 백업하려면 대신 **백업 → 백업 추가**(3b 항목)를 쓰세요 — 설치 프로그램이 아예 필요 없습니다.

그 PC에서 `BackupMesh-Source-0.3.7-win-x64-Setup.exe`를 실행하세요. 사용자 프로필 아래에 대화형 콘솔 없이 설치되고, 기존 설정이 없으면 빈 `backupmesh.json`을 만들며, 백그라운드 감시용 사용자별 예약 작업을 등록합니다. 제거 프로그램은 설정·페어링된 신원·repository 암호를 보존합니다.

스크립트 기반 설치나 문제 해결이 필요하면 패키지와 설치 스크립트를 직접 사용할 수 있습니다.

```powershell
Set-Location artifacts\BackupMesh-Source-win-x64
.\Install-BackupMeshSource.ps1
```

스토리지 앱에서 **연결 초대 복사**를 누르고 다음 명령의 입력 요청에 초대를 붙여넣으세요.

```powershell
& "$env:LOCALAPPDATA\BackupMesh\Source\backupmesh-agent.exe" pair `
  -config "$env:LOCALAPPDATA\BackupMesh\Source\backupmesh.json"
```

`Uninstall-BackupMeshSource.ps1`은 예약 작업과 바이너리만 제거하고, `%LOCALAPPDATA%\BackupMesh\Source` 아래의 설정·페어링된 신원·repository 암호는 그대로 유지합니다.

## 5. Source 페어링

양쪽 에이전트가 0.3.6 이상이면 **근처 컴퓨터**에서 **연결 요청**을 누르고 양쪽 컴퓨터의 6자리 번호가 같을 때만 승인합니다. Ubuntu 0.3.7 설정은 요청 UUID를 노출하지 않고 비교 번호를 보여 준 뒤 승인을 묻습니다. 여러 요청이 있으면 먼저 번호 목록의 순번으로 하나를 선택합니다. Windows는 로컬 확인 창을 사용합니다. 첫 인증 카탈로그가 도착하면 연결된 목록으로 이동합니다. 요청을 거부·취소하거나 만료되게 두면 접근 권한을 주지 않습니다.

탐색이 차단되면 **원격 에이전트 연결**, **연결 초대 복사**를 선택하고 다음 고급 대체 명령에 초대를 붙여넣으세요. 초대는 10분 후 만료되며 한 번만 사용할 수 있습니다.

```sh
sudo /opt/backupmesh/backupmesh-agent pair \
  -config /etc/backupmesh/backupmesh.json \
  -output /etc/backupmesh/pairing
```

Source는 코드를 보내기 전에 표시된 인증서 지문을 고정 검증하고, 이후 Source에 결속된 토큰, 클라이언트 인증서와 개인 키, 고정된 Storage 인증서를 소유자 전용 권한으로 설치합니다. 개인 키가 전송 파일에 기록되지 않으며 운영체제 전역 신뢰 저장소도 변경하지 않습니다.

Remote Agent의 개인 키나 인증서를 잃어버렸다면(예: `pairing` 디렉터리를 삭제한 경우) **Pair a Remote Agent** 대신 **Remote Agents** 탭의 목록에서 해당 Source를 선택하고 **Re-pair**를 사용하세요. 이 코드는 그 특정 Source의 자격 증명만 재발급할 수 있으며, 새 Source를 만들거나 다른 Source의 신원을 대신 차지할 수 없습니다.

명령 감시 서비스를 시작합니다.

```sh
sudo systemctl enable --now backupmesh-source-watch.service
sudo systemctl status backupmesh-source-watch.service
```

## 6. Backup Set과 저장 위치 매핑

Source가 동기화되면 트레이 앱의 **Backups** 탭을 엽니다.

1. **What to back up**에서 Backup Set을 선택합니다 — 연결된 원격 컴퓨터의 Backup Set이나 이 창에서 추가한 로컬 폴더를 사용할 수 있습니다.
2. **Where to store it**에서 연결된 드라이브를 선택하거나 **Choose folder…**를 누릅니다.
3. **Full destination path**에서 실제 전체 저장 경로를 확인하거나 수정합니다.
4. **Add backup…**을 눌러 백업 규칙 창을 작성한 뒤 **Add backup**을 누릅니다. 기존 행을 더블클릭하면 같은 창에서 규칙을 수정할 수 있습니다. 원본·대상 장치·대상 폴더가 모두 같은 규칙은 중복 저장되지 않습니다.

매핑은 다대다입니다. 하나의 장치에 여러 Source를 각기 다른 폴더 또는 공통 상위 폴더 아래 저장할 수 있고, 하나의 Backup Set을 여러 장치에 동시에 백업할 수도 있습니다. 의도적으로 공유하는 경우가 아니라면 독립된 Backup Set마다 별도 repository 하위 폴더를 사용하세요. 표의 **Enabled** 확인란으로 특정 백업만 삭제하지 않고 일시 중단할 수 있습니다.

Remote Agent와 Storage Agent는 Storage Agent의 로컬 HTTPS 주소를 사용해 같은 PC에서 실행할 수 있습니다. USB뿐 아니라 로컬 고정 드라이브와 등록 폴더도 대상 장치로 사용할 수 있으므로 로컬 데이터→외장 저장장치와 외장 원본→로컬 저장장치 구성을 모두 만들 수 있습니다. 후자의 경우 외장 원본 볼륨을 Storage 장치로 등록합니다. Storage가 도착을 감지하고 그 볼륨 안에 원본 경로가 있는 Backup Set을 찾아 준비된 모든 대상 매핑의 명령을 보냅니다. Remote Agent는 Storage가 승인한 명령만 실행하며 장치 감지나 정책을 소유하지 않습니다.

## 7. 백업 실행과 확인

대상 장치를 연결하고 Settings 탭에서 설정한 arrival delay가 끝날 때까지 기다립니다. BackupMesh가 매핑된 Source에 자동으로 백업을 요청합니다. 트레이 앱에서 대기·실행 상태, 처리 파일과 바이트, 진행률, 결과, 최근 성공 시각을 확인할 수 있습니다. 실행 중인 작업을 취소하면 Source가 restic을 종료하고 `CANCELLED` 결과를 보고합니다.

백업이 시작되면 트레이 아이콘 옆에 진행률과 취소 버튼을 보여주는 작은 팝업(트레이 플라이아웃)이 뜹니다. Settings 탭의 "Show a status window when a backup starts" 토글(기본 켜짐)로 켜고 끌 수 있습니다.

Source에서 직접 실행하려면 다음 명령을 사용합니다.

```sh
sudo /opt/backupmesh/backupmesh-agent backup \
  -config /etc/backupmesh/backupmesh.json \
  -set documents \
  -restic /opt/backupmesh/restic
```

이동식 저장장치를 Windows에서 분리하기 전에 해당 장치를 대상으로 한 대기·실행 작업이 없는지 확인하세요.

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

## 언어와 프로젝트 정보

**설정 → 언어**에서 시스템 기본값, 한국어, English를 선택합니다. 선택은 즉시 저장되고 트레이 메뉴까지 바로 적용됩니다. 재시작하거나 백업을 중단할 필요가 없습니다. 시스템 기본값은 한국어 Windows에서는 한국어, 그 외에는 영어입니다. 날짜와 숫자는 지역 설정을 유지합니다. 기존 활동 기록은 작성 당시 언어를 유지합니다.

화면 하단에는 버전과 GitHub 링크가 항상 표시됩니다. **설정 → BackupMesh 정보**에서 프로젝트 주소, 사용 안내, 문제 신고, 라이선스를 확인할 수 있습니다. 서비스나 외부 도구의 진단 정보는 원문으로 표시될 수 있습니다.

## 문제 해결

- **Source가 보이지 않음:** 양쪽 에이전트가 실행 중이고 업데이트되었는지 확인하세요. 같은 IPv4 LAN에서 UDP 7445 탐색과 TCP 7443 및 저장소 포트 18000–18099 통신이 가능해야 합니다. IP 주소만 바뀐 경우 재페어링은 필요 없습니다.
- **준비된 대상이 없음:** 장치 연결, 저장된 매핑, Source catalog 동기화, arrival delay 경과 여부를 확인합니다.
- **인증서 오류:** Storage Agent가 광고하는 호스트명을 수정한 뒤 다시 페어링합니다. BackupMesh 사설 CA를 Windows 시스템 신뢰 저장소에 설치하지 마세요.
- **공간 부족:** 공간을 확보하거나 다른 매핑 장치를 선택합니다. 한 대상의 실패가 준비된 다른 대상의 시도까지 막지는 않습니다.
- **중단된 실행:** 장치를 다시 연결하고 재시도합니다. 서비스 복구 후 오래된 작업 상태가 해제되며 restic은 이미 저장한 데이터를 안전하게 재사용합니다.
- **제거:** 관리자 권한으로 `Uninstall-BackupMesh.ps1`을 실행합니다. 설정과 repository는 의도적으로 보존됩니다.

## 현재 검증 범위

저장소의 테스트는 실제 파일을 사용해 인증된 페어링, TLS repository 전송, 다중 대상 백업, 복원, SHA-256 일치를 검증합니다. 실제 운영 전에는 사용할 Windows·Linux 장비와 저장장치에서 설치, 백업, 취소, 연결 단절, 복원을 다시 시험하세요.
