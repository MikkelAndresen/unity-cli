# #29 — Refresh/compile can leave assemblies older than their sources

## Summary

A requested compile was treated as proof that `Library/ScriptAssemblies/<asm>.dll` contained the
current sources. The first version of the check (this branch, commit `1a4e845`) compared each
assembly's DLL mtime with its newest source, but it never fired in the live project: Unity hands
`CompilationPipeline` source paths back **project-relative and virtual** (`Assets/...`,
`Packages/<pkg>/...`) while the check matched them against **absolute** source roots, so every
assembly was filtered out of the scan and the count stayed 0.

The live round fixed the path handling, and found a second defect in the rule itself: Unity does not
rewrite a DLL whose contents it considers current, so a raw "DLL older than source" rule reports an
mtime-only change (`touch`, re-checkout) as stale forever — the refusal would loop. The rule now
judges staleness against the compile clock: a source an already-finished compile had time to read is
reported as `not stale:` instead of refusing tests over.

## Reported behaviour

Observed live by the orchestrator, and reproduced below: after `touch
Assets/ClaudeTools/Editor/BuildWithProfile.cs` (no refresh), `compile_status` named the file under
`stale_line` (the #26 source scan worked) but reported `stale_assembly_count: 0` /
`stale_assembly_line: null`, even though that assembly's DLL in `Library/ScriptAssemblies` was older
than the source. Same with `touch unity-connector/Tests/Editor/HeartbeatPayloadTests.cs` — a `file:`
package assembly (`LocalPackages/unity-cli/unity-connector`, seen by Unity as
`Packages/com.youngwoocho02.unity-cli-connector/...`) was not counted either.

## Reproduction attempts

All live against the running editor (Unity 6000.6.5f1, connector installed as a `file:` package).

1. **The stale state is real, by mtime.** `stat -c "%y %n" Library/ScriptAssemblies/... Assets/ClaudeTools/Editor/BuildWithProfile.cs`
   → `Assembly-CSharp-Editor.dll 14:23:13`, `BuildWithProfile.cs 14:32:45`. The DLL really was older
   than its source, so the check had something to find. **Reproduced (the state), not the report.**

2. **The report stayed empty.** `/tmp/claude-1000/uc-dev … compile_status` with that state in place:
   `"stale_count": 1, "stale_files": ["Assets/ClaudeTools/Editor/BuildWithProfile.cs"]`, but
   `"stale_assembly_count": 0, "stale_assemblies": [], "stale_assembly_line": null`.
   **Reproduced: yes.**

3. **The scan found no assemblies at all.** Calling the connector's own code from the live editor —
   `unity-cli exec 'var r=UnityCliConnector.AssemblyStaleness.ScanProject(2000); …'` →
   `root=/Users/mikkelandresen/Work/unity-cli-project scanned=0 stale=0 complete=True ms=0`. Not a
   budget problem (`complete=True`, 0 ms): every assembly was filtered out before any stat ran.
   **Reproduced: yes, and it isolates the cause to the filter.**

4. **Path shapes proved the mismatch.** `unity-cli exec` dump of `CompilationPipeline.GetAssemblies`:
   `src=Packages/com.youngwoocho02.unity-cli-connector/Editor/Heartbeat.cs`,
   `src=Assets/ClaudeTools/Editor/BuildWithProfile.cs`,
   `out=Library/ScriptAssemblies/Assembly-CSharp-Editor.dll` — all project-relative — while
   `CompileProvenance.SourceRoots()` yields `/Users/…/Assets` and
   `/Users/…/LocalPackages/unity-cli/unity-connector`. `StartsWith(root + "/")` can therefore never
   match. `Packages/com.youngwoocho02.unity-cli-connector` is also virtual: the project's `Packages/`
   directory holds only `manifest.json` and `packages-lock.json`. **Reproduced: yes (root cause).**

5. **Does Unity rebuild a touched file? No.** `uc-dev … editor refresh --compile` after the touch →
   `compiled 09:37:04 (took 12 s) · 1 source file changed since previous compile`,
   `rebuilt_assemblies: []`, and `Assembly-CSharp-Editor.dll` kept mtime `14:23:13`.
   **Reproduced: no rebuild happens**, which is what makes the old raw-mtime rule loop: the state is
   never cleared, so `verify` would refuse, retry a clean rebuild, and refuse again.

6. **`CleanBuildCache` does not rewrite those DLLs either.** `uc-dev … editor refresh --compile clean`
   → 65 s compile, `rebuilt_assemblies: []`, `Library/Bee/artifacts` recreated at 14:52:26, yet
   `Library/ScriptAssemblies/*.dll` mtimes unchanged (14:41:33/14:41:35): Bee restores its
   `CachedNodeOutput` with the timestamps it recorded. **Reproduced: no.** So the first draft's advice
   ("force a rebuild with … --compile clean") cannot clear the state and was dropped from the message.

7. **After the fix, both cases are flagged.** Touch both files, then `compile_status` →
   `stale_assembly_count: 2`, naming `UnityCliConnector.Tests.Editor`
   (`newest_source: LocalPackages/unity-cli/unity-connector/Tests/Editor/HeartbeatPayloadTests.cs`)
   and `Assembly-CSharp-Editor` (`newest_source: Assets/ClaudeTools/Editor/BuildWithProfile.cs`).
   `test --mode EditMode --assembly UnityCliConnector.Tests.Editor` then refuses with
   `blocked:"stale-assembly"`, exit 1 — before the fix it would have run the old DLL.
   **Reproduced: yes (detection now works).**

## Root cause

- `unity-connector/Editor/Core/AssemblyStaleness.cs:76-81` (pre-fix) put `assembly.sourceFiles` and
  the `.asmdef` path into the input list unmodified, and `:203` (`IsProjectAssembly`) compared them
  with `StartsWith(root + "/")` against the **absolute** roots from `CompileProvenance.SourceRoots()`.
  `CompilationPipeline` returns project-relative paths, and for a `file:`/local/registry/git package
  it returns the **virtual** `Packages/<name>/…` form, so nothing ever matched and
  `ScanProject` evaluated an empty list. `outputPath` (`Library/ScriptAssemblies/<asm>.dll`) was
  relative too; `File.Exists` only resolved it because the editor's CWD happened to be the project
  root, which is not guaranteed for an editor launched outside a shell.
- The rule itself was also wrong in the other direction: `Evaluate` compared only DLL mtime with
  source mtime. A content-identical `touch` leaves both in that relation forever (reproduction 5), so
  `verify`/`test` would refuse a project that is actually current, and the `CleanBuildCache` retry
  could not fix it (reproduction 6).

## Fix

- `AssemblyStaleness.ResolvePath(path, projectRoot, virtualResolver)`: rooted paths pass through,
  `Packages/<pkg>/…` is resolved through
  `UnityEditor.PackageManager.PackageInfo.FindForAssetPath` to the package's real `resolvedPath`
  (`file:`/embedded packages land inside the project, registry/git packages land under
  `Library/PackageCache` and stay outside the roots), anything else is joined to the project root.
  `ScanProject` resolves sources, the `.asmdef`, and `outputPath` before filtering or stat-ing, so
  `Assets` assemblies and `file:` package assemblies are both project assemblies and
  `Library/PackageCache` ones are not.
- `Evaluate(…, coveredSinceMs)` takes the last compile's start clock
  (`CompileProvenance.LastCompileStartedAtMs()`, falling back to its finish when this domain never saw
  a compile start). If the newest source predates that moment, the compile could read it and left the
  DLL alone deliberately: the assembly is counted in `stale_assembly_covered_count` and named in
  `stale_assembly_covered_line` (`not stale: …`, printed by the CLI next to the provenance line)
  instead of being reported stale. A missing DLL is still always stale, and a source written after the
  compile started is still stale.
- `unity-cli editor refresh --compile clean` now really sends `compile:"clean"`. `--compile` is a
  boolean flag in the CLI, so the `--compile clean` that the connector's advice told people to run was
  silently downgraded to a plain request; an unknown value is now rejected on the Go side instead of
  reaching the connector, where an unrecognised compile mode means "don't compile".
- `attachCompileReport` (Go) overwrites the refresh reply's request-time `stale_assembly_*` fields with
  the post-compile numbers, so a report no longer says `stale_after_compile: false` next to a list of
  "stale" assemblies.
- Tests: `AssemblyStalenessTests` gained the pipeline path shapes (relative `Assets/…`, virtual
  `Packages/…` resolved by a fake resolver, `Library/PackageCache` excluded), the covered/stale split,
  and two live tests that run `ScanProject` against this project's real `CompilationPipeline` — one
  asserting this package's own test assembly is scanned, one asserting a built-in package's is not.
  Go tests cover the covered line printing once (not duplicated by verify's refusal path), the
  request-time restatement, `--compile clean`, and rejecting an unknown compile mode.

Round 1 (commit `1a4e845`, written without a compiler or a live editor) is what the fix above builds on;
unchanged from it: `CompileProvenance` carries `stale_assembly_*` into `compile_status` and
`compile_diagnostics`; `RefreshUnity` reports them at request time, fixes `force` to mean "mode force
or force:true" and accepts `compile:"clean"` under `RequestScriptCompilationOptions.CleanBuildCache`
(gated on the `clean_compile_supported` the refresh reply advertises, so older connectors never get an
unsupported mode); `editor refresh --compile` asks once for a clean rebuild when assemblies are still
stale after the compile and reports `stale_after_compile`; `RunTests` refuses with
`blocked:"stale-assembly"` and `unity-cli test --allow-stale` is its escape hatch; `verify` refuses
before Burst/tests and `--allow-stale` forwards `allowStale` to `run_tests`.

## Verification

Go (in this repo):

```
$ gofmt -l . ; go vet ./... && go build -o /tmp/claude-1000/uc-dev . && go test ./cmd ./internal/...
VET clean, BUILD-OK
ok  github.com/youngwoocho02/unity-cli/internal/client
ok  github.com/youngwoocho02/unity-cli/internal/editor
--- FAIL: TestFindAsset   update_test.go:15: findAsset: should find asset for current platform
```

Only the known pre-existing `cmd/update_test.go` failure (no linux/arm64 release asset). New Go tests
run explicitly: `TestEditorCmd_RefreshCompileRestatesStalenessAfterTheCompile`,
`TestPrintProvenance_PrintsTheCoveredDllLineOnce`,
`TestEditorCmd_RefreshCompileCleanDiscardsTheBuildCache`,
`TestEditorCmd_RefreshCompileRejectsAnUnknownMode`, plus the two pre-existing stale-retry tests and the
two `TestVerify_Stale*` tests: all PASS. `golangci-lint` is not installed in this container, so the
lint steps of AGENTS.md were not run.

C#, against the live editor — compile plus the connector's EditMode suite:

```
$ /tmp/claude-1000/uc-dev --project "$AGENT_SANDBOX_PROJECT" --ignore-version-mismatch --timeout 900000 \
      verify --assembly UnityCliConnector.Tests.Editor
exit=0
compile: ok
compiled 09:58:53 (5 s ago, took 11 s) · 0 source files changed since previous compile
not stale: UnityCliConnector.Tests.Editor.dll (built 09:55:34) is older than LocalPackages/unity-cli/unity-connector/Tests/Editor/HeartbeatPayloadTests.cs (09:57:51) (+1 more), but the compile that started 09:58:41 read that source and kept the DLL; a moved mtime alone does not mean the DLL is out of date
tests: 331/332 passed, 1 skipped
```

(The one skip is the pre-existing `[Test, Explicit("Opens a real modal dialog")]` in
`ModalDialogTests`. An earlier run of the same command showed one
`ApiLookupTests.MemberOnlyQuery_FindsTheTypesThatDeclareIt` failure: `ApiIndex` is on a bounded scan
budget and had not indexed `UnityEditor` yet; it passes on the next run, so it is a load-dependent
flake, not a change in behaviour.)

Live staleness loop, end to end, after both source files were touched at 09:57:51:

```
$ uc-dev … compile_status
  "stale_assembly_count": 2, "stale_assembly_covered_count": 0,
  "stale_assembly_line": "stale assembly: UnityCliConnector.Tests.Editor.dll (built 09:55:34) is older than LocalPackages/unity-cli/unity-connector/Tests/Editor/HeartbeatPayloadTests.cs (09:57:51) (+1 more) — run unity-cli editor refresh --compile"
  (entries: UnityCliConnector.Tests.Editor, Assembly-CSharp-Editor)

$ uc-dev … test --mode EditMode --assembly UnityCliConnector.Tests.Editor      # no compile first
exit=1
Error: stale assembly: UnityCliConnector.Tests.Editor.dll (built 09:55:34) is older than … (09:57:51) — run unity-cli editor refresh --compile Tests were not run; pass --allow-stale to run them anyway.
Details: {"blocked":"stale-assembly", … "stale_assembly_count":1, …}

$ uc-dev … verify --no-tests                                                  # compiles, then re-checks
exit=0
compile: ok
not stale: UnityCliConnector.Tests.Editor.dll (built 09:55:34) is older than … (09:57:51) (+1 more), but the compile that started 09:58:09 read that source and kept the DLL; a moved mtime alone does not mean the DLL is out of date

$ uc-dev … editor refresh --compile            # (same state earlier in the session)
exit=0, stale_after_compile: false, stale_assembly_count: 0, stale_assembly_covered_count: 2
no CleanBuildCache retry was requested (the compile covered the sources instead of leaving them out)
```

`git -C /Users/mikkelandresen/Work/unity-cli-project status --short` → `?? Docs/` only: the touched
files kept their contents.

## Remaining risk

- The covered rule trusts a finished compile to have read every source written before its start. If
  Unity's cache wrongly skips an assembly whose source really did change *before* the compile started,
  this reports `not stale:` instead of refusing — that is the original #29 hypothesis, which is still
  unobserved, and content hashing (which would settle it) is far more expensive on the main thread
  than the stat budget this scan runs in.
- A source written while a compile is in flight sits between the compile's start and finish; the check
  calls it stale, and the next compile clears it.
- `CompileProvenance.LastCompileStartedAtMs()` is per editor session (SessionState). Before the first
  compile of a session there is no clock and the scan falls back to raw mtimes, which can report a
  long-untouched assembly as stale until the first compile of that session.
- `UnityEditor.PackageManager.PackageInfo.FindForAssetPath` is called once per `Packages/…` source path
  in the scan (no memoisation). Measured live: `ScanProject(5000)` took 9-11 ms over 7 project
  assemblies (`Assembly-CSharp-Editor`, `Main`, the five `UnityCliConnector.*` assemblies; the
  `com.mikkelandresen.*` git packages and every registry package were excluded, as intended), and
  `compile_status` reports `stale_assembly_scan_complete: true` inside the 200 ms budget. A project with
  tens of thousands of sources would report the scan as incomplete rather than stall the main thread,
  and `verify` only refuses on assemblies it actually found.
- Round 1 (written without a compiler or a live editor) also claimed: `RefreshUnity` treated a compile
  request as proof of a rebuild, `RunTests` started without checking DLL age, and `verify` never asked.
  Those code readings stand and the code they describe is what the live round refined; the mtime
  comparison they shipped is the part that never worked as intended.

## Review follow-up

The review found that the compile-clock "covered" rule still defined away #29: an edit made before a
compile that did not rewrite the DLL had a source older than the compile start, so it was reported
`not stale:` and tests could run the old DLL. That rule is now content-based:

- `CompilationGate`'s `assemblyCompilationFinished` hook (only when that assembly had no errors) asks
  `AssemblyStaleness.NoteAssemblyCompiled` to record a SHA-256 per source file, keyed by assembly name
  and DLL path, in `Library/unity-cli/assembly-sources.json`. The file survives domain reloads and
  editor restarts; SessionState alone would lose it on restart.
- A source newer than the DLL is hashed only when its mtime is newer too. Different from the record
  means stale even when a compile ran since (#29); identical means a touch, reported as `not stale:`
  because the content matches. With no record, the old compile-clock rule still applies but the line
  is marked `unverified, no content record`.
- The scan budget now covers source path collection (and dedup is a hash set), not just the stat loop.
  A deadline hit while scanning one assembly's sources no longer evaluates that partial assembly, and
  `run_tests` refuses an incomplete scan with `blocked:"stale-assembly-scan-incomplete"` instead of
  silently running (the `--allow-stale` escape hatch remains).
- The covered line names the compile that *finished* when `LastCompileStartedAtMs` fell back to the
  finish clock.
- `refresh_unity` advertises `clean_compile_supported` only where `CleanBuildCache` exists (Unity
  2021.1+), and rejects an explicit `compile:"clean"` on an older editor instead of silently doing a
  plain request. The Go-side gating of `editor refresh --compile clean` is handled with finding 4.

The covered unit tests were updated: the no-record case now asserts the `unverified` wording, and new
tests cover a touch with matching content, a content change before a compile that did not rebuild the
DLL (the previously masked #29 sequence), a source missing from the record, and the finish-clock
wording. `RunTests_AnIncompleteScanRefusesInsteadOfRunningTheOldDll` covers the new refusal.

Live, against the running editor: touching `Heartbeat.cs` then `editor refresh --compile` reported the
DLL as covered because "its content matches what the compile recorded for it"; a real edit to
`Tests/Editor/HeartbeatPayloadTests.cs` rebuilt `UnityCliConnector.Tests.Editor` and left no stale
assembly; editing `Heartbeat.cs` and calling `compile_status` before any compile reported
`stale_assembly_count: 1` while the content record still held the old hash. All edited files were
restored byte-for-byte.

## Review follow-up (Go half)

- `verify --assembly A,B` now applies that filter to the provenance's stale-assembly list, the same
  rule `test --assembly` uses: a stale assembly outside the requested set is reported but no longer
  refuses the tests, and a stale assembly inside it still does. A truncated list keeps the full
  count because it cannot prove the requested assemblies are clean.
- An explicit `editor refresh --compile clean` is refused when the connector's reply does not
  advertise `clean_compile_supported`; an older connector would ignore the mode and answer a plain
  refresh, so the CLI no longer reports "nothing to compile" (exit 0) for a rebuild that never ran.
