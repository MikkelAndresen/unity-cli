# #9 No reliable timeout: SIGALRM ignored, one call could burn two budgets

## Summary

`unity-cli` now uses one deadline for discovery, waiting for a fresh heartbeat, and the request
itself. A client deadline is reported as a timeout that says the editor may still be running the
command, not as "cannot connect". SIGINT, SIGTERM, SIGALRM and SIGHUP cancel the in-flight request
and exit with `128+signo`. `test`, `verify` and `editor refresh --compile` have a bounded 30-minute
default instead of an accidental `0` (no deadline), while `wait --timeout` and build polling keep
their own budgets.

## Reported behaviour

An agent wrapped calls in `perl -e 'alarm N; exec @ARGV'`; the alarm never killed the CLI. A call
could also run for about twice the documented `--timeout`, and when the client gave up the error
read `cannot connect to Unity … context deadline exceeded`, which agents interpreted as a dead
editor and answered with restart loops.

## Reproduction attempts

### 1(a) SIGALRM is ignored by the old CLI

```bash
timeout 15 perl -e 'alarm 2; exec @ARGV' /tmp/claude-1000/uc-ds \
  --project "$AGENT_SANDBOX_PROJECT" exec 'await System.Threading.Tasks.Task.Delay(8000); return 1;'
```

Old binary: exit 0 after 9 s, stdout `1`. The Perl alarm fired at 2 s and did nothing.
Reproduced: yes. Go installs a handler for SIGALRM without `signal.Notify`, so the runtime consumed
it. `timeout -s ALRM 2` had the same result; an unrelated domain reload in the shared editor ended
one attempt early, which is why the Perl case above is the clean record.

### 1(b) A client deadline is reported as a connection failure

Against a fake heartbeat and a deliberately slow HTTP server (no shared editor involved; the
throwaway script wrote a `~/.unity-cli/instances` heartbeat and answered `/command` after 5 s):

```bash
python3 /tmp/repro_single.py /tmp/claude-1000/uc-old 2000
```

```
binary=/tmp/claude-1000/uc-old budget=2000ms rc=1 elapsed=2.01s
stderr: Error: cannot connect to Unity at port 38527: Post "http://127.0.0.1:38527/command":
        context deadline exceeded (Client.Timeout exceeded while awaiting headers)
```

Reproduced: yes. The same run with the fixed binary exits 1 after 2.01 s with
`timed out after 2s waiting for Unity to answer exec (the editor may still be running it)`.

Live equivalent (real editor, same shape):

```bash
/tmp/claude-1000/uc-ds --project "$AGENT_SANDBOX_PROJECT" --timeout 2000 \
  exec 'await System.Threading.Tasks.Task.Delay(8000); return 1;'
```

Fixed: exit 1 after 2.0 s, `timed out after 2s … (the editor may still be running it)`.
Old: `cannot connect … Client.Timeout exceeded while awaiting headers`.

### 1(c) Waiting and sending each spent the full budget

The throwaway `/tmp/repro_double.py` wrote a fake heartbeat that was one second stale, refreshed it
after 2 s, and ran a fake HTTP server that answered after 5 s. With `--timeout 4000`:

```bash
python3 /tmp/repro_double.py /tmp/claude-1000/uc-old 4000
python3 /tmp/repro_double.py /tmp/claude-1000/uc-ds  4000
```

```
OLD elapsed=6.02s  Error: cannot connect ... context deadline exceeded (Client.Timeout exceeded ...)
NEW elapsed=4.01s  Error: timed out after 2s waiting for Unity to answer exec (the editor may still be running it)
```

Reproduced: yes. The old binary spent 2 s waiting for the heartbeat plus its own 4 s request
timeout; the fixed binary stops at the 4 s invocation deadline.

### 1(d) `test` and `verify` passed `timeout 0`

Reading the old code confirmed it: `cmd/root.go` passed `0` for the test send and verify's test
send, and `http.Client{Timeout: 0}` is unbounded. Reproduced by inspection, not by running the
editor's test suite (the shared editor must not be driven into tests by this worker).

## Root cause

- `cmd/root.go` called `waitForAlive(resolve, flagTimeout)` and then `client.Send(..., timeout)`
  with the same number, so `--timeout 4000` could legitimately produce about 8 s of wall time
  before the send even started.
- `cmd/root.go` passed `0` to `client.Send` on the `test` path and verify's test send; Go treats
  that as no deadline.
- `internal/client/client.go` used `http.Client{Timeout: 0}` for unbounded requests and returned
  `cannot connect to Unity at port %d: %v` for every transport error, including a client deadline.
- No `signal.Notify` existed anywhere, so SIGALRM (and SIGTERM/SIGHUP) were swallowed by the Go
  runtime.

## Fix

Go side only:

- `cmd/root.go` computes one `effectiveTimeout` before discovery and creates one `context.WithTimeout`
  for the invocation. `waitForAliveContext` and every `client.SendContext` call draw from
  `remainingTimeoutMs`, so waiting and sending cannot each take the full number.
- `test`, `verify` and `editor refresh --compile` use `longCommandTimeoutMs` (30 minutes) when
  `--timeout` was not given explicitly; an explicit `--timeout 0` is still an intentional no-deadline
  escape hatch. `wait` and `build` are excluded from the invocation deadline because their own
  budgets (wait seconds, build contact timeout) are the meaningful bounds.
- `exec`'s `--await_timeout` still widens the one budget (the existing `execRequestTimeout`), so the
  client does not give up before the connector's `timed_out` marker.
- `internal/client/client.go` gained `SendContext` and `RequestTimeoutError`; a context deadline is
  reported as `timed out after <budget> waiting for Unity to answer <command> (the editor may still
  be running it)`. Connection failures still go through #30's `explainDroppedConnection`, so crash
  and reload classification is unchanged. A canceled context is reported as cancellation, never as
  a timeout or crash.
- `cmd/signals.go`, `cmd/signals_unix.go`, `cmd/signals_windows.go` install SIGINT/SIGTERM/SIGALRM/
  SIGHUP handling on POSIX. The handler cancels the request context, records `128+signo`, and exits
  with that code; `main.go` and `cmd.exitNow` make an interrupt outrank verify/wait's own exit code.
  The platform split keeps the Windows build valid.
- `testCmd`, `verifyCmd` and `waitCmd` take the invocation context so PlayMode result polling and
  the wait loop stop on cancellation; `wait` still uses its own `--timeout` in seconds.

## Verification

```bash
cd /tmp/claude-1000/uc-wt
gofmt -l .                      # empty
go vet ./...                    # clean
go test ./...                   # cmd/update_test.go TestFindAsset is the known missing-asset
                                # failure; everything else passes
```

New tests cover the error text and crash separation (`internal/client/timeout_test.go`), the
single-budget deadline (`cmd/timeout_test.go`), the long-command defaults, and a subprocess test
that sends SIGALRM and SIGTERM to a helper process and checks `128+signo` (`cmd/signals_unix_test.go`).

Live after the fix:

```
perl -e 'alarm 2; exec @ARGV' uc-ds … exec 'await Task.Delay(8000); return 1;'
  -> exit 142 after 2.02 s, "unity-cli: interrupted by alarm clock"

timeout -s ALRM 2 uc-ds … exec '…Delay(8000)…'
  -> exit 124 after 2.02 s (GNU timeout's own code), same interrupt line

uc-ds --timeout 2000 … exec '…Delay(8000)…'
  -> exit 1 after 2.0 s, "timed out after 2s waiting for Unity to answer exec (the editor may
     still be running it)"
```

The fake server/heartbeat comparison above shows 6.02 s (old, two budgets) versus 4.01 s (fixed,
one 4 s budget). `test`/`verify` are no longer `0`; they default to 1800 s and are covered by
`TestEffectiveInvocationTimeout`.

## Remaining risk

- A client deadline still does not cancel the work inside the editor: the connector may finish the
  queued `exec`/`test` later. The message now says so, but a subsequent call can still see a busy
  editor; that is the connector-side half of the original issue and is not changed here.
- The watchdog's fallback `os.Exit` fires after 250 ms. A command blocked in a long non-context
  sleep (for example a build poll sleep) is stopped by that fallback rather than by its own loop.
- `editor refresh --compile` got the 30-minute default because it waits out a compile; a user who
  explicitly sets a short `--timeout` still gets that shorter overall budget.
- `test --mode PlayMode` polling now honours the invocation context, but its existing 10-minute
  result-file limit remains as a second, independent bound.

## Review follow-up

A read-only review of the timeout branch found two remaining deadline/wording holes, both fixed:

- `waitForReadyTo` (verify) and `waitForCompileTo` (`editor refresh --compile`) polled a private
  5-minute `compileWaitTimeout` with `time.Sleep`, so an explicit short `--timeout` could still run
  for five minutes before the next request noticed. Both now wait on the invocation context as well
  and report `timed out waiting for compilation (--timeout)` when it expires.
- The "timed out after X" sentence was formatted by hand in `waitForAliveTimeout` and
  `invocationDeadlineError`, so a 60 s budget could read `59.997s`. `internal/client`'s budget
  formatter is now exported as `FormatBudget` and used by all three producers of that sentence.

Low-severity review notes left as-is: `--timeout 0` now means "no deadline" for the heartbeat wait
(and PlayMode polling still has its 10-minute cap), `exec` reads piped stdin before discovery, the
250 ms signal fallback can truncate a slow write, `client.Send`/`waitForAlive` remain as test-only
shims, and one timeout test keeps tight wall-clock margins.
