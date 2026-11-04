# #17 follow-up: lockfile age is not a crash, and a loaded connector that went silent is not startup

## Summary

`Temp/UnityLockfile` is written once when Unity opens a project, so its age is the session age. The
CLI treated an old lockfile as "Unity probably crashed" even when a heartbeat newer than the lockfile
proved the connector had loaded this session, and it called every such silence
`editor_starting_or_blocked`. This change classifies a heartbeat written after the lockfile as a
separate state, `editor_unresponsive`, and only hints at a crash from lockfile age when no heartbeat
from that session exists.

## Reported behaviour

An editor open for 8 hours had its main thread wedged for 16 minutes. Every command printed
`editor_starting_or_blocked ... (Temp/UnityLockfile, age 8h18m10s; heartbeat last seen 16m2s ago ...)`
with `older than 2h0m0s: Unity probably crashed instead of quitting`, although the connector had
loaded and its last state was `ready`.

## Reproduction attempts

The live shared editor was wedged/restarting and the worker instructions said not to depend on it, so
all reproductions used a fake HOME and fake project.

1. Built the pre-fix checkout (`git archive HEAD` into a temporary directory, `go build` there) and ran
   the fixed and pre-fix binaries against the same fixture: an 8-hour-old `Temp/UnityLockfile`, plus a
   silent 16-minute-old `state: "ready"` heartbeat whose PID belonged to a live non-Unity process.

   ```bash
   HOME="$H" USERPROFILE="$H" /tmp/claude-1000/uc-old \
     --ignore-version-mismatch --project "$P" --timeout 2000 exec 'return 1;'
   ```

   Pre-fix output (reproduced: yes):

   ```
   Error: editor_starting_or_blocked: Unity appears open for ... (Temp/UnityLockfile, age 8h0m24s;
   heartbeat last seen 16m4s ago, pid 81052) but the connector has not loaded ...
     Lockfile: ... (written ...)
               older than 2h0m0s: Unity probably crashed instead of quitting
   ```

2. The same fixture and command with the fixed `/tmp/claude-1000/uc-ds` binary reported
   `editor_unresponsive`, with no crash hint (reproduced fixed: yes).

3. Also ran both binaries with `status` on the same fixture: pre-fix classified it as
   `editor_starting_or_blocked`; fixed reports `not responding` plus the `editor_unresponsive` evidence
   line. The fixed `status` exit code remained 0.

4. Startup (old lockfile, no heartbeat file) and crash-artifact (same session heartbeat plus a fresh
   `mono_crash.*.json`) fixtures were run with the fixed binary; see Verification.

## Root cause

`cmd/evidence.go` decided the state only from the presence of a lockfile, not from whether the
heartbeat postdated it. `evidenceSentence` therefore always chose `editor_starting_or_blocked` when a
lockfile existed, and `evidenceReport` printed the "probably crashed" lockfile hint for any lockfile
older than 2 hours, regardless of a heartbeat from the current session. `internal/client/evidence.go`
had the lockfile mtime and the stale heartbeat side by side but no comparison between them.

## Fix

- Added `(*client.EditorEvidence).HeartbeatThisSession()` in `internal/client/evidence.go:60`: any
  heartbeat at or after the lockfile's mtime is from this session, including a fresh or cleanly
  stopped one; only when there is no lockfile to compare does the heartbeat itself count.
  `SilentHeartbeatThisSession()` narrows that to the silent heartbeat the report describes.
- `cmd/evidence.go:65` now emits `editor_unresponsive` for that silent case, naming the project,
  silence age, pid, and last state, with instructions to try `unity-cli dialogs` and then restart.
- `cmd/evidence.go:98` only prints the lockfile "probably crashed" hint when the lockfile is old **and**
  no heartbeat from this session exists. A previous session's heartbeat older than the lockfile still
  permits the hint; any heartbeat after the lockfile suppresses it.
- Crash artifacts still win: `annotateNoEditor` and `stalledEditorReport` check the crash verdict first,
  so #30's `crashed` state is unchanged.
- Exit codes are unchanged: `crashed` still exits non-zero; `status` on a merely unresponsive editor
  still exits 0; other commands that fail to reach the editor still fail.
- Updated `cmd/evidence_test.go`, `internal/client/evidence_test.go`, `README.md`, and the `status` /
  `dialogs` help text.

## Verification

```bash
cd /tmp/claude-1000/uc-wt
gofmt -l .        # no output
go vet ./...      # clean
go clean -testcache
go test ./...
# cmd fails only the known TestFindAsset (no linux/arm64 release asset)
# internal/client and internal/editor pass
go build -o /tmp/claude-1000/uc-ds .
```

Three fake scenarios from the `/tmp/claude-1000/uc-ds` build:

```
=== 1. startup: lockfile, no heartbeat this session ===
Error: editor_starting_or_blocked: Unity appears open for .../startup-project (Temp/UnityLockfile, age 6h0m0s) but the connector has not loaded — it is still importing/compiling, or a startup dialog (license, safe mode, "project already open", package resolution) is waiting.
  Lockfile: .../startup-project/Temp/UnityLockfile (written 2026-11-04T06:48:17Z)
            older than 2h0m0s: Unity probably crashed instead of quitting
  Lookup:   no Unity instances running
  Next:     run 'unity-cli dialogs' on the macOS host to read the dialog's title and buttons, or ask the user to look at the editor.
exit=1

=== 2. unresponsive: session heartbeat silent 16m, lockfile age 8h ===
Unity (port 8090): not responding (last heartbeat 16m0s ago)
editor_unresponsive: Unity's connector for .../unresponsive-project stopped answering 16m0s ago, pid 80920 (last state "ready") — the main thread is likely blocked (long exec/build/import), a modal dialog is open, or the editor hung. Try 'unity-cli dialogs' on the macOS host; if it stays silent, restart the editor ('unity-restart' in agent-sandbox).
  CLI:     dev
  Connector: 0.7.0
  Compatibility: development CLI; compatibility not checked
exit=0

=== 3. crash artifact: session heartbeat silent 16m + mono_crash ===
crashed: the Unity editor died for .../crash-project (heartbeat last seen 16m0s ago, pid 80920) with no "stopped" marker.
  Heartbeat: .../instances/session.json (state "ready", pid 80920, last beat 16m0s ago)
  Process:   pid 80920 exists here but runs "sleep", not Unity
  Crash log: .../crash-project/mono_crash.fake.0.json (mono_crash, written 0s ago)
             signal 11 (SIGSEGV); System.NullReferenceException: Object reference not set to an instance of an object
  Next:      Unity has to be relaunched by the user — no CLI call brings it back. Editor.log rotates when it does, so read the crash log above first.
Error: crashed (port 8090, pid 80920): 1 crash artifact(s), newest mono_crash.fake.0.json; heartbeat silent 16m0s with no "stopped" marker
exit=1
```

## Remaining risk

- The comparison uses the lockfile's filesystem mtime against the heartbeat's millisecond wall-clock
  timestamp. Both come from the same host, but a very coarse lockfile timestamp or a clock adjustment
  could theoretically make the first heartbeat look older than the lockfile; the consequence is only a
  startup classification instead of `editor_unresponsive`.
- `status` still prints its existing first line `not responding`; the new state appears in the evidence
  line below it. Changing that first line would be a larger presentation change outside this follow-up.
- The live shared editor was not used for the final verification, per the worker instructions, so the
  integration check is the fake-HOME binary run above rather than a wedged real editor.

## Review follow-up

A code review of the #17 stack found one remaining misclassification in this fix. `status` calls an
editor unresponsive once its heartbeat is 3 s old (`client.BeatSilenceAge`), but the evidence path
only had a *silent* heartbeat after 10 s (`heartbeatStaleAge`). A 3–10 s gap — an ordinary domain
reload or compile pause — therefore had a fresh `SessionHeartbeat` and no `StaleHeartbeat`, so the
report fell through to the lockfile branch and said `editor_starting_or_blocked ... the connector has
not loaded` about a connector that had demonstrably loaded this session.

`(*client.EditorEvidence).UnresponsiveHeartbeat()` now returns the silent heartbeat, or the session
heartbeat once it is at least `BeatSilenceAge` old, and `cmd/evidence.go` uses that single predicate
for the state sentence, the heartbeat line, and the restart advice. A clean `stopped` heartbeat and a
heartbeat written before the lockfile still read as not-unresponsive, and a heartbeat inside the 3 s
margin changes nothing.

## Review follow-up (round 2)

- The state sentence now lets the lockfile age decide its wording: a lockfile older than two hours
  with no heartbeat from this session says the editor probably crashed instead of claiming it is
  still importing, and the `Next:` line tells the operator to relaunch rather than to look for a
  startup dialog.
- `editor_unresponsive` no longer gives dialog advice for an editor that left no lockfile: the
  report says the project holds no `Temp/UnityLockfile`, that the editor has probably exited, and
  to relaunch it. README now states exactly when each state applies (the unresponsive one does not
  require a lockfile).
- `dialogs` reads the project from the newest heartbeat instead of `client.DiscoverInstance`, so a
  read-only listing no longer prunes dead-pid instance files or picks an arbitrary project.
- A click that matches several buttons on one window is reported as such (button count and titles)
  instead of claiming "matches 2 windows" and listing the same window twice.
- The click AppleScript already enumerates each window and then its sheets, uses the listing's
  parent-title fallback, and contains no `continue repeat`. A Go test now asserts that structure
  and records the list-to-click pid/title/button handoff through an injectable osascript runner;
  the script itself is still unexecuted because this machine has no osascript (no macOS host run).
