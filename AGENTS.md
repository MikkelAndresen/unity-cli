# unity-cli

CLI tool to control Unity Editor from the command line.

## Structure

```
cmd/                  # Go CLI — thin passthrough layer
  root.go             # Entry point, flag/arg parsing, default passthrough
  editor.go           # editor command (waitForReady polling)
  quit.go             # editor quit: quit_editor, wait for exit, optional SIGTERM fallback
  restart.go          # editor restart: stop, optional clean, relaunch, wait for ready
  test.go             # test command (PlayMode result polling)
  build.go            # build command (job start, build_status polling, progress lines)
  verify.go           # verify command (compile + compile_diagnostics + tests, brief output)
  wait.go             # wait command: block until the heartbeat is idle, then report compile provenance
  provenance.go       # compileProvenance payload shared by verify, editor refresh --compile and wait
  playmode.go         # editor play/stop state confirmation, and the state report after a reload took a command's answer
  status.go           # status, waitForAlive, heartbeat reading
  signals.go          # SIGINT/SIGTERM/SIGALRM/SIGHUP handling; signals_unix.go/signals_windows.go
  evidence.go         # editor_starting_or_blocked: the startup-dialog report built from on-disk evidence
  dialogs.go          # dialogs command (macOS only): list/click Unity windows with AppleScript
  update.go           # self-update from GitHub releases
  version_check.go    # periodic update notice (12h interval)
internal/client/      # Unity HTTP client, instance discovery, Temp/UnityLockfile evidence,
                      # crash artifacts (mono_crash/Editor-prev.log) and why a dropped connection died
internal/editor/      # editor lifecycle: heartbeat files, exit waiting, process termination,
                      # editor executable discovery, launch, project clean/version checks
unity-connector/      # C# Unity Editor package (UPM)
  Editor/
    Core/             # Shared utilities (Response, ParamCoercion, ToolParams, StringCaseUtility, StackTraceFilter, CompileProvenance,
                      # AssemblyStaleness (output DLL vs newest source), ExecAwait, ExecCompileCache, ExecCompiler, ExecSource, MainThread,
                      # RequestTicket/RequestTracker (every accepted request gets one answer: queue deadline,
                      # domain-reload answer, skip-on-abandon), ErrorCodes (data.code contract with the CLI),
                      # ResponseJson (serialization can never leave a connection unanswered),
                      # PlayModeTransitions (play-mode change deferred a tick so its answer survives the reload it starts))
                      # ApiIndex/ApiReflection/XmlDocLookup back `api`; CompileHints reuses them for compile-error hints
    Tools/            # Tool implementations (auto-registered via [UnityCliTool] attribute)
    Burst/            # burst_status tool; own asmdef, only compiles when com.unity.burst is present
    BuildJobs/        # Player build jobs: job record, Temp/ persistence across reloads, runner off the command lock
    InputInjection/   # `input` tool — own asmdef, only compiles when com.unity.inputsystem is present
    Modal/            # macOS modal dialog handling: AppKit via objc_msgSend, runs during the modal loop
    TestRunner/       # Test runner (RunTests, TestRunnerState)
```

## Development

### Adding a Command

1. Add a C# tool in `unity-connector/Editor/Tools/` with `[UnityCliTool(Name = "command_name")]`
2. CLI command name matches the tool name — default passthrough handles dispatch
3. Positional args arrive as `args` array, flags as named params
4. Go-side code is only needed for polling/waiting logic (editor, test)
5. A tool that needs an optional package goes in its own folder + asmdef with `versionDefines` and
   `defineConstraints` (see `Editor/InputInjection/` and `Editor/Burst/`); ToolDiscovery scans every
   loaded assembly, so no registration is needed. Unity must generate the `.meta` files — refresh the
   asset database rather than writing GUIDs. Keep the version-sensitive part in that assembly and the
   pure logic (parsing, filtering) in `Editor/Core/` so the tests compile without the package.
6. A tool that must answer while the main thread is busy (a modal loop, a player build) is marked
   `[UnityCliTool(RunOffMainThread = true)]`: the HTTP server calls it on the request thread, past the
   command lock and the compilation gate. It may then touch no Unity API except through a thread-safe
   path of its own (`modal`, `build_status`).

## Verification

Run all of the following before pushing:

```bash
go clean -testcache
gofmt -w .
~/go/bin/golangci-lint run ./...
~/go/bin/golangci-lint fmt --diff
go test ./...
```

### Integration Tests (requires Unity)

Integration tests are tagged with `//go:build integration` and excluded from the default test run.
Run them manually when Unity Editor is open:

```bash
go test -tags integration ./...
```

CI skips these since Unity is not available.

## Checklist

### 변경 시

CLI option, command, parameter를 수정하면 관련된 모든 곳을 함께 반영한다:
- C# tool (Parameters class, HandleCommand)
- Go help text (root.go의 overview + command별 detailed help)
- README.md, README.ko.md

### 버전 관리

CLI(Go)와 Connector(C#)는 같은 릴리스 버전으로 맞춘다.

- **CLI**: git tag는 `vX.Y.Z`
- **CLI binary**: `.github/workflows/release.yml`의 `-X main.Version=${VERSION}`로 tag가 주입되고, `main.go`가 `cmd.Version`에 전달한다. 소스 기본값 `dev`는 배포 버전으로 쓰지 않는다.
- **Connector**: `unity-connector/package.json` 버전은 `X.Y.Z`
- **Connector heartbeat**: `unity-connector/Editor/Heartbeat.cs`의 `CONNECTOR_VERSION`도 같은 `X.Y.Z`로 갱신한다.
- 개발 빌드의 CLI `Version=dev`는 비교 예외지만, 배포 빌드는 항상 Connector 버전과 같아야 한다.
- Go 코드나 C# 커넥터 코드 중 하나만 바뀌어도, 배포 시에는 tag와 `package.json` 버전을 같은 번호로 갱신한다.
- CLI는 실행 전 Connector heartbeat의 `connectorVersion`을 읽어 호환성을 판정한다. patch 차이는 조용히 허용하고, minor/major 차이는 stderr 경고 한 줄만 낸다. 진단용 명령은 게이트하지 않으며, `minConnectorVersion`(0.7.0)보다 오래된 connector만 거부한다. 버전 갱신 누락은 경고로 드러내되 세션을 막지 않는다.

### 작업 마무리 시

- Verification 항목 전부 실행
- 변경한 기능은 Unity가 열려 있으면 `unity-cli`로 직접 실행해서 동작 확인
- 로컬 임시 파일(테스트용 스크립트, 디버깅 출력 등) 정리
- 관련 없는 변경은 별도 커밋으로 분리

## Git

Commit all unstaged changes before finishing. Unrelated changes should be committed separately.

## 실행 규칙

`go run .`은 테스트 목적일 때만 사용. CLI 기능 실행은 반드시 설치된 바이너리 `unity-cli`로.

## 릴리스 플로우

"커밋하고 올려" 지시 시 아래를 한 번에 수행:

1. Verification 전부 실행
2. CLI tag `vX.Y.Z`와 Connector `unity-connector/package.json` `X.Y.Z` 버전을 같은 번호로 갱신
3. 커밋 + push
4. CLI 변경 있으면 새 tag push
5. CI(CI + Release) 완료 대기 (`gh run watch --exit-status`, background)
6. `go clean -cache -testcache`로 빌드/테스트 캐시 전부 정리
7. 둘 다 성공하면 `unity-cli update`로 설치된 CLI 업데이트

## CI

- `push/PR → main`: build, vet, test, lint, format
- `tag push (v*)`: cross-compile (linux/darwin/windows × amd64/arm64) + GitHub Release
