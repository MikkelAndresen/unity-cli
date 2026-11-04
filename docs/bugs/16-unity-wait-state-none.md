# #16 — `unity-wait` reports connector state `None` while the connector is listening

## Summary

The connector started its HTTP listener without writing a heartbeat. On a fresh editor start (or
right after `unity-restart`) the only instance file on disk was the previous session's
`state=stopped` file until `EditorApplication.update` ticked for the first time, and a cold import
can hold that tick for longer than `unity-wait`'s timeout. The host daemon therefore saw no
`ready`/`starting` heartbeat and reported `connector_state=None` even though port 8090 answered.
The connector now publishes a `starting` heartbeat from the listener start, keeps it fresh from a
timer until the first main-thread tick, and the Go CLI understands `starting` as alive-but-not-idle.
The connector and CLI path normalization were aligned (forward slashes, no trailing separator); the
common-case hash is unchanged.

The `unity-wait` client and the host daemon are outside this repo. The reproduction of that
client's `None` verdict was therefore simulated through its input, the heartbeat files, rather than
by reading the daemon.

## Reported behaviour

`unity-wait` timed out after 90 s with connector state `None` although the connector was listening
on 8090. Seen in 10 calls across 2 benchmark runs, both immediately after an editor restart.

## Reproduction attempts

The shared editor must not be restarted from this worktree, so the live editor was only observed
read-only. Its heartbeat at the time was `a7cab838d3b4330d.json`, `state=ready`, pid 93699, port
8090; no `None` window was live to catch.

1. **Live hash check — did the daemon look for a different file than the connector wrote?**
   ```
   AGENT_SANDBOX_PROJECT=/Users/mikkelandresen/Work/unity-cli-project
   printf '%s' "$AGENT_SANDBOX_PROJECT" | md5sum | cut -c1-16
   # a7cab838d3b4330d
   cat ~/.unity-cli/instances/a7cab838d3b4330d.json
   # {"state":"ready","projectPath":"/Users/mikkelandresen/Work/unity-cli-project", ...}
   ```
   Reproduced: no mismatch in this environment. The connector's hash and the existing heartbeat
   filename agree, so path shape was not the cause of this instance of the report.

2. **Fake HOME with a lockfile but no heartbeat — the startup window.**
   ```
   mkdir -p "$proj/Temp" "$home/.unity-cli/instances"; touch "$proj/Temp/UnityLockfile"
   HOME=$home /tmp/claude-1000/uc-ds --project "$proj" --ignore-version-mismatch wait --timeout 2
   # editor_starting_or_blocked: Unity appears open ... but the connector has not loaded
   # exit=2
   ```
   Reproduced the CLI side of "the editor is up but no heartbeat exists" (the state the daemon
   called `None`). The old connector produced exactly this window: listener up, no heartbeat until
   the first update tick.

3. **Fake HOME with a fresh `starting` heartbeat.**
   ```
   write {"state":"starting", "projectPath":..., "timestamp":now, "port":8090, ...}
   HOME=$home /tmp/claude-1000/uc-ds --project "$proj" --ignore-version-mismatch wait --timeout 2
   ```
   Before the Go fix this stayed inside the root `waitForAlive` on the global 120 s request budget,
   so `--timeout 2` did not take effect (`timeout 10` killed it). After the fix it reports
   `Unity (port 8090): starting`, `gave up after ... --timeout 2s ran out`, exit 1. Reproduced
   and fixed the new-state integration.

4. **Fake HOME with a one-shot `starting` heartbeat aged 30 s.**
   ```
   HOME=$home /tmp/claude-1000/uc-ds --project "$proj" --ignore-version-mismatch wait --timeout 2
   # Unity (port 1): not responding (last heartbeat 30s ago)
   # editor_unresponsive: ... (last state "starting") ...
   # exit=2
   ```
   Reproduced why a single write is not enough: during a large import the main thread may not tick
   for more than the CLI's 10 s silence threshold. The connector therefore refreshes `starting`
   from a timer, using the existing any-thread snapshot writer, until the first tick.

5. **Fake HOME with a differently-shaped project argument.**
   ```
   heartbeat projectPath=/work/FoW
   HOME=$home /tmp/claude-1000/uc-ds --project /work/fow ... status
   # Error: no Unity instance found for project: /work/fow   (exit=1)
   HOME=$home /tmp/claude-1000/uc-ds --project /work/FoW  ... status
   # Unity (port 8090): ready                                  (exit=0)
   ```
   Reproduced that case differences in the supplied path make the hashed filename and the Go
   project match disagree. The live project did not have this shape, so it was not the cause here,
   but it is a real remaining path-shape failure. Hash variants: `/work/FoW`,
   `/work/FoW/`, `/work/fow` -> `69c08b653356bfe3`, `ed62227a5dca834b`, `a2e25000c1b420e5`.

## Root cause

- The connector's listener and heartbeat were independent:
  `unity-connector/Editor/HttpServer.cs:86` (`TryStartOnPort`) started listening without writing a
  heartbeat, and `unity-connector/Editor/Heartbeat.cs:162` (`Tick`) returns immediately when the
  listener is not running and otherwise waits for `EditorApplication.update`. Between the old
  editor's `MarkStopped` and the new editor's first tick there was no heartbeat for the project, so
  the daemon's only record was `stopped`/missing -> `connector_state=None`.
- `afterAssemblyReload` reset the write timer but left the first post-reload heartbeat to the next
  tick (`Heartbeat.cs`, old inline handler), so a domain reload reopened the same window.
- Path shape: the connector keyed the file on `md5(Application.dataPath minus "/Assets")`
  (`Heartbeat.cs:188`/`GetFilePath`) and the Go CLI normalizes `\` to `/` and trims trailing
  separators (`internal/editor/heartbeat.go:24`). Common macOS paths agreed; case/symlink
  differences did not, and neither side resolves them.
- On the Go side, `waitForAliveContext` (`cmd/status.go:289`) accepted any fresh heartbeat as
  "alive", and `root.go` used it for `wait` too. Once `starting` existed, the root wait consumed the
  global 120 s request budget before `wait --timeout` even began.

## Fix

Connector (C#):

- `unity-connector/Editor/Heartbeat.cs`
  - `OnListenerStarted()` writes `starting` immediately when the first heartbeat of a domain has not
    been written, otherwise writes the current state. A startup timer rewrites the same snapshot
    (through `WriteFromAnyThread`, which keeps all main-thread-only data snapshotted) every 500 ms
    until the first `EditorApplication.update` tick replaces `starting` with the real state.
  - `afterAssemblyReload` resets the tick timer and, if the listener is already running, calls
    `OnListenerStarted()`, so the first post-reload heartbeat is not deferred to a tick.
  - `ProjectRoot()` normalizes `Application.dataPath` to forward slashes and trims trailing
    separators before hashing; the common macOS string is unchanged.
  - `MarkStopped`/before-reload dispose the startup timer.
- `unity-connector/Editor/HttpServer.cs` — `TryStartOnPort` calls `Heartbeat.OnListenerStarted()`
  as soon as the listener is listening.
- `unity-connector/Tests/Editor/HeartbeatPayloadTests.cs` — asserts the payload carries
  `state=starting` before the first tick.

Go CLI:

- `cmd/wait.go` — documents `starting` as alive but not idle; adds `isStartingState`.
- `cmd/status.go` — `waitForAliveContext` no longer returns on a fresh/advancing `starting`
  heartbeat; it waits for the first non-starting beat.
- `cmd/root.go` — for `wait`, an already-starting instance bypasses the root `waitForAlive`, because
  `wait`'s own `--timeout` is its budget and `waitCmd` polls `starting` itself.
- `cmd/status.go` — `status` prints what `starting` means instead of leaving it as an unexplained
  state; the evidence path already carries the state into its reports.
- `internal/client/crash.go` — `reloadNote` names `starting` as a busy-but-explained state.
- Tests: `cmd/wait_test.go`, `cmd/status_test.go`.

## Verification

Go checks run from `/tmp/claude-1000/uc-wt`:

```
gofmt -w cmd internal      # applied to changed files
go vet ./...               # clean
go test ./...              # only the known cmd/update_test.go findAsset failure
```

Live editor evidence (read-only): `unity-cli status --project "$AGENT_SANDBOX_PROJECT"` reported
`Unity (port 8090): ready`, pid 93699, and the heartbeat file was
`a7cab838d3b4330d.json`; `printf '%s' "$AGENT_SANDBOX_PROJECT" | md5sum | cut -c1-16` was also
`a7cab838d3b4330d`. The new C# could not be compiled or exercised here (no `verify`/refresh/restart
allowed for this worktree); the orchestrator must compile it.

No real `unity-wait`/restart reproduction was possible: `unity-wait` is a sandbox shim and the host
daemon source is not mounted here. Attempts 2–5 above exercise the heartbeat inputs that daemon
consumes.

## Recommendation for the sandbox's `unity-wait` / host daemon (outside this repo)

- Treat a heartbeat as the editor's identity, not `ready` as the only live state: report `starting`,
  `compiling`, `refreshing`, `reloading`, `building` and `modal` distinctly, and keep waiting until
  `ready` (or report the actual busy state at timeout instead of `None`).
- Find the editor by `pid`/`port` plus `projectPath`, preferably by reading every
  `~/.unity-cli/instances/*.json` and comparing `realpath(projectPath)` case-insensitively, instead
  of relying only on the hash filename. If the hash is used, use the same rule as the connector and
  CLI (`md5` of the project root with `/` separators and no trailing separator), and on a miss report
  the file it looked for plus every instance file it found.
- On `state=starting`, do not time out at 90 s merely because no newer beat arrived: the connector's
  startup beater now refreshes that file every 500 ms; a silent `starting` means the editor's main
  thread or listener died, not that the project path is wrong. A longer first-open budget is still
  reasonable for a very large import.

## Remaining risk

- A case-different or symlinked `--project` path still produces a different hash filename for
  `unity-cli editor restart/quit` (`internal/editor.ReadHeartbeat`), and for the out-of-repo daemon.
  The connector/CLI common-case normalization is aligned, but no symlink/case canonicalization was
  added because that would change which file existing tools find; the daemon recommendation above
  covers the robust lookup.
- The startup beater is a new timer in the connector. It is stopped on the first tick, before/after
  a domain reload, on quit and on listener failure; its writes go through the existing snapshot and
  file lock, but it has not been compiled or run in this worktree.
- `waitForAlive` still accepts other busy states (`compiling`, `refreshing`, `reloading`, `building`)
  as alive, as before; only `starting` is held back because it means no main-thread update has run.
