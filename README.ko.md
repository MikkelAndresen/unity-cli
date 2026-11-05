# unity-cli

[English](README.md) | [한국어](README.ko.md)

> 커맨드라인으로 Unity Editor를 제어합니다. AI 에이전트를 위해 만들었지만, 무엇이든 사용 가능합니다.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**서버 실행 없음. 설정 파일 없음. 프로세스 관리 없음. 명령어만 치면 됩니다.**

## 왜 만들었나

터미널에서 Unity를 제어하고 싶었습니다. 기존 MCP 기반 연동은 Python 런타임, WebSocket 릴레이, JSON-RPC 프로토콜 레이어, 설정 파일, 켜고 꺼야 하는 서버 프로세스, 도구 등록 절차, 수만 줄의 과잉 설계된 코드를 요구했습니다. 단순한 명령 하나 보내는 데 이 모든 게 필요했습니다.

게다가 AI 에이전트마다 MCP 설정과 연동을 따로 해줘야 했습니다. CLI는 그런 게 없습니다 — 셸 명령어를 실행할 수 있는 에이전트라면 바로 쓸 수 있습니다.

이상하다고 느꼈습니다. `curl`로 URL 하나 쏠 수 있는데, 왜 그 모든 게 필요한가?

그래서 정반대로 만들었습니다. Unity에 HTTP로 직접 통신하는 바이너리 하나. 서버를 띄울 필요 없이 — Unity 패키지가 자동으로 수신합니다. 설정 파일 없이 — Unity 인스턴스를 알아서 찾습니다. 도구 등록 없이 — 이름으로 바로 호출합니다. 캐싱도, 프로토콜 레이어도, 절차도 없습니다.

CLI 전체가 Go ~800줄(+ help text ~300줄), Unity 커넥터가 C# ~2,300줄입니다. 셸에서 Unity를 다루게 해주는 아주 얇은 레이어 — 그 본분에 충실합니다. 바이너리 설치하고, Unity 패키지 추가하면 끝입니다.

## 설치

### Linux / macOS

```bash
curl -fsSL https://raw.githubusercontent.com/MikkelAndresen/unity-cli/main/install.sh | sh
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/MikkelAndresen/unity-cli/main/install.ps1 | iex
```

### 기타 방법

```bash
# Go install (Go가 설치된 모든 플랫폼)
go install github.com/youngwoocho02/unity-cli@latest

# 수동 다운로드 (플랫폼 선택)
# Linux amd64 / Linux arm64 / macOS amd64 / macOS arm64 / Windows amd64
curl -fsSL https://github.com/MikkelAndresen/unity-cli/releases/latest/download/unity-cli-linux-amd64 -o unity-cli
chmod +x unity-cli && sudo mv unity-cli /usr/local/bin/
```

지원 플랫폼: Linux (amd64, arm64), macOS (Intel, Apple Silicon), Windows (amd64).

### 업데이트

```bash
# 최신 버전으로 자동 업데이트
unity-cli update

# 새 버전 확인만
unity-cli update --check
```

## Unity 설정

**Package Manager → Add package from git URL**에서 추가:

```
https://github.com/MikkelAndresen/unity-cli.git?path=unity-connector
```

또는 `Packages/manifest.json`에 직접 추가:
```json
"com.youngwoocho02.unity-cli-connector": "https://github.com/MikkelAndresen/unity-cli.git?path=unity-connector"
```

특정 버전을 고정하려면 URL 끝에 태그를 추가하세요 (예: `#v0.2.21`).

추가 후 Unity를 열면 커넥터가 자동으로 시작됩니다. 별도 설정 불필요.

### 권장: Editor 쓰로틀링 비활성화

기본적으로 Unity는 창이 포커스를 잃으면 에디터 업데이트를 쓰로틀링합니다. Unity API 작업은 Editor 메인 스레드에서 디스패치되므로 CLI 명령 처리가 지연될 수 있습니다.

**Edit → Preferences → General → Interaction Mode**에서 **No Throttling**으로 설정하세요.

커넥터도 CLI 요청이 들어올 때마다 PlayerLoop 업데이트를 요청합니다. 그래도 백그라운드 응답성을 가장 안정적으로 유지하려면 No Throttling 설정을 권장합니다.

## 빠른 시작

```bash
# Unity 연결 확인
unity-cli status

# 플레이 모드 진입 후 대기
unity-cli editor play --wait

# Unity 안에서 C# 코드 실행
unity-cli exec "return Application.dataPath;"

# 콘솔 로그 읽기
unity-cli console --type error,warning,log
```

## 동작 방식

```
터미널                                Unity Editor
──────                                ────────────
$ unity-cli editor play --wait
    │
    ├─ ~/.unity-cli/instances/*.json 스캔
    │  → Unity가 포트 8090에 있음을 확인
    │
    ├─ POST http://127.0.0.1:8090/command
    │  { "command": "manage_editor",
    │    "params": { "action": "play",
    │                "wait_for_completion": true }}
    │                                      │
    │                                  HttpServer 수신
    │                                      │
    │                                  CommandRouter 디스패치
    │                                      │
    │                                  ManageEditor.HandleCommand()
    │                                  → EditorApplication.isPlaying = true
    │                                  → PlayModeStateChange 대기
    │                                      │
    ├─ JSON 응답 수신  ←──────────────────┘
    │  { "success": true,
    │    "message": "Entered play mode (confirmed)." }
    │
    └─ 출력: Entered play mode (confirmed).
```

Unity 커넥터의 동작:
1. Editor 시작 시 `localhost:8090`에 HTTP 서버를 열고
2. `~/.unity-cli/instances/`에 프로젝트별 instance 파일을 기록하여 CLI가 연결할 수 있게 하고
3. 0.5초마다 instance 파일에 현재 상태를 갱신하고 (heartbeat)
4. 매 요청마다 리플렉션으로 `[UnityCliTool]` 클래스를 탐지하고
5. 수신된 명령을 메인 스레드의 해당 핸들러로 라우팅하고
6. 도메인 리로드(스크립트 재컴파일)에서도 유지됩니다

컴파일이나 리로드 직전에 상태(`compiling`, `reloading`)를 instance 파일에 기록합니다. HTTP 리스너가 올라오자마자 `starting`을 기록하고 에디터의 첫 update tick까지 그 heartbeat를 신선하게 유지하므로, 콜드 임포트가 "커넥터 없음"으로 보고되지 않습니다. 메인 스레드가 멈추면 timestamp 갱신이 중단되고, CLI는 새로운 timestamp가 찍힐 때까지 대기한 후 명령을 전송합니다. 플레이어 빌드는 컴파일도 리로드도 하지 않은 채 메인 스레드를 몇 분간 붙잡습니다. 이때 커넥터는 빌드 자신의 writer 스레드에서 `building`으로 heartbeat를 계속 찍으므로([플레이어 빌드](#플레이어-빌드-장기-실행) 참고), 빌드 중인 에디터가 멈춘 에디터로 오인되지 않습니다.

커넥터가 받아들인 모든 명령은 정확히 하나의 답을 받습니다. 요청마다 이를 추적하고, 에디터 메인 스레드가 요청의 queue deadline 안에 그 명령을 시작하지 못하면 커넥터가 직접 왜 바쁜지(메인 스레드를 잡고 있는 명령, heartbeat 상태, 큐 깊이)를 답하며 그 명령이 실행되지 않았다는 것을 밝힙니다. Unity가 리로드되거나 종료될 때 아직 큐에 남아 있던 요청은 리스너가 닫히기 전에 `domain reload in progress, retry`로 답하고, 버려지지 않습니다. [큐에 걸린 명령과 서버 측 deadline](#큐에-걸린-명령과-서버-측-deadline)을 참고하세요.

## 내장 명령어

| 명령어 | 설명 |
|--------|------|
| `editor` | Unity Editor play/stop/pause/refresh/quit/restart 제어 |
| `input` | 플레이 모드에서 새 Input System의 키보드/마우스/게임패드/터치 상태를 주입하고 플레이어 루프를 돌림 |
| `console` | 콘솔 로그 읽기, 필터링, 지우기 (최신 50개 항목, 스택 트레이스 캡) |
| `exec` | Unity 안에서 임의 C# 코드 실행 (`--skip_compile_check`로 컴파일 게이트 우회) |
| `api` | 로드된 타입/멤버의 실체: 종류, 어셈블리, 모든 overload의 C# 시그니처 |
| `test` | EditMode/PlayMode 테스트 실행 |
| `verify` | 컴파일, 내 코드의 에러 출력, 테스트 실행을 한 번에 짧은 출력으로 |
| `wait` | 에디터가 idle이 될 때까지 대기한 뒤 마지막 컴파일이 무엇을 반영하는지 출력 |
| `compile_status` | 마지막 컴파일이 언제 끝났고 무엇을 다시 빌드했으며 어떤 소스가 그보다 새로운지 |
| `build` | 플레이어 빌드를 job으로 시작하고 진행 상황을 지켜본 뒤 BuildReport 보고 |
| `menu` | Unity 메뉴 아이템을 경로로 실행 |
| `reserialize` | Unity 시리얼라이저를 통해 에셋 재직렬화 |
| `clear_burst_cache` | `Library/BurstCache`를 삭제해 Burst가 처음부터 다시 컴파일하게 함 |
| `burst_status` | Burst 컴파일이 끝났는지, 어떤 `[BurstCompile]` 대상이 컴파일됐는지, 무엇이 실패했는지(BCxxxx) |
| `screenshot` | 실행 중인 게임(Game 뷰, OnGUI 포함), 카메라, 또는 Scene 뷰를 PNG로 캡처 |
| `profiler` | 프로파일러 하이어라키 읽기, 녹화 제어 |
| `modal` | 에디터를 막고 있는 모달 다이얼로그 확인 및 응답 (macOS) |
| `dialogs` | 에디터 밖에서 Unity 창을 나열/클릭 — 시작 다이얼로그, 커넥터 불필요 (macOS 호스트) |
| `list` | 사용 가능한 모든 도구와 파라미터 스키마 표시 |
| `status` | Unity Editor 연결 상태 확인 |
| `update` | CLI 바이너리 자동 업데이트 |

### Editor 제어

```bash
# 플레이 모드 진입
unity-cli editor play

# 플레이 모드 진입 후 커넥터가 직접 상태 변화를 기다리게 함 (그 변화가 도메인을 리로드하면
# 생략됨 — 어느 쪽이든 CLI가 heartbeat로 상태를 확인)
unity-cli editor play --wait

# 플레이 모드 종료
unity-cli editor stop

# 결과 상태를 읽지 않고 변경만 요청 (--wait와 --no-wait는 함께 쓸 수 없음)
unity-cli editor stop --no-wait

# 일시정지 토글 (플레이 모드에서만 동작)
unity-cli editor pause

# 에셋 새로고침 (플레이모드에서는 --force 없으면 차단)
unity-cli editor refresh

# 새로고침 + 스크립트 컴파일 (플레이모드에서는 --force 없으면 차단)
unity-cli editor refresh --compile

# Unity의 컴파일된 어셈블리 캐시(Bee)를 버리고 다시 컴파일 — 캐시가 거짓말한다고
# 믿을 때 쓰는 망치; 그래도 Library/ScriptAssemblies의 mtime은 그대로일 수 있음
unity-cli editor refresh --compile clean

# 플레이모드 중 강제 새로고침
unity-cli editor refresh --force
```

`editor play`와 `editor stop`은 보고(`action`, `wanted_state`, `editor_state`, `editor_label`, `confirmed`, `answer_lost`, `waited_ms`)로 답하고, 에디터가 요청한 상태에 도달하지 못하면 0이 아닌 코드로 종료합니다. 대부분의 프로젝트에서 플레이 모드 진입/종료는 도메인 리로드를 일으키고, 리로드를 시작한 명령은 그 리로드를 넘어 자신의 결과를 보고할 수 없습니다. 그래서 커넥터는 상태 변경을 한 update tick 뒤로 미루고 CLI가 heartbeat로 이를 확인합니다 ([issue #10 write-up](docs/bugs/10-reload-loses-response.md) 참고).

`editor refresh --compile`은 컴파일이 끝날 때까지 기다린 뒤 단순한 `refresh_triggered` 대신 하나의 보고로 답합니다: `compile_requested`, `compile_succeeded`, `error_count`, `errors[]`(file/line/col/severity/code/message, 커넥터가 답할 수 있는 에러 코드에는 `hint`도 — [검증](#검증-verify) 참고), `timed_out`, `elapsed_ms`, 그리고 [컴파일 provenance](#컴파일-provenance와-wait)(`provenance`, `provenance_line`, 컴파일 이후 소스 파일이 기록되면 `stale_line`, 프로젝트 DLL이 소스보다 오래되면 `stale_assembly_line`)입니다. 컴파일 뒤에도 어셈블리가 stale이면 `CleanBuildCache` 재빌드를 한 번 요청하고, `stale_after_compile`이 그 재빌드로 DLL이 고쳐졌는지를 알려 줍니다. 최상위 `stale_assembly_*` 필드는 항상 이 컴파일이 남긴 상태를 뜻하며, 요청이 시작될 때의 상태가 아닙니다. `timed_out: true`는 대기가 5분 뒤 포기했다는 뜻이지 컴파일이 실패했다는 뜻이 아닙니다. `nothing_to_compile: true`는 요청 이후 어떤 컴파일도 실행되는 것이 관측되지 않았다는 뜻이고, 이때 `compile_succeeded`는 이 명령이 일으킨 컴파일이 아니라 에디터의 현재 컴파일 상태를 설명합니다. 컴파일이 실제로 돌았다는 증거는 커넥터의 compile-finished clock입니다: refresh 응답이 요청 시점의 값을 알려 주고 heartbeat를 그 숫자가 움직일 때까지 폴링하므로, 이미 `ready`였던 heartbeat를 끝난 컴파일로 착각하지 않습니다. 컴파일이 실패하거나 시간 초과하면 종료 코드는 1이고, 보고는 어느 경우든 출력됩니다. `--compile`은 콘솔을 먼저 비워 이전 컴파일의 에러가 이번 컴파일의 에러로 보고되지 않게 하고, `--keep-console`이 그 비우기를 생략합니다. compile-finished clock이 없는 커넥터는 heartbeat의 `compileErrors` 플래그로 대체하고 그 사실을 `notes`에 적습니다.

### 스크린샷

```bash
# 실행 중인 게임이 보여주는 화면 (OnGUI/IMGUI HUD 포함, 플레이 모드 필요)
unity-cli editor play --wait
unity-cli screenshot --mode gameview --output_path Temp/shots/hud.png

# 카메라 하나를 지정한 크기로 오프스크린 렌더 (OnGUI·에디터 오버레이 없음)
unity-cli screenshot --mode camera --width 3840 --height 2160

# Scene 뷰 카메라 — 플레이 모드가 아닐 때 모드 없는 `screenshot`의 의미
unity-cli screenshot
```

| mode | 캡처 대상 | 포함 | 제외 |
|---|---|---|---|
| `gameview` | 실행 중인 게임이 만든 프레임(`ScreenCapture.CaptureScreenshot`), Game 뷰 자체 해상도 | 게임의 OnGUI/IMGUI HUD — 측정: `Rect(40,40,400,240)`에 그린 `GUI.Box`가 정확히 그 픽셀 사각형에 나타남 | Game 뷰 탭의 에디터 UI(툴바, 해상도 드롭다운) |
| `camera` | `Camera.main`을 `--width`/`--height` 크기의 렌더 텍스처로 오프스크린 렌더 | 해당 카메라의 렌더만 | 게임 GUI(OnGUI/IMGUI) — 측정: Game 뷰가 HUD를 보여 주는 동안 1920x1080 프레임의 2,073,600 픽셀 전부가 단색 배경이었음 — 그리고 Scene 뷰 기즈모·Handles·에디터 오버레이 |
| `scene` | Scene 뷰 카메라를 `--width`/`--height`로 오프스크린 렌더 | 현재 Scene 카메라가 보는 장면 | 기즈모와 Handles(`SceneView.drawGizmos`를 토글해도 파일이 바이트 단위로 동일), Scene 뷰 오버레이 UI, 게임 GUI |

`gameview`는 플레이 모드의 기본값이고 `scene`은 에디트 모드의 기본값입니다(모드 없는 `screenshot`이 원래 하던 일). `gameview`는 플레이 모드가 필요합니다: 에디트 모드에서는 게임 프레임이 없어 Unity가 파일을 쓰지 않으므로, 멈추지 않고 그 사실을 알려줍니다. 일시정지된 게임도 프레임을 만들지 않으므로, 모드 없는 `screenshot`은 이때 `camera`로 대체하고 응답에 `"mode":"camera","fallback":"paused"`를 담습니다. `--mode gameview`를 명시하면 같은 이유로 에러를 내며, 이때는 게임 루프 없이 렌더하는 `--mode camera`를 쓰면 됩니다. Unity는 캡처를 같은 디렉터리의 `*.partial.png`에 쓰고, 파일이 완전해진(PNG 트레일러가 있는) 뒤에야 `--output_path`로 옮기므로, 실패하거나 시간 초과된 캡처는 그 경로에 있던 기존 스크린샷을 건드리지 않습니다. 쓰기가 비동기이므로 도구는 에디터 업데이트를 돌리며 완성된 파일이 생기는 즉시(최대 5초) 경로·바이트 수·해상도를 보고합니다 — 에디터 메인 스레드를 막지 않고.

픽셀은 한 캡처 안에서 비교하고, 모드 사이에서 비교하지 마세요: `gameview` 프레임은 게임 자신의 파이프라인이 백버퍼에 쓴 것이고, `camera`/`scene`은 평범한 렌더 텍스처를 읽으므로 같은 배경색도 다른 값으로 나옵니다.

에디터 창의 화면 픽셀을 읽는 모드는 없습니다. 모든 모니터 밖으로 옮겨진 에디터 창을 읽으면 에디터가 네이티브 단계에서 죽기 때문에, `screenshot`은 카메라 렌더와 게임 자체의 `ScreenCapture`만 사용합니다.

`--view`는 `--mode`의 옛 표기입니다: `--view scene`은 `--mode scene`이지만, `--view game`은 Game 뷰가 아니라 오프스크린 렌더인 `--mode camera`입니다.

### Quit

```bash
# 종료하고 Unity가 실제로 끝날 때까지 대기 (저장하지 않은 씬 변경은 버려짐)
unity-cli editor quit

# 열린 씬과 에셋을 먼저 저장
unity-cli editor quit --save

# 저장하지 않은 씬 변경을 버린다고 명시
# (--save와 --discard는 서로 모순이라 CLI가 아무것도 멈추기 전에 거부합니다:
#  unity-cli editor quit --save --discard  -> exit 1, 종료 없음)
unity-cli editor quit --discard

# 종료를 무시하거나 모달 다이얼로그가 막고 있으면 Unity를 강제 종료
unity-cli editor quit --save --force
```

`exec "EditorApplication.Exit(0)"` 대신 `editor quit`을 쓰세요. 명령 핸들러 안에서 `Exit`를 호출하면 응답과 종료가 서로 경쟁하게 되고, `EditorApplication.wantsToQuit`가 종료를 거부한 경우 그 사실을 보고할 방법이 없습니다. `editor quit`은 `quit_editor`를 보내고, 이 요청은 먼저 답한 뒤 종료를 다음 에디터 tick으로 예약합니다. 그다음 heartbeat가 `stopped`를 보고하거나 더 이상 전진하지 않을 때까지 기다리고, 60초 안에 어느 쪽도 일어나지 않으면 실패합니다(`--timeout`으로 바꿀 수 있음).

`--save` 없이 실행하면 저장하지 않은 씬은 버려지고, 응답이 그 목록을 `discardedScenes`에 담아 알려 줍니다. `--save`는 dirty 씬을 각각 동기적으로 저장하고, 제목이 없는 씬에 저장하지 않은 변경이 있으면 종료를 거부합니다. 그 씬을 저장하면 CLI가 응답할 수 없는 파일 다이얼로그가 열리기 때문입니다. `--discard`는 기본 동작을 명시한 형태입니다. 응답에는 보이는 `EditorApplication.wantsToQuit` 구독자도 나열되고, 예약된 `Exit` 뒤에도 Unity가 살아 있으면 커넥터가 그 이름을 담은 에러를 로그에 남깁니다 — 그것이 거부가 외부에서 보이는 모습입니다.

`--force`를 주면 제때 종료하지 않거나, `quit_editor` 요청을 거부하거나, 모달 다이얼로그에 막혀 있는 에디터를 강제 종료합니다(macOS/Linux는 SIGTERM, Windows는 `TerminateProcess`). 저장하지 않은 변경은 사라집니다. 커넥터가 거부한 `--save` 요청은 `--force`로도 뒤집히지 않습니다. 그 거부는 저장하지 않은 작업을 기록하지 못했다는 뜻일 수 있기 때문입니다. CLI는 프로세스 이름에 `Unity`가 들어 있는 PID에만 시그널을 보냅니다. 호스트 에디터의 PID를 볼 수 없는 컨테이너에서 CLI를 실행하면 `--force`는 실패하고 Unity는 호스트에서 중지해야 합니다. 최종 JSON은 `method`(`graceful` 또는 `terminate`)를 보고하고, `editor`에 커넥터의 씬/거부 보고가 담깁니다.

### Restart

```bash
# 종료, 재실행, 준비될 때까지 대기
unity-cli editor restart

# 종료 전에 열린 씬과 에셋 저장
unity-cli editor restart --save

# 재실행 전에 Library/와 Temp/ 삭제 (전체 재임포트 강제)
unity-cli editor restart --clean

# 특정 Unity 에디터 바이너리로 실행
unity-cli editor restart --editor-path /Applications/Unity/Hub/Editor/6000.1.0f1/Unity.app/Contents/MacOS/Unity
```

`editor restart`는 에디터를 멈추기 전에 프로젝트(실행 중인 인스턴스, `--project`, 또는 현재 디렉터리에서 해석)와 실행 파일을 먼저 찾고, 종료한 뒤 선택적으로 `Library/`와 `Temp/`를 지우고 다시 실행해서 새 에디터가 `ready`를 보고할 때까지 기다립니다. 진행 상황과 삭제한 경로는 stderr로, 최종 JSON에는 프로젝트 경로, 이전/새 PID, 삭제한 경로, 걸린 시간이 담깁니다.

기본 대기 예산은 5분이고 `--clean`이면 15분입니다. `--timeout`은 그 예산과 60초 종료 대기 모두를 덮어씁니다. Restart는 `editor quit`과 같은 방식으로 Unity를 멈추고 `--save`/`--discard`/`--force` 플래그도 똑같이 받습니다. `--force` 없이는 프로세스를 절대 죽이지 않으므로, 모달 다이얼로그가 종료를 막고 있으면 먼저 응답하세요. Restart는 Unity에 시그널을 보내고 실행할 수 있는 곳에서 실행해야 하고, 컨테이너에서는 에디터를 종료하기 전에 거부합니다. 그렇지 않으면 에디터를 멈춘 채 다시 실행할 방법이 없어지기 때문입니다. 시작이 멈춘 것 같으면 에디터 창에 다이얼로그(safe mode, 백업 복원, 버전 업그레이드)가 기다리고 있을 수 있습니다.

### 입력 주입 (플레이 모드)

```bash
# 플레이 모드부터 진입 — 주입에는 실행 중인 플레이어가 필요
unity-cli editor play --wait

# 키보드: Key enum 이름 (별칭: ctrl, alt, shift, cmd, esc, return, up, down)
unity-cli input key Space            # 기본 mode는 press
unity-cli input key Space press
unity-cli input key Space release
unity-cli input key W tap --hold_ms 200

# 마우스: Game 뷰 픽셀 (0,0은 Game 뷰의 좌상단), 델타, 버튼, 휠.
# --dx/--dy는 화면 델타: 양수가 오른쪽, 그리고 아래쪽입니다 — 스크린샷과 같습니다.
unity-cli input mouse --x 100 --y 200
unity-cli input mouse --dx 10 --dy -4
unity-cli input mouse --button left --mode click
unity-cli input mouse --scroll_y -2

# 게임패드: 가상 Gamepad가 필요할 때 추가됨; --control은 InputSystem 컨트롤 이름
# ('axis'는 'gamepad'의 별칭)
unity-cli input gamepad --control leftStick --x 1 --y 0
unity-cli input gamepad --control leftTrigger --value 0.6
unity-cli input gamepad --control buttonSouth --mode tap
unity-cli input axis --control dpad --x 1 --y 0

# 터치: 마우스와 같은 Game 뷰 픽셀; 가상 Touchscreen이 필요할 때 추가됨
unity-cli input touch --x 64 --y 128 --mode tap

# Input System이 지금 보는 것, 그리고 포커스와 InputSettings 상태
unity-cli input state

# 주입한 상태 해제, 도구가 추가한 가상 디바이스 제거, InputSettings 복원
unity-cli input reset
```

`input`은 OS 키 입력을 보내는 대신 디바이스 상태를 씁니다: 디바이스의 현재 상태에서 컨트롤 하나만 덮어쓴 `StateEvent`를 큐에 넣고(그래서 다른 키, 마우스 위치, 다른 스틱이 살아남습니다) 플레이어 루프를 돌리므로(`EditorApplication.QueuePlayerLoopUpdate`와 repaint), 플레이어 업데이트가 그 이벤트를 소비하고 게임 코드는 `wasPressedThisFrame`/`wasReleasedThisFrame`까지 같은 호출 안에서 봅니다. 상태는 펌프 뒤 한 번의 도구 업데이트로 다시 읽습니다. `--pump_frames 0`이면 그 readback이 유일한 업데이트이므로 게임 코드는 새로운 edge를 보지 못합니다. 위치 인자 축약도 됩니다(`unity-cli input key Space press` — 키와 모드는 내용으로 판별하므로 순서가 달라도 같습니다). 플래그는 항상 위치 인자보다 우선합니다. `state`와 `reset`만 플레이 모드 밖에서 동작합니다.

주입된 이벤트는 **새 Input System**(`UnityEngine.InputSystem`, 그 위의 `InputAction`)에만 보이고, 레거시 `UnityEngine.Input`이나 IMGUI `Event` 코드에는 절대 보이지 않습니다. 모든 주입 응답이 이 사실을 `legacy_input_note`에 반복합니다.

응답 필드:

| 필드 | 의미 |
|------|------|
| `consumed` | 디바이스 상태가 이제 요청과 일치함 — 펌프 뒤에 `isPressed`/`value`를 다시 읽은 값 |
| `fresh_press_frames`, `fresh_release_frames` | 플레이어 루프의 입력 업데이트가 그 컨트롤을 새로 눌림/놓임으로 보고한 횟수 — 게임 코드가 `Update()`에서 `wasPressedThisFrame`/`wasReleasedThisFrame`로 보는 값. 도구 자신의 readback 업데이트는 세지 않음 |
| `held_state`, `state` | 누르고 있는 동안 다시 읽은 값, 그리고 마지막 펌프 뒤의 값 |
| `position`, `space` | 마우스/터치: 호출자가 쓴 것과 같은 공간의 위치, `top_left_px` (Game 뷰 픽셀, 좌상단이 0,0) |
| `focus.game_view_focused` | Game View 창이 지금 에디터 포커스를 가지고 있음 |
| `focus.application_is_focused` | Input System 자신의 포커스 게이팅이 읽는 플래그; 에디터가 맨 앞 애플리케이션이 아니어도 true일 수 있음 |
| `focus.editor_focused` | Unity가 맨 앞 애플리케이션임 (`EditorApplication.isFocused`) |
| `settings` | 적용 중인 InputSettings, 도구가 바꾸기 전에 저장한 값, 그리고 `held_controls` |

도구에 중립이 아닌 주입 상태가 하나라도 있는 동안은 `InputSettings.editorInputBehaviorInPlayMode = AllDeviceInputAlwaysGoesToGameView`와 `backgroundBehavior = IgnoreFocus`도 설정합니다. 프로젝트 기본값에서는 Input System이 키보드와 포인터 이벤트를 에디터 업데이트로 미뤄 플레이어에 도달하지 않게 하고, 포커스를 잃으면 그 디바이스를 비활성화합니다 — 큐에 넣은 이벤트가 사라진 것처럼 보이던 원인입니다. 저장한 값은 `settings` 아래에 보고되고, 아무것도 잡고 있지 않게 되면, `input reset`에서, 그리고 플레이 모드가 끝날 때 복원됩니다. 프로젝트가 `InputSettings.UpdateMode.ProcessEventsManually`를 쓰면 도구는 `InputSystem.Update()`를 직접 부르지 않고 게임에 맡깁니다.

마우스와 터치 좌표는 스크린샷과 같은 Game 뷰 픽셀이고 `(0,0)`이 좌상단입니다: `--dy`와 `--y`는 아래로 커집니다. 도구는 Game 뷰 렌더 높이(플레이어 루프가 한 번 렌더한 뒤 `Screen.height`가 보고하는 값)를 써서 위쪽 모서리를 Input System의 좌하단 화면 공간으로 변환하고, 위치는 `top_left_px` 공간으로 다시 돌려줍니다. `input state`도 마우스/터치 위치를 같은 공간에 `position_space: top_left_px`로 보고합니다.

주입된 상태는 *디바이스 상태*이므로 나중에 디바이스 자신의 백엔드가 덮어쓸 수 있습니다 — 실제 마우스를 움직이면 주입한 위치가 바뀝니다. 주입 직후 `input state`를 읽거나 다시 주입하세요. 도구가 추가한 가상 디바이스(Gamepad, Touchscreen)는 `input reset`이 제거하고, 플레이 모드가 끝날 때도 자동으로 제거됩니다.

이 도구는 별도 어셈블리(`unity-connector/Editor/InputInjection/`, `com.unity.inputsystem`에 define-constrained)에 있으므로, Input System 패키지가 없는 프로젝트는 그대로 컴파일되고 `input` 도구가 없을 뿐입니다.

### 콘솔 로그

`console`은 기본적으로 출력을 작게 유지합니다: 최신 50개 항목, 항목당 최대 6개 스택 프레임,
항목당 1000자, 전체 15 KB. `--stacktrace user`가 기본값이고, `--full`은 모든 크기 제한을
해제합니다.

```bash
# 최근 50개 항목, 스택 트레이스 캡 적용 (기본값)
unity-cli console

# 모든 타입의 최근 20개 로그 읽기
unity-cli console --lines 20 --type error,warning,log

# 에러만, 한 줄씩 읽기
unity-cli console --type error --stacktrace none

# 에러의 모든 프레임 (항목 크기 제한은 유지)
unity-cli console --type error --stacktrace full --frames -1

# 크기 제한 없이 전부 (stacktrace 필터는 그대로 적용)
unity-cli console --full

# 콘솔 지우기
unity-cli console --clear
```

`--stacktrace user`는 유저 코드 프레임만 남깁니다 — 프레임의 선언 타입 루트 namespace가 Unity나
런타임 것(`UnityEngine.*`, `UnityEditor.*`, `Unity.*` (Entities/Burst/Jobs/Collections 포함),
`System.*`, `Mono.*`, `NUnit.*`)이거나, 관리되는 타입이 아예 없거나(네이티브 주소, `<Module>`,
컴파일러가 만든 `<...>` 홀더, `(wrapper ...)`), 소스가 `Library/` 아래에 있으면 엔진 노이즈로
보고, 연속된 노이즈는 `... (+N engine frames)` 한 줄로 접습니다. 프레임이 전부 엔진 노이즈인
트레이스는 `UnityEngine.Debug`가 아닌 첫 프레임을 남기므로, 손댈 위치는 항상 있습니다.

모든 캡에는 자기 플래그가 있습니다 — `--lines`, `--entry_chars`, `--max_bytes`는 0으로 제한을
없애고, `--frames`는 -1로 없애며 0이면 "프레임 없음"이 됩니다 — `--full`은 이 모두를 없앱니다.
무언가 잘렸을 때 응답은 무엇이 얼마나 잘렸는지 말해 주므로, 호출자는 추측 대신 질의를 넓힐 수
있습니다:

```json
{
  "entries": [
    "IndexOutOfRangeException : Index was out of bounds.\n... (+12 engine frames)\nFoW.FogOfWarUpdater:Update () (at Assets/FogOfWar/Runtime/Components/FogOfWarUpdater.cs:31)"
  ],
  "entries_total": 213,
  "entries_returned": 50,
  "entries_omitted": 163,
  "frames_omitted": 412,
  "chars_omitted": 0,
  "truncated": true,
  "truncated_by": ["lines", "frames"],
  "limits": { "lines": 50, "frames": 6, "entry_chars": 1000, "max_bytes": 15360, "stacktrace": "user" }
}
```

`entries`는 오래된 것부터이고 캡이 허용한 최신 항목들을 담습니다. `entries_total`은 캡을 적용하기
전, `--type`에 맞는 항목 수입니다. `--lines`만큼 일치하는 항목을 찾으면 스캔을 멈추므로 그때는
`entries_total_is_lower_bound`가 true이고 `entries_total`은 읽은 최신 일치 항목만 셉니다
(`entries_scanned`가 검사한 콘솔 항목 수를 알려 줍니다). 조기 중단이 없으면 총계는 정확합니다.
도구는 읽는 동안 Console 창의 Log/Warning/Error 토글을 켜고 검색어를 지우고 Collapse를 끈 뒤
창을 원래대로 복원하므로, `--type`은 창에 보이는 항목이 아니라 모든 항목을 좁힙니다. 무언가를
바꿔야 했다면 `console_window_filter`에 그 내용이 담깁니다(예:
`{"available":true,"note":"ignored Console window filter: log/warning hidden, search 'foo'","log_hidden":true,"warning_hidden":true,"error_hidden":false,"search":"foo","collapsed":false}`).
Unity 버전이 창 필터를 노출하지 않으면 `"available":false`와 안내 문구를 대신 보내므로, 빈
결과가 깨끗한 콘솔로 오인되지 않습니다.

캡이 들어오기 전의 `console`은 일치하는 모든 항목을 그대로 담은 배열을 돌려주었고,
`stacktrace=user`는 하드코딩된 이름 목록으로 필터링해서 Burst와 Entities 트레이스가 빠져나갔습니다
— 한 번 호출에 35 KB가 나오던 이유입니다.

### C# 코드 실행

가장 강력한 명령어입니다. Unity Editor 런타임에서 임의의 C# 코드를 실행합니다. UnityEngine, UnityEditor, ECS 및 로드된 모든 어셈블리에 접근 가능합니다. 일회성 조회나 수정을 위해 커스텀 도구를 만들 필요가 없습니다.

한 표현식만 있는 스니펫은 그 값이 그대로 반환되므로 `exec "Time.time"`은 숫자를 답합니다. 표현식을 넘어서는 본문은 statement body이므로 결과를 받으려면 `return`을 써야 합니다(`return`이 없는 본문은 실행된 뒤 `null`을 답합니다). 주요 namespace는 기본 포함. 프로젝트 전용 타입(예: `Unity.Entities`)만 `--usings`로 추가합니다. csc 컴파일러와 dotnet 런타임은 자동 탐지되고, 탐지에 실패하면 `--csc <path>` 또는 `--dotnet <path>`로 직접 지정합니다.

`System`과 `UnityEngine`은 모두 `Object`, `Random`, `Debug`를 정의하므로, 이 이름들은 기본 alias로 Unity 타입에 연결되어 CS0104가 나지 않습니다. 스니펫 맨 위의 `using` 지시문은 생성된 메서드 위로 옮겨지고, 직접 선언한 alias가 기본 alias보다 우선합니다. 기본 집합을 완전히 바꾸려면 `--no_default_usings`로 기본 import와 alias를 끄고 `--usings`로 필요한 것을 나열합니다.

**컴파일 게이트.** 모든 도구는 컴파일 검사 뒤에서 실행되므로, 지금 컴파일되지 않는 프로젝트는 호출을 실행하는 대신 에러를 답합니다. 검사는 현재 컴파일만 사용합니다: 살아 있는 컴파일러 진단과 그 컴파일에서 나온 콘솔 항목이고, 오래된 콘솔 줄이나 실패한 빌드는 쓰지 않습니다. 막힐 때 에러는 첫 세 진단(`file:line:col CODE message`)을 알려 주고, 그 컴파일이 얼마나 오래됐는지 말하고, 탈출구를 다시 알려 줍니다. 그래도 실행하려면 `--skip_compile_check`(또는 `--params '{"skip_compile_check":true}'`)를 주세요 — 진단용이거나 `exec`로 소스를 고칠 때 유용합니다.

```bash
unity-cli exec "return Application.dataPath;"
unity-cli exec "return EditorSceneManager.GetActiveScene().name;"
unity-cli exec "return World.All.Count;" --usings Unity.Entities
unity-cli exec "return typeof(Object).FullName;"                  # UnityEngine.Object
unity-cli exec "using Object = System.Object; return typeof(Object).FullName;"   # System.Object
unity-cli exec "return typeof(Object).FullName;" --no_default_usings --usings System
unity-cli exec "return 1;" --skip_compile_check                 # 컴파일 에러가 있는 프로젝트에서 실행

# stdin으로 파이프하면 shell escaping 문제 없음
echo 'Debug.Log("hello"); return null;' | unity-cli exec
echo 'var go = new GameObject("Marker"); go.tag = "EditorOnly"; return go.name;' | unity-cli exec
```

**Async와 coroutine.** 본문에서 `await`을 사용할 수 있고, 반환한 `Task`는 커넥터가 대신 대기한다 — 응답에는 `Task` 객체가 아니라 그 결과가 담긴다. 대기 중에도 에디터 메인 스레드를 막지 않으므로, 에디터가 계속 동작해야 끝나는 작업(에디터 콜백, 플레이 모드 전환)도 정상적으로 완료된다.

```bash
unity-cli exec "await Task.Delay(100); return EditorApplication.timeSinceStartup;"
unity-cli exec "return Task.Run(() => AssetDatabase.Refresh());"
unity-cli exec "return Task.Run(() => ImportEverything());" --await_timeout 300000
```

`yield return`을 사용한 본문은 coroutine으로 실행된다: `yield return null`은 에디터 update 1회를 기다리고, yield된 `Task`는 대기되며, 그 외의 yield 값은 수집되어 list로 반환된다. Unity의 `YieldInstruction`(WaitForSeconds, WaitForEndOfFrame)은 조용히 무시되는 대신 거부된다 — 시간을 기다리려면 `await Task.Delay(ms)`를 사용한다.

```bash
echo 'for (int i = 0; i < 3; i++) { yield return null; yield return i; }' | unity-cli exec   # -> [0, 1, 2]
```

대기는 `--await_timeout <ms>`로 제한된다 (기본 30000, `0`은 무한 대기). 대기가 이 시간을 넘기면 응답이 `{"timed_out": true, "status": "WaitingForActivation", "steps": N}`를 담은 error로 온다 — 작업은 에디터에서 계속 동작 중이며 그 응답으로는 결과를 알 수 없다. domain reload는 대기 중인 결과를 조용히 사라지게 하므로, 오래 걸리는 결과는 static field나 `ScriptableObject`에 보관하고 두 번째 `exec`로 읽어온다.

**결과 형태.** 반환값은 public field와 읽을 수 있는 public property에서 직렬화되므로, anonymous type, property만 가진 에디터 API 객체, list, dictionary, 중첩 객체가 `ToString()` 문자열이 아니라 JSON 객체로 돌아옵니다. 컬렉션은 100개, 깊이는 6에서 잘리고, cycle은 `"<cycle: Type>"`, 예외를 던지는 property는 형제를 잃지 않은 채 `"<error: Type: message>"`가 됩니다. Unity 객체는 식별 문자열(`"Main Camera (UnityEngine.Camera)"`)을 유지하고, Unity math struct는 `Vector3.normalized` 같은 파생값 대신 데이터 멤버를 노출합니다. 직렬화나 `ToString()` fallback이 일어나면 `data`는 문자열이 되고 `meta`에 `serialization_error`가 담깁니다 — `--meta`로 확인합니다.

**컴파일 캐시.** `exec`는 스니펫을 `csc` subprocess로 컴파일하고, 에디터 메인 스레드를 막지 않고 기다린 뒤 로드/실행합니다. 같은 호출(같은 본문, 같은 `--usings`, 같은 reference 집합)은 이미 로드된 어셈블리를 재사용하므로 반복 probe가 프레임 하나로 끝납니다. 응답은 `meta: {"compile":"compiled|cached","compile_ms":N,"reference_assemblies":N}`로 이를 알리며 `data`와 분리되어 있어 결과 파싱은 그대로입니다. `--meta`를 주면 stderr로 출력됩니다. `compile_ms`는 csc subprocess만의 시간이라 캐시 hit이면 `0`이고, 스니펫 자체의 실행 시간(`await` 포함)은 여기에 들어가지 않습니다:

```bash
unity-cli exec --meta "return Application.dataPath;"   # meta: {"compile":"compiled","compile_ms":403,…}
unity-cli exec --meta "return Application.dataPath;"   # meta: {"compile":"cached","compile_ms":0,…}
```

기본은 꺼져 있습니다: 그렇지 않으면 `exec … 2>&1 | tail -1`이 결과 대신 이 안내를 집어 갑니다. 캐시 조회는 256개로 제한되며, evict된 스니펫의 어셈블리는 어차피 다음 domain reload 전까지 domain에 남습니다 (Mono는 unload하지 못합니다). 컴파일 중 에디터가 reload되면 stale 컴파일을 실행하지 않고 error로 응답합니다.

`exec`는 실제 C#을 컴파일하고 실행하므로, 커스텀 도구가 할 수 있는 모든 것을 할 수 있습니다 — ECS 엔티티 조사, 에셋 수정, 내부 API 호출, 에디터 유틸리티 실행. AI 에이전트에게 이것은 **도구 코드를 한 줄도 작성하지 않고 Unity 전체 런타임에 즉시 접근**할 수 있다는 의미입니다. stdin 파이프를 사용하면 복잡한 코드에서 shell escaping 문제를 피할 수 있습니다.

### API 조회

에디터가 로드한 어셈블리 — 프로젝트 코드가 컴파일되는 바로 그 집합 — 에서 읽은 API의 실체입니다. `Library/PackageCache/com.unity.<pkg>@<hash>/…`를 grep하는 것은 더 느리고, 컴파일된 것과 어긋납니다(해시 폴더명, stub, `#if` 분기). 리플렉션은 어긋나지 않습니다.

```bash
# 종류, namespace, 어셈블리, base 타입, 인터페이스, 멤버 수
unity-cli api Vector3

# 한 멤버의 모든 overload를 C#으로: 수정자, 제네릭 + 제약, ref/out/in/params
unity-cli api Vector3.Lerp

# property, 중첩 타입 — 또는 멤버 이름만: 어떤 타입이 선언하는지
unity-cli api EditorApplication.isCompiling
unity-cli api GetSingletonBuffer --assembly Entities

# 검색 범위를 좁히고, Unity가 자체 모듈에 제공하는 XML 문서를 요청
unity-cli api List.Add --assembly mscorlib --limit 5
unity-cli api UnityEngine.Object.FindObjectOfType --docs
```

```json
{
  "summary": "UnityEngine.Vector3 (struct, UnityEngine.CoreModule): 2 'Lerp' matches",
  "types": [{
    "type": "UnityEngine.Vector3", "kind": "struct", "assembly": "UnityEngine.CoreModule",
    "members": [
      { "name": "Lerp", "signature": "public static Vector3 Lerp(in Vector3 a, in Vector3 b, float t)" },
      { "name": "Lerp", "signature": "public static Vector3 Lerp(Vector3 a, Vector3 b, float t)" }
    ]
  }]
}
```

타입 질의는 간단한 이름, 전체 이름, 그리고 제네릭의 두 표기(`BufferLookup`, `BufferLookup<T>`, `` BufferLookup`1 ``)를 받습니다. 여러 타입이 일치하면 가장 잘 맞는 것부터 각각(최대 `--max_types`, 기본 5) 설명합니다. `[Obsolete]` 멤버는 메시지, 에러 여부, 그리고 안내가 대체 멤버를 지목하면 그 대체(`Use X instead`, `(UnityUpgradable) -> X`)를 보고합니다. `--docs`는 어셈블리 옆에 있는 `.xml`의 `<summary>`를 더합니다 — Unity는 자체 모듈에 문서를 제공하고, 대부분의 서드파티 어셈블리는 제공하지 않으며, 문서 파일이 없으면 아무것도 출력하지 않습니다.

실체가 없는 질의는 성공한 조회가 아닙니다. 커넥터는 오타에 가장 가까운 실제 이름과 무엇이 없었는지 말하는 문장을 답하고, CLI는 그 문장을 먼저 출력한 뒤 답을 그대로 보여 주고 exit 0으로 끝냅니다:

```
no member named 'Lerpp' on UnityEngine.Vector3, System.Numerics.Vector3; closest: Lerp, Slerp
{ "exact": false, "matched_by": "type.member", ... }
```

가까운 것조차 없는 질의(`api NoSuchTypeXyz`)는 커넥터의 `no type or member named …` 메시지와 함께 exit 1입니다. 응답에는 `exact`, `note`, `matched_by`(`type`, `type.member`, `member`, `closest_types`)가 실리므로 스크립트가 문장을 파싱하지 않고도 판단할 수 있습니다. 응답이 `note` 없이 inexact만 알리면 CLI가 직접 `no exact match; closest: …` 줄을 만듭니다.

인덱스는 lazy하게 만들어져 에디터 세션 동안 캐시됩니다. `--budget <ms>`(기본 1500)가 한 호출의 시간을 제한하고, `index.complete: false`는 모든 어셈블리가 검색 가능해지기 전에 예산이 끝났다는 뜻이므로 예산을 올리거나 다시 물어보세요. 이 명령은 프로젝트가 컴파일되지 않는 동안에도 쓸 수 있습니다: 로드된 어셈블리는 마지막으로 정상이던 것들이고, 바로 그때가 실제 API가 필요한 순간입니다.

### 메뉴 아이템

```bash
# Unity 메뉴 아이템을 경로로 실행
unity-cli menu "File/Save Project"
unity-cli menu "Assets/Refresh"
unity-cli menu "Window/General/Console"
```

안전을 위해 `File/Quit`은 차단됩니다.

### 에셋 리시리얼라이즈

AI 에이전트(와 사람)는 Unity 에셋 파일 — `.prefab`, `.unity`, `.asset`, `.mat` — 을 텍스트 YAML로 직접 수정할 수 있습니다. 하지만 Unity의 YAML 시리얼라이저는 엄격합니다: 필드 누락, 잘못된 들여쓰기, 오래된 `fileID` 하나면 에셋이 조용히 깨집니다.

`reserialize`가 이걸 해결합니다. 텍스트 수정 후 실행하면 Unity가 에셋을 메모리에 로드한 뒤 자체 시리얼라이저로 다시 기록합니다. Inspector에서 수정한 것과 동일한, 깨끗하고 유효한 YAML 파일이 됩니다.

```bash
# 전체 프로젝트 리시리얼라이즈 (인자 없이)
unity-cli reserialize

# 프리팹의 Transform 값을 텍스트로 수정한 후
unity-cli reserialize Assets/Prefabs/Player.prefab

# 여러 씬을 일괄 수정한 후
unity-cli reserialize Assets/Scenes/Main.unity Assets/Scenes/Lobby.unity

# 머티리얼 속성 수정 후
unity-cli reserialize Assets/Materials/Character.mat
```

이것이 텍스트 기반 에셋 수정을 안전하게 만드는 핵심입니다. 이게 없으면 YAML 필드 하나 잘못 놓은 것이 런타임에서야 드러나는 프리팹 파손으로 이어집니다. 이게 있으면 **AI 에이전트가 어떤 Unity 에셋이든 텍스트로 자신 있게 수정**할 수 있습니다 — 프리팹에 컴포넌트 추가, 씬 계층 구조 변경, 머티리얼 속성 조정 — 결과가 정상적으로 로드된다는 것을 보장하면서.

### Burst 캐시

Burst는 컴파일된 JIT 라이브러리를 `<프로젝트>/Library/BurstCache`에 보관합니다. 이 캐시가 오래되면 — Burst 패키지 업그레이드, Burst 옵션 변경, 혹은 job이 예전 코드처럼 동작할 때 — 캐시를 지우고 Burst가 다시 컴파일하게 하는 것이 해결책입니다.

`clear_burst_cache`가 셸 없이 에디터 안에서 그 일을 합니다. `Library/BurstCache` 하위 경로만 건드리고(경로 인자는 아예 받지 않습니다), 삭제한 파일 수와 용량을 보고하며, dry run으로 같은 정보를 미리 볼 수 있습니다.

```bash
# 캐시 전체 삭제 — Burst가 다음 컴파일 때 다시 만듭니다
unity-cli clear_burst_cache

# 무엇이 지워질지 용량과 함께 먼저 확인
unity-cli clear_burst_cache --dry_run

# JIT 라이브러리 캐시만, AOT 설정 해시는 유지
unity-cli clear_burst_cache --scope jit
```

Burst가 아직 매핑 중인 파일은 삭제 전체를 중단시키는 대신 경로별로 보고되므로, 부분 삭제 결과도 항상 근거가 남습니다.

### Burst 컴파일 상태

`burst_status`는 나머지 절반을 답합니다: Burst가 컴파일을 *끝냈는지*, 무엇을 컴파일했는지, 무엇이 실패했는지. Burst는 Unity의 스크립트 컴파일과 별개의 일정으로 자체 컴파일하므로, `compile: ok`에 테스트 통과, 빈 콘솔이 겹쳐도 `[BurstCompile]` job이 실제로 컴파일됐다는 증거가 되지 않고, `BCxxxx` 에러는 컴파일이 이미 깨끗해 보인 뒤에 나타날 수 있습니다. `Library/BurstCache`를 직접 읽어도 답은 없습니다(번들 파일명만 있고 상태도 에러도 없으며, 이전 작업의 코드가 섞여 있습니다).

```bash
# idle 여부, 컴파일된 대상 수, Burst 에러
unity-cli burst_status

# 최대 60초 동안 Burst가 끝나기를 기다린 뒤 답한다
unity-cli burst_status --wait 60

# 조건에 맞는 대상과 상태 목록
unity-cli burst_status --type ScaleValuesJob
unity-cli burst_status --assembly MyGame.Runtime --list

# 이번 호출에서 Burst를 강제로 다시 컴파일하게 함
unity-cli burst_status --compile --wait 120 --timeout 130000
```

```
Burst idle: 3/4 targets compiled, 1 Burst error(s).
  Assets/Jobs/HeavyJob.cs:39 BC1006 The `catch` construction is not supported
```

대상은 Burst가 직접 나열하는 `[BurstCompile]` job과 static method이고, 목록에는 프로젝트 자체 어셈블리의 대상만 올린다(패키지와 엔진 대상은 따로 세며, 목록에 넣으려면 `--include_packages`가 필요하다). 상태는 `compiled`, `failed`, `not-compiled-yet`, `unknown` 네 가지다. Burst는 어셈블리 컴파일이 끝날 때 아는 대상 전부를 eager 컴파일하므로, “Burst의 큐가 idle” + “이 대상을 지칭하는 에러가 없음”이 `compiled`의 뜻이고, 에러가 대상을 지칭하지 못하면 추측 대신 `notes`에 그 수를 적는다. `method` 필드는 답이 eager 큐에서 나왔는지 이번 호출이 강제한 재컴파일(`forced_recompile`)에서 나왔는지 알려 준다.

job producer 에러가 가리키는 위치는 엔진이 생성한 job struct이므로, 도구는 엔진 밖의 첫 stack frame — 사용자의 파일과 라인 — 을 우선해 `file:line:col BCxxxx message` 형태로 출력한다. `verify`가 컴파일 뒤에 이 단계를 실행하므로 별도로 물어 볼 필요가 없고, `--no-burst`로 건너뛸 수 있다.

이 도구는 `com.unity.burst`가 설치된 프로젝트에서만 빌드된다(어셈블리가 패키지로 constrained). Burst가 없는 프로젝트에서는 명령이 unknown이고 `verify`에 `burst:` 줄이 나오지 않는다.

Burst가 public으로 열어 둔 면은 좁다. `IsCurrentCompilationDone`, `TriggerRecompilation`, 인스펙터의 대상 목록(`GetInspectorEntryPoints`), eager 컴파일 상태는 internal이거나 private이므로 커넥터는 reflection으로 묶고, 이름이 없으면 실패 대신 `unknown`으로 답한다. Unity 6000.6.5f1 + Burst 2.0.0에서 측정한 사실: 대상별 컴파일을 강제하는 경로(`BurstCompilerService.GetDisassembly`)는 유효한 대상에서도 `Unable to compile method in BurstCompilerService::CompileAsync`로 실패해 상태 판정에 쓰지 않는다. `--compile`은 Burst 자신의 재컴파일 패스를 돌리며, 코드 수준 에러(BC1006/BC1091)는 다시 기록되지만 어셈블리 post-process 중에만 나오는 에러(BC1051, job struct의 관리되는 필드)는 다시 나오지 않는다. 또한 Burst는 method의 에러를 세션당 한 번 기록하므로, 비운 콘솔은 실제로 실패하는 job에 대해 조용할 수 있다. `notes`에 근거가 적힌다.

### 프로파일러

```bash
# 프로파일러 하이어라키 읽기 (마지막 프레임, 최상위)
unity-cli profiler hierarchy

# 재귀적 드릴다운
unity-cli profiler hierarchy --depth 3

# 이름으로 root 지정 (substring match) — 특정 시스템에 집중
unity-cli profiler hierarchy --root SimulationSystem --depth 3

# 특정 item ID로 드릴다운
unity-cli profiler hierarchy --parent 4 --depth 2

# 최근 30프레임 평균
unity-cli profiler hierarchy --frames 30 --min 0.5

# 특정 프레임 범위 평균
unity-cli profiler hierarchy --from 100 --to 200

# 필터 및 정렬
unity-cli profiler hierarchy --min 0.5 --sort self --max 10

# 프로파일러 녹화 켜기/끄기
unity-cli profiler enable
unity-cli profiler disable

# 프로파일러 상태 확인
unity-cli profiler status

# 캡쳐된 프레임 초기화
unity-cli profiler clear
```

### 테스트 실행

Unity Test Framework를 통해 EditMode/PlayMode 테스트를 실행합니다.

```bash
# EditMode 테스트 실행 (기본값)
unity-cli test

# PlayMode 테스트 실행
unity-cli test --mode PlayMode

# 열린 dirty 씬 저장 후 테스트 실행
unity-cli test --auto-save-scenes

# 테스트 하나(전체 이름) 또는 클래스 하나(namespace를 포함한 클래스 이름)만
unity-cli test --filter MyNamespace.MyTests
unity-cli test --filter MyNamespace.MyTests.SpecificTest

# 지정한 어셈블리의 테스트만 실행 (쉼표로 구분)
unity-cli test --assembly MyGame.Tests.Editor

# 어셈블리가 소스보다 오래됐어도 실행
unity-cli test --allow-stale
```

`--filter`는 테스트의 전체 이름에 점(.)으로 구분된 구간 단위로 포함되는지로 판단합니다: `MyTests`나 `MyNamespace.MyTests`는 그 클래스의 테스트를, `MyTests.SpecificTest`는 테스트 하나를, `MyNamespace`는 그 namespace를 실행하며, 구간의 일부(`My`)만으로는 아무것도 선택되지 않습니다. 정규식 문법(`^ $ * + ? [ ] { } | \`)이 들어간 filter는 전체 이름에 대한 정규식으로 쓰입니다. 아무 테스트도 선택되지 않은 실행은 통과가 아니라 실패입니다: `test`는 `no tests matched (filter …, assembly …)`와 함께 exit 1로 끝나며, 이는 테스트가 하나도 실행되지 않았을 때 `verify`가 내리는 판정과 같습니다. `--assembly`를 주면 그 선택 범위 *밖*의 stale 어셈블리도 함께 보고합니다(`N stale assembly(ies) are outside --assembly; the tests will not load them`) — 그 실행은 해당 어셈블리를 로드하지 않기 때문입니다.

Unity Test Framework 패키지가 필요합니다. 열린 씬에 저장되지 않은 변경이 있으면 기본적으로 테스트를 막습니다. `--auto-save-scenes`로 먼저 저장하거나, `--allow-dirty-scenes`로 그대로 실행할 수 있습니다. 테스트 대상 프로젝트 어셈블리가 소스보다 오래된 경우에도 막습니다 — stale DLL은 예전 코드를 실행하고, 그 결과는 진짜 결과와 똑같이 보입니다. `unity-cli editor refresh --compile`로 다시 빌드하거나, 의도한 것이라면 `--allow-stale`을 주세요. PlayMode 테스트는 도메인 리로드를 트리거하며, CLI가 자동으로 결과를 폴링합니다.

테스트는 몇 분씩 걸리므로 `test`의 호출 예산은 전역 120초 대신 30분이 기본값입니다. `--timeout <ms>`가 이를 덮어쓰고, `--timeout 0`은 deadline 없음(PlayMode 결과 폴링 포함)을 뜻합니다. 예산을 넘긴 실행은 잃어버린 에디터가 아니라 클라이언트 deadline으로 보고됩니다.

### 검증 (verify)

프로젝트가 컴파일되고 테스트가 통과하는지 한 번의 호출로 확인합니다. 에이전트 컨텍스트에 맞게 출력이 짧습니다.

```bash
# 컴파일, 내 코드의 에러 출력, 그 다음 EditMode 테스트 실행
unity-cli verify

# 컴파일까지만
unity-cli verify --no-tests

# Burst 단계 건너뛰기
unity-cli verify --no-burst

# 일부 테스트만 (--filter, --mode PlayMode도 가능)
unity-cli verify --assembly MyGame.Tests.Editor

# 어셈블리가 소스보다 오래됐어도 테스트 실행
unity-cli verify --allow-stale

# 텍스트 대신 JSON 객체 하나
unity-cli verify --json
```

```
compile: 1 error(s)
  Assets/Scripts/Player.cs:42:13 CS0103 The name 'speed' does not exist in the current context
```

에러가 에이전트가 바로 손댈 수 있는 종류일 때 — 컴파일러가 찾지 못한 멤버나 타입, 또는 obsolete API — 커넥터는 로드된 어셈블리에서 답을 찾아 에러 아래에 출력합니다:

```
compile: 1 error(s)
  Assets/Scripts/Player.cs:12:9 CS1061 'Transform' does not contain a definition for 'positon' and no accessible extension method …
    hint: closest: Transform.position, Transform.localPosition, Rigidbody.position
```

이는 `CS0117`/`CS1061`(멤버 없음: 지목한 타입의 가장 가까운 멤버부터, 그다음 다른 타입의 같은 이름), `CS0246`/`CS0234`(타입/namespace 없음: 가장 가까운 실제 타입과 그것이 사는 어셈블리), `CS0619`/`CS0618`(obsolete: 안내가 지목한 대체 멤버를 실제 시그니처로 해석 — `hint: use UnityEngine.Object.GetEntityId()`)를 다룹니다. 에러당 최대 세 후보, 힌트당 200자이고, 이 패스 전체는 결과당 100 ms 예산을 가집니다(`compile_diagnostics --hint_budget <ms>`, `--include_hints false`로 끔). 조회는 캐시에서 반복됩니다. 힌트는 실제 답이 있을 때만 나옵니다: 파서가 모르는 메시지나 멤버를 지목하지 않는 deprecation 안내는 아무것도 덧붙이지 않습니다.

C# 컴파일이 깨끗해도 Burst 줄이 따라 나온다. Burst는 스크립트 컴파일 밖에서 컴파일하고 `BCxxxx` 에러는 깨끗해 보인 뒤 도착할 수 있기 때문이다:

```
compile: ok
compiled 14:02:11 (3 s ago, took 2.2 s) · 1 source file changed since previous compile · rebuilt: Game, Game.Tests.Editor
burst: 14 targets compiled, 0 error(s)
tests: 31/31 passed
```

`compiled ...` 줄은 [컴파일 provenance](#컴파일-provenance와-wait)입니다: 이 판정이 어느 컴파일에서 나왔는지, 그리고 프로젝트의 무언가가 그 컴파일보다 새로운지를 알려 줍니다.

Burst 에러는 컴파일 에러와 같은 형태로 출력되고, 테스트가 실행되기 전에 verify를 실패시킨다(exit 1). `--burst-wait <초>`는 Burst가 idle이 되기를 기다리는 시간의 상한이다(기본 60, `--timeout`을 그보다 크게 유지할 것). Burst 확인을 할 수 없는 경우 — Burst가 설치되지 않아 `burst_status` 도구가 없거나 에디터가 응답하지 않는 경우 — 는 아무것도 출력하지 않거나 메모만 남기고 판정은 바꾸지 않는다.

`verify`는 먼저 콘솔을 비웁니다 (Burst 에러는 코드가 바뀐 뒤에도 콘솔에 남고, 다음 컴파일에서 Burst가 살아 있는 에러를 다시 기록합니다. `--keep-console`로 생략 가능). 그다음 리프레시와 컴파일을 하고, 커넥터의 `compile_diagnostics` 도구로 이번 컴파일의 에러를 출력합니다. `Library/`와 `PackageCache/`의 에러는 다른 실패가 없을 때만 보여 줍니다. 이어서 프로젝트 어셈블리가 소스보다 오래됐으면(DLL이 예전 코드를 실행하게 되므로) 테스트를 거부하고, `--allow-stale`이 이를 덮어씁니다. 이미 끝난 컴파일이 DLL을 다시 쓸 시간이 있었던 어셈블리는 거부하지 않습니다 (`--json`과 `unity-cli wait`는 `not stale:`로 보고합니다) — [컴파일 provenance](#컴파일-provenance와-wait) 참고. 그렇지 않으면 저장되지 않은 열린 씬을 저장하고 테스트를 실행해 `passed/total`과 실패마다 첫 줄을 출력하고, 실패한 테스트가 던지는 대신 로그로 남긴 예외를 그 아래에 붙입니다 (`logged InvalidOperationException: … (at file:line)`; Entities는 시스템이 `OnUpdate`에서 던진 예외를 로그로 남기므로, 나중에 실패하는 assertion은 그 예외를 언급하지 않습니다). `--max`로 두 목록의 길이를 제한합니다 (기본값 20). Burst 줄은 Burst 대상이 있거나 보고할 것이 있을 때만 나옵니다. 종료 코드 0은 컴파일 성공, Burst 무오류, 모든 테스트 통과를 뜻하고, 1은 컴파일 에러, Burst 에러, stale 어셈블리, 실패한 테스트, 또는 실행된 테스트 없음을 뜻하며, 2는 에디터가 응답하지 못함(실행 중이 아님, 시간 초과, 모달 다이얼로그에 막힘)을 뜻한다. 다이얼로그는 응답 명령과 함께 표시됩니다. 0.7.0 이전 커넥터에는 `compile_diagnostics`가 없고 `--assembly`를 무시합니다. 이때 `verify`는 콘솔 에러를 보여 주고 그 사실을 알립니다.

`verify`는 테스트를 실행하므로 호출 예산이 전역 120초 대신 30분입니다. `--timeout <ms>`가 이를 덮어쓰고(`--timeout 0`은 deadline 없음), 직접 설정할 때는 `--burst-wait`보다 크게 유지하세요.

### 컴파일 provenance와 `wait`

모든 컴파일 결과는 어느 컴파일을 반영하는지 말해 주므로, 깨끗한 답과 오래된 답을 구분할 수 있습니다:

```
compiled 14:02:11 (3 s ago, took 2.2 s) · 1 source file changed since previous compile · rebuilt: Game, Game.Tests.Editor
stale: Assets/Scripts/Player.cs modified after this compile — run verify again
stale assembly: Game.Tests.dll (built 14:01:50) is older than Assets/Game/Tests.cs (14:04:02) — run unity-cli editor refresh --compile
not stale: Game.Tests.dll (built 14:01:50) is older than Assets/Game/Tests.cs (14:04:02), but the compile that started 14:05:10 read that source and kept the DLL; a moved mtime alone does not mean the DLL is out of date
```

세 번째 줄은 어셈블리의 출력 DLL이 없거나 최신 `.cs`/`.asmdef`/`.asmref` 소스보다 오래된 경우에 나옵니다(프로젝트와 `file:`/embedded 패키지만; `Packages/<name>` 경로는 패키지가 실제로 사는 디렉터리로 해석되고, 그래서 `file:` 패키지가 포함됩니다). Unity의 어셈블리 캐시가 재빌드를 건너뛴 채 컴파일이 "성공"했을 수 있습니다. 이 상태에서 `verify`와 `test`는 `--allow-stale` 없이는 테스트를 거부하고, `editor refresh --compile`은 보고 전에 `CleanBuildCache` 재빌드를 한 번 요청해 `stale_after_compile`로 결과를 알려 줍니다.

네 번째 줄은 반대 경우이고, 의도적으로 거부가 아닙니다: Unity는 내용이 최신이라고 판단한 DLL을 다시 쓰지 않으므로, mtime만 움직인 소스(`touch`, 재체크아웃)는 컴파일이 읽었어도 영원히 DLL보다 오래된 상태로 남습니다. 마지막 컴파일이 그 소스가 기록된 *뒤에* 시작했다면 그 어셈블리는 `stale_assembly_covered_count`에 포함되고 `stale_assembly_covered_line`에 이름이 오르며 stale로 보고되지 않습니다 — 그렇지 않으면 `touch` 뒤의 모든 `verify`가 거부하고, clean 재빌드를 요구하고, 다시 거부하게 됩니다. 6000.6.5f1에서 측정: touch한 파일은 `rebuilt_assemblies: []`로 11초 컴파일을 만들고 DLL mtime은 그대로였고, `editor refresh --compile clean`(`RequestScriptCompilationOptions.CleanBuildCache`, 65초)도 Bee가 캐시한 노드 출력을 기록된 timestamp로 복원하기 때문에 그 mtime을 건드리지 않았습니다. 그래서 이 경우를 결정하는 것은 DLL의 mtime이 아니라 컴파일 clock이고, `--compile clean`은 캐시가 거짓말한다고 믿을 때 쓰는 망치로 남습니다.

첫 줄은 커넥터의 것입니다: 마지막 스크립트 컴파일이 언제 끝났는지(그것이 트리거한 리로드 포함), 얼마나 걸렸는지, 그 이전 컴파일 이후 소스 파일 몇 개가 기록됐는지, 어떤 어셈블리를 다시 빌드했는지. 두 번째 줄은 `Assets/` 아래나 `file:`/embedded 패키지의 `.cs`, `.asmdef`, `.asmref`가 그 컴파일 **이후**에 기록됐을 때 나옵니다: 수정은 프로젝트에 있지만 위의 결과에는 없고, 그것이 에이전트를 `sleep 45 && unity-cli compile_errors` 폴링 루프로 몰던 의문입니다.

| 보이는 곳 | 필드 |
|---|---|
| `verify` | 컴파일 판정 뒤에 출력; `--json`이면 `compile.provenance` |
| `editor refresh --compile` | `provenance`, `provenance_line`, `stale_line`, 그리고 컴파일되지 않은 소스를 지목하는 note |
| `compile_diagnostics` | `provenance` 아래에 중첩(`--include_provenance false`로 생략) |
| `compile_status` | 객체 전체: `compiled_at_ms`, `compiled_at` (ISO), `duration_ms`, `changed_since_previous_compile`, `rebuilt_assemblies`, `stale_files`, `stale_assembly_count`, `stale_assemblies[]`, `stale_assembly_line`, `stale_assembly_covered_count`, `stale_assembly_covered_line`, `scan_ms`, `scan_complete`, `provenance_line`, `stale_line` |

```bash
# 에디터가 idle이 될 때까지 대기한 뒤 상태와 provenance 줄을 출력
unity-cli wait

# 방금 저장한 수정이 마지막 컴파일에 들어 있다는 증거까지 요구
unity-cli wait --compile --timeout 120

# 대기 없이 provenance만
unity-cli compile_status
unity-cli compile_status --scan false        # timestamp만, 파일시스템 순회 없이
unity-cli compile_status --scan_budget 1000  # 스캔에 최대 1초 허용
```

`wait`는 에디터가 idle이면(heartbeat가 `ready`, `playing`, `paused` — `starting`, `compiling`, `refreshing`, `reloading`, `building`이 아니고 열린 다이얼로그가 없음) 0으로, 포기하면 1로, 에디터가 응답하지 못하면 2로 종료하며 이때 모달 다이얼로그와 그것을 응답하는 명령을 지목합니다. `--compile`은 "idle"을 "idle이고 증명된 컴파일"로 바꿉니다: 커넥터가 그것을 증명할 수 없으면(heartbeat에 compile clock이 없거나 `compile_status`가 답하지 않음) `wait --compile`은 이유와 함께, 그리고 실제로 증명하는 명령과 함께 exit 1로 끝납니다. 따라서 `wait --compile && test`가 이 wait가 빌드됐다고 본 적 없는 코드로 테스트를 돌리는 일은 없습니다. `--timeout`은 초 단위이고 명령 이름 뒤에 와야 합니다. 그 앞의 전역 `--timeout`은 밀리초 단위 요청 timeout으로 남습니다.

`--compile`을 주면 0으로 종료하기 위해 둘 중 하나가 참이어야 합니다: `wait`가 시작된 뒤 커넥터의 compile-finished clock이 움직였거나(비교의 양쪽이 모두 커넥터 stamp이므로, 에디터와 시계가 다른 에이전트 샌드박스에서도 동작합니다), 소스 스캔이 마지막 컴파일보다 새로운 것을 찾지 못한 경우(수정이 이미 들어 있다는 뜻)입니다. `wait`가 시작되기 *전에* 끝난 컴파일과 stale 소스는 증거가 되지 못하므로 `wait`는 계속 기다리고, 10초 뒤 그 사실을 알려 줍니다 — Unity는 포커스가 있을 때만 자동 리프레시하기 때문입니다. 터미널에서 구동되는 에디터는 결코 포커스를 갖지 않으므로, 그때 정직한 답은 note에 `run unity-cli verify`를 담은 exit 1입니다:

```
Unity (port 8090): ready
gave up after 11s: still ready — no compilation started within 10s of finding uncompiled sources
compiled 14:02:11 (7 min ago, took 3.4 s) · 1 source file changed since previous compile · rebuilt: Assembly-CSharp
stale: Assets/Scratch/Stale.cs modified after this compile — run verify again
  (Unity only auto-refreshes while it has focus; run unity-cli verify (or unity-cli editor refresh --compile) to compile them)
```

스캔은 `Assets/`와 embedded, `file:`/local 패키지를 순회합니다 — `Library/PackageCache` 아래의 registry/git 패키지는 당신의 코드가 아니고 예산을 전부 써 버립니다. 에디터 메인 스레드의 200 ms를 쓰고, 프로젝트가 그보다 크면 에디터를 멈추게 하는 대신 그렇다고 말합니다(`scan_complete: false`, 수치는 하한). `compile_status`가 없는 커넥터는 provenance를 줄 수 없습니다: `wait`와 컴파일 보고는 그때 상태를 출력하고 provenance를 쓸 수 없다고 말합니다.

### 플레이어 빌드 (장기 실행)

```bash
# Build Profile로 빌드하고 결과를 기다림 (진행 상황은 stderr, JSON은 stdout)
unity-cli build --profile macOS_dev --output Builds/Mac/App.app

# 같은 빌드를 development 플레이어 옵션으로
unity-cli build --profile macOS --output Builds/Mac/App.app --development

# BuildTarget으로, 씬을 직접 지정해 빌드
unity-cli build --target StandaloneOSX --output Builds/Mac/App.app --scenes Assets/Main.unity

# 시작만 하고 떠나기; 나중에 폴링
unity-cli build --no-wait --profile macOS_dev --output Builds/Mac/App.app
unity-cli build status
unity-cli build status build-20240101-114213-9f0ac1
```

```
build job build-20240101-114213-9f0ac1 queued (macOS_dev -> /proj/Builds/Mac/App.app)
building 12s  Compiling PlayerAssemblies 23%
building 3m27s  Compiling PlayerAssemblies 61%
build succeeded in 5m02s — 132.4 MB, 0 error(s), 4 warning(s) -> /proj/Builds/Mac/App.app
```

`BuildPipeline.BuildPlayer`는 Unity 메인 스레드에서 돌고 빌드 내내 그것을 붙잡으므로, `exec`로 요청한 빌드는 멈춘 에디터처럼 보입니다: 연결이 끊기고, 다른 모든 명령이 그 뒤에 큐로 쌓이고, 지켜볼 방법은 `Editor.log`를 긁는 것뿐입니다.

`build`는 같은 빌드를 job으로 합니다. 커넥터가 파라미터를 검증하고 job을 기록한 뒤(`<프로젝트>/Temp/unity-cli/build-jobs.json`, 그래서 빌드가 트리거할 수 있는 도메인 리로드를 견딥니다) id를 반환하고, 빌드는 커맨드 락 밖에서 다음 에디터 update tick에 시작됩니다. 실행 중에는:

- heartbeat가 `ready` 대신 job id, phase, 경과 시간과 함께 `building`을 말하므로, 대기자와 restart 데몬이 빌드 중인 에디터와 멈춘 에디터를 구분할 수 있고;
- `build_status`는 메인 스레드를 결코 기다리지 않는 스레드에서 답하므로 `unity-cli build status`와 `unity-cli status`가 계속 동작하며;
- 대기는 끊긴 연결과 도메인 리로드를 넘어 계속 폴링합니다(답이 전혀 없는 상태로 3분이 지나야 포기하고, 마지막으로 받은 보고를 반환합니다).

퍼센트는 Unity의 Progress 창에서 오고, 빌드가 에디터 update 루프를 돌릴 때만 나타납니다. 그렇지 않으면 줄에는 경과 시간만 담깁니다. Progress API는 모두 메인 스레드 밖에서 예외를 던지므로, 빌드가 도는 동안 거기서 다른 것을 읽을 수는 없습니다.

빌드 하나가 도는 동안 두 번째 `build`는 heartbeat에서 즉시 거부됩니다. 커넥터에 물으면 그 요청이 실행 중인 빌드 뒤에 큐로 쌓여 멈춘 것처럼 보이기 때문입니다. 에디터만 알 수 있는 것들 — 알 수 없는 build profile, 프로젝트에 더 이상 없는 씬, 모듈이 설치되지 않은 target — 은 빌드 몇 분 뒤가 아니라 job이 시작되기 전에 거부됩니다.

phase는 `queued`, `building`, `succeeded`, `failed`, `cancelled`, `interrupted`이고, 마지막은 에디터가 빌드 중이던 job 보고를 멈춘 경우라 그 빌드의 결과는 알 수 없습니다. stdout의 최종 JSON은 job 레코드입니다: phase, `result`, 출력 경로, 크기, 소요 시간, 에러/경고 수, 그리고 `BuildReport`의 첫 에러 메시지들. 성공하면 exit 0, 실패하면 exit 1이고, 사전 검증 에러(알 수 없는 build profile, 모듈이 설치되지 않은 target, `Assets/` 안의 출력 경로)도 포함합니다.

### 모달 다이얼로그 (macOS)

```bash
# 에디터를 막고 있는 다이얼로그의 텍스트와 버튼 표시
unity-cli modal

# 버튼을 제목(대소문자·둥근 따옴표 무시) 또는 인덱스로 클릭
unity-cli modal answer --button "Don't Save"
unity-cli modal answer --index 2

# 확인한 다이얼로그가 다른 것으로 바뀌었으면 클릭하지 않음
unity-cli modal answer --button Cancel --id 27186
```

모달 다이얼로그(`EditorUtility.DisplayDialog`, 저장 확인, 파일 패널)는 Unity 메인 스레드를 멈추게 하므로, 누군가 응답하기 전까지 일반 명령은 실행되지 않습니다. 다이얼로그가 열려 있는 동안 heartbeat는 상태 `modal`과 함께 다이얼로그의 텍스트와 버튼을 기록합니다. 다른 모든 명령은 멈춰 있지 않고 즉시 그 내용을 담은 에러로 실패합니다. 실행 중인 명령이 다이얼로그를 띄운 경우에도 CLI는 같은 에러를 반환하며, 해당 명령은 다이얼로그에 응답한 뒤 Unity 안에서 이어서 실행됩니다.

Connector는 Unity 프로세스 내부에서 메인 큐로 작업을 보내 다이얼로그에 응답합니다. AppKit은 모달 루프 중에도 메인 큐를 처리하기 때문입니다. 손쉬운 사용(Accessibility) 권한이 필요 없고, Unity 자신의 창에만 접근할 수 있습니다. IMGUI로 그린 다이얼로그(`EditorWindow.ShowModal`)는 네이티브 버튼이 없어 목록에는 나오지만 응답할 수 없습니다. Connector가 로드되기 전에 뜨는 다이얼로그(라이선스 안내, Safe Mode, "project already open", 패키지 해석)는 `modal`의 손이 닿지 않습니다 — [시작 다이얼로그](#시작-다이얼로그-macos-호스트)를 참고하세요.

### 시작 다이얼로그 (macOS 호스트)

```bash
# Unity 창을 텍스트와 버튼 이름과 함께 나열 (JSON)
unity-cli --project ~/Work/MyGame dialogs

# 방금 나열한 다이얼로그의 버튼을 누름
unity-cli dialogs --click "Continue"

# 같은 버튼 제목이 여러 창에 있을 때: 창을 지목
unity-cli dialogs --click "Don't Save" --window "Save Changes"
```

`modal`은 커넥터가 필요하고, 커넥터는 자신의 어셈블리가 로드되어야 동작합니다 — 바로 그 시점이 시작 다이얼로그가 나타나는 때입니다. `dialogs`는 AppleScript와 System Events(Accessibility)로 Unity 프로세스 밖에서 같은 창들을 읽으므로, 커넥터도 heartbeat도 없이 동작합니다. macOS에서만 실행되며, 다른 시스템에서는 그렇다고 말하고 호스트에서 실행하라고 안내합니다. 에이전트 샌드박스에는 Accessibility 권한이 주어지지 않기 때문입니다.

heartbeat가 답하지 않을 때, 모든 명령은 "Unity 인스턴스를 찾지 못함"이라고 말하는 대신 heartbeat 레코드와 프로젝트의 `Temp/UnityLockfile`로 어느 상황인지 지목합니다:

- `editor_starting_or_blocked` — lockfile은 있지만 그것보다 새로운 heartbeat가 없으므로, 이번 세션에 커넥터가 로드되지 않았습니다. 에디터가 아직 임포트/컴파일 중이거나, 시작 다이얼로그(라이선스, safe mode, "project already open", 패키지 해석)가 기다리고 있습니다. 이 세션의 heartbeat가 없는 2시간 넘은 lockfile이면 에디터가 아직 시작 중이라기보다 아마 크래시했다고 대신 말합니다.
- `editor_unresponsive` — 이번 세션에 기록된 heartbeat는 있지만 조용해졌습니다. 커넥터가 로드된 뒤 답을 멈췄으므로: 메인 스레드가 긴 exec/빌드/임포트에 막혔거나, 모달 다이얼로그가 열려 있거나, 에디터가 멈춘 것입니다. 이 경우는 lockfile이 필요 없고, lockfile이 사라졌으면 에디터가 아마 종료됐다고 보고하며 다시 실행하라고 안내합니다.

```
editor_starting_or_blocked: Unity appears open for /Users/me/Work/MyGame (Temp/UnityLockfile, age 42s)
but the connector has not loaded — it is still importing/compiling, or a startup dialog (license,
safe mode, "project already open", package resolution) is waiting.
  Lockfile: /Users/me/Work/MyGame/Temp/UnityLockfile (written 2026-11-04T04:26:32Z)
  Next:     run 'unity-cli dialogs' on the macOS host to read the dialog's title and buttons,
            or ask the user to look at the editor.

editor_unresponsive: Unity's connector for /Users/me/Work/MyGame stopped answering 16m0s ago, pid 65279
(last state "ready") — the main thread is likely blocked (long exec/build/import), a modal dialog is
open, or the editor hung.
  Next:     try 'unity-cli dialogs' on the macOS host; if it stays silent, restart the editor
            ('unity-restart' in agent-sandbox).
```

`Temp/UnityLockfile`은 Unity가 프로젝트를 열 때 한 번 기록되므로, 그 나이는 세션의 길이이지 크래시의 증거가 아닙니다. CLI는 그 세션의 heartbeat가 하나도 없고 lockfile이 2시간보다 오래됐을 때만 "아마 크래시"라고 말합니다. heartbeat가 lockfile보다 나중이 된 뒤에는 침묵을 `editor_unresponsive`로 보고합니다.

창은 창 제목에서 요청한 프로젝트의 폴더 이름을 맞춰 좁힙니다. 아무것도 맞지 않으면 모든 Unity 창을 나열합니다 — 시작 다이얼로그는 프로젝트 이름을 거의 담지 않기 때문입니다. 그 필터링되지 않은 목록에서 클릭하려면, 나열된 모든 창이 하나의 Unity 프로세스에 속하지 않는 한 `--window`가 필요하므로 다른 프로젝트의 에디터 버튼을 누를 수 없습니다. Accessibility가 없으면 에러가 정확한 부여 항목을 지목합니다(System Settings → Privacy & Security → Accessibility, `/usr/bin/osascript`와 명령을 실행하는 앱에 대해). 버튼 일치는 정확한 제목 우선, 그다음 제목 시작부터 대소문자·따옴표 무시 매칭입니다(`Save`가 `Don't Save`를 누를 수 없음). 모호한 제목은 아마 틀릴 다이얼로그를 클릭하는 대신 보고됩니다.

Unity가 직접 그리는 창(IMGUI 패널, 일부 Safe Mode 안내)은 Accessibility에 네이티브 버튼을 노출하지 않습니다: `dialogs`는 제목과 텍스트를 나열하지만 클릭할 것이 없고, 사람만 응답할 수 있습니다. 정적 텍스트는 창당 12개로 제한되므로(`texts_total`이 실제 개수를 알려 줍니다) 에디터 창 하나가 출력을 범람시키지 않습니다.

### 도구 목록

```bash
# 사용 가능한 모든 도구 (내장 + 프로젝트 커스텀)와 파라미터 스키마 표시
unity-cli list
```

### 파라미터 전달

`exec`, `build`, 그리고 이름으로 호출하는 커스텀 도구는 개별 플래그 대신 `--params '<json object>'`를 받습니다. CLI는 그 결과 객체를 도구의 파라미터로 커넥터에 보냅니다. 자체 플래그가 있는 내장 명령(`test`, `verify`, `editor`, `wait`)은 그 플래그로 파라미터를 만들고 `--params`를 읽지 않습니다.

```bash
# exec을 JSON 파라미터로
unity-cli exec --params '{"code":"return 42;"}'

# 반복한 --params 객체는 순서대로 병합되므로 뒤의 키가 이김
unity-cli exec --params '{"code":"return 42;"}' --params '{"await_timeout":1000}'
# code와 await_timeout을 함께 보냄
```

`--params`는 유일하게 반복할 수 있는 subcommand 플래그입니다. 다른 subcommand 플래그는 한 번만 줄 수 있고, 두 번째(`--filter a --filter b` 같은)는 첫 값을 조용히 덮어쓰는 대신 에러입니다. 명령이 받지 않는 플래그도 에러입니다 — `test --filtre x`는 전체 스위트를 실행하는 대신 `unknown flag --filtre for test; see unity-cli test --help`와 함께 exit 1입니다. 커스텀 도구의 파라미터는 커넥터가 판단하며, 모르는 파라미터는 `data.code: "unknown_parameter"`로 거부합니다(아무것도 실행되지 않았으므로 `unity-cli list`로 스키마를 확인하세요). 어느 `--params` 객체든 잘못된 JSON도 에러입니다. 같은 키가 일반 플래그와 `--params` 양쪽에서 오면 `--params` 값이 이깁니다.

### 커스텀 도구

```bash
# 커스텀 도구를 이름으로 직접 호출
unity-cli my_custom_tool

# 파라미터와 함께 호출
unity-cli my_custom_tool --params '{"key": "value"}'
```

### 상태 확인

```bash
# Unity Editor 상태 확인
unity-cli status
# 출력: Unity (port 8090): ready
#   Project: /path/to/project
#   Version: 6000.1.0f1
#   CLI:     0.7.1
#   Connector: 0.7.0
#   Compatibility: compatible (same major.minor 0.7)
#   PID:     12345
```

명령 전송 전에 CLI가 자동으로 Unity 상태를 확인합니다. Unity가 바쁜 상태(컴파일, 리로드)이면 응답 가능해질 때까지 대기합니다. 모달 다이얼로그가 열려 있으면(상태 `modal`) 즉시 실패하고 다이얼로그 내용을 보여줍니다. [모달 다이얼로그](#모달-다이얼로그-macos)를 참고하세요. 플레이어 빌드가 메인 스레드를 잡고 있으면(상태 `building`) job id, phase, 경과 시간이 함께 출력됩니다. [플레이어 빌드](#플레이어-빌드-장기-실행)를 참고하세요. 아무것도 답하지 않는데 프로젝트는 열려 있는 것 같으면 상태가 이유를 지목합니다: 이번 세션에 커넥터가 로드되지 않았으면 `editor_starting_or_blocked`, 이번 세션의 heartbeat가 답을 멈췄으면 `editor_unresponsive`입니다. [시작 다이얼로그](#시작-다이얼로그-macos-호스트)를 참고하세요. 에디터 프로세스가 죽은 경우라면 상태는 `crashed`입니다. [에디터 크래시](#에디터-크래시)를 참고하세요.

### 에디터 크래시

```bash
unity-cli status
# 이 경우 보고는 stderr로 나가고 stdout은 비어 있습니다.
# crashed: the Unity editor died for /Users/me/Work/MyGame (heartbeat last seen 30s ago, pid 65279) with no "stopped" marker.
#   Heartbeat: /Users/me/.unity-cli/instances/1f0c….json (state "ready", pid 65279, last beat 30s ago)
#   Process:   pid 65279 is not in this machine's process table, which still lists Unity as pid 12345
#   Crash log: /Users/me/Work/MyGame/mono_crash.1b6a4e0a.0.json (mono_crash, written 25s ago)
#              signal 11 (SIGSEGV); System.NullReferenceException: Object reference not set to an instance of an object
#   Next:      Unity has to be relaunched by the user — no CLI call brings it back. Editor.log rotates when it does, so read the crash log above first.
# Error: crashed (port 8090, pid 65279): the crash report above names the evidence
# (exit 1)
```

하드 크래시, 도메인 리로드, 끊긴 소켓은 HTTP 위에서 모두 똑같이 보입니다: `EOF`. 그중 하나를 보고하기 전에 CLI는 에디터가 디스크에 남긴 것을 읽고 셋 중 어느 것인지 말합니다:

| 판정 | 근거 | CLI가 말하는 것 |
|------|------|-----------------|
| `crashed` | heartbeat의 pid가 프로세스 테이블에 여전히 Unity가 있는 머신에서 사라졌거나, heartbeat가 `stopped` 마커를 쓰지 않고 멈췄는데 그 마지막 beat보다 새로운 크래시 산출물이 있을 때 | `editor process <pid> exited during the request (crash)`, 산출물 경로, 그 첫 예외 줄 |
| reload / 포트 변경 | heartbeat가 계속 오지만 `compiling`/`reloading`/`refreshing`/`building`/`modal`을 말하거나, 커넥터가 새 포트에서 답함 | `Unity reloaded or rebound its port during the request (not a crash)`; `unity-cli wait` 실행 |
| 그대로 | 위 어디에도 해당하지 않음(단순히 시간 초과된 요청 포함) | 원래의 `cannot connect to Unity at port …: EOF` |

크래시 산출물은 `<프로젝트>/mono_crash.*.json`(`<프로젝트>/Logs` 아래의 같은 파일 포함), 그리고 macOS 호스트의 `~/Library/Logs/DiagnosticReports/Unity-*.ips`와 `~/Library/Logs/Unity/Editor-prev.log`입니다. **마지막 heartbeat보다 새로운**(또는 요청 시작보다 새로운) 산출물만 세므로, 이전 크래시의 덤프가 새 침묵의 탓으로 돌려지지 않습니다. `Editor-prev.log`는 깨끗한 종료에서도 Unity가 회전시키므로 tail에 크래시 문구가 있어야 인정됩니다. 산출물은 최대 세 개까지, 각각 파일에서 가져온 한 줄(경계 있음)과 함께 출력됩니다 — Mono의 `payload`/`threads` 구조는 방어적으로 파싱하고, 잘린 덤프도 이유는 내놓습니다.

그 지목이 중요한 이유는 `Logs/Editor.log`가 Unity 재시작 때 회전하기 때문입니다: 크래시 뒤에 그것을 죽인 것에 대한 유일한 생존 기록은 덤프입니다.

`status`는 `crashed`를 보고하고 **0이 아닌 코드로 종료합니다** — 죽은 에디터는 에이전트가 기다려서 벗어날 수 있는 상태가 아닙니다. 재시도 루프는 전체 timeout 동안 죽은 에디터를 폴링하는 대신 그 판정에서 멈춥니다. 호스트의 프로세스를 볼 수 없는 컨테이너 안에서는 pid 부재가 아무것도 증명하지 못하므로, 거기서는 산출물 경로만 판정할 수 있습니다. 판정은 항상 어느 근거를 썼는지 출력합니다.

### 큐에 걸린 명령과 서버 측 deadline

Unity 메인 스레드에서 도는 명령은 큐에 들어가 `EditorApplication.update`가 집어 갑니다. 에디터가 컴파일 중이거나, 긴 도구 안에 있거나, 모달 다이얼로그에 막혀 있거나, 리로드 중이면 그 pickup이 늦어지거나 영영 오지 않을 수 있습니다. 커넥터는 연결을 답 없이 두는 대신 대기에 한도를 둡니다:

- 각 요청은 클라이언트 예산(`deadline_ms`, `--timeout`의 남은 부분)을 함께 보냅니다. 커넥터는 그것을 여유를 뺀 queue deadline으로 쓰므로, 에디터가 클라이언트가 포기하기 **전에** 답합니다. 0.5초 floor보다 작은 예산은 여전히 클라이언트 쪽이 먼저 끝날 수 있고, 이 floor는 작은 예산이 커넥터의 답과 경쟁하지 못하게 하려고 있습니다. deadline이 없으면 커넥터의 60초 기본값이 적용되고, 호출별 `queue_deadline_ms` 파라미터가 양쪽을 모두 덮어씁니다(0.5–600초로 클램프).
- deadline은 메인 스레드가 명령을 *시작*하기까지의 대기만 제한하고, 명령의 실행 시간은 결코 제한하지 않습니다. 실행 중인 도구는 `--timeout`으로만 제한됩니다.
- `beforeAssemblyReload`와 종료 시점에는 진행 중인 모든 요청이 먼저 답을 받고, 새 메인 스레드 요청은 리스너가 다시 올라올 때까지 거부됩니다. 시작하지 못한 요청은 `domain_reload`로 답하고(다시 시도), 도구 핸들러가 이미 실행 중이던 요청은 `reload_during_command`로 답합니다 — 실행은 됐고 반환값만 옛 도메인과 함께 사라졌습니다. 도구가 이미 반환한 요청은 자신의 결과를 지킵니다: 리로드 대기가 아직 쓰이지 않은 응답이 소켓에 도달할 시간을 경계 있게 줍니다. 종료에 대한 답에는 `data.code`가 없습니다: 에디터가 사라졌으므로 기다릴 것이 없고, 명령을 반복할지는 그것이 시작됐는지에 달려 있습니다. `RunOffMainThread`로 표시된 도구(`modal`, `build_status`)는 그동안에도 계속 호출할 수 있습니다.
- 이미 답한 요청은 자기 차례가 왔을 때 건너뛰므로, 호출자가 실행되지 않았다고 들은 뒤에 부수 효과를 적용할 수 없습니다.

```bash
unity-cli --timeout 5000 exec 'return 1;'
# Error: exec was queued but the main thread did not pick it up within 3.9s (waited 4.1s):
#        editor busy (blocked by the running command test_run (42s)) [heartbeat: playing,
#        queue depth: 2]. The command did not run; nothing changed.
# Details: {"code":"main_thread_busy","command":"exec","running_command":"test_run",…}
#   next:      the command did not run and nothing changed; run 'unity-cli wait' until the
#              editor is idle, then repeat it

# 한 호출의 queue deadline을 넓히기
unity-cli exec --params '{"code":"AssetDatabase.Refresh(); return 1;","queue_deadline_ms":300000}'
```

| `data.code` | 무슨 일이 있었나 | 명령이 실행됐나? |
|-------------|------------------|------------------|
| `main_thread_busy` | 메인 스레드가 집어 가지 못함 | 아니오 — 아무것도 바뀌지 않음 |
| `domain_reload` | 명령이 아직 큐에 있는 동안 Unity가 리로드됨 | 아니오 — 반복해도 안전 |
| `reload_during_command` | 명령이 실행 중일 때 Unity가 리로드됨 | 예, 적어도 일부는 — 반복 전에 상태를 확인 |
| `serialization_failed` | 도구의 결과를 직렬화하지 못함 | 예 — 효과는 남고 반환값만 사라짐 |

`reload_during_command`이면 CLI가 heartbeat를 다시 읽어 에디터가 어디에 도착했는지 출력하므로, 호출자는 물어볼 필요 없이 `state: Unity is back: Edit mode (heartbeat "ready", port 8090)`를 봅니다. 자체 리로드 대기를 가진 명령(`editor play/stop`, `editor refresh --compile`, `test`, `verify`, `build`, `wait`)은 스스로 보고합니다.

느리지만 성공한 처리도 `meta`에 드러납니다: 대기가 1초를 넘으면 `--meta`가 `queue_wait_ms`와 `queue_depth`를 보여 줍니다. `queue_wait_ms`는 도구 핸들러가 시작될 때 멈추고, `total_ms`는 요청 전체(에디터 측)를 다루므로 `queue_wait_ms + compile_ms`가 호출의 실제 시간을 넘지 않습니다(`exec`의 `compile_ms` 같은 도구 자체의 시간은 핸들러 시간 안에, 핸들러 시간은 `total_ms` 안에 있습니다). 메인 스레드 밖에서 처리되는 `build_status`나 `modal`은 큐를 거치지 않으므로 `queue_wait_ms`가 없습니다. HTTP 왕복과 CLI의 탐색은 `total_ms` 밖에 있습니다.

## 글로벌 옵션

| 플래그 | 설명 | 기본값 |
|--------|------|--------|
| `--port <N>` | 활성 heartbeat 포트로 Unity 인스턴스 선택 | auto |
| `--project <path>` | 프로젝트 경로로 Unity 인스턴스 선택 | latest |
| `--timeout <ms>` | 호출당 하나의 예산으로, 인스턴스 탐색, 새 heartbeat 대기, 요청이 함께 씁니다. 남은 예산은 `deadline_ms`로 커넥터에 전달되므로, 커넥터가 이 deadline보다 먼저 큐에 걸린 명령에 답합니다 — [큐에 걸린 명령과 서버 측 deadline](#큐에-걸린-명령과-서버-측-deadline) 참고. 클라이언트 deadline은 연결 실패가 아니라 `timed out ... (the editor may still be running it)`으로 보고됩니다. `test`, `verify`, `editor refresh --compile`은 1800000(30분)이 기본이고, 명시적 `0`은 deadline 없음을 뜻합니다(그래도 커넥터는 큐에 걸린 명령에 60초 기본값 뒤 답합니다). 이는 이전 릴리스와의 호환성 변경입니다: 예전에는 `--timeout 0`이면 heartbeat 대기가 즉시 실패했으므로, 그 의미에 의존하던 스크립트는 작은 양수를 넘겨야 합니다. `wait --timeout`과 build 폴링은 각자의 예산을 유지합니다. `editor quit`/`restart`에서는 종료 대기와 시작 예산이기도 합니다. | 120000 |
| `--ignore-version-mismatch` | 버전 불일치 경고(및 0.7.0보다 오래된 커넥터에 대한 거부)를 숨김 | false |
| `--meta` | 응답의 `meta`(호출이 어떻게 처리됐는지, 예: `exec`의 컴파일 캐시 상태)를 stderr로 출력. `compile_ms`는 csc subprocess만의 시간이고, `queue_wait_ms`는 핸들러가 시작되기 전까지의 대기, `total_ms`는 그 둘을 모두 포함하는 에디터 측 전체 구간입니다. 어느 것도 wall-clock 총합이 아닙니다 | false |

```bash
# 특정 Unity 인스턴스에 연결
unity-cli --port 8091 editor play

# 여러 Unity 인스턴스 중 프로젝트 경로로 선택
unity-cli --project MyGame editor stop

# 설치된 connector 릴리스 버전이 달라도 경고 없이 실행
unity-cli --ignore-version-mismatch exec 'return 1 + 1;'
```

CLI와 connector 버전이 정확히 같을 필요는 없습니다. 같은 major.minor 안의 patch 차이는 조용히 허용됩니다. minor나 major가 다르면 두 버전, CLI 바이너리, 해결 방법을 담은 경고를 stderr에 한 줄 출력하고 실행합니다. 진단 명령(`status`, `console`, `compile_diagnostics`, `compile_status`, `api`, `burst_status`, `modal`, `tools`, `tool_describe`, `wait`, `dialogs`, `list`)은 이 때문에 실패하지 않습니다. (`status`와 `dialogs`는 게이트가 돌기 전에 답하고, `help`/`version`/`update`도 마찬가지입니다.) CLI가 거부하는 것은 0.7.0보다 오래된 커넥터뿐이며, 0.7.0이 CLI가 말할 수 있는 가장 오래된 프로토콜입니다. `status`는 항상 두 버전과 호환성 판정을 출력합니다.

모든 명령어에 `--help`를 붙이면 상세 사용법을 볼 수 있습니다:

```bash
unity-cli editor --help
unity-cli exec --help
unity-cli profiler --help
```

## 커스텀 도구 만들기

Editor 어셈블리에 `[UnityCliTool]` 어트리뷰트를 가진 static 클래스를 만드세요. 도메인 리로드 시 자동으로 탐지됩니다.

```csharp
using UnityCliConnector;
using Newtonsoft.Json.Linq;
using UnityEngine;

[UnityCliTool(Name = "spawn", Description = "지정 위치에 적 스폰", Group = "gameplay")]
public static class SpawnEnemy
{
    public class Parameters
    {
        [ToolParameter("X 월드 좌표", Required = true)]
        public float X { get; set; }

        [ToolParameter("Y 월드 좌표", Required = true)]
        public float Y { get; set; }

        [ToolParameter("Z 월드 좌표", Required = true)]
        public float Z { get; set; }

        [ToolParameter("Resources 폴더 내 프리팹 이름", DefaultValue = "Enemy")]
        public string Prefab { get; set; }
    }

    public static object HandleCommand(JObject parameters)
    {
        var p = new ToolParams(parameters);
        float x = p.GetFloat("x", 0);
        float y = p.GetFloat("y", 0);
        float z = p.GetFloat("z", 0);
        string prefabName = p.Get("prefab", "Enemy");

        var prefab = Resources.Load<GameObject>(prefabName);
        var instance = Object.Instantiate(prefab, new Vector3(x, y, z), Quaternion.identity);

        return new SuccessResponse("Enemy spawned", new
        {
            name = instance.name,
            position = new { x, y, z }
        });
    }
}
```

flag 또는 JSON으로 호출:

```bash
unity-cli spawn --x 1 --y 0 --z 5 --prefab Goblin
unity-cli spawn --params '{"x":1,"y":0,"z":5,"prefab":"Goblin"}'
```

**주요 사항:**

- **이름**: `Name` 없으면 클래스명에서 자동 생성 (`SpawnEnemy` → `spawn_enemy`, `UITree` → `ui_tree`). `Name = "spawn"`이면 `unity-cli spawn`으로 호출.
- **Parameters 클래스**: 선택 사항이지만 권장. `unity-cli list`에서 파라미터 이름, 타입, 설명, 필수 여부를 노출 — AI 어시스턴트가 소스 코드 없이 도구 사용법을 파악 가능.
- **ToolParams**: `p.Get()`, `p.GetInt()`, `p.GetFloat()`, `p.GetBool()`, `p.GetRaw()`로 일관된 파라미터 읽기.
- **탐지**: `unity-cli list`에서 built-in 도구(`group: "built-in"`)가 먼저, 연결된 프로젝트의 custom 도구(`group: "custom"`)가 뒤에 표시.

**어트리뷰트 참조:**

| 어트리뷰트 | 속성 | 설명 |
|---|---|---|
| `[UnityCliTool]` | `Name` | 명령어 이름 오버라이드 (기본: 클래스명 → snake_case) |
| | `Description` | `list`에 표시되는 도구 설명 |
| | `Group` | 분류용 그룹 이름 |
| `[ToolParameter]` | `Description` | 파라미터 설명 (생성자 인자) |
| | `Required` | 필수 여부 (기본: `false`) |
| | `Name` | 파라미터 이름 오버라이드 |
| | `DefaultValue` | 기본값 힌트 |

### 규칙

- 클래스는 `static`이어야 합니다
- `public static object HandleCommand(JObject parameters)` 또는 `async Task<object>` 변형이 필요합니다
- `SuccessResponse(message, data)` 또는 `ErrorResponse(message)`를 반환하세요
- 응답의 선택적 세 번째 인자 `meta`는 호출이 *어떻게* 처리됐는지(예: `exec`의 `compile`/`compile_ms`) 보고합니다. `data` 밖에 남으므로 도구 결과를 파싱하는 호출자는 어느 쪽이든 같은 형태를 봅니다. CLI는 `--meta`를 줬을 때만 stderr로 출력합니다
- `Parameters` 중첩 클래스에 `[ToolParameter]` 어트리뷰트를 추가하면 자동 문서화됩니다
- 클래스 이름이 자동으로 snake_case 명령어 이름으로 변환됩니다
- `[UnityCliTool(Name = "my_name")]`으로 이름을 재정의할 수 있습니다
- Unity 메인 스레드에서 실행되므로 모든 Unity API를 안전하게 호출할 수 있습니다
- `[UnityCliTool(RunOffMainThread = true)]`는 HTTP 요청 스레드에서 실행합니다 — 커맨드 락과 컴파일 게이트를 지나므로, 모달 다이얼로그나 플레이어 빌드가 메인 스레드를 잡고 있어도 답합니다. 그런 핸들러는 자신만의 스레드 안전 경로를 통하지 않고서는 어떤 Unity API도 호출할 수 없습니다
- Editor 시작 시와 스크립트 재컴파일 후 자동으로 탐지됩니다
- 중복된 도구 이름은 감지되어 에러로 로그됩니다 — 먼저 발견된 핸들러만 사용됩니다

## 여러 Unity 인스턴스

여러 Unity Editor가 열려 있으면, 각각 다른 포트(8090, 8091, ...)에 등록됩니다:

```bash
# 실행 중인 모든 인스턴스 확인
ls ~/.unity-cli/instances/

# 프로젝트 경로로 선택
unity-cli --project MyGame editor play

# 활성 heartbeat 포트로 선택
unity-cli --port 8091 editor play

# 기본: 가장 최근 등록된 인스턴스 사용
unity-cli editor play
```

## MCP와 비교

| | MCP | unity-cli |
|---|-----|-----------|
| **설치** | Python + uv + FastMCP + config JSON | 바이너리 하나 |
| **의존성** | Python 런타임, WebSocket 릴레이 | 없음 |
| **프로토콜** | JSON-RPC 2.0 over stdio + WebSocket | 직접 HTTP POST |
| **설정** | MCP 설정 생성, AI 도구 재시작 | Unity 패키지 추가, 끝 |
| **재연결** | 복잡한 도메인 리로드 재연결 로직 | 요청별 무상태 |
| **호환성** | MCP 호환 클라이언트만 | 셸이 있는 모든 것 |
| **커스텀 도구** | 동일한 `[Attribute]` + `HandleCommand` 패턴 | 동일 |

## 만든 사람

**DevBookOfArray**

[![YouTube](https://img.shields.io/badge/YouTube-DevBookOfArray-red?logo=youtube&logoColor=white)](https://www.youtube.com/@DevBookOfArray)
[![GitHub](https://img.shields.io/badge/GitHub-youngwoocho02-181717?logo=github)](https://github.com/youngwoocho02)

## 라이선스

MIT
