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

컴파일이나 리로드 직전에 상태(`compiling`, `reloading`)를 instance 파일에 기록합니다. 메인 스레드가 멈추면 timestamp 갱신이 중단되고, CLI는 새로운 timestamp가 찍힐 때까지 대기한 후 명령을 전송합니다.

## 내장 명령어

| 명령어 | 설명 |
|--------|------|
| `editor` | Unity Editor play/stop/pause/refresh 제어 |
| `console` | 콘솔 로그 읽기, 필터링, 지우기 |
| `exec` | Unity 안에서 임의 C# 코드 실행 |
| `test` | EditMode/PlayMode 테스트 실행 |
| `verify` | 컴파일, 내 코드의 에러 출력, 테스트 실행을 한 번에 짧은 출력으로 |
| `menu` | Unity 메뉴 아이템을 경로로 실행 |
| `reserialize` | Unity 시리얼라이저를 통해 에셋 재직렬화 |
| `clear_burst_cache` | `Library/BurstCache`를 삭제해 Burst가 처음부터 다시 컴파일하게 함 |
| `burst_status` | Burst 컴파일이 끝났는지, 어떤 `[BurstCompile]` 대상이 컴파일됐는지, 무엇이 실패했는지(BCxxxx) |
| `screenshot` | 실행 중인 게임(Game 뷰, OnGUI 포함), 카메라, 또는 Scene 뷰를 PNG로 캡처 |
| `profiler` | 프로파일러 하이어라키 읽기, 녹화 제어 |
| `modal` | 에디터를 막고 있는 모달 다이얼로그 확인 및 응답 (macOS) |
| `list` | 사용 가능한 모든 도구와 파라미터 스키마 표시 |
| `status` | Unity Editor 연결 상태 확인 |
| `update` | CLI 바이너리 자동 업데이트 |

### Editor 제어

```bash
# 플레이 모드 진입
unity-cli editor play

# 플레이 모드 진입 후 완전히 로드될 때까지 대기
unity-cli editor play --wait

# 플레이 모드 종료
unity-cli editor stop

# 일시정지 토글 (플레이 모드에서만 동작)
unity-cli editor pause

# 에셋 새로고침 (플레이모드에서는 --force 없으면 차단)
unity-cli editor refresh

# 새로고침 + 스크립트 컴파일 (플레이모드에서는 --force 없으면 차단)
unity-cli editor refresh --compile

# 플레이모드 중 강제 새로고침
unity-cli editor refresh --force
```

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
| `gameview` | 실행 중인 게임이 만든 프레임(`ScreenCapture.CaptureScreenshot`), Game 뷰 자체 해상도 | 게임의 OnGUI/IMGUI HUD | Game 뷰 탭의 에디터 UI(툴바, 해상도 드롭다운) |
| `camera` | `Camera.main`을 `--width`/`--height` 크기의 렌더 텍스처로 오프스크린 렌더 | 해당 카메라의 렌더 | 게임 GUI(OnGUI/IMGUI), Scene 뷰 기즈모·Handles·에디터 오버레이 |
| `scene` | Scene 뷰 카메라를 `--width`/`--height`로 오프스크린 렌더 | 현재 Scene 카메라가 보는 장면 | 기즈모와 Handles, Scene 뷰 오버레이 UI, 게임 GUI |

`gameview`는 플레이 모드의 기본값이고 `scene`은 에디트 모드의 기본값입니다(모드 없는 `screenshot`이 원래 하던 일). `gameview`는 플레이 모드가 필요합니다: 에디트 모드에서는 게임 프레임이 없어 Unity가 파일을 쓰지 않으므로, 멈추지 않고 그 사실을 알려줍니다. 일시정지된 게임도 프레임을 만들지 않으므로, 모드 없는 `screenshot`은 이때 `camera`로 대체하고 응답에 `"fallback":"paused"`를 담습니다. `--mode gameview`를 명시하면 같은 이유로 에러를 내며, 이때는 게임 루프 없이 렌더하는 `--mode camera`를 쓰면 됩니다. Unity는 캡처를 같은 디렉터리의 `*.partial.png`에 쓰고, 파일이 완전해진(PNG 트레일러가 있는) 뒤에야 `--output_path`로 옮기므로, 실패하거나 시간 초과된 캡처는 그 경로에 있던 기존 스크린샷을 건드리지 않습니다. 쓰기가 비동기이므로 도구는 에디터 업데이트를 돌리며 완성된 파일이 생기는 즉시(최대 5초) 경로·바이트 수·해상도를 보고합니다.

에디터 창의 화면 픽셀을 읽는 모드는 없습니다. 모든 모니터 밖으로 옮겨진 에디터 창을 읽으면 에디터가 네이티브 단계에서 죽기 때문에, `screenshot`은 카메라 렌더와 게임 자체의 `ScreenCapture`만 사용합니다.

`--view`는 `--mode`의 옛 표기입니다: `--view scene`은 `--mode scene`이지만, `--view game`은 Game 뷰가 아니라 오프스크린 렌더인 `--mode camera`입니다.

### 콘솔 로그

`console`은 기본적으로 출력을 작게 유지합니다: 최신 50개 항목, 항목당 최대 6개 스택 프레임,
항목당 1000자, 전체 15 KB. `--stacktrace user`(기본값)는 유저 코드 프레임만 남기고 엔진
프레임은 `... (+N engine frames)`로 접습니다. `--full`은 모든 크기 제한을 해제합니다.

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

응답이 잘리면 `truncated`, `truncated_by`, `entries_total` 등으로 무엇이 얼마나 빠졌는지
알려줍니다. `console`은 읽는 동안 Console 창의 Log/Warning/Error 토글을 켜고 검색어를 지우고
Collapse를 끈 뒤 원래대로 복원하므로, `--type`은 창에 보이는 항목이 아니라 전체 항목을
대상으로 합니다. 필터를 바꿔야 했다면 `console_window_filter`에 그 내용이 담깁니다. Unity
버전이 창 필터를 노출하지 않으면 `"available":false`와 안내 문구를 대신 보냅니다.

### C# 코드 실행

가장 강력한 명령어입니다. Unity Editor 런타임에서 임의의 C# 코드를 실행합니다. UnityEngine, UnityEditor, ECS 및 로드된 모든 어셈블리에 접근 가능합니다. 일회성 조회나 수정을 위해 커스텀 도구를 만들 필요가 없습니다.

`return`으로 결과를 받습니다. 주요 namespace는 기본 포함. 프로젝트 전용 타입(예: `Unity.Entities`)만 `--usings`로 추가합니다.

`System`과 `UnityEngine`은 모두 `Object`, `Random`, `Debug`를 정의하므로, 이 이름들은 기본 alias로 Unity 타입에 연결되어 CS0104가 나지 않습니다. 스니펫 맨 위의 `using` 지시문은 생성된 메서드 위로 옮겨지고, 직접 선언한 alias가 기본 alias보다 우선합니다. 기본 집합을 완전히 바꾸려면 `--no_default_usings`로 기본 import와 alias를 끄고 `--usings`로 필요한 것을 나열합니다.

```bash
unity-cli exec "return Application.dataPath;"
unity-cli exec "return EditorSceneManager.GetActiveScene().name;"
unity-cli exec "return World.All.Count;" --usings Unity.Entities

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

`exec`는 실제 C#을 컴파일하고 실행하므로, 커스텀 도구가 할 수 있는 모든 것을 할 수 있습니다 — ECS 엔티티 조사, 에셋 수정, 내부 API 호출, 에디터 유틸리티 실행. AI 에이전트에게 이것은 **도구 코드를 한 줄도 작성하지 않고 Unity 전체 런타임에 즉시 접근**할 수 있다는 의미입니다. stdin 파이프를 사용하면 복잡한 코드에서 shell escaping 문제를 피할 수 있습니다.

**결과 형태.** 반환값은 public field와 읽을 수 있는 public property에서 직렬화되므로, anonymous type, property만 가진 에디터 API 객체, list, dictionary, 중첩 객체가 `ToString()` 문자열이 아니라 JSON 객체로 돌아옵니다. 컬렉션은 100개, 깊이는 6에서 잘리고, cycle은 `"<cycle: Type>"`, 예외를 던지는 property는 형제를 잃지 않은 채 `"<error: Type: message>"`가 됩니다. Unity 객체는 식별 문자열(`"Main Camera (UnityEngine.Camera)"`)을 유지하고, Unity math struct는 `Vector3.normalized` 같은 파생값 대신 데이터 멤버를 노출합니다. 직렬화나 `ToString()` fallback이 일어나면 `data`는 문자열이 되고 `meta`에 `serialization_error`가 담깁니다 — `--meta`로 확인합니다.

**컴파일 캐시.** `exec`는 스니펫을 `csc` subprocess로 컴파일하고, 에디터 메인 스레드를 막지 않고 기다린 뒤 로드/실행합니다. 같은 호출(같은 본문, 같은 `--usings`, 같은 reference 집합)은 이미 로드된 어셈블리를 재사용하므로 반복 probe가 프레임 하나로 끝납니다. 응답은 `meta: {"compile":"compiled|cached","compile_ms":N,"reference_assemblies":N}`로 이를 알리며 `data`와 분리되어 있어 결과 파싱은 그대로입니다. `--meta`를 주면 stderr로 출력됩니다.

```bash
unity-cli exec --meta "return Application.dataPath;"   # meta: {"compile":"compiled",…}
unity-cli exec --meta "return Application.dataPath;"   # meta: {"compile":"cached",…}
```

캐시 조회는 256개로 제한되며, evict된 스니펫의 어셈블리는 어차피 다음 domain reload 전까지 domain에 남습니다 (Mono는 unload하지 못합니다). 컴파일 중 에디터가 reload되면 stale 컴파일을 실행하지 않고 error로 응답합니다.

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

Burst는 Unity의 스크립트 컴파일과 별다른 일정으로 자체 컴파일한다. `compile: ok`에 테스트 통과, 빈 콘솔이 겹쳐도 `[BurstCompile]` job이 실제로 컴파일됐다는 증거가 아니고, `BCxxxx` 에러는 컴파일이 깨끗해 보인 뒤에도 뒤늦게 나타난다. `Library/BurstCache`를 직접 읽어도 답은 없다 — 파일명만 남고 상태와 에러는 없다.

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

이 도구는 `com.unity.burst`가 설치된 프로젝트에서만 빌린다(어셈블리가 패키지로 constrained). Burst가 없는 프로젝트에서는 명령이 unknown이고 `verify`에 `burst:` 줄이 나오지 않는다.

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

# 테스트 이름으로 필터링 (부분 문자열 매치)
unity-cli test --filter MyTestClass

# 지정한 어셈블리의 테스트만 실행 (쉼표로 구분)
unity-cli test --assembly MyGame.Tests.Editor
```

Unity Test Framework 패키지가 필요합니다. 열린 씬에 저장되지 않은 변경이 있으면 기본적으로 테스트를 막습니다. `--auto-save-scenes`로 먼저 저장하거나, `--allow-dirty-scenes`로 그대로 실행할 수 있습니다. PlayMode 테스트는 도메인 리로드를 트리거하며, CLI가 자동으로 결과를 폴링합니다.

### 검증 (verify)

프로젝트가 컴파일되고 테스트가 통과하는지 한 번의 호출로 확인합니다. 에이전트 컨텍스트에 맞게 출력이 짧습니다.

```bash
# 컴파일, 내 코드의 에러 출력, 그 다음 EditMode 테스트 실행
unity-cli verify

# 컴파일까지만
unity-cli verify --no-tests

# 일부 테스트만 (--filter, --mode PlayMode도 가능)
unity-cli verify --assembly MyGame.Tests.Editor

# Burst 단계 건너뛰기
unity-cli verify --no-burst

# 텍스트 대신 JSON 객체 하나
unity-cli verify --json
```

```
compile: 1 error(s)
  Assets/Scripts/Player.cs:42:13 CS0103 The name 'speed' does not exist in the current context
```

C# 컴파일이 깨끗해도 Burst 줄이 따라 나온다. Burst는 스크립트 컴파일 밖에서 컴파일하고 `BCxxxx` 에러는 깨끗해 보인 뒤 도착할 수 있기 때문이다:

```
compile: ok
burst: 14 targets compiled, 0 error(s)
tests: 31/31 passed
```

Burst 에러는 컴파일 에러와 같은 형태로 출력되고, 테스트가 실행되기 전에 verify를 실패시킨다(exit 1). `--burst-wait <초>`는 Burst가 idle이 되기를 기다리는 시간의 상한이다(기본 60, `--timeout`을 그보다 크게 유지할 것). Burst 확인을 할 수 없는 경우 — Burst가 설치되지 않아 `burst_status` 도구가 없거나 에디터가 응답하지 않는 경우 — 는 아무것도 출력하지 않거나 메모만 남기고 판정은 바꾸지 않는다.

`verify`는 먼저 콘솔을 비웁니다 (Burst 에러는 코드가 바뀐 뒤에도 콘솔에 남고, 다음 컴파일에서 Burst가 살아 있는 에러를 다시 기록합니다. `--keep-console`로 생략 가능). 그다음 리프레시와 컴파일을 하고, 커넥터의 `compile_diagnostics` 도구로 이번 컴파일의 에러를 출력합니다. `Library/`와 `PackageCache/`의 에러는 다른 실패가 없을 때만 보여 줍니다. 이어서 저장되지 않은 열린 씬을 저장하고 테스트를 실행해 `passed/total`과 실패마다 첫 줄을 출력합니다 (`--max`로 두 목록의 길이를 제한, 기본값 20). 종료 코드 0은 컴파일 성공, Burst 무오류, 모든 테스트 통과를 뜻하고, 1은 컴파일 에러, Burst 에러, 실패한 테스트, 또는 실행된 테스트 없음을 뜻하며, 2는 에디터가 응답하지 못함(실행 중이 아님, 시간 초과, 모달 다이얼로그에 막힘)을 뜻한다. 다이얼로그는 응답 명령과 함께 표시됩니다. 0.7.0 이전 커넥터에는 `compile_diagnostics`가 없고 `--assembly`를 무시합니다. 이때 `verify`는 콘솔 에러를 보여 주고 그 사실을 알립니다.

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

Connector는 Unity 프로세스 내부에서 메인 큐로 작업을 보내 다이얼로그에 응답합니다. AppKit은 모달 루프 중에도 메인 큐를 처리하기 때문입니다. 손쉬운 사용(Accessibility) 권한이 필요 없고, Unity 자신의 창에만 접근할 수 있습니다. IMGUI로 그린 다이얼로그(`EditorWindow.ShowModal`)는 네이티브 버튼이 없어 목록에는 나오지만 응답할 수 없습니다. Connector가 로드되기 전에 뜨는 다이얼로그(예: 시작 시 Safe Mode 확인)는 처리할 수 없습니다.

### 도구 목록

```bash
# 사용 가능한 모든 도구 (내장 + 프로젝트 커스텀)와 파라미터 스키마 표시
unity-cli list
```

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
#   PID:     12345
```

명령 전송 전에 CLI가 자동으로 Unity 상태를 확인합니다. Unity가 바쁜 상태(컴파일, 리로드)이면 응답 가능해질 때까지 대기합니다. 모달 다이얼로그가 열려 있으면(상태 `modal`) 즉시 실패하고 다이얼로그 내용을 보여줍니다. [모달 다이얼로그](#모달-다이얼로그-macos)를 참고하세요.

## 글로벌 옵션

| 플래그 | 설명 | 기본값 |
|--------|------|--------|
| `--port <N>` | 활성 heartbeat 포트로 Unity 인스턴스 선택 | auto |
| `--project <path>` | 프로젝트 경로로 Unity 인스턴스 선택 | latest |
| `--timeout <ms>` | HTTP 요청 타임아웃 | 120000 |
| `--ignore-version-mismatch` | CLI와 connector 버전이 달라도 실행 | false |

```bash
# 특정 Unity 인스턴스에 연결
unity-cli --port 8091 editor play

# 여러 Unity 인스턴스 중 프로젝트 경로로 선택
unity-cli --project MyGame editor stop

# 설치된 connector 릴리스 버전이 달라도 그대로 실행
unity-cli --ignore-version-mismatch status
```

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
- `Parameters` 중첩 클래스에 `[ToolParameter]` 어트리뷰트를 추가하면 자동 문서화됩니다
- 클래스 이름이 자동으로 snake_case 명령어 이름으로 변환됩니다
- `[UnityCliTool(Name = "my_name")]`으로 이름을 재정의할 수 있습니다
- Unity 메인 스레드에서 실행되므로 모든 Unity API를 안전하게 호출할 수 있습니다
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
