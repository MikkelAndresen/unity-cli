# #34 console only returns what the Console window's filter shows

## Summary

`ReadConsole` read the Console window's filtered view instead of the editor's full in-memory log.
With the window's Log and Warning toggles off (and/or a search string or Collapse enabled),
`console --type log` returned nothing even though the entries existed. The tool now temporarily
widens the window filter for the read — all three type toggles on, search cleared, collapse off —
and restores the exact previous flags and search text in a `finally`. The compile-diagnostics
fallback shares the same reflection path, so hidden error toggles can no longer hide compile
errors from the gate either. When a filter was overridden the reply includes
`console_window_filter`.

## Reported behaviour

`unity-cli console --params '{"type":"log","lines":300}'` returned none of the `Debug.Log` lines
an `exec` had just written, although they were in `Logs/Editor.log`. In another project every
caller saw errors only because the Console window had Log and Warning hidden.

## Reproduction attempts

Unity 6000.6.5f1, live editor, read-only `exec` calls reflecting over
`UnityEditor.LogEntries`. The first probe found the real API: `consoleFlags` (property),
`GetFilteringText`/`SetFilteringText`, `GetStatusMask`, `GetCountsByType`, plus the internal
`UnityEditor.ConsoleWindow+ConsoleFlags` enum. The saved flags were `3586 = ClearOnBuild +
ShowTimestamp + LogLevelError + ClearOnPlay`, i.e. Log and Warning toggles off.

```bash
unity-cli exec '... reflect LogEntries; log Debug.Log/LogWarning/LogError markers; read GetCount and GetCountsByType ...'
```

Baseline with the user's flags: `count=16`, only the error marker was returned by
`GetCount`/`GetEntryInternal`; `GetCountsByType` still reported `E16,W5,L14`, proving the entries
existed. Reproduced: yes.

```bash
unity-cli exec '... save consoleFlags; for bit 0..16 XOR one bit, read GetCount after each ...'
```

`xor bit 7` (LogLevelLog) took the count from 15 to 29 (errors + logs), `xor bit 8`
(LogLevelWarning) to 20 (errors + warnings), and clearing `bit 9` (LogLevelError) left 0 with
Log/Warning still hidden. This pinned the type mask exactly. Reproduced: yes.

```bash
unity-cli exec '... log marker log1/log2/warn/err; read rows with saved flags; then all type flags + collapse off; then collapse on; then search text ...'
```

Saved error-only flags returned just the error marker. All type flags with collapse off returned
all four markers (`count=38`); with collapse on `count=32`; with the matching search text set the
count narrowed to the matching rows. This confirmed the window filter controls both `GetCount`
and row indexing, and that collapse changes the count. Reproduced: yes.

## Root cause

`unity-connector/Editor/Tools/ReadConsole.cs:104-105` (`GetEntries`, pre-fix) called
`LogEntries.StartGettingEntries()` and `GetCount()` directly, and `ReadConsole.cs:132` re-read the
same filtered rows with `GetEntryInternal`. Unity's `LogEntries` applies the Console window's
`consoleFlags` type mask, filtering text and Collapse mode before exposing the row list, so the
tool's `--type` could only narrow what the window already showed. `ConsoleLogEntries.cs`'s compile
fallback went through the same `ReadConsole.TryForEachEntry` path, so the user's error toggle could
hide compile diagnostics too.

## Fix

Changed `unity-connector/Editor/Tools/ReadConsole.cs`:

- Binds the optional `LogEntries.consoleFlags` property and `GetFilteringText`/`SetFilteringText`
  alongside the existing reflection binding. If they are unavailable the tool still reads as
  before instead of failing.
- `EnterConsoleFilterOverride` saves the flags and search text, ORs in
  `LogLevelLog|LogLevelWarning|LogLevelError` (`1<<7|1<<8|1<<9`) and clears `Collapse` (`1<<0`),
  and clears the search text. `ConsoleFilterScope.Dispose` restores the saved values exactly.
- Both `GetEntries` and `TryForEachEntry` use the scope, so `console` and the
  `CompilationGate`/`ConsoleLogEntries` fallback (also used by `burst_status`) see every entry.
- The console reply gains a `console_window_filter` object only when something was overridden,
  with `note`, `log_hidden`, `warning_hidden`, `error_hidden`, `search` and `collapsed`; the
  summary also appends the note.

Added EditMode tests in `unity-connector/Tests/Editor/ReadConsoleCapsTests.cs` that save/restore
the real window filter in `SetUp`/`TearDown`, hide Log/Warning/Error, set a search string and
enable Collapse, and assert the tool still returns the entries and restores the flags and search
text exactly.

Updated the `console` section of `README.md` to describe the override and the new reply field.

## Verification

- Live pre-fix reproduction above (three attempts) on Unity 6000.6.5f1.
- Post-fix live algorithm check via `exec` (the worktree's C# cannot be loaded into the shared
  editor without a compile): with flags `3586` and a hiding search string the old read saw
  `countBefore=0`; applying the fix's flags (`3970`) and clearing search saw `countUnfiltered=41`
  with all three new markers; afterward `restoredFlags=3586`, `restoredFilter=""`,
  `flagsRestored=True`, `filterRestored=True`.
- Go checks: `gofmt -w .`, `go vet ./...`, `go test ./...` (the only failure is the pre-existing
  `cmd/update_test.go` `findAsset` linux/arm64 case).
- The C# compile and EditMode suite were not run in this worker (the task forbids
  `unity-cli verify`/refresh here); the orchestrator compiles and runs them. The new tests and
  production code follow the surrounding reflection style and the live API shape observed above.

## Remaining risk

- The flag values are the internal `UnityEditor.ConsoleWindow+ConsoleFlags` bits observed on
  Unity 6000.6.5f1. Another Unity version could move them; the override is optional but would not
  detect a wrong bit assignment, so a future canary test would help.
- If setting the flags or search succeeds only partially, the scope restores what it changed; a
  failure in the restore itself is swallowed like the existing `EndGettingEntries` cleanup.
- The read briefly changes the Console window's visible state on the main thread and restores it
  in the same call; no repaint is expected between those points, but this is observable to any
  code re-entering the console during the read.

## Review follow-up

- When `LogEntries.consoleFlags` is missing, cannot be read, or the widened flags cannot be
  written, the console reply now carries `console_window_filter: {"available": false, ...}` with a
  note instead of staying silent. That still cannot name the hidden filter on such a Unity version,
  but an empty result is no longer presented as if the window filter had been checked.
- The filter override also runs from the compile gate's console fallback, on every gated tool
  (`exec`, menu item, reserialize, `editor`, build). That is intentional: a hidden error toggle
  must not hide compile errors from the gate. Ungated tools (`api`, `screenshot`, `editor stop`)
  do not run the gate and are unaffected. A failed restore remains best-effort exactly like the
  existing `EndGettingEntries` cleanup; it is not a new failure mode.
