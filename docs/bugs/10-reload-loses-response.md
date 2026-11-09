# #10 A command that triggers a domain reload loses its answer

## Summary

A command that starts a domain reload used to be reported as one that never ran. The connector now
answers a request whose handler had already started with a distinct code
(`reload_during_command`: it ran, only its return value was lost), keeps the real result of a request
whose tool had already returned, defers Play-mode changes one update tick so the answer is written
before the reload the change starts, and has the CLI read the resulting state back from the heartbeat.
`editor play` / `editor stop` now answer with the state the editor is actually in and exit non-zero if
the requested state was never reached.

## Reported behaviour

> Domain reloads break the response. Leaving Play mode via `exec`
> (`EditorApplication.isPlaying = false`) hung. Deferring it with `EditorApplication.delayCall`
> worked. `manage_editor stop` in 0.5.1 covers this case.

## Reproduction attempts

All runs are against the live editor (Unity 6000.6.5f1, PID 93699, connector = this repo as a `file:`
package), with the editor backgrounded on the host — the state it is in for every CLI-driven run here.
The dev CLI is built from this tree; `unity-cli` on PATH is the released 0.7.0 binary.

### Environment fact that shapes every play-mode case — checked first

```sh
unity-cli exec 'return string.Join(",", System.Enum.GetNames(typeof(UnityEditor.EnterPlayModeOptions)))
  + " values=" + string.Join(",", System.Enum.GetValues(typeof(UnityEditor.EnterPlayModeOptions)).Cast<int>())
  + " enabled=" + UnityEditor.EditorSettings.enterPlayModeOptionsEnabled
  + " opts=" + (int)UnityEditor.EditorSettings.enterPlayModeOptions;'
# None,DisableDomainReload,DisableSceneReload,DisableSceneBackupUnlessDirty values=0,1,2,4 enabled=True opts=3
```

This project has Enter Play Mode Options **on**, with `opts=3` = `DisableDomainReload | DisableSceneReload`
(`ProjectSettings/EditorSettings.asset`: `m_EnterPlayModeOptionsEnabled: 1`, `m_EnterPlayModeOptions: 3`).
So in *this* project entering and leaving Play mode does not reload the domain, and the reported
"leaving Play mode hung" shape cannot be reproduced with play-mode commands alone. The same connector
code path (`beforeAssemblyReload` while a command is Running) is reproducible with a script-compile
reload, which is what attempts 6 and 7 use.

### 1. `exec 'EditorApplication.isPlaying = false; return 1;'` from Play mode — reproduced: no

```sh
uc-dev … exec 'EditorApplication.isPlaying = false; return 1;' --timeout 20000     # rc=0, out: 1
unity-cli … exec 'EditorApplication.isPlaying = false; return 1;' --timeout 20000  # rc=0, out: 1
```

Both answered. `isPlaying = false` from `exec` works here because the play-mode exit does not reload
the domain in this project (see above). The case is still wrong in a project with domain reload on,
which is what attempts 6-8 exercise.

### 2. `exec 'EditorApplication.isPlaying = true; return 1;'` and `exec '…ExitPlaymode();'` — reproduced: no

Answered (`1`, `exit requested`); the heartbeat went `entering_playmode` → `playing` → `ready`. Same
reason as attempt 1.

### 3. `editor play` / `editor stop` — reproduced: yes (an unconfirmed promise, not a hang)

```sh
unity-cli … editor play   # rc=0, out: Entered play mode.          (released 0.7.0, also dev CLI)
unity-cli … editor stop   # rc=0, out: Exited play mode.
```

`manage_editor` wrote `EditorApplication.isPlaying` inline and answered in the same call. Nothing here
proves the state change happened, and in a project where the change reloads the domain the answer
itself is the thing at risk — `wait_for_completion` is worse: the handler sits inside its
`playModeStateChanged` await when the reload it started fires, so the confirmation is destroyed with
the domain that produced it.

### 4. `exec` that requests a compile and returns — reproduced: no (the write won)

```sh
uc-dev … exec 'UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(); return 1;' --timeout 90000
# rc=0, out: 1   … then status showed state "reloading"
```

The tool handler had already answered, so the caller got the real value: `beforeAssemblyReload`
answers only what is still unanswered and then waits up to 1.5 s for tracked answers to reach the
socket (`#8`'s fix). Attempt 8 shows the same path losing the race is still possible when the request
dies in the socket window.

### 5. `exec` that answers, with the reload scheduled for the next tick — reproduced: no

```sh
uc-dev … exec 'void T(){ EditorApplication.update -= T; CompilationPipeline.RequestScriptCompilation(); }
               EditorApplication.update += T; return "answered-first";' --timeout 90000
# rc=0, out: answered-first
```

The reload began right after the handler returned and the response still got out, which is the flush
window doing its job.

### 6. `exec` running when the reload arrives — reproduced: yes (the headline)

```sh
uc-dev … --timeout 90000 exec 'UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(); await Task.Delay(6000); return 42;'
```

Before the fix (connector at HEAD before this change, dev CLI):

```
rc=1 elapsed=7s
Error: exec was running when Unity stopped answering (domain reload in progress, retry); it may have
       partially run. Check the editor state before retrying.
Details: {"code":"domain_reload","command":"exec","waited_ms":6396,"started":true,…}
  next:      the command did not reach a tool handler; run 'unity-cli wait' for the reloaded editor,
             then repeat it
```

The command had demonstrably run — it is what requested the compile the reload came from — and the
`data` even said `started:true`, yet the code and the CLI's own next-step line told the caller the
command never reached a handler and was safe to repeat. Repeating it applies the effect twice. The
released 0.7.0 CLI printed the same message with no hint at all.

### 7. Same call with the released 0.7.0 CLI — reproduced: yes

```
rc=1  Error: exec was running when Unity stopped answering (domain reload in progress, retry); it may
      have partially run. …   (no `next:` line: 0.7.0 does not know the code)
```

### 8. A reload-during-request variant that is still lost — reproduced: yes (once)

```sh
uc-dev … --timeout 90000 exec '… delayCall += () => RequestScriptCompilation(); return "answered-first";'
rc=1  Error: Unity reloaded or rebound its port during the request (not a crash):
          Post "http://127.0.0.1:8090/command": EOF
      heartbeat: ~/.unity-cli/instances/a7cab838d3b4330d.json still says "reloading" (last beat 1s ago)
```

The request died in the window where the OS has accepted the socket and the connector never parsed the
request — `#8`'s documented remaining risk, and `#28`'s fix (re-resolve and retry on EOF). It is now
at least followed by the state report.

### 9. `EditorApplication.delayCall` does not run in a backgrounded editor — reproduced: yes

This one is a bug the fix caused and then removed. The first version of the deferred Play-mode change
used `EditorApplication.delayCall`, and `editor play` then sat 90 s without entering Play mode:

```
Error: editor play: Unity did not reach Play mode within 26s; it is in Edit mode (heartbeat "ready")
```

Probing the mechanism directly:

```sh
uc-dev … exec 'EditorApplication.delayCall += () => SessionState.SetBool("uc_delay_marker", true); return "scheduled";'
uc-dev … exec 'return SessionState.GetBool("uc_delay_marker", false);'      # False — after 3 s and later
uc-dev … exec 'void Tick(){ EditorApplication.update -= Tick; SessionState.SetBool("uc_update_marker", true); }
               EditorApplication.update += Tick; return "registered";'
uc-dev … exec 'return SessionState.GetBool("uc_update_marker", false);'     # True
```

The heartbeat keeps beating (0.5 s), so `EditorApplication.update` runs, but the tick `delayCall`
drains from does not. Note this contradicts the issue's own observation ("deferring it with
`delayCall` worked"): that worked in a focused editor. The deferral now rides `EditorApplication.update`
(the same pump the connector's command queue uses), with the measurement recorded next to the code.

### 10. Requests racing a compile-triggered reload — regression check: clean

```sh
# 20 × exec 'return 1;' (--timeout 60000) every 0.4 s, with a compile request in the middle
18 × {"code":"domain_reload","started":false}  "exec never ran: … (the domain is reloading, waited 2.8-8.7s)"
 2 × answered after waiting out the reload
 0 × "connection closed before response" reported as success
 0 × reload_during_command  (correct: none of these had started)
```

### 11. A play-mode change *with* a domain reload — not attempted

Turning Enter Play Mode Options off would change this shared project's editor settings (outside this
repo, and it would change the ground under other work). The two halves of that case are covered
separately: the reload-while-running path by attempts 6-7, and the deferred play-mode change by the
EditMode tests plus attempts 1-3 live.

## Root cause

Connector, at HEAD before this change:

- `unity-connector/Editor/Core/RequestTracker.cs:196-206` — `BuildReloadError` used one code and one
  family of words for both stages of a request. For a request whose handler was already `Running` it
  emitted `code: domain_reload` with "it may have partially run", so the caller could not tell
  "repeat it, nothing happened" from "something changed and you have not seen the result".
- `unity-connector/Editor/Tools/ManageEditor.cs:50-77` — `play` and `stop` assigned
  `EditorApplication.isPlaying` inside the handler. In a project where the change reloads the domain,
  the reload begins on a later tick while the response is still on its way out, and with
  `wait_for_completion` the handler is still awaiting `playModeStateChanged` inside the domain the
  change destroys — the confirmation is then unanswerable from inside it.
- `unity-connector/Editor/Tools/ExecuteCsharp.cs:225` — `exec` invokes the snippet with no notion of
  what the snippet set in motion, so the answer never says that the editor is about to change state.

CLI:

- `cmd/connector_error.go:24` — `domain_reload` → *"the command did not reach a tool handler"*. With
  the connector using that code for running commands too, this was an actively wrong instruction.
- `cmd/editor.go:34-42` — `editor play`/`editor stop` returned the connector's sentence and nothing
  else: no check of the state the editor ended in, no distinction between "applied" and "asked for".

Found on the way, same area:

- `unity-connector/Editor/HttpServer.cs:464` — `success.meta as JObject ?? new JObject()`: a tool's
  `meta` is a CLR object, so on any pickup slower than 1 s the merge threw the tool's own keys away
  and reported only the queue numbers. That hid exec's compile cache state, and would have hidden the
  new state-change note.

What was already right (from `#8`): `beforeAssemblyReload` answers what is still unanswered and then
waits up to 1.5 s (`PENDING_ANSWER_WAIT_MS`, `HttpServer.cs:36,206`) for tracked answers to be
written. Attempts 4 and 5 show that window holding, and a request whose tool had already returned is
never overwritten by the reload answer.

## Fix

Connector:

- **`Editor/Core/ErrorCodes.cs`** — new `reload_during_command`: the handler was entered, so the
  command ran (at least partly) and only its return value went down with the old domain. `#8`'s
  `domain_reload` now means only what it says: never ran, safe to repeat.
- **`Editor/Core/RequestTracker.cs`** — `BuildReloadError(ticket, why, quitting)`: a `Running` request
  is answered before the listener stops with the new code and *"had already started … its effects may
  have been applied, but its return value was lost … the change may already be in"*; a queued one
  keeps the `domain_reload` wording; the quit path carries no code and no "wait for the reload"
  advice, because a quit editor is not coming back. A request whose tool had already answered keeps
  its own value (asserted, not just relied on).
- **`Editor/Core/PlayModeTransitions.cs` (new)** — `DomainReloadExpected(optionsEnabled, options)`
  (Enter Play Mode Options only skips the reload when it is enabled **and** names
  `DisableDomainReload`), `ApplyOnNextTick(play)` behind a scheduler seam, and
  `ChangeStartedSince(...)` which reports only what is observable at the moment the answer is decided
  (leaving Play mode keeps `isPlayingOrWillChangePlaymode` set, so an exit is not guessable there).
- **`Editor/Tools/ManageEditor.cs`** — `play`/`stop` answer first
  (`{action, scheduled, domain_reload_expected}`) and apply the state change on the next tick, so the
  response is written by the domain that is still alive. `wait_for_completion` awaits the state change
  only when no reload is coming; with a reload coming it says so and leaves the confirmation to the
  caller.
- **`Editor/Tools/ExecuteCsharp.cs`** — `meta.state_change` names the editor state the snippet moved
  to (`"a play mode change is in flight"`, `"entered play mode"`, `"a script compilation is in flight"`),
  and the message repeats it, so a caller is not surprised by the next command needing `wait`.
  (Initially it read "this snippet started …"; the review follow-up below says why the cause is not
  claimed.)
- **`Editor/HttpServer.cs`** — the slow-pickup meta is merged instead of replaced; `AnswerPending`
  distinguishes reload from quit instead of passing an error code around.

CLI:

- **`cmd/playmode.go` (new)** — `waitForEditorState` (heartbeat polling that stops early on a modal
  dialog and never outruns the caller's `--timeout`), `editorStateLabel` (heartbeat state → "Edit
  mode" / "Play mode" / "a domain reload"), `answerLostToReload`, and `stateAfterReloadNote`. Budgets:
  90 s to confirm a play-mode change, 60 s for the post-reload state note, both capped by the
  invocation deadline.
- **`cmd/editor.go`** — `editor play|stop` confirm the requested state from the heartbeat and answer
  with a report (`action`, `wanted_state`, `editor_state`, `editor_label`, `confirmed`, `answer_lost`,
  `waited_ms`, `connector_reply`). Reaching the state decides the exit code, including when the
  connector's own answer was lost: then the report says *"Unity's own answer was lost in the reload,
  but it is in Edit mode now"* and exits 0. A refusal that says the command never ran is passed
  through untouched rather than waited out. `--no-wait` keeps the old fire-and-forget behaviour.
- **`cmd/root.go`** — for any other command whose answer was lost to a reload
  (`reload_during_command`, or an EOF the client classified as a reload), wait for the editor to come
  back and print `state: Unity is back: Edit mode (heartbeat "ready", port 8090)`, or the state it is
  stuck in. Commands that run their own reload wait (`editor play/stop`, `editor refresh --compile`,
  `test`, `verify`, `build`, `wait`) are excluded so the state is not printed twice.
- **`cmd/connector_error.go`** — the new code's next step: *"the command had already started when Unity
  reloaded, so its effects may be applied and only its return value was lost; check `unity-cli status`
  before repeating it"*.
- README, `unity-cli --help`/`editor --help`, and AGENTS.md's file map updated.

## Verification

```sh
gofmt -l .                      # empty
go vet ./...                    # clean
go test ./...                   # only cmd/update_test.go TestFindAsset (known missing linux/arm64 asset)
unity-cli verify --assembly UnityCliConnector.Tests.Editor
  compile: ok
  tests: 317/318 passed, 1 skipped      # the skipped one is the opt-in real-dialog modal test
uc-dev … --timeout 900000 verify --assembly UnityCliConnector.Tests.Editor
  compile: ok
  compiled 09:01:55 (5 s ago, took 10 s) · 0 source files changed since previous compile
  tests: 317/318 passed, 1 skipped
  verify exit=0
```

New EditMode tests: `Tests/Editor/PlayModeTransitionTests.cs` (the reload prediction over every
`EnterPlayModeOptions` combination, the deferred change not touching `isPlaying` inline, and
`ChangeStartedSince` over every state pair), and three more in `RequestDeadlineTests.cs` (running
request → `reload_during_command` with "return value was lost" and no "never ran"; a completed request
keeping its own value through the reload; a quit answering without a waitable code; the meta merge
keeping the tool's own keys). New Go tests: `cmd/playmode_test.go` (`answerLostToReload` table,
state labels, the two waits, the state note, which categories get it) and play/stop cases in
`cmd/editor_test.go` (confirmed play, confirmed stop after a transition, answer lost to a reload but
state reached ⇒ exit 0, EOF classified as reload ⇒ confirmed, state never reached ⇒ exit 1, `--no-wait`
⇒ no polling, `main_thread_busy` refusal ⇒ passed through without waiting).

Live re-runs of the reproduction cases with the fix in (dev CLI, connector = this repo):

```
1 exec isPlaying=true (edit→play)   rc=0 out=1
  meta: {"compile":"compiled","compile_ms":2024,"reference_assemblies":230,"serialization_error":null,
         "state_change":"a play mode change is in flight","queue_wait_ms":3011,"queue_depth":1}
2 exec ExitPlaymode()               rc=0 out=exit requested        heartbeat afterwards: ready
3 exec isPlaying=false (in edit)    rc=0 out=1
4 editor play                       rc=0 {"action":"play","confirmed":true,"editor_label":"Play mode",
                                         "editor_state":"playing","waited_ms":1088,"answer_lost":false,
                                         "connector_reply":{"action":"play","scheduled":true,
                                         "domain_reload_expected":false}}
5 exec isPlaying=false (play→edit)  rc=0 out=1                     heartbeat afterwards: ready
6 editor stop (already in edit)     rc=0 {"confirmed":true,"editor_label":"Edit mode","waited_ms":29}
7 exec RequestScriptCompilation();
  return 1;                         rc=0 out=1                     heartbeat afterwards: reloading
8 exec compile-request + await 6s   rc=1
     Error: exec had already started when Unity stopped answering (the domain is reloading); its
            effects may have been applied, but its return value was lost. Check the editor's state
            before repeating it — the change may already be in. Unity answers again once the reload is over.
     Details: {"code":"reload_during_command","command":"exec","started":true,…}
       next:   the command had already started when Unity reloaded, so its effects may be applied and
               only its return value was lost; check 'unity-cli status' before repeating it
       state:  Unity is back: Edit mode (heartbeat "ready", port 8090)
9 exec answer, reload scheduled on
  the next update tick              rc=0 out=answered-first
10 status                           rc=0  Unity (port 8090): ready      # left in Edit mode

--no-wait keeps the old shape, and the following call still confirms it:

```
editor stop --no-wait   rc=0 {"action":"stop","scheduled":true,"domain_reload_expected":false}
editor stop             rc=0 {"confirmed":true,"editor_label":"Edit mode","waited_ms":30}
```
```

The released 0.7.0 CLI against the fixed connector prints the connector's new message verbatim with no
`next:` line and no state line (it predates both), and `editor play` still answers before the state
change lands — the confirmation is a CLI-side behaviour.

## Remaining risk

- **A play-mode change that reloads the domain was never driven end-to-end** (attempt 11). The
  deferral is proven live for a change that does *not* reload (attempts 4-6), and the
  reload-while-running path is proven live for a compile-triggered reload (attempt 8); the pair
  together is covered by tests, not by one run.
- `editor play` waits up to 90 s (or the remaining `--timeout`, whichever is smaller) for the heartbeat
  to report Play mode. A heavy scene plus a domain reload can exceed that; the failure line names the
  budget it had, and `--no-wait` is the escape hatch. A project that wants the old behaviour entirely
  can pass `--no-wait`.
- **The socket window from #8 is still open.** Attempt 8 reproduced one request lost with no answer at
  all during a reload; the CLI now classifies it and reports the resulting state, but it does not
  resend it. That needs #28's re-resolve-and-retry-on-EOF.
- `exec` that leaves Play mode reports no `state_change`: Unity's `isPlayingOrWillChangePlaymode`
  stays set until the change lands, so claiming it at answer time would be a guess. The pinned test
  asserts the silence.
- A raw `manage_editor` call (not `editor play|stop`) answers before the change is applied and does
  not wait for the state — the payload says `scheduled: true`. `#28`'s heartbeat wait is the follow-up.
- Go's transport logs `Unsolicited response received on idle HTTP channel` on stderr when a reload
  closes the keep-alive connection mid-session. `verify` and `editor refresh --compile` suppress that
  line already; plain `exec` still shows it. Cosmetic, and unchanged by this fix.
- Nothing in this repo may rely on `EditorApplication.delayCall`: it never runs while the editor is
  backgrounded (attempt 9). Deferred connector work must ride `EditorApplication.update`.

## Review follow-up

The review of `fix/10-reload-response` raised three C# findings; the Go ones are handled separately.

- **Confirmed — every ongoing compile was attributed to the snippet.** `ChangeStartedSince` ended
  with an unconditional `if (compiling) return "a script compilation is in flight";`, so a compile an
  asset import started while the snippet sat in an `await` came back as
  `(this snippet started a script compilation; …)`. Two changes: the helper takes `wasCompiling` and
  only reports a compile that was not already running when the snippet began, and the message states
  the observed change instead of its cause
  (`(since the snippet started: a script compilation is in flight; …)`). The cause is genuinely
  unknowable here — a compile an import starts and one the snippet requests look identical from
  `EditorApplication.isCompiling` — so the answer says what changed and leaves `meta.state_change` as
  the state it is. Proven live against the editor by scheduling `RequestScriptCompilation()` from an
  earlier, already-answered command's update tick and having the next snippet poll until
  `isCompiling`: the response message is the new one and `meta.state_change` is still reported.
  Covered by `PlayModeTransitionTests.ChangeStartedSince_DoesNotBlameTheSnippetForACompileAlreadyRunning`
  (fails against the unconditional check) plus the reworked table test.
- **Accepted, not changed — a reload can still answer a request whose tool has already returned**
  (medium, unproven). `ProcessItem` answers with `item.Ticket.TryAnswer(r)` on the main-thread
  continuation *after* `await CommandRouter.Dispatch(...)` returns, so the ticket is still `Running`
  in the window from Dispatch completing to that continuation running. `beforeAssemblyReload` runs on
  the same main thread, and if it is served inside that window it sees `Stage == Running`, answers
  `reload_during_command`, and the later `TryAnswer(r)` loses to the answered ticket — so the
  "a request whose tool had already returned keeps its own value" claim above holds only once
  `TryAnswer` has run, not the moment the tool returned. The alternative that closes the window is
  `ConfigureAwait(false)` on that await so the ticket is answered without waiting for a main-thread
  turn; it was rejected because it moves `Debug.LogException` and the command-lock release off the
  main thread, where the tool-wrapping code would be touching Unity APIs from a thread-pool thread.
  Not proven reachable in a live editor either (both the reload callback and the continuation are
  main-thread work items, and attempts 4-10 never showed this interleave), so it stays a documented
  race rather than an unverified rework.
- **Confirmed — the queued-on-quit answer still told the caller to retry "once Unity answers again".**
  `BuildReloadError`'s not-started branch carried that sentence even with `quitting: true`, where the
  editor is gone and no answer is coming; `BuildReloadError(..., quitting: true)` said so for the
  started branch only. The not-started quit branch now ends with *"The editor was quitting, so
  nothing was applied."* and keeps the null `data.code` (no editor to wait for). Covered by
  `RequestDeadlineTests.BuildReloadError_AQueuedRequestDuringAQuitIsNotToldToWait` (fails against the
  old sentence).

## Review follow-up (end-to-end run)

- **`Unsolicited response received on idle HTTP channel` — reproduced, and it is not a double write.**
  The note above called it cosmetic and Go-only. It is a connector-side defect, just not the one the
  finding suspected: `HttpServer` writes exactly one response per request (`WriteResponse` is the only
  writer, and the ticket CAS makes a second answer impossible), and every response carries a JSON body.
  The stray response has `Content-Length: 0`, `Connection: close` and `Server: Mono-HTTPAPI/1.0` — a
  shape no code path here produces. It is Mono's `HttpListener` closing the connections it still holds
  when the listener stops for a domain reload: reproduced with a raw socket kept idle after its answer,
  a real content-change compile+reload, and the empty 200 arriving on that idle connection.
  `HandleRequest` now sets `response.KeepAlive = false`, so the client closes instead of pooling a
  connection the reload can write into (a CLI invocation opens a connection per command anyway; a
  local connect measured 0-4 ms). Covered by
  `RequestDeadlineTests.Responses_AskTheClientNotToReuseTheConnection`; the probe that reproduced the
  stray response now sees the connection close with zero extra bytes.
