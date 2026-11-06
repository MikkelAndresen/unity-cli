# #8 `exec` hangs when the editor main thread is not ticking (no server-side deadline)

## Summary

The connector now guarantees that every accepted request gets exactly one answer. A request that the
editor's main thread never picks up is answered by the request thread itself — with the reason the
editor is busy, what is holding it, and an explicit "the command did not run" — and a request that is
still queued when Unity reloads or quits is answered with `domain reload in progress, retry` before
the listener stops. The CLI tells the connector how much time it has left so the connector answers
first, and a request that was already answered is skipped instead of running late. An unserializable
tool payload, which used to leave the connection hanging with no body at all, now comes back as a
`serialization_failed` error.

## Reported behaviour

`exec` hung indefinitely, several times in one session: right after entering Play mode, when it set
`EditorApplication.isPlaying = false`, on a plain scene-state check, and on a development-build
script whose compile error the hang hid (that is why "the build never started", #3). Fresh evidence
from the orchestrator: immediately after `unity-cli verify` finished (which recompiles and reloads),
an `exec` with the released 0.7.0 binary hung for more than 10 minutes, while an `exec` a minute
later answered instantly.

## Reproduction attempts

All runs are against the live editor (Unity 6000.6.5f1, connector `file:` package = this repo), with
the editor backgrounded on the host — which is the state it is in for every run here. The "before"
rows used the pre-fix connector (connector 0.7.0) and a dev CLI built from `HEAD`.

### 1. Two requests, one of them freezing the main thread — reproduced: yes

```bash
# shell A
uc-dev … --timeout 60000 exec 'System.Threading.Thread.Sleep(20000); return 1;'
# shell B, 3 s later
uc-dev … --timeout 60000 exec 'return 2;'
```

B printed `2` after **20.7 s**. There is no bound anywhere on the connector side: B sat in `s_Queue`
behind A. Nothing hung forever only because A's block was finite.

### 2. The same, with a caller that gives up — reproduced: yes, and the command ran anyway

```bash
# A holds the command lock (awaits without blocking the main thread)
uc-dev … exec 'await System.Threading.Tasks.Task.Delay(30000); return "A done";' --await_timeout 35000
# B, 2 s later, sets a marker the caller can look for afterwards
uc-dev … --timeout 15000 exec 'UnityEditor.SessionState.SetBool("unitycli_side_effect_marker", true); return "B ran";'
# after A finished
uc-dev … exec 'return UnityEditor.SessionState.GetBool("unitycli_side_effect_marker", false).ToString();'
```

```
B(queued, client gives up at 15s): rc=1 elapsed=15.0s
  Error: timed out after 15s waiting for Unity to answer exec (the editor may still be running it)
check: 'True (marker set => B ran even though the caller was told nothing)'
```

This is the worst part of the bug: the caller gets an uncertain message, and the command **does run
later**, after the caller stopped watching. A retried call and the abandoned one can then both apply
their side effects.

### 3. Requests racing a script recompile — reproduced: yes

```bash
# 60 hammer calls `exec 'return 1;'` with --timeout 30000, every 0.3 s, while:
unity-cli … editor refresh --compile        # triggered in the background
```

Before (connector 0.7.0):

```
call 10 t=  3.0 rc=0 out='exec sent (connection closed before response)'   <- reported as SUCCESS
call 20 t=  6.0 rc=0 out='exec sent (connection closed before response)'
call 15 t=  4.5 rc=0 out='exec sent (connection closed before response)'
call 12 t=  3.6 rc=1 Error: Unity reloaded or rebound its port during the request … EOF
call 32 t=  9.7 rc=0 elapsed= 24.2s out='1'                                <- sat through the reload
```

Three callers were told `rc=0` with no result at all (`client.Send` treats an empty body as success),
the rest either waited 20-27 s or got a connection error. The queued `TaskCompletionSource`s were
abandoned by `beforeAssemblyReload`; `s_Queue` is a static and the reload wipes it.

### 4. The orchestrator's scenario: `verify` and then an immediate `exec` — reproduced: yes

```bash
unity-cli … verify --assembly UnityCliConnector.Tests.Editor    # released 0.7.0 CLI, in the background
# 80 hammer calls `exec 'return 1;'` --timeout 60000 alongside it
```

Heartbeat: `compiling` at t=3.5, `reloading` at t=9.8, `ready` again at t=26.1. Plain `exec
'return 1;'` calls took **40.1-47.1 s** (the client waited for a fresh heartbeat, then queued behind
the reload, and nothing answered them early). No call failed, but none of them was distinguishable
from a hang until the client's own timeout. This is the shape of the ">10 minutes" report: with a
longer `--timeout` the same requests simply wait longer.

### 5. Backgrounded / throttled editor — not reproduced on its own

The editor was backgrounded for every run above. Pickups stayed at 0.2-3 s, so throttling by itself
did not stall the queue: `HttpServer.ForceEditorUpdate()` posts `QueuePlayerLoopUpdate` +
`RepaintAllViews`, which wakes the pump even without focus. What throttling *does* do is make the
wait invisible, which is why slow pickups now report `meta: {"queue_wait_ms":…}` (see Verification).

### 6. Play-mode entry/exit — not attempted

Entering Play mode in this shared editor would disturb other agents working in it, and the Play-mode
reload path is issue #10. The reload path exercised here (script recompile → `beforeAssemblyReload`)
goes through the same connector code: `StopListener` plus a wiped static queue.

### 7. A wedge this change caused, then fixed — reproduced: yes (and it is #8's symptom, in the editor itself)

The first version of the new EditMode tests included

```csharp
var result = CommandRouter.Dispatch("list", null, ticket).Result;
```

EditMode `test_run` holds `CommandRouter.s_Lock` for the whole test run (its handler awaits the run),
and the release continuation needs the main-thread pump. Calling `Dispatch` with a blocking `.Result`
from inside a test therefore deadlocked the editor's main thread: heartbeat silent for ~16 minutes,
every CLI call reporting "not responding", `unity-restart --reason=wedged-b8` needed. The editor
never recovered on its own — the exact symptom this issue is about, produced by a nested wait on the
non-reentrant command lock. The tests now assert the guard (`CommandRouter.ShouldRun`) without taking
the lock, and `CommandRouter` carries a comment saying the lock is not reentrant and that waiting for
it must stay asynchronous.

## Root cause

Connector side, in the pre-fix tree (`HEAD` before this change):

- `unity-connector/Editor/HttpServer.cs:335-343` — `HandleRequest` builds a
  `TaskCompletionSource<object>`, enqueues it and `await`s it with **no deadline and no tracking**.
  Only `ProcessQueue` (off `EditorApplication.update`) can complete it, so a main thread that is
  frozen, inside a long tool, or gone is indistinguishable from a slow one.
- `unity-connector/Editor/HttpServer.cs:59` — `beforeAssemblyReload += StopListener`: the listener is
  closed and the static `s_Queue` (with its pending TCSs) is wiped. Nothing answers the requests that
  were already accepted, so the caller sees a dropped connection (EOF) or an empty body, or waits.
- `unity-connector/Editor/HttpServer.cs:353` — `JsonConvert.SerializeObject(result)` ran outside any
  `try`, on the request thread, *after* the answer had been decided. A payload the serializer rejects
  (e.g. a reference loop) threw out of `HandleRequest`, whose `Task` nobody observes: the response was
  never written and the connection closed with no body — which the CLI reports as
  `"connection closed before response"` success.
- `unity-connector/Editor/HttpServer.cs:210-219` — `ProcessItem` is `async void` and called
  `Tcs.SetResult`, so a second completion (deadline, reload) would throw instead of being ignored.
- `unity-connector/Editor/CommandRouter.cs:s_Lock` — one semaphore serializes everything, so one
  stuck command hides behind "the editor is busy" with no way for a waiter to learn what is holding
  it, and an abandoned request still ran its tool later.

## Fix

Connector:

- **`unity-connector/Editor/Core/RequestTicket.cs` (new)** — one request with a stage
  (`Queued → Running → Answered`) moved only by CAS. The request thread (deadline, reload) and the
  editor main thread (queue pickup, command lock) race for it; the winner decides whether the tool
  handler runs at all. That makes "answered without running" and "ran without being answered"
  mutually exclusive.
- **`unity-connector/Editor/Core/RequestTracker.cs` (new)** — every accepted-but-unanswered request is
  tracked. `QueueWaitMsFor` is the deadline policy: the client's own declared deadline minus a margin
  (a tenth, never under 1 s), clamped to 500 ms…10 min, default **60 s** when the client declares
  none, `queue_deadline_ms` as a per-call override. `TryAnswerWaiting` answers a request that has not
  started, `AnswerAllPending` answers everything when the domain goes away, `AwaitForAnswers` gives
  the HTTP threads a bounded window (1.5 s) to write before the listener is closed. The messages carry
  the heartbeat state, the running command and its age, the queue depth, and the modal dialog title
  when there is one.
- **`unity-connector/Editor/Core/ErrorCodes.cs` (new)** — `main_thread_busy`, `domain_reload`,
  `serialization_failed` in `data.code`; the CLI maps them to a next step.
- **`unity-connector/Editor/Core/ResponseJson.cs` (new)** — `SerializeOrError` never throws: a payload
  that cannot be serialized becomes a `serialization_failed` error naming the command, the
  serializer's message, the value's type and a 300-character preview; if even that fails, a
  hand-written JSON literal is sent.
- **`unity-connector/Editor/HttpServer.cs`** — the queue path takes a ticket, awaits
  `Task.WhenAny(ticket.Tcs.Task, Task.Delay(queueWaitMs))`, and answers with the busy error when the
  deadline wins. `beforeAssemblyReload` now answers every pending request with `domain reload in
  progress, retry` (and a distinct message if the command had already started, where the truth is
  "it may have partially run"), waits up to 1.5 s for the answers to reach the wire, then stops the
  listener; `EditorApplication.quitting` does the same with "the editor is quitting". A `volatile`
  `s_RefusingWork` flag, set by `StopListener`/the reload path and cleared when a listener comes up,
  makes new main-thread requests get `BuildRefusedError` immediately instead of being queued into a
  domain that is on its way out. `ProcessItem` uses `TryAnswer`, and response writing is wrapped so a
  client that hung up cannot take the request thread down. Slow pickups (>1 s) report
  `queue_wait_ms`/`queue_depth` in `meta`. The off-main-thread path (`modal`, `build_status`, …) and
  the CLI-side modal fail-fast are untouched, so a dialog is still answered without the main thread.
- **`unity-connector/Editor/CommandRouter.cs`** — `Dispatch` now takes the request ticket, and it is
  taken *after* acquiring the command lock; a request that was answered while it waited is skipped
  (`ShouldRun` false → no tool, no side effects). The old two-argument overload is gone, so no dispatch
  path can bypass the guard. The comment records that the lock is not reentrant.
- **`unity-connector/Editor/Heartbeat.cs`** — `LastState` exposes the last heartbeat state snapshot,
  readable from a request thread, so the busy error can say what the editor thinks it is doing.

CLI:

- **`internal/client/client.go`** — the request body carries `deadline_ms`, the caller's remaining
  budget for that request (shrinking for each later request in one invocation), omitted when the
  caller declared no deadline. This is what lets the connector answer before the client gives up.
- **`internal/client/codes.go`, `cmd/connector_error.go`** — read `data.code`, and print a `next:`
  line for the three known codes: busy and reload say the command did not run and to `unity-cli wait`
  then repeat, `serialization_failed` says the command ran and only the value was lost. An unknown
  code gets no guess.

## Verification

Checks (dev CLI built from this tree; the connector is the live `file:` package):

```bash
gofmt -l .                      # empty
go vet ./...                    # clean
go test ./...                   # only cmd/update_test.go TestFindAsset (known missing linux/arm64 asset)
golangci-lint                   # not installed in this container, not run
unity-cli verify --assembly UnityCliConnector.Tests.Editor
  compile: ok
  tests: 263/264 passed, 1 skipped          # the skipped one is the opt-in real-dialog modal test
  exit=0
```

New EditMode tests (`unity-connector/Tests/Editor/RequestDeadlineTests.cs`) cover the deadline policy
(defaults, margin, clamp, override), the stage machine (answered ⇒ never begins; begun ⇒ never
answered as "skipped"; answered once), the busy message and its `data` keys, the reload answers for
the queued and the started case, `BuildRefusedError`, `AwaitForAnswers`, `ResponseJson` with a cyclic
payload plus the hand-written fallback, and the router's skip guard. New Go tests
(`internal/client/deadline_request_test.go`, `cmd/connector_error_test.go`) cover `deadline_ms` being
sent, shrinking, and omitted when unbounded, and the code→hint mapping including unknown codes.

Against the live editor after the fix:

```
# A holds the command lock for 30 s; B has a 15 s budget and a side effect
B: rc=1 elapsed=13.7s
  Error: exec was queued but the main thread did not pick it up within 13.5s (waited 13.7s):
         editor busy (blocked by the running command exec (15.7s))[heartbeat: ready, queue depth: 2].
         The command did not run; nothing changed.
  Details: {"code":"main_thread_busy","command":"exec","waited_ms":13665,"queue_deadline_ms":13467,
            "queue_depth":2,"running_command":"exec","running_ms":15671,"heartbeat_state":"ready",
            "modal":null,"started":false}
    next:      the command did not run and nothing changed; run 'unity-cli wait' until the editor
               is idle, then repeat it
A: rc=0 out='A done'
check: 'marker=False'          # B did NOT run late — before the fix this was True

# released 0.7.0 CLI sends no deadline_ms, so the connector's 60 s default applies
B(unity-cli 0.7.0, --timeout 120000): rc=1 elapsed=60.1s
  Error: exec was queued but the main thread did not pick it up within 60s (waited 60.1s): …
# before the fix: the same call waited out the client's 120 s.

# main thread frozen in Thread.Sleep(20000), caller budget 5 s
B: rc=1 elapsed=4.2s  Error: … did not pick it up within 3.9s … The command did not run; nothing changed.

# per-call override
exec --params '{"code":"return 1;","queue_deadline_ms":3000}'  → answered at 3.8s, "within 3s"

# reload race: 60 hammer calls alongside `editor refresh --compile`
0 × 'connection closed before response'          (was 3)
16 × 'exec never ran: … (domain reload in progress, retry, waited 25ms…5.6s)'  code=domain_reload
 1 × 'exec did not run: Unity is not taking new main-thread commands …'        code=domain_reload
 7 × 'exec was queued but the main thread did not pick it up within …'         code=main_thread_busy
13 × answered normally (waits of 0.2-29 s, none beyond its deadline)
 5 × EOF, classified by the CLI as "Unity reloaded or rebound its port (not a crash)"
 9 × client-side timeout — see Remaining risk

Re-running the same race with a 25-call hammer: 20 × code=domain_reload, 0 × fake success.

# slow pickup made visible (editor backgrounded, A slept 6 s inside the tool)
exec 'return "B picked up late";' --meta → meta: {"queue_wait_ms":10582,"queue_depth":1}
```

The connector also logs `Answered N in-flight request(s): …` on the reload path, but this editor's
Console window hides info-level entries (the `#34` window-filter problem), so the answers above are the
observable proof: neither `console` nor `console_filter` could read that line back.

## Remaining risk

- **A request swallowed by a dying listener is still invisible.** During a reload there is a window
  where the socket is accepted by the OS but the connector never parses the request; those 9 hammer
  calls above got nothing until their own client timeout. The connector cannot answer what it never
  read; closing it means re-resolving the instance and retrying on EOF/refused, and requiring a
  non-reloading heartbeat before sending — that is #28's fix, not this one.
- **A command that has started is bounded only by the caller's client timeout.** The server deadline
  deliberately covers the queue wait, not the handler's runtime: answering a running command early
  would report a result for something still changing the editor. `wait`/`build_status`/`modal` stay
  off-main-thread, so a busy editor is still observable.
- `--timeout 0` no longer means "the connector will sit on this forever": with no declared deadline
  the connector answers a queued request after 60 s (`queue_deadline_ms` raises it). Execution itself
  is still unbounded client-side.
- The reload/quit handler blocks the main thread for at most 1.5 s while in-flight answers are
  written; a slow or wedged client costs that much reload time, after which the listener closes and
  the caller sees EOF as before.
- Play-mode entry/exit (#10) was not exercised here; the code path it needs (`beforeAssemblyReload`
  answering pending requests, then refusing new ones) is the one exercised above.

## Review follow-up

The code review of `fix/8-server-deadline` raised four findings against this change; two had already
been fixed by later commits on the stack.

- **Confirmed — quit could accept a request after answering the snapshot.** `Stop()` now sets
  `s_RefusingWork` before `AnswerPending`, so the quit path mirrors `beforeAssemblyReload`.
  `HandleRequest` also re-reads the flag after `Track`: a reload that flips it between the first read
  and tracking would otherwise miss the ticket in its "answer everything" snapshot and leave the
  caller with an empty body, which the CLI reports as success.
- **Confirmed — the quit answer's missing `data.code` was contradicted by the README.** The code is
  deliberate (see #10): a quit has no editor to wait for, so `BuildReloadError(..., quitting: true)`
  carries no code. The README now says so instead of claiming every quit answer is `domain_reload`.
- **Confirmed — a very short client budget can expire before the 0.5 s queue floor.** The floor stays
  (it keeps a tiny budget from racing the answer), and the README now says a sub-floor budget can time
  out client-side first.
- **Already fixed — slow-pickup `meta` overwrote `exec`'s compile meta**, and the CLI hint treated an
  already-started request as one that never ran. The current tree merges a CLR `meta` object with
  `JObject.FromObject` and answers started requests `reload_during_command`; both have regression
  tests.
- Also cancelled the queue-deadline timer once the real answer wins. Low-severity review notes about
  comment narration and the missing space before `[heartbeat:` were handled only for the message
  itself, not by rewriting unrelated comments.
