# #28 The call after a reload hits a stale port

## Summary

Discovery now follows the newest heartbeat for a project instead of the first file read from the
instances directory, and a request whose connection died to a reload is re-resolved and retried once
when that is safe. A read-only command may be repeated after an EOF; a state-changing command such as
`exec` is only repeated when the connection was refused (the request never reached a listener).
Otherwise the error names the new port and says the command may have run. `test` and `verify` now
resolve every send instead of pinning the instance captured before the command.

## Reported behaviour

Right after a domain reload the next call fails with EOF or connection refused while `status` reports
the editor ready, sometimes on a different port. Retrying a few seconds later works.

## Reproduction attempts

The live editor is shared with another worker and must not be reloaded from this worktree, so the
reload window was reproduced with isolated `HOME` heartbeat directories and Python HTTP servers. Both
the pre-fix binary (built from `HEAD` with `git archive`) and the fixed dev binary were driven through
the same script. Scenario A has two heartbeat files for the same project; the older one sorts first by
filename. Scenarios B and C start on an old port whose server reads the request, waits 0.6 s, and
closes without a response; 0.25 s in, a new heartbeat appears on a second server that answers.

### 1. Two heartbeat files for one project — reproduced: yes

```
HOME=<fake> uc-ds --project <project> --ignore-version-mismatch --timeout 5000 status
HOME=<fake> uc-ds --project <project> --ignore-version-mismatch --timeout 5000 console
```

Before:

```
status : (0, ..., 'Unity (port 48063): not responding (last heartbeat 30s ago)\n
          editor_unresponsive: ... stopped answering 30s ago ...')
console: (1, '', 'Waiting for Unity...\nError: editor_unresponsive: ... stopped answering 35s ago
          ... Heartbeat: .../aaa_old.json ... Lookup: timed out after 4.999s ...')
```

The old port was stale by 30 s and the fresh record was ignored, while the editor was actually healthy.

After:

```
status : (0, 'Unity (port 46401): ready ...', '')
console: (0, 'answered-on-46401', '')
```

### 2. Safe command after the old listener drops — reproduced: yes

```
HOME=<fake> uc-ds --project <project> --ignore-version-mismatch --timeout 5000 console
```

Before:

```
console: (1, '', 'Error: Unity reloaded or rebound its port during the request (not a crash):
          cannot connect to Unity at port 46831: ... EOF
          heartbeat: .../zzz_new.json ... is answering on port 42139 now
          next: run 'unity-cli wait' ... then repeat the request.
          state: Unity is back: Edit mode (heartbeat \"ready\", port 46831)')
```

The state line even went back to the stale port because the state lookup used the same first-match
discovery.

After:

```
console: (0, 'answered-on-39697', '')
```

The retry used the freshest heartbeat and succeeded without operator action.

### 3. State-changing `exec` after an EOF — reproduced: yes

Same server/heartbeat setup, but the command is `exec 'return 1;'`.

After:

```
exec: (1, '', 'Error: Unity reloaded or rebound its port during the request (not a crash):
       cannot connect to Unity at port 40895: ... EOF
       heartbeat: .../zzz_new.json ... is answering on port 44521 now
       next:      run 'unity-cli wait' ...
       retry:     the editor reloaded and now listens on port 44521; the exec may have run — check before retrying
       state:     Unity is back: Edit mode (heartbeat \"ready\", port 44521)')
```

It is not resent: the old server may already have read and started the snippet. A refused connection
(a dial failure) is the only transport error that allows resending `exec`; that path is covered by
`TestSendAfterReload_RetriesStateChangingCommandOnlyWhenRefused`.

### 4. Live editor, read-only — reproduced: no (nothing to reproduce)

```
uc-ds --ignore-version-mismatch status
# Unity (port 8090): ready ... PID 93699
uc-ds --ignore-version-mismatch exec 'return 1+1;'
# 2
uc-ds --ignore-version-mismatch tools
# { "tools": [ "api", ... ] }
```

The shared editor was not reloaded: `editor refresh --compile`, `verify` and Play-mode changes were
off-limits while the other worker used it. The reload window itself is therefore covered by the fake
conditions above and by unit tests, not by a live domain reload.

## Root cause

- `internal/client/client.go:247` (before this change) — `DiscoverInstance` returned the **first**
  active instance whose project path matched, in directory order. Two records for one project (an old
  worker file, or a file written under a different path hash) meant the stale port could win forever.
  `status` used the same function, so it could report a stale record too; the `state:` note after a
  lost answer used it as well (visible as port 46831 in attempt 2 before the fix).
- `internal/client/client.go:386-390` — `SendContext` classified an EOF/refused connection as a
  reload (`ConnectionFailure`) and returned it; nothing re-resolved the instance and resent a request
  that could safely be repeated.
- `cmd/root.go:194,215` (before this change) — `test` and `verify` captured `currentInst` once and
  sent every command to that fixed port.

## Fix

- **`internal/client/client.go`** — project and cwd discovery now choose the newest matching active
  heartbeat (`newestMatching`). Added `RequestNotDelivered`, which recognises a dial failure
  (`net.OpError.Op == "dial"` / `ECONNREFUSED`) as a request that no listener could have read.
- **`internal/client/evidence.go`** — `ReconnectInstance` reads the raw heartbeat files, ignores
  `stopped`/timestamp-less records, scopes to the same project (or port when the project is unknown),
  and returns the newest record; a matching pid breaks a timestamp tie.
- **`cmd/root.go`** — `sendAfterReload` wraps every send: it resolves, sends, and after a
  `FailureReload` re-resolves with `ReconnectInstance`. It retries once only when the heartbeat moved
  port or advanced, and only when the command is in `retrySafeCommands` (`console`,
  `compile_status`, `compile_diagnostics`, `tools`, `tool_describe`, `modal`, `list`, `status`) or the
  connection was never established. Otherwise it appends `retry: the editor reloaded and now listens
  on port N; the <command> may have run — check before retrying`. The retry spends
  `remainingTimeoutMs(ctx, …)`, so the one invocation deadline still bounds it. `test`/`verify` use
  the same wrapper and resolve on each send instead of pinning `currentInst`.

## Verification

```sh
gofmt -l .                       # empty
go vet ./...                     # clean
go test ./...                    # only the known cmd/update_test.go TestFindAsset failure
go test ./cmd -run 'SendAfterReload|DiscoverStatusInstance_PicksFreshest' -count=1
# ok
go test ./internal/client -run 'DiscoverInstance_ProjectPicks|ReconnectInstance|RequestNotDelivered' -count=1
# ok
GOOS=windows GOARCH=amd64 go build ./...   # ok
GOOS=darwin  GOARCH=arm64 go build ./...   # ok
```

New tests:

- `internal/client/client_test.go` — freshest duplicate heartbeat wins; `ReconnectInstance` follows
  the newest live record for the project (ignoring a newer `stopped` file and another project);
  `RequestNotDelivered` is true for a refused dial and false for EOF.
- `cmd/reload_retry_test.go` — safe command on a moved port retries and answers; `exec` retries after
  a refused connection; `exec` does **not** retry after EOF and reports the new port and "may have
  run"; a safe command is retried exactly once; `status` discovery picks the freshest project record.

The Python reproduction was run against both `/tmp/claude-1000/uc-ds-old` (HEAD) and the fixed
`/tmp/claude-1000/uc-ds`; attempts 1-3 above quote both outcomes. The live editor was only used for
`status`, `exec 'return 1+1;'` and `tools`, all successful.

## Remaining risk

- A command classified as safe may still not be perfectly idempotent in a broad sense: `console`
  with `clear`, and `modal` with `answer`, change state. Repeating them after a lost answer is at
  worst a second clear or an answer to a dialog that is still open; they are grouped with the reads
  because the alternative is a guaranteed failure on a reloaded editor. State-changing tools
  (`exec`, `menu`, `screenshot` with a write path, `reserialize`, `refresh_unity`, …) are never
  blindly resent.
- The retry needs the new heartbeat to exist by the time the old connection fails. If it does not,
  the original EOF error is returned; the next invocation resolves the fresh heartbeat normally.
  The CLI does not invent a port or poll indefinitely inside the retry.
- `test`/`verify` now resolve per send, but the PlayMode results file is still polled by the port
  captured at the start (`testCmd`'s `port` argument). A reload that changes ports before the test
  result is written could still leave that poll on the old result path. The test command itself is
  no longer pinned to the old port.
- The lower-level `Send` used by code outside `cmd` still has no retry; this fix is in the CLI send
  path, which is where the reported next-call failures occurred.

## Follow-up: retry a command the connector says never ran

### Change

`#28` retried on transport failures only, so a read-only call made during a reload could still fail
with the connector's `domain_reload` answer even though `data.started:false` guarantees the tool
handler was never entered. `cmd/root.go` now recognises exactly that answer
(`answerSaysNeverRan`: code `domain_reload` and an explicit `started:false`, parsed by
`client.ConnectorResponseStarted`) and repeats it **once for every command**, including `exec` and
other state-changing tools. Before the retry it polls discovery with `waitForEditorAnswering` until
the heartbeat is fresh (within `staleHeartbeatAge`) and idle (`ready`/`playing`/`paused`), so the
retry goes to the port that is answering after the reload. The wait and the retry both spend the
invocation's remaining budget; if the deadline runs out the original connector answer is returned
unchanged. `reload_during_command` (the command ran) and `main_thread_busy` are still never retried.
A successful retry prints nothing extra on stderr and adds nothing to `meta`.

New tests in `cmd/reload_retry_test.go`:

- `domain_reload` + `started:false` on an `exec` retries once against the fresh heartbeat and
  succeeds;
- `started:true` and a missing `started` flag are not retried;
- an exhausted invocation deadline leaves the original `domain_reload` answer as the result.

### Live verification

Built with `go build -o /tmp/claude-1000/uc-dev .`; the connector is the live `file:` package.
`unity-connector/Editor/Core/Response.cs` was touched, then `editor refresh --compile` ran in the
background while the two loops below ran against the editor. JSON bodies from `compile_status` are
omitted; the rc/elapsed columns are the loop output.

```
compile_status #01 rc=0   2883ms
compile_status #02 rc=0  18638ms  Waiting for Unity... / Unity is ready.
compile_status #03 rc=0   1354ms
compile_status #04 rc=0   1038ms
compile_status #05 rc=0    924ms
compile_status #06 rc=0   1143ms
compile_status #07 rc=0    863ms
compile_status #08 rc=0   1046ms
compile_status #09 rc=0    861ms
compile_status #10 rc=0    775ms
compile_status #11 rc=0    681ms
compile_status #12 rc=0    891ms
compile_status #13 rc=0    965ms
compile_status #14 rc=0    969ms
compile_status #15 rc=0   1013ms
exec #01 rc=0   5216ms 1
exec #02 rc=0    980ms 1
exec #03 rc=0   1057ms 1
exec #04 rc=0   1071ms 1
exec #05 rc=0    993ms 1
exec #06 rc=0    859ms 1
exec #07 rc=0    965ms 1
exec #08 rc=0    874ms 1
exec #09 rc=0    785ms 1
exec #10 rc=0    962ms 1
refresh rc=0
```

Every call succeeded; the slow ones waited out the compile/reload instead of failing. The refresh
itself reported `compile_succeeded:true`, `force:false`, `stale_after_compile:false` and
`changed_since_previous_compile:1` for `Response.cs`.

## Follow-up 2: the two refusals the first fix still let through

### Change

The first follow-up only recognised the connector's `domain_reload` + `started:false` answer. A live
reload test left two holes:

- **The compile gate refused before dispatch.** `CompilationGate.CheckOrNull`'s "Editor is currently
  compiling" answer carried no code and no `started` flag, so the CLI could not tell it apart from
  an ordinary tool failure. The refusal now carries `code: "compiling"` (`ErrorCodes.Compiling`) and
  `started: false`, and `cmd/connector_error.go` maps the code to a next step. `answerSaysNeverRan`
  accepts both `compiling` and `domain_reload`, so **every** command — including `exec` — waits for
  an idle heartbeat (`waitForEditorAnswering`) and is repeated once; the invocation's remaining
  budget still bounds the wait. The gate's "compile has errors" refusal is deliberately unchanged:
  it is not transient, so repeating it would only repeat the same failure.
- **A safe command lost to an EOF was not retried while the heartbeat still said `reloading`.** The
  old path required `ReconnectInstance` to return a moved port before repeating, and gave up
  otherwise. For commands in `retrySafeCommands`, a reload-classified transport failure now waits
  with `waitForEditorAnswering` until the heartbeat is fresh and idle, then retries once. If the
  deadline runs out the original EOF error is returned. State-changing commands keep the current
  "may have run" report unless the connection was refused (`RequestNotDelivered`).

New Go tests in `cmd/reload_retry_test.go`:

- a `compiling` + `started:false` refusal on `exec` waits out the compile and succeeds on the fresh
  port; a `compiling` answer without `started` is not retried;
- `compile_status` that gets an EOF while the heartbeat still says `reloading` waits until the
  editor is `ready` and then succeeds;
- the existing safe-command retry tests now resolve the fresh heartbeat, and the safe retry still
  happens exactly once.

### Live verification

Built with `go build -o /tmp/claude-1000/uc-dev .`; the connector is the live `file:` package.
`unity-connector/Editor/Core/Response.cs` was touched, `editor refresh --compile` ran in the
background, and the loop ran `compile_status --params '{"scan":false}'` + `exec 'return 7;'` 12
times, 1 s apart, each with `--timeout 45000`.

```
compile_status #01 rc=0 1349ms
exec #01 rc=0 27442ms out=7
compile_status #02 rc=0 931ms
exec #02 rc=0 1011ms out=7
compile_status #03 rc=0 946ms
exec #03 rc=0 862ms out=7
compile_status #04 rc=0 968ms
exec #04 rc=0 981ms out=7
compile_status #05 rc=0 937ms
exec #05 rc=0 928ms out=7
compile_status #06 rc=0 980ms
exec #06 rc=0 1476ms out=7
compile_status #07 rc=0 687ms
exec #07 rc=0 810ms out=7
compile_status #08 rc=0 747ms
exec #08 rc=0 925ms out=7
compile_status #09 rc=0 743ms
exec #09 rc=0 889ms out=7
compile_status #10 rc=0 915ms
exec #10 rc=0 873ms out=7
compile_status #11 rc=0 928ms
exec #11 rc=0 1017ms out=7
compile_status #12 rc=0 867ms
exec #12 rc=0 904ms out=7
refresh rc=0
```

24/24 calls succeeded. `exec #01` (27.4 s) is the compile refusal path: it was refused while
compiling, waited for the reload to finish, and then returned `7`; the remaining calls were served
directly. The refresh itself reported `compile_succeeded: true`, `stale_after_compile: false` and
`changed_since_previous_compile: 1` for `Response.cs`.

C# check: `verify --assembly UnityCliConnector.Tests.Editor` — compile ok, 331/332 tests passed (the
skipped one is the opt-in real-dialog modal test).

## Review follow-up

- The retry target now comes from the same newest-record helper as `NewestHeartbeat`
  (`newestMatching` over the prune-free instance list); the untestable pid tie-break is gone, and
  both the newer-beat and unchanged-timestamp branches have tests.
- An explicit `--port` pins the target: if the heartbeat moved to another port, the CLI reports the
  original failure instead of silently retrying against an instance the caller did not select.
- When the retry itself fails, the error keeps the first failure reachable, so the original reload
  diagnosis is not replaced by a bare second refusal.
