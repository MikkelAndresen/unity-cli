# unity-cli

[English](README.md) | [Korean](README.ko.md)

> Control Unity Editor from the command line. Built for AI agents, works with anything.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**No server to run. No config to write. No process to manage. Just type a command.**

## Why this exists

I wanted to control Unity from the terminal. The existing MCP-based integrations required Python runtimes, WebSocket relays, JSON-RPC protocol layers, config files, server processes that need to be started and stopped, tool registration ceremonies, and tens of thousands of lines of over-engineered code. All just to send a simple command to Unity.

On top of that, every AI agent that wanted to use it needed its own MCP config and integration setup. The CLI doesn't care — any agent that can run a shell command can use it immediately.

That felt wrong. If I can `curl` a URL, why do I need all that?

So I built the opposite: a single binary that talks directly to Unity via HTTP. No server to run — the Unity package listens automatically. No config to write — it discovers Unity instances on its own. No tool registration — just call by name. No caching, no protocol layers, no ceremony.

The entire CLI is ~800 lines of Go (plus ~300 lines of help text). The Unity-side connector is ~2,300 lines of C#. It's just a thin layer that lets you control Unity from the shell — nothing more. You install the binary, add the Unity package, and it works.

## Install

### Linux / macOS

```bash
curl -fsSL https://raw.githubusercontent.com/MikkelAndresen/unity-cli/main/install.sh | sh
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/MikkelAndresen/unity-cli/main/install.ps1 | iex
```

### Other options

```bash
# Go install (any platform with Go)
go install github.com/youngwoocho02/unity-cli@latest

# Manual download (pick your platform)
# Linux amd64 / Linux arm64 / macOS amd64 / macOS arm64 / Windows amd64
curl -fsSL https://github.com/MikkelAndresen/unity-cli/releases/latest/download/unity-cli-linux-amd64 -o unity-cli
chmod +x unity-cli && sudo mv unity-cli /usr/local/bin/
```

Supported platforms: Linux (amd64, arm64), macOS (Intel, Apple Silicon), Windows (amd64).

### Update

```bash
# Update to the latest version
unity-cli update

# Check for updates without installing
unity-cli update --check
```

## Unity Setup

Add the Unity Connector package via **Package Manager → Add package from git URL**:

```
https://github.com/MikkelAndresen/unity-cli.git?path=unity-connector
```

Or add directly to `Packages/manifest.json`:
```json
"com.youngwoocho02.unity-cli-connector": "https://github.com/MikkelAndresen/unity-cli.git?path=unity-connector"
```

To pin a specific version, append a tag to the URL (e.g. `#v0.2.21`).

Once added, the Connector starts automatically when Unity opens. No configuration needed.

### Recommended: Disable Editor Throttling

By default, Unity throttles editor updates when the window is unfocused. This can delay CLI commands because Unity API work is dispatched on the Editor main thread.

To fix this, go to **Edit → Preferences → General → Interaction Mode** and set it to **No Throttling**.

The connector also requests a PlayerLoop update whenever a CLI request arrives. No Throttling is still recommended for the most responsive background behavior.

## Quick Start

```bash
# Check Unity connection
unity-cli status

# Enter play mode and wait
unity-cli editor play --wait

# Run C# code inside Unity
unity-cli exec "return Application.dataPath;"

# Read console logs
unity-cli console --type error,warning,log
```

## How It Works

```
Terminal                              Unity Editor
────────                              ────────────
$ unity-cli editor play --wait
    │
    ├─ scans ~/.unity-cli/instances/*.json
    │  → finds Unity on port 8090
    │
    ├─ POST http://127.0.0.1:8090/command
    │  { "command": "manage_editor",
    │    "params": { "action": "play",
    │                "wait_for_completion": true }}
    │                                      │
    │                                  HttpServer receives
    │                                      │
    │                                  CommandRouter dispatches
    │                                      │
    │                                  ManageEditor.HandleCommand()
    │                                  → EditorApplication.isPlaying = true
    │                                  → waits for PlayModeStateChange
    │                                      │
    ├─ receives JSON response  ←───────────┘
    │  { "success": true,
    │    "message": "Entered play mode (confirmed)." }
    │
    └─ prints: Entered play mode (confirmed).
```

The Unity Connector:
1. Opens an HTTP server on `localhost:8090` when the Editor starts
2. Writes a per-project instance file to `~/.unity-cli/instances/` so the CLI knows where to connect
3. Updates the instance file every 0.5s with the current state (heartbeat)
4. Discovers all `[UnityCliTool]` classes via reflection on each request
5. Routes incoming commands to the matching handler on the main thread
6. Survives domain reloads (script recompilation)

Before compiling or reloading, the Connector records the state (`compiling`, `reloading`) to the instance file. As soon as the HTTP listener is up it writes `starting` and keeps that heartbeat fresh until the editor's first update tick, so a cold import is not reported as "no connector". When the main thread freezes, the timestamp stops updating. The CLI detects this and waits for a fresh timestamp before sending commands. A player build holds the main thread for minutes without compiling or reloading anything; the Connector keeps beating as `building` from the build's own writer thread (see [Build a Player](#build-a-player-long-running)), so a building editor is not mistaken for a wedged one.

Every command the Connector accepts gets exactly one answer. It tracks each request, and if the
editor's main thread has not started it within the request's queue deadline, the Connector itself
answers with why the editor is busy — the command holding it, the heartbeat state, the queue depth —
and states that the command did not run. A request still queued when Unity reloads or quits is answered
"never ran" (with `data.code` `domain_reload` for a reload, so the caller can retry) before the
listener closes, instead of being dropped. See
[Queued Commands and Server-Side Deadlines](#queued-commands-and-server-side-deadlines).

A parameter the target tool does not declare is refused before the tool runs, with
`data.code: unknown_parameter` and the parameters it does accept — so a misspelled flag fails loudly
instead of being ignored. The keys the CLI and the connector inject themselves (`args`,
`queue_deadline_ms`, `deadline_ms`, `skip_compile_check`) are always accepted. Tools without a declared
parameter schema keep accepting anything.

## Built-in Commands

| Command | Description |
|---------|-------------|
| `editor` | Play/stop/pause/refresh/quit/restart the Unity Editor |
| `input` | Inject new-Input-System keyboard/mouse/gamepad/touch state in Play mode and pump the player loop |
| `console` | Read, filter, and clear console logs (newest 50 entries, capped stack traces) |
| `exec` | Run arbitrary C# code inside Unity (`--skip_compile_check` bypasses the compile gate) |
| `api` | What a loaded type or member actually is: kind, assembly, and C# signatures of every overload |
| `test` | Run EditMode/PlayMode tests |
| `verify` | Compile, list errors in your code, run tests: one call, brief output |
| `wait` | Block until the editor is idle, then say what the last compile covers |
| `compile_status` | When the last compile finished, what it rebuilt, which sources are newer than it |
| `build` | Start a player build as a job, watch progress, report the BuildReport |
| `menu` | Execute any Unity menu item by path |
| `reserialize` | Re-serialize assets through Unity's serializer |
| `clear_burst_cache` | Delete `Library/BurstCache` so Burst recompiles from scratch |
| `burst_status` | Is Burst done compiling, which `[BurstCompile]` targets compiled, which failed (BCxxxx) |
| `screenshot` | Capture the running Game view (OnGUI included), a camera, or the Scene view as PNG |
| `profiler` | Read profiler hierarchy, control recording |
| `modal` | Inspect and answer a modal dialog blocking the editor (macOS) |
| `dialogs` | List or click Unity's windows from outside the editor — startup dialogs, no connector (macOS host) |
| `list` | Show all available tools with parameter schemas |
| `status` | Show Unity Editor connection state |
| `update` | Self-update the CLI binary |

### Editor Control

```bash
# Enter play mode
unity-cli editor play

# Enter play mode and have the Connector itself wait for the state change (skipped when that
# change reloads the domain — the CLI confirms the state from the heartbeat either way)
unity-cli editor play --wait

# Stop play mode
unity-cli editor stop

# Request the change without reading the resulting state back (--wait and --no-wait are exclusive)
unity-cli editor stop --no-wait

# Toggle pause (only works during play mode)
unity-cli editor pause

# Refresh assets (blocked in play mode unless --force is set)
unity-cli editor refresh

# Refresh and recompile scripts (also blocked in play mode unless --force is set)
unity-cli editor refresh --compile

# Recompile after discarding Unity's compiled-assembly cache (Bee) — the hammer for a build cache you
# believe is lying; note it can still leave Library/ScriptAssemblies mtimes untouched
unity-cli editor refresh --compile clean

# Force refresh while in play mode
unity-cli editor refresh --force
```

`editor play` and `editor stop` answer with a report (`action`, `wanted_state`, `editor_state`,
`editor_label`, `confirmed`, `answer_lost`, `waited_ms`) and exit non-zero if the editor never reached
the requested state. Entering or leaving Play mode reloads the domain in most projects, and a command
that starts a domain reload cannot report its own result across it, so the Connector defers the state
change one update tick and the CLI confirms it from the heartbeat (see
[issue #10 write-up](docs/bugs/10-reload-loses-response.md)).

`editor refresh --compile` waits for the compilation and answers with one report instead of a bare
`refresh_triggered`: `compile_requested`, `compile_succeeded`, `error_count`, `errors[]`
(file/line/col/severity/code/message, plus `hint` for the error codes the connector can answer —
see [Verify](#verify)), `timed_out`
and `elapsed_ms`, plus the [compile provenance](#compile-provenance-and-wait) (`provenance`,
`provenance_line`, `stale_line` when a source file was written after the compile, and
`stale_assembly_line` when a project DLL is older than its sources). If an assembly is still stale
after the compile, one `CleanBuildCache` rebuild is requested and `stale_after_compile` reports
whether it fixed the DLL; the top-level `stale_assembly_*` fields always describe the state the
compile left you in, not the state the request started from. `timed_out: true` means the wait gave up after 5 minutes — not that the compile
failed. `nothing_to_compile: true` means no compilation was seen to run after the request, in which
case `compile_succeeded` describes the editor's current compile state rather than a compile this
command caused.

When the project can be proved current — a finished compile to compare against, no compile errors, no
pending import, no source written since that compile and no assembly whose DLL is older than its
sources — the connector skips the request. `RequestScriptCompilation` forces a full script compile even
when Bee would rebuild nothing, and its domain reload costs ~10 s on a project with heavy
`[InitializeOnLoad]` work; on an unchanged project the same command took 20-28 s before and ~2.5 s now.
The compile clock is stamped either way, which is what the CLI's wait treats as "compile finished". The proof a compile actually ran is the connector's compile-finished clock: the
refresh reply reports it at request time and the heartbeat is polled until that number moves, so a
heartbeat that was already `ready` is never mistaken for a finished compile. Exit code is 1 when the
compile failed or timed out, and the report is printed either way. `--compile` clears the console
first so an error left over from an earlier compile is not reported as this one's; `--keep-console`
skips the clear. Connectors without the compile-finished clock fall back to the heartbeat's
`compileErrors` flag and say so in `notes`.

### Quit

```bash
# Quit and wait until Unity has actually exited (unsaved scene changes are discarded)
unity-cli editor quit

# --save and --discard contradict each other: the CLI refuses the pair before it stops anything
# unity-cli editor quit --save --discard   -> exit 1, nothing quits

# Save open scenes and assets first
unity-cli editor quit --save

# Say explicitly that unsaved scene changes are discarded
unity-cli editor quit --discard

# Terminate Unity if it ignores the quit or a modal dialog blocks it
unity-cli editor quit --save --force
```

Use `editor quit` rather than `exec "EditorApplication.Exit(0)"`. Calling `Exit`
from inside a command handler shares the command's fate: the reply and the shutdown
race, and an exit vetoed by `EditorApplication.wantsToQuit` has no way to report
itself. `editor quit` sends `quit_editor`, which answers first and schedules the
exit for a later editor tick. It then waits until the heartbeat reports `stopped`
or stops advancing, and fails if neither happens within 60 seconds
(`--timeout` overrides it).

Without `--save`, dirty scenes are discarded; the reply lists them under
`discardedScenes`. `--save` saves each dirty scene synchronously and refuses to
quit while an untitled scene has unsaved changes, because saving it would open a
file dialog the CLI cannot answer. `--discard` is the explicit form of the
default. The reply also lists any visible `EditorApplication.wantsToQuit`
subscribers, and the connector logs an error naming them if Unity is still alive
after the scheduled `Exit` - that is what a veto looks like from outside.

With `--force`, an editor that does not exit in time, that refuses the
`quit_editor` request, or that is parked on a modal dialog, is terminated
(SIGTERM on macOS/Linux, `TerminateProcess` on
Windows). Unsaved changes are lost. A `--save` request that the connector refuses
is not overridden by `--force`, because the refusal may mean unsaved work could
not be written. The CLI only signals a PID whose process
name contains `Unity`. When the CLI runs in a container that cannot see the host
editor's PID, `--force` fails and Unity has to be stopped from the host. The final
JSON reports `method`: `graceful` or `terminate`, and `editor` carries the
connector's scene/veto report.

### Restart

```bash
# Quit, relaunch, and wait until Unity is ready
unity-cli editor restart

# Save open scenes and assets before quitting
unity-cli editor restart --save

# Delete Library/ and Temp/ before relaunching (forces a full reimport)
unity-cli editor restart --clean

# Launch a specific Unity editor binary
unity-cli editor restart --editor-path /Applications/Unity/Hub/Editor/6000.1.0f1/Unity.app/Contents/MacOS/Unity
```

`editor restart` resolves the project (from the running instance, `--project`, or
the current directory) and the launch executable before stopping the editor,
then quits it, optionally removes `Library/` and `Temp/`, relaunches it, and
waits until the new editor reports `ready`. Progress and removed paths go to
stderr; the final JSON contains the project path, old and new PIDs, cleaned
paths, and elapsed time.

The default wait budget is 5 minutes, or 15 minutes with `--clean`; `--timeout`
overrides both that and the 60-second quit wait. Restart stops Unity the same way
as `editor quit`, and accepts the same `--save`/`--discard`/`--force` flags.
Without `--force` it never kills the process, so if a modal dialog blocks
quitting, answer it first. Restart must run where it can signal and launch Unity;
from a container it refuses before quitting the editor, because it would otherwise
leave the editor stopped with no way to relaunch it. If startup stalls, a dialog
may be waiting in the editor window (safe mode, restore backup, or version upgrade).

### Input Injection (Play mode)

```bash
# Enter play mode first — injection needs a running player
unity-cli editor play --wait

# Keyboard: Key enum names (aliases: ctrl, alt, shift, cmd, esc, return, up, down)
unity-cli input key Space            # default mode is press
unity-cli input key Space press
unity-cli input key Space release
unity-cli input key W tap --hold_ms 200

# Mouse: Game view pixels (0,0 is the top-left of the Game View), deltas, buttons, wheel.
# --dx/--dy are screen deltas: positive moves right and DOWN, like a screenshot.
unity-cli input mouse --x 100 --y 200
unity-cli input mouse --dx 10 --dy -4
unity-cli input mouse --button left --mode click
unity-cli input mouse --scroll_y -2

# Gamepad: a virtual Gamepad is added on demand; --control names the InputSystem control
# ('axis' is an alias for 'gamepad')
unity-cli input gamepad --control leftStick --x 1 --y 0
unity-cli input gamepad --control leftTrigger --value 0.6
unity-cli input gamepad --control buttonSouth --mode tap
unity-cli input axis --control dpad --x 1 --y 0

# Touch: same Game view pixels as mouse; a virtual Touchscreen is added on demand
unity-cli input touch --x 64 --y 128 --mode tap

# What the Input System currently sees, plus focus and InputSettings state
unity-cli input state

# Release injected state, remove the virtual devices the tool added, restore InputSettings
unity-cli input reset
```

`input` writes device state instead of sending OS keystrokes: it queues a `StateEvent` built from the
device's current state with one control overwritten (so other keys, the mouse position and the other
stick survive) and pumps the player loop (`EditorApplication.QueuePlayerLoopUpdate` plus a repaint) so
the player update consumes the event and game code sees it in the same call — including
`wasPressedThisFrame`/`wasReleasedThisFrame`. State is read back with one tool update after the pump;
with `--pump_frames 0` that readback is the only update, so game code does not see a fresh edge.
Positional shorthand works (`unity-cli input key Space press` — the key and mode are taken by content,
so either order reads the same) and flags always win over positional values. Only `state` and `reset`
work outside play mode.

Injected events are visible to the **new Input System** (`UnityEngine.InputSystem`, `InputAction`s on
it) and never to legacy `UnityEngine.Input` or IMGUI `Event` code; every injection response repeats
that in `legacy_input_note`.

Response fields:

| Field | Meaning |
|-------|---------|
| `consumed` | The device state now matches the request — `isPressed`/`value` read back after the pump |
| `fresh_press_frames`, `fresh_release_frames` | How many player-loop input updates reported the control as freshly pressed/released — the view game code gets in `Update()` via `wasPressedThisFrame`/`wasReleasedThisFrame`. The tool's own readback updates are not counted |
| `held_state`, `state` | The control read back while held, and after the final pump |
| `position`, `space` | For mouse/touch: the position in the same space the caller used, `top_left_px` (Game view pixels, 0,0 at the top-left) |
| `focus.game_view_focused` | A Game View window currently has editor focus |
| `focus.application_is_focused` | The flag the Input System's own focus gating reads; it can be true while the editor is not frontmost |
| `focus.editor_focused` | Unity is the frontmost application (`EditorApplication.isFocused`) |
| `settings` | InputSettings in effect, the values saved before the tool changed them, and `held_controls` |

While the tool has anything injected that is not neutral, it also sets
`InputSettings.editorInputBehaviorInPlayMode = AllDeviceInputAlwaysGoesToGameView` and
`backgroundBehavior = IgnoreFocus`. With the project defaults the Input System defers keyboard and
pointer events into editor updates (they never reach the player) and disables those devices on focus
loss, which is what made queued events look dropped. The saved values are reported under `settings`,
and are restored when nothing is held anymore, by `input reset`, and when play mode exits. If the
project sets `InputSettings.UpdateMode.ProcessEventsManually`, the tool leaves `InputSystem.Update()`
to the game instead of calling it itself.

Mouse and touch coordinates are Game view pixels with `(0,0)` at the top-left, matching a screenshot:
`--dy` and `--y` grow downward. The tool converts the top edge to the Input System's bottom-left screen
space using the Game view render height (the value `Screen.height` reports once the player loop has
rendered), and echoes positions back in `top_left_px` space. `input state` reports mouse/touch positions
in the same space under `position_space: top_left_px`.

Injected state is *device state*, so the device's own backend can overwrite it later — a real mouse
move replaces an injected position, for example. Read `input state` right after injecting, or inject
again. Virtual devices the tool added (Gamepad, Touchscreen) are removed by `input reset` and are
also dropped automatically when play mode exits.

The connector keeps this tool in its own assembly
(`unity-connector/Editor/InputInjection/`, define-constrained on `com.unity.inputsystem`), so projects
without the Input System package compile unchanged and simply have no `input` tool.

### Console Logs

`console` is small by default so one chatty editor still fits in one tool output: the **newest 50**
entries, at most **6 stack frames** each, at most **1000 characters** per entry and **15 KB** of
entry text in total. `--stacktrace user` (the default) keeps only user-code frames — a frame is
engine noise when its declaring type's root namespace is Unity's or the runtime's
(`UnityEngine.*`, `UnityEditor.*`, `Unity.*` including Entities/Burst/Jobs/Collections, `System.*`,
`Mono.*`, `NUnit.*`), when it carries no managed type at all (native addresses, `<Module>`,
compiler-generated `<...>` holders, `(wrapper ...)`), or when its source is under `Library/`; runs of
them collapse into one `... (+N engine frames)` line. A trace whose frames are all engine noise keeps
its first non-`UnityEngine.Debug` frame, so there is still a location to act on.

```bash
# Newest 50 entries, capped stack traces
unity-cli console

# Newest 20 entries of all types
unity-cli console --lines 20 --type error,warning,log

# Only errors, one line each
unity-cli console --type error --stacktrace none

# Every frame of every error, still per-entry capped
unity-cli console --type error --stacktrace full --frames -1

# No size caps (--stacktrace full for every frame)
unity-cli console --full

# Clear console
unity-cli console --clear
```

Every cap has its own flag — `--lines`, `--entry_chars` and `--max_bytes` are removed by 0, `--frames`
is removed by -1 and set to "no frames" by 0 — and `--full` removes all of them. When anything was
cut the reply says what and how much, so a caller knows how to widen the query instead of guessing:

```json
{
  "entries": [
    "IndexOutOfRangeException : Index was out of bounds.\n... (+12 engine frames)\nFoW.FogOfWarUpdater:Update () (at Assets/FogOfWar/Runtime/Components/FogOfWarUpdater.cs:31)"
  ],
  "types": ["exception"],
  "entries_total": 213,
  "entries_returned": 50,
  "entries_omitted": 163,
  "frames_omitted": 412,
  "chars_omitted": 0,
  "truncated": true,
  "truncated_by": ["lines", "frames"],
  "limits": { "lines": 50, "frames": 6, "entry_chars": 1000, "max_bytes": 15360, "stacktrace": "user" }
}
```

`entries` is oldest-first and holds the newest entries the caps allowed, and `types` is index-aligned
with it: one `log`/`warning`/`error`/`exception`/`assert` per entry, so a mixed listing can be read
without re-querying. `--type` selects `log`/`warning`/`error` only (an exception or assert is chosen
by `error`), and any other value is refused with the ones it accepts rather than answering nothing;
`--lines` accepts 0 (no limit) or a positive count. `entries_total` counts the entries matching
`--type`, before any cap. The scan stops once `--lines` matches are found, so when
that happens `entries_total_is_lower_bound` is true and `entries_total` counts only the newest matches
that were read (`entries_scanned` says how many console entries were examined); without an early stop
the total is exact. The tool temporarily turns on the Console window's
Log/Warning/Error toggles, clears its search text and disables collapse for the read, then restores
the window exactly as it was. `--type` therefore narrows every entry, not just the ones the window
currently shows. When anything had to be overridden the reply says so in `console_window_filter`
(e.g. `{"available":true,"note":"ignored Console window filter: log/warning hidden, search 'foo'",
"log_hidden":true,"warning_hidden":true,"error_hidden":false,"search":"foo","collapsed":false}`).
If the Unity version does not expose the window filter, the reply carries `"available":false` and a
note instead, so an empty result is not mistaken for a clean one.

Before the caps release `console` returned a bare array of
all matching entries and `stacktrace=user` filtered with a hardcoded list of names that Burst and
Entities traces slipped through, which is what a single call costing 35 KB came from.

### Execute C# Code

Run arbitrary C# code inside the Unity Editor at runtime. This is the most powerful command — it gives you full access to UnityEngine, UnityEditor, ECS, and every loaded assembly. No need to write a custom tool for one-off queries or mutations.

Common namespaces are included by default. A snippet that is a single expression (`Time.time`, `40 + 2`,
`new Vector3(0, 1, 0)`) is compiled as `return (<expr>);`, so the value comes back without a `return`;
a body of statements still needs one (`return 1;`). A body without a return answers `null`, so a bare
`Debug.Log("x")` runs and reports `OK`. Add `--usings` only for project-specific types (e.g. `Unity.Entities`). The csc compiler and dotnet runtime are auto-detected; if detection fails, specify manually with `--csc <path>` or `--dotnet <path>`.

`System` and `UnityEngine` both define `Object`, `Random`, and `Debug`, so those names resolve through default aliases to the Unity types instead of failing with CS0104. A `using` directive at the top of the snippet is moved above the generated method, and your own alias wins over the default one. To replace the default set completely, pass `--no_default_usings` (the default imports and aliases are dropped) and list what you want with `--usings`.

**Compile gate.** Every tool runs behind a compile check, so a project that does not currently compile answers with the errors instead of running the call. The check only uses the current compile: live compiler diagnostics plus console entries from that compile, not old console lines or a failed build. When it blocks, the error names the first three diagnostics (`file:line:col CODE message`), says how old the compile is, and repeats the escape hatch. Pass `--skip_compile_check` (or `--params '{"skip_compile_check":true}'`) to run anyway — useful for diagnostics or to edit the source from `exec`.

```bash
unity-cli exec "Time.time"                                        # a bare expression returns its value
unity-cli exec "return Application.dataPath;"
unity-cli exec "return EditorSceneManager.GetActiveScene().name;"
unity-cli exec "return World.All.Count;" --usings Unity.Entities
unity-cli exec "return typeof(Object).FullName;"                  # UnityEngine.Object
unity-cli exec "using Object = System.Object; return typeof(Object).FullName;"   # System.Object
unity-cli exec "return typeof(Object).FullName;" --no_default_usings --usings System
unity-cli exec "return 1;" --skip_compile_check                 # run while the project has compile errors

# Pipe via stdin to avoid shell escaping issues
echo 'Debug.Log("hello"); return null;' | unity-cli exec
echo 'var go = new GameObject("Marker"); go.tag = "EditorOnly"; return go.name;' | unity-cli exec
```

**Async and coroutines.** `await` works in the body, and a `Task` you return is awaited for you — the response carries its result, not the `Task` object. Nothing blocks the editor's main thread while waiting, so work that needs the editor to keep ticking (an editor callback, a play-mode transition) still completes.

```bash
unity-cli exec "await Task.Delay(100); return EditorApplication.timeSinceStartup;"
unity-cli exec "return Task.Run(() => AssetDatabase.Refresh());"
unity-cli exec "return Task.Run(() => ImportEverything());" --await_timeout 300000
```

A body using `yield return` runs as a coroutine: `yield return null` waits one editor update, a yielded `Task` is awaited, and any other yielded value is collected and returned as a list. Unity's `YieldInstruction`s (`WaitForSeconds`, `WaitForEndOfFrame`) are rejected rather than silently ignored — use `await Task.Delay(ms)` to wait for time.

```bash
echo 'for (int i = 0; i < 3; i++) { yield return null; yield return i; }' | unity-cli exec   # -> [0, 1, 2]
```

Waits are bounded by `--await_timeout <ms>` (default 30000; `0` waits forever). If a wait outlives it, the response is an error carrying `{"timed_out": true, "status": "WaitingForActivation", "steps": N}` — the operation keeps running in the editor and its result is not observable from that response. A domain reload drops a pending result silently, so park long-lived results in a static field or `ScriptableObject` and read them back with a second `exec`.

**Diagnostics.** A compile error names the snippet's own line (`L3: …`, not the generated file's
line 21), and a runtime exception comes back as `Runtime error: <Type>: <message>` followed by a short
user-code stack whose snippet frames read `in snippet:line N`. The snippet is compiled with a portable
PDB so those frames carry line numbers, and Unity's engine frames are collapsed the same way
`console --stacktrace user` collapses them.

**Result shape.** A returned value is serialized from its public fields and readable public properties, so anonymous types, property-only editor API objects, lists, dictionaries, and nested objects come back as JSON objects rather than `ToString()` text. Collections cap at 100 entries and depth at 6, cycles are reported as `"<cycle: Type>"`, and a property that throws becomes `"<error: Type: message>"` without dropping its siblings. Unity objects keep their identity string (`"Main Camera (UnityEngine.Camera)"`), and Unity math structs expose their data members rather than derived values such as `Vector3.normalized`. If serialization itself fails, `data` falls back to a string and `meta` carries `serialization_error`; use `--meta` to see it.

**Compiled-snippet cache.** `exec` compiles the snippet with `csc` in a subprocess and waits for it **without** blocking the editor's main thread, then loads and invokes it back on that thread. The same call again — same body, same `--usings`, same reference set — reuses the already-loaded assembly instead of compiling, which is the difference between a repeated probe costing one frame or three seconds. The response reports that as `meta: {"compile":"compiled|cached","compile_ms":N,"reference_assemblies":N}`, kept out of `data` so parsing a result is unchanged; `--meta` prints it to stderr. `compile_ms` is the csc subprocess only — a cache hit reports `0`, and the snippet's own run time (including `await`) is not part of it:

```bash
unity-cli exec --meta "return Application.dataPath;"   # meta: {"compile":"compiled","compile_ms":403,…}
unity-cli exec --meta "return Application.dataPath;"   # meta: {"compile":"cached","compile_ms":0,…}
```

It is off by default: `exec … 2>&1 | tail -1` would otherwise pick the note up instead of the result. Cached lookups are bounded at 256 entries; an evicted snippet's assembly stays in the domain until the next domain reload either way, because Mono cannot unload one. If the editor reloads while a snippet is being compiled, the call answers an error instead of running the stale compile.

Because `exec` compiles and runs real C#, it can do anything a custom tool can — inspect ECS entities, modify assets, call internal APIs, run editor utilities. For AI agents, this means **zero-friction access to Unity's entire runtime** without writing a single line of tool code. Piping via stdin avoids shell escaping headaches with complex code.

### API Lookup

What an API actually is, read off the assemblies the editor has loaded — the same set the project's code compiles against. Grepping `Library/PackageCache/com.unity.<pkg>@<hash>/…` is slower, and it disagrees with what compiled (hashed folder names, stubs, `#if` branches); reflection cannot.

```bash
# kind, namespace, assembly, base type, interfaces, member counts
unity-cli api Vector3

# every overload of one member, in C#: modifiers, generics + constraints, ref/out/in/params
unity-cli api Vector3.Lerp

# a property, a nested type — or a bare member name: which types declare it
unity-cli api EditorApplication.isCompiling
unity-cli api GetSingletonBuffer --assembly Entities

# narrow the search, and ask for the XML docs Unity ships for its own modules
unity-cli api List.Add --assembly mscorlib --limit 5
unity-cli api UnityEngine.Object.FindObjectOfType --docs
```

```json
{
  "summary": "UnityEngine.Vector3 (struct, UnityEngine.CoreModule): 2 'Lerp' matches",
  "types": [{
    "type": "UnityEngine.Vector3", "kind": "struct", "assembly": "UnityEngine.CoreModule",
    "members": [
      { "name": "Lerp", "signature": "public static Vector3 Lerp(in Vector3 a, in Vector3 b, float t)" },
      { "name": "Lerp", "signature": "public static Vector3 Lerp(Vector3 a, Vector3 b, float t)" }
    ]
  }]
}
```

A type query accepts the simple name, the full name, and either spelling of a generic (`BufferLookup`, `BufferLookup<T>`, `` BufferLookup`1 ``). An answer that is a nearest-match fallback says so: `exact: false` plus a `note` (`no member named 'Lerpp' on UnityEngine.Vector3; closest: Lerp, Slerp`) and, for a query that names nothing at all, an empty `types` list with the closest type names in `closest_types` — the CLI exits 1 on that, because the API the caller asked for does not exist. When several types match, each one is described, best match first (up to `--max_types`, default 5). `[Obsolete]` members report the message, whether it is an error, and the replacement when the notice names one (`Use X instead`, `(UnityUpgradable) -> X`). `--docs` adds the `<summary>` from the `.xml` sitting next to the assembly — Unity ships one for its own modules, most third-party assemblies ship none, and a missing doc file prints nothing.

A query that names nothing real is not a successful lookup. The Connector answers a typo with the closest real names and a sentence saying what did not exist, and the CLI prints that sentence first while still printing the answer and exiting 0:

```
no member named 'Lerpp' on UnityEngine.Vector3, System.Numerics.Vector3; closest: Lerp, Slerp
{ "exact": false, "matched_by": "type.member", ... }
```

A query with nothing close at all (`api NoSuchTypeXyz`) exits 1 with the Connector's `no type or member named …` message. The reply carries `exact`, `note` and `matched_by` (`type`, `type.member`, `member`, `closest_types`), so a script can decide without parsing the sentence; when a reply marks itself inexact without a `note`, the CLI builds the line itself (`no exact match; closest: …`).

The index is built lazily and cached for the editor session. `--budget <ms>` (default 1500) bounds one call; `index.complete: false` means the budget ran out before every assembly was searchable, so raise the budget or ask again. The command stays available while the project does not compile: the loaded assemblies are the last good ones, which is exactly when the real API is wanted.

### Menu Items

```bash
# Execute any Unity menu item by path
unity-cli menu "File/Save Project"
unity-cli menu "Assets/Refresh"
unity-cli menu "Window/General/Console"
```

Note: `File/Quit` is blocked for safety.

### Screenshots

```bash
# What the running game shows — OnGUI/IMGUI HUDs included (needs Play mode)
unity-cli editor play --wait
unity-cli screenshot --mode gameview --output_path Temp/shots/hud.png

# One camera rendered offscreen at a chosen size: no OnGUI, no editor overlays
unity-cli screenshot --mode camera --width 3840 --height 2160

# The Scene view camera — what a bare `screenshot` means while not in Play mode
unity-cli screenshot
```

| mode | what it captures | includes | not in it |
|---|---|---|---|
| `gameview` | the frame the running game produces (`ScreenCapture.CaptureScreenshot`), at the Game view's own resolution | the game's OnGUI/IMGUI HUDs, measured: a `GUI.Box` drawn at `Rect(40,40,400,240)` landed at exactly that pixel rect | the Game view tab's own editor chrome (toolbar, resolution dropdown) |
| `camera` | `Camera.main` rendered offscreen into a render texture at `--width`/`--height` | that camera's render only | the game's GUI (OnGUI/IMGUI) — measured: all 2,073,600 pixels of a 1920x1080 frame were the flat background color while the Game view was showing a HUD — plus Scene-view gizmos, Handles and editor overlays |
| `scene` | the Scene view camera rendered offscreen at `--width`/`--height` | the scene as the current scene camera sees it | gizmos and Handles (toggling `SceneView.drawGizmos` leaves the file byte-identical), the Scene view's overlay UI, any game GUI |

`gameview` is the default in Play mode, `scene` in Edit mode (what a bare `screenshot` has always done). `gameview` needs Play mode: in Edit mode no game frame is produced and Unity never writes the file, which the tool reports instead of hanging. A paused game produces no frame either, so an explicit `--mode gameview` reports that too; a bare `screenshot` (no `--mode`) falls back to `camera` while paused and says so in the reply (`"mode":"camera","fallback":"paused"`). `--mode camera` renders without the game loop. Unity writes the capture to a sibling `*.partial.png` and the tool moves it onto `--output_path` only once the file is complete (its PNG trailer is there), so a failed or timed-out capture leaves an existing screenshot at that path untouched. Because the write is asynchronous, the tool pumps editor updates and answers as soon as the complete file is there (bounded at 5 s), reporting path, byte count and dimensions — without blocking the editor's main thread.

Compare pixels inside one capture, not across modes: the `gameview` frame is what the game's own pipeline wrote to its backbuffer, while `camera`/`scene` read a plain render texture, and the same background color comes out at different values.

No mode reads screen pixels out of an editor window. An editor window moved off every display makes such a read abort the editor natively, so `screenshot` sticks to camera renders and the game's own `ScreenCapture`.

`--view` is the older spelling of `--mode`: `--view scene` is `--mode scene`, but `--view game` is `--mode camera` — the offscreen render, not the Game view.

### Asset Reserialize

AI agents (and humans) can edit Unity asset files — `.prefab`, `.unity`, `.asset`, `.mat` — as plain text YAML. But Unity's YAML serializer is strict: a missing field, wrong indent, or stale `fileID` will corrupt the asset silently.

`reserialize` fixes this. After a text edit, it tells Unity to load the asset into memory and write it back out through its own serializer. The result is a clean, valid YAML file — as if you had edited it through the Inspector.

```bash
# Reserialize the entire project (no arguments)
unity-cli reserialize

# After editing a prefab's transform values in a text editor
unity-cli reserialize Assets/Prefabs/Player.prefab

# After batch-editing multiple scenes
unity-cli reserialize Assets/Scenes/Main.unity Assets/Scenes/Lobby.unity

# After modifying material properties
unity-cli reserialize Assets/Materials/Character.mat
```

Every path must exist: `ForceReserializeAssets` silently ignores one that does not, so a typo used to
answer exactly like a success. The tool now names every missing path and reserializes nothing.

This is what makes text-based asset editing safe. Without it, a single misplaced YAML field can break a prefab with no visible error until runtime. With it, **AI agents can confidently modify any Unity asset through plain text** — add components to prefabs, adjust scene hierarchies, change material properties — and know the result will load correctly.

### Burst: cache and compile status

Burst keeps its compiled JIT libraries in `<project>/Library/BurstCache`. When that cache goes stale — a Burst package upgrade, a switch of Burst options, or jobs behaving like old code — the fix is to delete it and let Burst recompile.

`clear_burst_cache` does that from inside the editor, without a shell: it only ever touches paths under `Library/BurstCache` (no path argument exists), reports every file and byte it removed, and can list the same for a dry run.

```bash
# Delete the whole cache; Burst rebuilds it on the next compile
unity-cli clear_burst_cache

# See what would go first, sizes included
unity-cli clear_burst_cache --dry_run

# Only the JIT library cache, keep the AOT settings hashes
unity-cli clear_burst_cache --scope jit
```

Files Burst still has mapped are reported per path instead of aborting the clear, so a partial result is always accounted for.

`burst_status` answers the other half: whether Burst is *done* compiling, what it compiled, and what failed. Burst compiles on its own schedule, outside Unity's script compilation, so `compile: ok` plus passing tests plus an empty console does not prove a `[BurstCompile]` job was ever compiled — and a `BCxxxx` error can show up after the compile already looked clean. Reading `Library/BurstCache` by hand answers none of that (bundle file names only, no status, no errors, and code from earlier work mixed in).

```bash
# Idle or compiling, how many targets compiled, and any Burst error
unity-cli burst_status

# Wait up to 60 s for Burst to finish first
unity-cli burst_status --wait 60

# List the matching targets with per-target status
unity-cli burst_status --type ScaleValuesJob
unity-cli burst_status --assembly MyGame.Runtime --list

# Force Burst to recompile now, so the answer comes from this call
unity-cli burst_status --compile --wait 120 --timeout 130000
```

```
Burst idle: 3/4 targets compiled, 1 Burst error(s).
  Assets/Jobs/HeavyJob.cs:39 BC1006 The `catch` construction is not supported
```

Targets are the `[BurstCompile]` jobs and static methods Burst itself lists, filtered to the project's own assemblies (package and engine targets are counted separately and left out of the listing unless `--include_packages`). Status is `compiled`, `failed`, `not-compiled-yet`, or `unknown`: Burst eager-compiles everything it knows about when an assembly finishes compiling, so "Burst's own queue is idle" plus "no error names this target" is what `compiled` means — the `method` field says whether the answer came from that eager compile (`eager`) or from a recompile `burst_status` forced this call (`forced_recompile`). `failed` requires an error that names the target; when an error cannot be attributed, the count says so in `notes` instead of guessing.

Burst's own position for a job-producer error points at the engine's generated job struct; the tool prefers the first stack frame outside the engine, which is your file and line. `verify` runs this step after the compile, so an agent gets the answer without asking; `--no-burst` skips it.

The tool is built only when `com.unity.burst` is installed (its assembly is constrained on the package), so on a project without Burst the command is simply unknown and `verify` prints no `burst:` line.

What Burst exposes publicly is thin: `IsCurrentCompilationDone`, `TriggerRecompilation`, the inspector's entry-point list (`GetInspectorEntryPoints`) and the eager-compile status are internal or private, so the connector binds them by reflection and reports `unknown` instead of failing when a name is missing from the installed Burst. Measured on Unity 6000.6.5f1 with Burst 2.0.0: the per-method disassembly path (`BurstCompilerService.GetDisassembly`) rejected valid targets with `Unable to compile method in BurstCompilerService::CompileAsync`, so it is not used to decide per-target status. `--compile` uses Burst's own recompile pass, which re-logs code-level errors (BC1006/BC1091) but not the ones Unity only emits while post-processing an assembly (BC1051, an invalid managed field on a job struct), and Burst logs a method's error once per editor session — so a cleared console can be quiet about a job that really does fail. `notes` says which evidence the answer rests on.

```bash
# Delete the whole cache; Burst rebuilds it on the next compile
unity-cli clear_burst_cache

# See what would go first, sizes included
unity-cli clear_burst_cache --dry_run

# Only the JIT library cache, keep the AOT settings hashes
unity-cli clear_burst_cache --scope jit
```

Files Burst still has mapped are reported per path instead of aborting the clear, so a partial result is always accounted for.

### Profiler

```bash
# Read profiler hierarchy (last frame, top-level)
unity-cli profiler hierarchy

# Recursive drill-down
unity-cli profiler hierarchy --depth 3

# Set root by name (substring match) — focus on a specific system
unity-cli profiler hierarchy --root SimulationSystem --depth 3

# Drill into a specific item by ID
unity-cli profiler hierarchy --parent 4 --depth 2

# Average over last 30 frames
unity-cli profiler hierarchy --frames 30 --min 0.5

# Average over a specific frame range
unity-cli profiler hierarchy --from 100 --to 200

# Filter and sort
unity-cli profiler hierarchy --min 0.5 --sort self --max 10

# Enable/disable profiler recording
unity-cli profiler enable
unity-cli profiler disable

# Show profiler state
unity-cli profiler status

# Clear captured frames
unity-cli profiler clear
```

### Run Tests

Run EditMode and PlayMode tests via the Unity Test Framework.

```bash
# Run EditMode tests (default)
unity-cli test

# Run PlayMode tests
unity-cli test --mode PlayMode

# Save dirty open scenes before running tests
unity-cli test --auto-save-scenes

# Only one test (its full name) or one class (the namespace-qualified class name)
unity-cli test --filter MyNamespace.MyTests
unity-cli test --filter MyNamespace.MyTests.SpecificTest

# Only tests in these assemblies (comma-separated)
unity-cli test --assembly MyGame.Tests.Editor

# Run anyway when an assembly is older than its sources
unity-cli test --allow-stale
```

`--filter` selects the tests whose full name contains it as whole dot-separated segments: `MyTests` or `MyNamespace.MyTests` runs that class's tests, `MyTests.SpecificTest` runs one test, and `MyNamespace` runs that namespace; part of a segment (`My`) selects nothing. A filter with regex syntax (`^ $ * + ? [ ] { } | \`) is used as a regex on the full name. A run that selects no test is a failure, not a pass: `test` exits 1 with `no tests matched (filter …, assembly …)`, the same verdict `verify` gives when no test ran. With `--assembly` given, a stale assembly *outside* that selection is reported too (`N stale assembly(ies) are outside --assembly; the tests will not load them`), because the run cannot see it.

Requires the Unity Test Framework package. Tests are blocked by default when any open scene has unsaved changes. Use `--auto-save-scenes` to save them first, or `--allow-dirty-scenes` to run anyway. They are also blocked when a project assembly under test is older than its sources — a stale DLL runs old code and looks exactly like a real result. Rebuild with `unity-cli editor refresh --compile`, or pass `--allow-stale` deliberately. PlayMode tests trigger a domain reload — the CLI polls for results automatically.

Tests run for minutes, so `test`'s invocation budget defaults to 30 minutes instead of the global 120 s. `--timeout <ms>` overrides it, and `--timeout 0` means no deadline (PlayMode result polling included); a run that exceeds the budget is reported as a client deadline, not as a lost editor.

### Verify

Check that the project compiles and its tests pass in one call, with output brief enough for an agent's context.

```bash
# Compile, list errors in your code, then run EditMode tests
unity-cli verify

# Stop after the compile
unity-cli verify --no-tests

# Skip the Burst step
unity-cli verify --no-burst

# Only some tests (also --filter, --mode PlayMode)
unity-cli verify --assembly MyGame.Tests.Editor

# Run tests even when an assembly is older than its sources
unity-cli verify --allow-stale

# One JSON object instead of text
unity-cli verify --json
```

```
compile: 1 error(s)
  Assets/Scripts/Player.cs:42:13 CS0103 The name 'speed' does not exist in the current context
```

When the error is one an agent can act on — a member or type the compiler could not find, or an API that is obsolete — the connector looks the answer up in the loaded assemblies and prints it under the error:

```
compile: 1 error(s)
  Assets/Scripts/Player.cs:12:9 CS1061 'Transform' does not contain a definition for 'positon' and no accessible extension method …
    hint: closest: Transform.position, Transform.localPosition, Rigidbody.position
```

That covers `CS0117`/`CS1061` (member not found: the closest members on the named type first, then the same name on other types), `CS0246`/`CS0234` (type or namespace not found: the closest real types, with the assembly each lives in) and `CS0619`/`CS0618` (obsolete: the replacement the notice names, resolved to a real signature — `hint: use UnityEngine.Object.GetEntityId()`). At most three candidates per error and 200 characters per hint; the whole pass has a 100 ms budget per result (`compile_diagnostics --hint_budget <ms>`, `--include_hints false` to switch it off), and lookups repeat from cache. A hint appears only when there is a real answer: a message the parser does not recognize, or a deprecation notice that names no member, prints nothing extra.

A clean C# compile also gets a Burst line, because Burst compiles outside script compilation and a `BCxxxx` error can arrive after it looked clean:

```
compile: ok
compiled 14:02:11 (3 s ago, took 2.2 s) · 1 source file changed since previous compile · rebuilt: Game, Game.Tests.Editor
burst: 14 targets compiled, 0 error(s)
tests: 31/31 passed
```

The `compiled ...` line is the [compile provenance](#compile-provenance-and-wait): it says which compile this verdict came from, and whether anything in the project is newer than it.

Burst errors print like compile errors and fail verify (exit 1) before the tests run; `--burst-wait <seconds>` bounds how long verify waits for Burst to go idle (default 60, keep `--timeout` above it). A Burst check that cannot run — no `burst_status` tool because Burst is not installed, or the editor wouldn't answer — prints nothing or a note and leaves the verdict alone.

`verify` clears the console first (a Burst error stays there after its code changes; Burst logs a live one again on the next compile; `--keep-console` skips the clear), refreshes and compiles, and lists this compile's errors from the connector's `compile_diagnostics` tool, leaving out `Library/` and `PackageCache/` unless nothing else failed. It then refuses to run the tests if any project assembly is older than its sources (the DLL would run old code); `--allow-stale` overrides that. An assembly whose DLL an already-finished compile had time to rewrite is not refused over (`--json` and `unity-cli wait` report it as `not stale:`) — see [Compile provenance](#compile-provenance-and-wait). Otherwise it saves dirty open scenes and runs the tests, printing `passed/total` and the first line of each failure, followed by any exception the failing test logged instead of threw (`logged InvalidOperationException: … (at file:line)`; Entities logs what a system throws in `OnUpdate`, so the assertion that fails later never names it). `--max` caps both lists (default 20). The Burst line appears only when Burst has targets or something to report. Exit code 0 means it compiles, Burst is quiet and every test passed; 1 means compile errors, Burst errors, stale assemblies, failing tests or no test ran; 2 means the editor couldn't answer: not running, timed out, or blocked by a modal dialog, which is named along with the command that answers it. Connectors before 0.7.0 have no `compile_diagnostics` and ignore `--assembly`; `verify` then lists console errors and says so.

`verify` runs the tests, so its invocation budget defaults to 30 minutes instead of the global 120 s. `--timeout <ms>` overrides it (`--timeout 0` means no deadline); when you set it explicitly, keep it above `--burst-wait`.

### Compile provenance, and `wait`

Every compile result says which compile it reflects, so a clean answer is told apart from a stale one:

```
compiled 14:02:11 (3 s ago, took 2.2 s) · 1 source file changed since previous compile · rebuilt: Game, Game.Tests.Editor
stale: Assets/Scripts/Player.cs modified after this compile — run verify again
stale assembly: Game.Tests.dll (built 14:01:50) is older than Assets/Game/Tests.cs (14:04:02) — run unity-cli editor refresh --compile
not stale: Game.Tests.dll (built 14:01:50) is older than Assets/Game/Tests.cs (14:04:02), but its content matches what the compile recorded for it; a moved mtime alone does not mean the DLL is out of date
```

The third line appears when an assembly's output DLL is missing or older than its newest `.cs`/`.asmdef`/`.asmref` source (project and `file:`/embedded packages only; `Packages/<name>` paths are resolved to the directory the package really lives in, which is what makes `file:` packages count). The compile may have "succeeded" while Unity's assembly cache skipped the rebuild; `verify` and `test` refuse to run tests in that state unless `--allow-stale` is passed, and `editor refresh --compile` asks once for a `CleanBuildCache` rebuild before reporting `stale_after_compile`.

The fourth line is the opposite case, and it is deliberately not a refusal: Unity does not rewrite a DLL whose contents it considers current, so a source whose mtime merely moved (a `touch`, a re-checkout) stays older than its DLL forever even though a compile read it. The first scan that finds a DLL current writes a content record for its sources, so the very next `touch` is answered by comparing hashes (‘its content matches what the compile recorded for it’) instead of the noisier ‘not stale, no content record yet’ line, which is now only for an assembly whose DLL predates its sources the first time the connector sees it. When the last compile *started* after that source was written, the assembly is counted in `stale_assembly_covered_count` and named in `stale_assembly_covered_line` instead of being reported stale — otherwise every `verify` after a `touch` would refuse, ask for a clean rebuild, and refuse again. Measured on 6000.6.5f1: a touched file produced an 11 s compile with `rebuilt_assemblies: []` and the DLL mtime unchanged, and even `editor refresh --compile clean` (`RequestScriptCompilationOptions.CleanBuildCache`, 65 s) left those mtimes alone because Bee restores its cached node output with the timestamps it recorded. So the compile clock, not the DLL's mtime, is what decides this case; `--compile clean` is still the hammer for a cache you believe is lying.

The first line is the connector's: when the last script compilation finished (the reload it triggered
included), how long it took, how many source files were written since the compile before it, and
which assemblies it rebuilt. The same clock is what the heartbeat publishes as `compileFinishedAt`; the
heartbeat and `compile_status` read it from the same record, so the CLI cannot see "this connector has
no compile clock" while a compile status names one. The second line appears when a `.cs`, `.asmdef` or `.asmref` under
`Assets/` or under a `file:`/embedded package was written **after** that compile: the edit is in the
project but not in the result above it, which is the doubt that otherwise makes agents poll the
editor with `sleep 45 && unity-cli compile_errors` loops.

| Where it shows | Fields |
|---|---|
| `verify` | printed after the compile verdict; `compile.provenance` with `--json` |
| `editor refresh --compile` | `provenance`, `provenance_line`, `stale_line`, and a note naming uncompiled sources |
| `compile_diagnostics` | nested under `provenance` (`--include_provenance false` drops it) |
| `compile_status` | the whole object: `compiled_at_ms`, `compiled_at` (ISO), `duration_ms`, `changed_since_previous_compile`, `rebuilt_assemblies`, `stale_files`, `stale_assembly_count`, `stale_assemblies[]`, `stale_assembly_line`, `stale_assembly_covered_count`, `stale_assembly_covered_line`, `scan_ms`, `scan_complete`, `provenance_line`, `stale_line` |

```bash
# Block until the editor is idle, then print its state and the provenance line
unity-cli wait

# Also require proof that the last compile covers the edit you just saved
unity-cli wait --compile --timeout 120

# Just the provenance, no waiting
unity-cli compile_status
unity-cli compile_status --scan false        # timestamps only, no filesystem walk
unity-cli compile_status --scan_budget 1000  # let the scan take up to a second
```

`wait` exits 0 when the editor is idle (heartbeat `ready`, `playing` or `paused` — not starting,
compiling, refreshing, reloading, building, and no dialog open), 1 when it gave up, and 2 when the editor could
not answer, naming a modal dialog and the command that answers it. `--compile` turns "idle" into "idle and
provably compiled": when the connector cannot prove that (no compile clock on the heartbeat, `compile_status`
not answering), `wait --compile` exits 1 with the reason and the command that does prove it, so
`wait --compile && test` never runs tests against code the wait could not see built. `--timeout` is seconds and belongs
after the command name; the global `--timeout` before it stays a request timeout in milliseconds.

With `--compile`, one of two things has to be true to exit 0: the connector's compile-finished clock
moved after `wait` started (both sides of that comparison are connector stamps, so an agent sandbox
with a different clock than the editor's still works), or the source scan found nothing newer than
the last compile, which means your edit is already in. A compile that finished *before* `wait`
started and stale sources prove nothing, so `wait` keeps waiting — and says so after 10 seconds,
because Unity only auto-refreshes while it has focus. An editor driven from a terminal never does,
so the honest answer there is exit 1 with `run unity-cli verify` in the note:

```
Unity (port 8090): ready
gave up after 11s: still ready — no compilation started within 10s of finding uncompiled sources
compiled 14:02:11 (7 min ago, took 3.4 s) · 1 source file changed since previous compile · rebuilt: Assembly-CSharp
stale: Assets/Scratch/Stale.cs modified after this compile — run verify again
  (Unity only auto-refreshes while it has focus; run unity-cli verify (or unity-cli editor refresh --compile) to compile them)
```

The scan walks `Assets/` plus embedded and `file:`/local packages — registry and git packages under
`Library/PackageCache` are not your code and would spend the whole budget. It gets 200 ms of the
editor's main thread and, when the project is bigger than that, says so (`scan_complete: false`,
counts are a lower bound) instead of stalling the editor. A connector without `compile_status` cannot supply provenance: `wait` and the compile reports then
print the state and say that provenance is unavailable.

### Build a Player (long-running)

```bash
# Build with a Build Profile and wait for the result (progress on stderr, JSON on stdout)
unity-cli build --profile macOS_dev --output Builds/Mac/App.app

# Same build with development player options
unity-cli build --profile macOS --output Builds/Mac/App.app --development

# Build by BuildTarget instead, with explicit scenes
unity-cli build --target StandaloneOSX --output Builds/Mac/App.app --scenes Assets/Main.unity

# Start it and go away; poll it later
unity-cli build --no-wait --profile macOS_dev --output Builds/Mac/App.app
unity-cli build status
unity-cli build status build-20240101-114213-9f0ac1
```

```
build job build-20240101-114213-9f0ac1 queued (macOS_dev -> /proj/Builds/Mac/App.app)
building 12s  Compiling PlayerAssemblies 23%
building 3m27s  Compiling PlayerAssemblies 61%
build succeeded in 5m02s — 132.4 MB, 0 error(s), 4 warning(s) -> /proj/Builds/Mac/App.app
```

`BuildPipeline.BuildPlayer` runs on Unity's main thread and blocks it for the whole build, so a build
asked for through `exec` looks like a hung editor: the connection dies, every other command queues
behind it, and the only way to watch it is to scrape `Editor.log`.

`build` does the same build as a job. The connector validates the parameters and records the job
(`<project>/Temp/unity-cli/build-jobs.json`, so it survives the domain reload a build can trigger),
then returns its id; the build starts on a later editor update tick, outside the command lock. While
it runs:

- the heartbeat says `building` with the job id, phase and elapsed time instead of `ready`, so waiters
  and restart daemons can tell a building editor from a wedged one;
- `build_status` answers on a thread that never waits for the main thread, so `unity-cli build status`
  and `unity-cli status` keep working;
- the wait keeps polling through dropped connections and the domain reload, instead of aborting on the
  first failed request (it gives up only after 3 minutes with no answer at all, and returns the last
  report it got).

The percent comes from Unity's Progress window and only appears when the build pumps the editor update
loop; when it does not, the lines carry elapsed time alone. Every Progress API throws off the main
thread, so nothing else can be read from there while the build is running.

While one build is running, a second `build` is refused immediately, from the heartbeat: asking the
connector instead would queue the request behind the running build and look like a hang. Things only
the editor can know — an unknown build profile, scenes that no longer exist in the project, a target
whose module is not installed — are refused before the job starts, not minutes later inside the build.

Phases are `queued`, `building`, `succeeded`, `failed`, `cancelled`, `interrupted` — the last one means
the editor stopped reporting the job while it was building, so that build's outcome is unknown. The
final JSON on stdout is the job record: phase, `result`, output path, size, duration, error and
warning counts, and the first error messages from the `BuildReport`. Exit 0 on success, 1 on failure,
including the up-front validation errors (unknown build profile, a target whose module is not
installed, an output inside `Assets/`).

### Modal Dialogs (macOS)

```bash
# Show the dialog currently blocking the editor, with its text and buttons
unity-cli modal

# Click a button by title (case and curly-apostrophe insensitive) or by index
unity-cli modal answer --button "Don't Save"
unity-cli modal answer --index 2

# Refuse to click if a different dialog has replaced the one you inspected
unity-cli modal answer --button Cancel --id 27186
```

A modal dialog (`EditorUtility.DisplayDialog`, save prompts, file panels) freezes Unity's main thread, so normal commands cannot run until someone answers it. While one is open, the heartbeat reports state `modal` together with the dialog's text and buttons. Every other command fails right away with that description instead of hanging. If a command opens a dialog while running, the CLI returns the same error; the command resumes inside Unity once the dialog is answered.

The connector answers the dialog from inside the Unity process by dispatching to the main queue, which AppKit keeps draining during a modal loop. No Accessibility permission is involved, and only Unity's own windows are reachable. Dialogs drawn with IMGUI (`EditorWindow.ShowModal`) have no native buttons; they are listed but cannot be answered. Dialogs shown before the connector loads (license prompts, Safe Mode, "project already open", package resolution) are out of reach for `modal` — see [Startup Dialogs](#startup-dialogs-macos-host).

### Startup Dialogs (macOS host)

```bash
# List Unity's windows with their texts and button names (JSON)
unity-cli --project ~/Work/MyGame dialogs

# Press a button of the dialog you just listed
unity-cli dialogs --click "Continue"

# Same button title on several windows: name the window
unity-cli dialogs --click "Don't Save" --window "Save Changes"
```

`modal` needs the connector, and the connector needs its assembly to have loaded — which is exactly
when startup dialogs appear. `dialogs` reads the same windows from outside the Unity process with
AppleScript and System Events (Accessibility), so it works with no connector and no heartbeat.
It runs only on macOS; on any other system it says so and tells you to run it on the host, because
an agent sandbox is granted no Accessibility permission.

When no heartbeat answers, every command names which situation it is instead of saying "no Unity
instance found", using the heartbeat records and the project's `Temp/UnityLockfile`:

- `editor_starting_or_blocked` — the lockfile exists but no heartbeat newer than it, so the connector
  never loaded this session. The editor is still importing/compiling, or a startup dialog (license,
  safe mode, "project already open", package resolution) is waiting. A lockfile older than 2 hours
  with no heartbeat from this session says instead that the editor probably crashed rather than
  still starting.
- `editor_unresponsive` — a heartbeat written this session exists but went silent. The connector
  did load, then stopped answering: the main thread is likely blocked by a long exec/build/import, a
  modal dialog is open, or the editor hung. This one does not need the lockfile; when the lockfile
  is gone the report says the editor has probably exited and to relaunch it.

```
editor_starting_or_blocked: Unity appears open for /Users/me/Work/MyGame (Temp/UnityLockfile, age 42s)
but the connector has not loaded — it is still importing/compiling, or a startup dialog (license,
safe mode, "project already open", package resolution) is waiting.
  Lockfile: /Users/me/Work/MyGame/Temp/UnityLockfile (written 2026-11-04T04:26:32Z)
  Next:     run 'unity-cli dialogs' on the macOS host to read the dialog's title and buttons,
            or ask the user to look at the editor.

editor_unresponsive: Unity's connector for /Users/me/Work/MyGame stopped answering 16m0s ago, pid 65279
(last state "ready") — the main thread is likely blocked (long exec/build/import), a modal dialog is
open, or the editor hung.
  Next:     try 'unity-cli dialogs' on the macOS host; if it stays silent, restart the editor
            ('unity-restart' in agent-sandbox).
```

`Temp/UnityLockfile` is written once when Unity opens the project, so its age is the length of the
session, not evidence of a crash. The CLI only says "probably crashed" from a lockfile older than
2 hours when no heartbeat from that session exists; once a heartbeat postdates the lockfile, a
silence is reported as `editor_unresponsive` instead.

Windows are narrowed to the requested project by matching the project folder name in the window
title; when nothing matches, every Unity window is listed, because startup dialogs rarely name the
project. A click in that unfiltered listing needs `--window` unless all listed windows belong to one
Unity process, so it cannot press a button in another project's editor. If Accessibility is missing,
the error names the exact grant (System Settings → Privacy & Security → Accessibility, for
`/usr/bin/osascript` and the app running the command). Button matching is exact-title first, then
case- and apostrophe-insensitive from the start of the title (so `Save` cannot press `Don't Save`),
and an ambiguous title is reported instead of clicking a possibly-wrong dialog.

Windows Unity draws itself (IMGUI panels, some Safe Mode prompts) expose no native buttons to
Accessibility: `dialogs` lists their title and texts, but there is nothing to click, and only a
human can answer them. Static texts are capped at 12 per window (`texts_total` reports the real
count) so an editor window does not flood the output.

### List Tools

```bash
# Show all available tools (built-in + project custom) with parameter schemas
unity-cli list
```

### Passing Parameters

`exec`, `build`, and custom tools called by name accept `--params '<json object>'` as an
alternative to individual flags. The CLI sends the resulting object to the connector as the tool's
parameters. Built-in commands with their own flags (`test`, `verify`, `editor`, `wait`) build their
parameters from those flags and do not read `--params`.

```bash
# exec with JSON parameters
unity-cli exec --params '{"code":"return 42;"}'

# Repeated --params objects are merged in order, so later keys win
unity-cli exec --params '{"code":"return 42;"}' --params '{"await_timeout":1000}'
# sends both code and await_timeout
```

`--params` is the only repeatable subcommand flag. Any other subcommand flag may be given
once; a second occurrence (for example `--filter a --filter b`) is an error instead of silently
replacing the first value. A flag the command does not take is an error too — `test --filtre x` exits 1 with
`unknown flag --filtre for test; see unity-cli test --help` instead of running the whole suite. Custom tools
keep their parameters on the connector side, which refuses one it does not know with
`data.code: "unknown_parameter"` (nothing ran; check the schema with `unity-cli list`).
Invalid JSON in any `--params` object is also an error. When the same key comes from both a plain
flag and `--params`, the `--params` value wins.

### Custom Tools

```bash
# Call a custom tool directly by name
unity-cli my_custom_tool

# Call with parameters
unity-cli my_custom_tool --params '{"key": "value"}'
```

### Status

```bash
# Show Unity Editor state
unity-cli status
# Output: Unity (port 8090): ready
#   Project: /path/to/project
#   Version: 6000.1.0f1
#   CLI:     0.7.1
#   Connector: 0.7.0
#   Compatibility: compatible (same major.minor 0.7)
#   PID:     12345
```

The CLI also checks Unity's state automatically before sending any command. If Unity is busy (compiling, reloading), it waits for Unity to become responsive. If a modal dialog is open (state `modal`), it fails immediately and shows the dialog; see [Modal Dialogs](#modal-dialogs-macos). If a player build has the main thread (state `building`), the job id, phase and elapsed time are printed with it; see [Build a Player](#build-a-player-long-running). If nothing answers but the project looks open, the state names why: `editor_starting_or_blocked` when the connector never loaded this session, `editor_unresponsive` when a heartbeat from this session stopped answering; see [Startup Dialogs](#startup-dialogs-macos-host). If the editor's process died instead, the state is `crashed`; see [Editor Crashes](#editor-crashes).

### Editor Crashes

```bash
unity-cli status
# The report goes to stderr; stdout stays empty for this case.
# crashed: the Unity editor died for /Users/me/Work/MyGame (heartbeat last seen 30s ago, pid 65279) with no "stopped" marker.
#   Heartbeat: /Users/me/.unity-cli/instances/1f0c….json (state "ready", pid 65279, last beat 30s ago)
#   Process:   pid 65279 is not in this machine's process table, which still lists Unity as pid 12345
#   Crash log: /Users/me/Work/MyGame/mono_crash.1b6a4e0a.0.json (mono_crash, written 25s ago)
#              signal 11 (SIGSEGV); System.NullReferenceException: Object reference not set to an instance of an object
#   Next:      Unity has to be relaunched by the user — no CLI call brings it back. Editor.log rotates when it does, so read the crash log above first.
# Error: crashed (port 8090, pid 65279): the crash report above names the evidence
# (exit 1)
```

A hard crash, a domain reload and a dropped socket all look the same over HTTP: `EOF`. Before reporting
one, the CLI reads what the editor left on disk and says which of the three it is:

| Verdict | Evidence | What the CLI says |
|---------|----------|-------------------|
| `crashed` | the heartbeat's pid is gone from a machine whose process table still lists Unity processes, **or** the heartbeat stopped without writing its `stopped` marker while a crash artifact newer than that beat exists | `editor process <pid> exited during the request (crash)`, the artifact path, and its first exception line |
| reload / port change | the heartbeat is still arriving, but says `compiling`/`reloading`/`refreshing`/`building`/`modal`, or the connector answers on a new port | `Unity reloaded or rebound its port during the request (not a crash)`; run `unity-cli wait` |
| unchanged | none of the above (including a request that merely timed out) | the original `cannot connect to Unity at port …: EOF` |

Crash artifacts are `<project>/mono_crash.*.json` (and the same file under `<project>/Logs`), plus the
macOS host's `~/Library/Logs/DiagnosticReports/Unity-*.ips` and `~/Library/Logs/Unity/Editor-prev.log`.
Only artifacts **newer than the last heartbeat** (or newer than the request start) count, so a dump from
an earlier crash is never blamed on a new silence. `Editor-prev.log` needs crash wording in its tail to
count, because Unity rotates it on clean quits too. At most three artifacts are printed, each with one
bounded line from the file — Mono's `payload`/`threads` structure is parsed defensively, and a truncated
dump still yields its reason.

That pointer matters because `Logs/Editor.log` rotates when Unity restarts: after the crash the dump is
the only surviving record of what killed it.

`status` reports `crashed` and **exits non-zero** — a dead editor is not a state an agent can wait out.
Retry loops stop on that verdict instead of polling a dead editor for their whole timeout. Inside a
container that cannot see the host's processes, a missing pid proves nothing, so only the artifact route
can decide there; the verdict always prints which evidence it used.

### Queued Commands and Server-Side Deadlines

A command that runs on Unity's main thread is queued and picked up by `EditorApplication.update`. While
the editor is compiling, inside a long tool, blocked by a modal dialog, or reloading, that pickup can
be late or never come. The Connector bounds the wait instead of leaving the connection unanswered:

- Each request carries its client budget (`deadline_ms`, the remaining part of `--timeout`). The
  Connector uses it as its queue deadline minus a margin, so the editor answers **before** the client
  gives up. A budget below the 0.5 s floor can still expire client-side first; the floor exists so a
  tiny budget cannot race the connector's answer. Without a declared deadline the Connector's own
  60 s default applies, and a per-call `queue_deadline_ms` parameter overrides both (clamped to
  0.5–600 s).
- The deadline bounds the wait for the main thread to *start* the command, never the command's runtime.
  A running tool is bounded only by your `--timeout`.
- On `beforeAssemblyReload` and on quit, every in-flight request is answered first, and new
  main-thread requests are refused until a listener is up again. A request that had not started is
  answered `domain_reload` (retry it); one whose tool handler was already running is answered
  `reload_during_command` — it ran, only its return value went down with the old domain. A request
  whose tool had already returned keeps its own result: the reload wait gives the unwritten response
  a bounded window to reach the socket. Answers to a quit carry no `data.code`: the editor is gone,
  so there is nothing to wait for, and repeating the command depends on whether it had started.
  Tools marked `RunOffMainThread` (`modal`, `build_status`) stay callable
  throughout.
- A request that was already answered is skipped when its turn comes, so it cannot apply side effects
  after the caller was told it did not run.

```bash
unity-cli --timeout 5000 exec 'return 1;'
# Error: exec was queued but the main thread did not pick it up within 3.9s (waited 4.1s):
#        editor busy (blocked by the running command test_run (42s)) [heartbeat: playing,
#        queue depth: 2]. The command did not run; nothing changed.
# Details: {"code":"main_thread_busy","command":"exec","running_command":"test_run",…}
#   next:      the command did not run and nothing changed; run 'unity-cli wait' until the
#              editor is idle, then repeat it

# widen one call's queue deadline
unity-cli exec --params '{"code":"AssetDatabase.Refresh(); return 1;","queue_deadline_ms":300000}'
```

| `data.code` | what happened | did the command run? |
|-------------|---------------|----------------------|
| `main_thread_busy` | the main thread never picked it up | no — nothing changed |
| `domain_reload` | Unity reloaded while the command was still queued | no — safe to repeat |
| `reload_during_command` | Unity reloaded while the command was running | yes, at least partly — check the state before repeating it |
| `serialization_failed` | the tool's result could not be serialized | yes — its effects stand, only the returned value is lost |

For `reload_during_command` the CLI then reads the heartbeat back and prints where the editor ended up,
so the caller sees `state: Unity is back: Edit mode (heartbeat "ready", port 8090)` instead of having
to ask. Commands that own their own reload wait (`editor play/stop`, `editor refresh --compile`,
`test`, `verify`, `build`, `wait`) report it themselves.

A slow but successful pickup says so in `meta`: `--meta` shows `queue_wait_ms` and `queue_depth` once
the wait exceeded a second. `queue_wait_ms` stops when the tool handler starts, and `total_ms` covers the
whole request, so `queue_wait_ms + compile_ms` can no longer exceed the invocation's wall time (a tool's
own timing such as `exec`'s `compile_ms` sits inside the handler time, which sits inside `total_ms`).

## Global Options

| Flag | Description | Default |
|------|-------------|---------|
| `--port <N>` | Select Unity instance by active heartbeat port | auto |
| `--project <path>` | Select Unity instance by project path | latest |
| `--timeout <ms>` | One budget per invocation, shared by discovery, waiting for a fresh heartbeat, and the request. The remaining budget is sent to the Connector as `deadline_ms`, so it answers a queued command before this deadline does — see [Queued Commands and Server-Side Deadlines](#queued-commands-and-server-side-deadlines). A client deadline is reported as `timed out ... (the editor may still be running it)`, not as a connection failure. `test`, `verify` and `editor refresh --compile` default to 1800000 (30 min); an explicit `0` means no deadline (the Connector still answers a queued command after its 60 s default). That is a compatibility break from older releases, where `--timeout 0` made the heartbeat wait fail at once; a script that relied on the old meaning must pass a small positive value. `wait --timeout` and build polling keep their own budgets. For `editor quit`/`restart` it is also the wait-to-exit and startup budget. | 120000 |
| `--ignore-version-mismatch` | Suppress the version-drift warning (and the refusal for a connector older than 0.7.0) | false |
| `--meta` | Print the response's `meta` (how the call was served, e.g. `exec`'s compile cache state) to stderr. `compile_ms` is the csc subprocess only; `queue_wait_ms` is the wait before the handler started and `total_ms` the whole editor-side span, which contains both. None is a wall-clock total — see [Queued Commands and Server-Side Deadlines](#queued-commands-and-server-side-deadlines) | false |

```bash
# Select an active Unity instance by heartbeat port
unity-cli --port 8091 editor play

# Select by project path when multiple Unity instances are open
unity-cli --project MyGame editor stop

# Hide the warning when the installed connector has a different release version
unity-cli --ignore-version-mismatch exec 'return 1 + 1;'
```

CLI and connector versions do not have to match exactly. A patch difference in the same major.minor
is accepted silently. A different minor or major version is accepted with one warning on stderr that
names both versions, the CLI binary and the fix; diagnostic commands (`status`, `console`,
`compile_diagnostics`, `compile_status`, `api`, `burst_status`, `modal`, `tools`, `tool_describe`,
`wait`, `dialogs`, `list`) never fail on it. (`status` and `dialogs` answer before the gate runs at
all, and `help`/`version`/`update` likewise.) The CLI refuses only a connector older than 0.7.0, the
oldest protocol it speaks.
`status` always prints both versions and the compatibility verdict.

Use `--help` on any command for detailed usage:

```bash
unity-cli editor --help
unity-cli exec --help
unity-cli profiler --help
```

## Writing Custom Tools

Create a static class with `[UnityCliTool]` attribute in any Editor assembly. The Connector discovers it automatically on domain reload.

```csharp
using UnityCliConnector;
using Newtonsoft.Json.Linq;
using UnityEngine;

[UnityCliTool(Name = "spawn", Description = "Spawn an enemy at a position", Group = "gameplay")]
public static class SpawnEnemy
{
    public class Parameters
    {
        [ToolParameter("X world position", Required = true)]
        public float X { get; set; }

        [ToolParameter("Y world position", Required = true)]
        public float Y { get; set; }

        [ToolParameter("Z world position", Required = true)]
        public float Z { get; set; }

        [ToolParameter("Prefab name in Resources folder", DefaultValue = "Enemy")]
        public string Prefab { get; set; }
    }

    public static object HandleCommand(JObject parameters)
    {
        var p = new ToolParams(parameters);
        float x = p.GetFloat("x", 0);
        float y = p.GetFloat("y", 0);
        float z = p.GetFloat("z", 0);
        string prefabName = p.Get("prefab", "Enemy");

        var prefab = Resources.Load<GameObject>(prefabName);
        var instance = Object.Instantiate(prefab, new Vector3(x, y, z), Quaternion.identity);

        return new SuccessResponse("Enemy spawned", new
        {
            name = instance.name,
            position = new { x, y, z }
        });
    }
}
```

Call it directly with flags or JSON:

```bash
unity-cli spawn --x 1 --y 0 --z 5 --prefab Goblin
unity-cli spawn --params '{"x":1,"y":0,"z":5,"prefab":"Goblin"}'
```

**Key points:**

- **Name**: without `Name`, auto-derived from class name (`SpawnEnemy` → `spawn_enemy`, `UITree` → `ui_tree`). With `Name = "spawn"`, the command becomes `unity-cli spawn`.
- **Parameters class**: optional but recommended. `unity-cli list` uses it to expose parameter names, types, descriptions, and required flags — so AI assistants can discover your tool without reading the source.
- **ToolParams**: use `p.Get()`, `p.GetInt()`, `p.GetFloat()`, `p.GetBool()`, `p.GetRaw()` for consistent param reading.
- **Discovery**: `unity-cli list` shows built-in tools first (`group: "built-in"`), then custom tools (`group: "custom"`) detected from the connected Unity project.

**Attribute reference:**

| Attribute | Property | Description |
|---|---|---|
| `[UnityCliTool]` | `Name` | Command name override (default: class name → snake_case) |
| | `Description` | Tool description shown in `list` |
| | `Group` | Group name for categorization |
| `[ToolParameter]` | `Description` | Parameter description (constructor arg) |
| | `Required` | Whether the parameter is required (default: `false`) |
| | `Name` | Parameter name override |
| | `DefaultValue` | Default value hint |

### Rules

- Class must be `static`
- Must have `public static object HandleCommand(JObject parameters)` or `async Task<object>` variant
- Return `SuccessResponse(message, data)` or `ErrorResponse(message)`
- A response's optional third argument, `meta`, reports *how* the call was served (e.g. `exec`'s
  `compile`/`compile_ms`). It stays out of `data`, so callers parsing a tool's result see the same shape
  either way; the CLI prints it to stderr only with `--meta`
- Add a `Parameters` nested class with `[ToolParameter]` attributes for discoverability
- Class name is auto-converted to snake_case for the command name
- Override with `[UnityCliTool(Name = "my_name")]` if needed
- Runs on Unity main thread, so all Unity APIs are safe to call
- `[UnityCliTool(RunOffMainThread = true)]` runs it on the HTTP request thread instead — past the command
  lock and the compilation gate, so it still answers while a modal dialog or a player build has the main
  thread. Such a handler may call no Unity API except through a thread-safe path of its own
- Discovered automatically on Editor start and after every script recompilation
- Duplicate tool names are detected and logged as errors — only the first discovered handler is used

## Multiple Unity Instances

When multiple Unity Editors are open, each registers on a different port (8090, 8091, ...):

```bash
# See all running instances
ls ~/.unity-cli/instances/

# Select by project path
unity-cli --project MyGame editor play

# Select by active heartbeat port
unity-cli --port 8091 editor play

# Default: uses the most recently registered instance
unity-cli editor play
```

## Compared to MCP

| | MCP | unity-cli |
|---|-----|-----------|
| **Install** | Python + uv + FastMCP + config JSON | Single binary |
| **Dependencies** | Python runtime, WebSocket relay | None |
| **Protocol** | JSON-RPC 2.0 over stdio + WebSocket | Direct HTTP POST |
| **Setup** | Generate MCP config, restart AI tool | Add Unity package, done |
| **Reconnection** | Complex reconnect logic for domain reloads | Stateless per request |
| **Compatibility** | MCP-compatible clients only | Anything with a shell |
| **Custom tools** | Same `[Attribute]` + `HandleCommand` pattern | Same |

## Author

Created by **DevBookOfArray**

[![YouTube](https://img.shields.io/badge/YouTube-DevBookOfArray-red?logo=youtube&logoColor=white)](https://www.youtube.com/@DevBookOfArray)
[![GitHub](https://img.shields.io/badge/GitHub-youngwoocho02-181717?logo=github)](https://github.com/youngwoocho02)

## License

MIT
