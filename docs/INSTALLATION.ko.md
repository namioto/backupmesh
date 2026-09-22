# BackupMesh 설치

BackupMesh는 Windows PC를 스토리지로 사용하고 Windows 또는 Ubuntu 컴퓨터를 원격 에이전트로 연결합니다.

## 1. Windows에 스토리지 설치

`BackupMesh-Storage-0.3.7-win-x64-Setup.exe`를 실행하고 라이선스에 동의한 뒤 **설치**를 선택합니다.

설치 후 BackupMesh를 여세요. 설치 프로그램이 스토리지 서비스를 시작하고 Private·Domain 네트워크의 로컬 서브넷 방화벽 규칙을 설정합니다.

## 2. Ubuntu에 원격 에이전트 설치

Ubuntu 컴퓨터에서 다음 명령을 실행합니다.

```sh
tar -xzf BackupMesh-Source-0.3.7-linux-x64.tar.gz
cd BackupMesh-Source-linux-x64
sudo sh install.sh
```

설정 마법사에서 백업할 폴더를 하나씩 입력하세요. `/home/minsu/Documents`처럼 절대 경로를 사용합니다. 입력을 끝내려면 빈 입력에서 Enter를 누르고, 연결 질문에는 Enter를 누르거나 `y`를 입력합니다.

Windows 스토리지 앱에서 **원격 에이전트**를 열고 **근처 컴퓨터**의 Ubuntu 컴퓨터를 선택한 뒤 **연결 요청**을 누릅니다. Ubuntu에 Windows와 같은 6자리 번호가 표시되는지 확인한 뒤 `y`로 승인합니다.

나중에 폴더를 추가하거나 페어링을 마치려면 설정을 다시 실행합니다.

```sh
sudo backupmesh-setup
```

기존 설정을 보존하며 변경이 성공하면 감시 서비스를 다시 시작합니다. `/etc/backupmesh/restic-password`의 보호된 복구 사본을 보관하세요. 이 암호 없이는 암호화된 백업을 복원할 수 없습니다.

페어링 후 Windows에서 **백업 → 백업 추가**를 여세요. Ubuntu 폴더와 대상 드라이브 또는 폴더를 선택하고 규칙을 저장합니다. 페어링만으로는 백업 대상이 만들어지지 않습니다.

## 3. Windows에 원격 에이전트 설치

다른 Windows PC에서 `BackupMesh-Source-0.3.7-win-x64-Setup.exe`를 실행합니다. 현재 사용자용으로 설치하고 빈 JSON 설정을 만든 뒤 로그인할 때 감시 프로그램을 시작합니다. 대화형 설정 콘솔은 열지 않습니다.

스토리지 앱의 **근처 컴퓨터**에서 연결을 요청하세요. 6자리 번호를 비교하고 원격 에이전트 PC에서 승인합니다. 백업 폴더는 `%LOCALAPPDATA%\BackupMesh\Source\backupmesh.json`의 JSON 설정에 추가합니다.

## 고급 대체 방법

UDP 7445 브로드캐스트가 차단되면 스토리지 앱에서 **연결 초대 복사**를 선택하고 `backupmesh-agent pair`를 실행합니다. 수동 JSON/YAML 설정과 자세한 명령은 [사용자 가이드](USER_GUIDE.ko.md)를 참고하세요.
