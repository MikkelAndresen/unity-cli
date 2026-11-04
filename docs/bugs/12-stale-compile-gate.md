# #12 Stale compile errors block exec; skip_compile_check is undiscoverable

## Summary

The compilation gate treated the in-memory console as a second, undated source of compile errors. A
console entry from an earlier compile, or a sticky entry left by a failed build/play-mode attempt,
could keep every gated tool — including `exec` — answering "Compilation has N error(s)" after the
live compiler was clean. The error also hid the escape hatch details: it did not name the errors, say
how old the compile was, or explain where to pass `skip_compile_check`, and `exec`'s schema/help
never mentioned the parameter.

The gate now takes console entries only when they belong to the current compile: a timestamp from
`LogEntries.GetEntryTimestampInternal` must not predate the last compile start (or, after a domain
reload, its finish). Sticky build/runtime entries are excluded, console entries for deleted source
files are dropped, and the blocked message names the first three diagnostics with the compile age
and both spellings of the escape hatch. `exec` now advertises `--skip_compile_check`.

## Reported behaviour

- After fixing a compiler error and recompiling cleanly, `exec` still failed with
  `Compilation has 1 error(s)… pass skip_compile_check=true`.
- A failed player build left console errors that the gate classified as compilation errors.
- `skip_compile_check` could only be learned from the failure message of the call it had just
  blocked; `exec --help` and `tool_describe exec` did not mention it.

## Reproduction attempts

All live calls were read-only. The shared editor was running the main checkout's older connector
(its `exec` schema did not even include `no_default_usings`), so the synthetic console injection
below exercises the old gate that the issue describes, not this worktree's uncompiled C#.

1. **Stale console entry with a clean live pipeline.** Injected a console diagnostic for
   `Assets/Ghost12DoesNotExist.cs` through the old gate's test seam, seeded the pipeline cache with
   a clean assembly, and called `CompilationGate.CheckOrNull`:

   ```bash
   unity-cli exec 'var c=typeof(UnityCliConnector.ConsoleLogEntries); var f=c.GetField("TestOverride", System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic); System.Func<bool,System.Collections.Generic.List<UnityCliConnector.ConsoleLogEntries.CompileDiag>> overrideFn = w => new System.Collections.Generic.List<UnityCliConnector.ConsoleLogEntries.CompileDiag>{ new UnityCliConnector.ConsoleLogEntries.CompileDiag{ File="Assets/Ghost12DoesNotExist.cs", Line=1, Col=1, Severity="error", Code="CS1002", Message="; expected", } }; f.SetValue(null, overrideFn); try { var g=typeof(UnityCliConnector.CompilationGate); var seed=g.GetMethod("SeedForTests", System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic); seed.Invoke(null,new object[]{"Library/ScriptAssemblies/Clean.dll", new UnityEditor.Compilation.CompilerMessage[0]}); var check=g.GetMethod("CheckOrNull", System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public); var resp=check.Invoke(null,new object[]{new Newtonsoft.Json.Linq.JObject()}); if(resp==null) return "NOT BLOCKED"; var msg=(string)resp.GetType().GetField("message").GetValue(resp); var data=resp.GetType().GetField("data").GetValue(resp); return "BLOCKED: "+msg+"\n"+(data==null?"":Newtonsoft.Json.JsonConvert.SerializeObject(data)); } finally { f.SetValue(null,null); typeof(UnityCliConnector.CompilationGate).GetMethod("ResetForTests", System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,null); }'
   ```

   Output:

   ```text
   BLOCKED: Compilation has 1 error(s). Fix them or pass skip_compile_check=true.
   {"compile_errors":[{"file":"Assets/Ghost12DoesNotExist.cs","line":1,"col":1,
   "severity":"error","message":"CS1002: ; expected","assembly":null,"source":"console"}],
   "total":1,"console_fallback_available":true,"console_fallback_error":null}
   ```

   Reproduced: yes, including two stale signals — the file does not exist and the pipeline is clean.
   The old message named no errors, no compile age, and no CLI flag.

2. **Compiler-shaped `Debug.LogError`.** Attempted the issue's exact route:

   ```bash
   unity-cli exec 'UnityEngine.Debug.LogError("Assets/Ghost12.cs(1,1): error CS1002: ; expected"); try { var g=typeof(UnityCliConnector.CompilationGate); var check=g.GetMethod("CheckOrNull", System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public); var resp=check.Invoke(null,new object[]{new Newtonsoft.Json.Linq.JObject()}); return resp==null ? "NOT BLOCKED" : "BLOCKED: "+(string)resp.GetType().GetField("message").GetValue(resp); } finally { UnityEngine.Debug.ClearDeveloperConsole(); }'
   ```

   The command timed out because the shared editor stopped answering (`unity-cli status`:
   `not responding`) while the other worker was using it. Reading the live connector's
   `TryConvertEntry` logic shows this route would not block: `Debug.LogError` carries
   `ScriptingError` (1<<8), which the conversion deliberately excludes from `CompileBlockingMask`.
   The real stale console source is the `ScriptCompileError`/`StickyError` entries, reproduced in
   attempts 1 and 3. Reproduced with this exact route: no (editor unavailable; mode bit argues no).

3. **Sticky build/runtime entry.** Attempted to call the live `TryConvertEntry` with mode `1<<13`
   and the play-mode banner:

   ```bash
   unity-cli exec 'var c=typeof(UnityCliConnector.ConsoleLogEntries); var m=c.GetMethod("TryConvertEntry", System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic); var args=new object[]{1<<13, "All compiler errors have to be fixed before you can enter play mode!", null, 0, false, null}; var ok=(bool)m.Invoke(null,args); if(!ok) return "sticky rejected"; var d=args[5]; return "sticky accepted as compile error: "+Newtonsoft.Json.JsonConvert.SerializeObject(d);'
   ```

   That call also timed out with the editor. The source and the existing
   EditMode test confirm the old conversion accepts `StickyError` as an `error`; the new gate marks
   it `Sticky` and refuses to block on it. Reproduced at source/test level: yes; live call: not
   completed.

4. **Discoverability.** Ran the released CLI against the shared editor:

   ```bash
   unity-cli exec --help
   unity-cli tool_describe --params '{"name":"exec"}'
   ```

   Neither output contained `skip_compile_check`; the schema listed only `code`, `usings`, `csc`,
   `dotnet`, and `await_timeout`. Reproduced: yes.

## Root cause

- `unity-connector/Editor/Core/CompilationGate.cs` (`CollectErrors`, pre-fix lines 152-166) merged
  every console entry that `ConsoleLogEntries.TryGetCompileDiagnostics` returned. The console is not
  cleared by a successful recompile, so its errors were undated and immortal.
- `unity-connector/Editor/Core/ConsoleLogEntries.cs:35` included `StickyError` (`1<<13`) in
  `CompileBlockingMask`. Sticky entries are how failed builds/play-mode attempts persist in the
  console, so "something failed once" looked like "the project does not compile".
- There was no file-existence check, so a diagnostic for a renamed/deleted file also survived.
- `unity-connector/Editor/Tools/ExecuteCsharp.cs` had no `SkipCompileCheck` parameter and
  `cmd/root.go`'s exec help never mentioned the gate's escape hatch.

`ByAssembly` itself was not the stale source: it is cleared on `compilationStarted`, as the triage
noted.

## Fix

C#:

- `unity-connector/Editor/Tools/ReadConsole.cs` binds the optional
  `LogEntries.GetEntryTimestampInternal(row, out string)` and adds a
  `TryForEachEntry(Action<int,string,string,int,string>)` overload. The old four-argument overload
  delegates to it, so `console` and `burst_status` are unchanged.
- `unity-connector/Editor/Core/ConsoleLogEntries.cs` records each entry's local time-of-day and a
  `Sticky` flag on `CompileDiag` (`ParseTimestampSeconds` accepts LogEntries' `[HH:MM:SS]` prefix).
- `unity-connector/Editor/Core/CompilationGate.cs` now keeps a compile-start clock alongside the
  existing finish clock. A console diagnostic is current only when it is not sticky, its file still
  exists, and its timestamp is no older than the compile start. If this domain joined after a
  successful compile (reload observed), the finish clock is the threshold; if it joined during a
  failed compile (`compilationFinished` without a start), the console is still trusted rather than
  discarding that compile's errors. Entries with no timestamp are not trusted once a compile clock
  exists.
- `GetLiveDiagnostics` (used by `compile_diagnostics`) applies the same filter, so it no longer
  reports console-only stale errors either.
- `CompilationGate.CollectErrors` now returns a typed diagnostic payload; the block
  message names the first three as `file(line,col) CODE: message [assembly]`, says how old the last
  compile is via `CompileProvenance.DescribeLastCompile()`, and prints
  `--skip_compile_check / skip_compile_check=true`.
- `unity-connector/Editor/Core/CompileProvenance.cs` adds `DescribeLastCompile()` (the existing
  provenance sentence without the source scan).
- `unity-connector/Editor/Tools/ExecuteCsharp.cs` adds the `SkipCompileCheck` parameter, which the
  schema converter exposes as `skip_compile_check`.

CLI/docs:

- `cmd/root.go`: exec overview and detailed help list `--skip_compile_check`.
- `README.md`: exec section documents the gate, its current-compile-only rule, the named errors, and
  the escape hatch; the command table mentions it.

Tests:

- `unity-connector/Tests/Editor/CompilationGateTests.cs` adds synthetic cases for a stale console
  entry after a clean compile, a fresh console error from the current compile, a fresh sticky entry,
  a deleted-file entry, the first-three/escape-hatch message, stale filtering in
  `GetLiveDiagnostics`, and timestamp parsing. The existing sticky conversion test now also asserts
  the `Sticky` marker.
- `unity-connector/Tests/Editor/CompileDiagnosticsTests.cs` sets the file-existence test seam so its
  synthetic console-only Burst diagnostic stays deterministic.

## Verification

Pre-fix live reproduction is recorded above. Post-fix Go checks in the worktree:

```text
gofmt -w .
go vet ./...                                             # clean
go test ./...                                            # only the known cmd/update_test.go findAsset failure
go build -o /tmp/claude-1000/uc-ds .
/tmp/claude-1000/uc-ds exec --help                       # lists --skip_compile_check and its gate explanation
```

The C# compile and EditMode suite were **not** run here: the task forbids `unity-cli
verify`/refresh/compile in this worker, and the shared editor went `not responding` mid-session
while another worker was using it. `docs/bugs/34-console-window-filter.md` records the same
constraint for the previous worker. The orchestrator must compile the connector and run
`unity-cli verify --assembly UnityCliConnector.Tests.Editor` before merging; the new reflection
binding is optional and degrades to no timestamp rather than failing initialization.

## Remaining risk

- The time-of-day comparison uses Unity's local `[HH:MM:SS]` console stamp and wraps at midnight.
  It has one second of slack, so a stale entry emitted in the same second as the compile start (or
  within a clock-skewed second) can still be treated as current. That is deliberately conservative
  in the blocking direction only for the current compile window.
- If a Unity version lacks `GetEntryTimestampInternal`, console fallback entries cannot prove
  freshness and are ignored once a compile clock exists. The live `CompilationPipeline` cache remains
  the primary source, so this only affects errors emitted before the connector subscribed.
- File-existence checking only resolves `Assets/`, `Packages/`, `Library/`, absolute, and `./`
  paths; other path shapes are trusted. Generated/virtual paths containing `unknown` are also
  trusted.
- The blocked message embeds `DescribeLastCompile()`, which is the last finished compile even when
  the errors came from the console of a later failed compile; `compiled_at_ms` is in `data` for
  callers that need the exact clock.
