# #15 — `EditorApplication.Exit(0)` via `exec` does not quit

## Summary

`exec "EditorApplication.Exit(0)"` is not a reliable quit path. The snippet runs inline on the
editor's main thread inside the connector's command dispatch, so the process teardown races the
HTTP reply, and a managed quit veto has no way to report itself. This change adds an explicit
`editor quit` / `editor restart` lifecycle that answers the request before scheduling `Exit`,
saves or discards dirty scenes deliberately, reports visible `wantsToQuit` subscribers, and can
fall back to terminating the process.

## Reported behaviour

From #7 item 8: the editor ignored a quit request. `EditorApplication.Exit(0)` via `exec` did not
quit it; it took SIGTERM.

## Reproduction attempts

The shared editor was already unresponsive while this task ran, and the task's absolute rule forbids
quitting or restarting it (`EditorApplication.Exit`, `editor quit`, `unity-restart`). The actual
quit was therefore **not exercised live**. These are the read-only attempts that were possible:

1. `unity-cli status` (2026-11-04 12:40:54 UTC):
   `Unity (port 8090): not responding (last heartbeat 12m14s ago)`.
   No live connector to probe. Reproduced: no (editor unavailable).
2. `timeout 35 unity-cli --timeout 15000 exec 'return 1+1;'`:
   `Waiting for Unity...` then `Error: timed out waiting for Unity`.
   Even a pure expression could not be dispatched, so dirty-scene and subscriber probes could not
   run. Reproduced: no (editor unavailable).
3. Earlier read-only probes of editor state and of the `EditorApplication.wantsToQuit` backing
   delegate (reflection over `GetInvocationList`) both timed out after 120 s with no output:
   `unity-cli exec 'return new { isCompiling = ... };'` and the corresponding reflection snippet.
   Reproduced: no (editor unavailable).
4. Static inspection of the connector and of the unmerged `origin/issue-15` commits. The branch's
   own live notes (`issue 24`) tested the *new* scheduled path, not `exec "EditorApplication.Exit(0)"`;
   its commit message says `exec` "gave no feedback when Unity ignored the quit" but does not record
   which Unity-side mechanism ignored it.

Because the original session's logs contain only the one-line observation, no candidate below can
be confirmed as *the* trigger for that exact run. What the code does prove is why `exec` is the wrong
place to call `Exit` and why the connector cannot report what happened.

## Root cause

The quit is issued inside the same command that must write the reply:

- `unity-connector/Editor/Tools/ExecuteCsharp.cs:225` invokes the submitted snippet inline on the
  editor main thread.
- `unity-connector/Editor/CommandRouter.cs:21` holds `s_Lock` for the whole handler.
- `unity-connector/Editor/HttpServer.cs:210-219` completes the request's `TaskCompletionSource`
  only after `CommandRouter.Dispatch` returns, and `HttpServer.cs:343-356` writes the HTTP response
  after awaiting that task.

Consequences, all consistent with the report:

1. **Reply/teardown race.** If `Exit` tears the process down before the continuation at
   `HttpServer.cs:356` runs, the CLI loses the reply even though the editor did quit. That looks
   like a failed quit to the caller and leaves no confirmation.
2. **`wantsToQuit` veto.** Unity's managed quit can be cancelled by any
   `EditorApplication.wantsToQuit` handler returning `false`. The connector subscribes to
   `quitting` (`HttpServer.cs:58`, `Heartbeat.cs:48`) but not to `wantsToQuit`; a veto means the
   editor keeps running and the CLI is never told why.
3. **Quit-time UI / blocked main thread.** If `Exit` reaches a save/quit prompt, the main thread is
   parked before the handler returns. `s_Lock` stays held and the queued HTTP reply is never written;
   subsequent commands look hung rather than "blocked by a quit dialog".
4. **`quitting` is too late to be useful.** `HttpServer.Stop` and `Heartbeat.Cleanup` mark the
   instance `stopped` only after Unity has accepted the quit. On a veto or prompt they never run,
   so the heartbeat cannot distinguish "still alive and refusing" from "still working".
5. **Compile/domain reload.** `HttpServer.cs:59` closes the listener before an assembly reload and
   `Heartbeat.cs:49` writes `reloading`; a reload triggered while the exec request is in flight can
   drop the pending reply exactly like #8/#10. Nothing about `Exit` is treated specially, so a
   quit issued during a compile/reload has the same lost-reply risk.

SIGTERM succeeds because it takes the native shutdown path, bypassing managed `wantsToQuit` and the
deferred editor update pump.

The fix therefore does not depend on knowing which of these fired: it removes the dependence on
`exec` and makes every outcome observable.

## Fix

- **Connector (`unity-connector/Editor/Tools/QuitEditor.cs`, new `quit_editor` tool).**
  - Computes dirty scenes first. `--save` saves each named scene synchronously and refuses an
    untitled dirty scene with `blocked: "untitled-scenes"` instead of opening a Save Scene panel.
    Without `--save`, dirty scenes are discarded; `--discard` is accepted as the explicit form.
  - Reads the `EditorApplication.wantsToQuit` backing delegate by reflection (without invoking the
    handlers) and returns the subscriber names in the reply.
  - Returns the success response first, then schedules `EditorApplication.Exit(0)` on a later
    `EditorApplication.update` tick (0.5 s) so the HTTP reply has flushed.
  - If `Exit` returns without ending the process, logs an error naming the visible `wantsToQuit`
    subscribers, which is the observable form of a veto.
  - Skips the compilation gate (`SkipCompilationGate`) because quitting must work in a broken editor.
- **Go CLI (`cmd/quit.go`, `cmd/restart.go`, `internal/editor/`).**
  - `editor quit [--save|--discard] [--force]` sends `quit_editor`, waits for the heartbeat to say
    `stopped` / the PID to disappear / the heartbeat to go stale, and otherwise fails with the
    timeout plus a `wantsToQuit`/modal explanation. `--force` terminates only after
    `editor.CanManageProcess` confirms the PID is a visible Unity process.
  - `editor restart [--clean] [--save|--discard] [--force] [--editor-path]` stops, optionally
    removes `Library/` and `Temp/`, relaunches the same project and waits for `ready`. It refuses
    before quitting when the CLI cannot manage the editor PID (the container case), because it
    could not relaunch it.
  - `internal/editor/` provides heartbeat-path reading, exit waiting, process termination,
    executable discovery, launch, clean and project-version helpers. `client.IsProcessAlive` is the
    conservative liveness probe shared with exit waiting.
- **Tests.** Go tests cover graceful quit, saved/discarded params, editor-report passthrough,
  ignored quit with/without force, modal blocking, termination failure, restart ordering, clean
  safety, container refusal, heartbeat naming and wait conditions. The C# test only checks tool
  registration/params and deliberately never calls `HandleCommand` because that schedules a quit.

## Verification

Because the C# package cannot be compiled in this environment (no `verify`/refresh allowed), only
the Go side is compiled here.

```text
$ gofmt -w .
(no output)

$ go vet ./...
(no output, exit 0)

$ go test ./...
--- FAIL: TestFindAsset (0.00s)
    update_test.go:15: findAsset: should find asset for current platform
FAIL    github.com/youngwoocho02/unity-cli/cmd
ok      github.com/youngwoocho02/unity-cli/internal/client
ok      github.com/youngwoocho02/unity-cli/internal/editor
```

`TestFindAsset` is the known pre-existing linux/arm64 failure. The new Go tests all pass.

```text
$ GOOS=darwin GOARCH=amd64 go build -o /tmp/uc-wt-darwin .
darwin-ok
$ GOOS=windows GOARCH=amd64 go build -o /tmp/uc-wt-windows.exe .
windows-ok
$ GOOS=darwin GOARCH=arm64 go build -o /tmp/uc-wt-darwin-arm64 .
darwin-arm64-ok
$ GOOS=windows GOARCH=arm64 go build -o /tmp/uc-wt-windows-arm64.exe .
windows-arm64-ok
```

What was **not** verified:

- The C# files were not compiled and the EditMode tests were not run.
- The actual quit was not exercised live. The shared editor was unresponsive and the task
  explicitly forbids quitting, exiting, killing or restarting it.
- `--force`/`Terminate` was not exercised against a real editor. Unit tests use fakes and the
  container cannot see the host editor PID.

## Remaining risk

- `QuitEditor.cs` is unverified by the C# compiler. The reflection field name `wantsToQuit` and
  the C# pattern/`Delegate` usage are the parts most worth checking in `unity-cli verify`.
- `EditorApplication.Exit(0)` behaviour itself is assumed from the branch's live test and Unity's
  API contract; the veto report is best-effort console evidence after the HTTP reply, not part of
  the reply (the reply must be sent first).
- Restart's container guard relies on the same `ps`-based name check as `--force`. If a host PID is
  accidentally visible and named `Unity` inside a container, restart would proceed.
- A quit that completes faster than the 0.5 s reply-flush delay is still possible, though the delay
  was chosen from the branch's live measurements.
