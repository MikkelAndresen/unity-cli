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

Before compiling or reloading, the Connector records the state (`compiling`, `reloading`) to the instance file. When the main thread freezes, the timestamp stops updating. The CLI detects this and waits for a fresh timestamp before sending commands. A player build holds the main thread for minutes without compiling or reloading anything; the Connector keeps beating as `building` from the build's own writer thread (see [Build a Player](#build-a-player-long-running)), so a building editor is not mistaken for a wedged one.

## Built-in Commands

| Command | Description |
|---------|-------------|
| `editor` | Play/stop/pause/refresh the Unity Editor |
| `input` | Inject new-Input-System keyboard/mouse/gamepad/touch state in Play mode and pump the player loop |
| `console` | Read, filter, and clear console logs (newest 50 entries, capped stack traces) |
| `exec` | Run arbitrary C# code inside Unity |
| `test` | Run EditMode/PlayMode tests |
| `verify` | Compile, list errors in your code, run tests: one call, brief output |
| `build` | Start a player build as a job, watch progress, report the BuildReport |
| `menu` | Execute any Unity menu item by path |
| `reserialize` | Re-serialize assets through Unity's serializer |
| `clear_burst_cache` | Delete `Library/BurstCache` so Burst recompiles from scratch |
| `screenshot` | Capture scene/game view as PNG |
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

# Enter play mode and wait until fully loaded
unity-cli editor play --wait

# Stop play mode
unity-cli editor stop

# Toggle pause (only works during play mode)
unity-cli editor pause

# Refresh assets (blocked in play mode unless --force is set)
unity-cli editor refresh

# Refresh and recompile scripts (also blocked in play mode unless --force is set)
unity-cli editor refresh --compile

# Force refresh while in play mode
unity-cli editor refresh --force
```

`editor refresh --compile` waits for the compilation and answers with one report instead of a bare
`refresh_triggered`: `compile_requested`, `compile_succeeded`, `error_count`, `errors[]`
(file/line/col/severity/code/message, from the connector's `compile_diagnostics` tool), `timed_out`
and `elapsed_ms`. `timed_out: true` means the wait gave up after 5 minutes — not that the compile
failed. `nothing_to_compile: true` means no compilation was seen to run after the request, in which
case `compile_succeeded` describes the editor's current compile state rather than a compile this
command caused. The proof a compile actually ran is the connector's compile-finished clock: the
refresh reply reports it at request time and the heartbeat is polled until that number moves, so a
heartbeat that was already `ready` is never mistaken for a finished compile. Exit code is 1 when the
compile failed or timed out, and the report is printed either way. `--compile` clears the console
first so an error left over from an earlier compile is not reported as this one's; `--keep-console`
skips the clear. Connectors without the compile-finished clock fall back to the heartbeat's
`compileErrors` flag and say so in `notes`.

### Input Injection (Play mode)

```bash
# Enter play mode first — injection needs a running player
unity-cli editor play --wait

# Keyboard: Key enum names (aliases: ctrl, alt, shift, cmd, esc, return, up, down)
unity-cli input key Space            # default mode is press
unity-cli input key Space press
unity-cli input key Space release
unity-cli input key W tap --hold_ms 200

# Mouse: Game view pixels (0,0 is the top-left of the Game View), deltas, buttons, wheel
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

# Touch: virtual Touchscreen added on demand
unity-cli input touch --x 64 --y 128 --mode tap

# What the Input System currently sees, plus focus and InputSettings state
unity-cli input state

# Release injected state, remove the virtual devices the tool added, restore InputSettings
unity-cli input reset
```

`input` writes device state instead of sending OS keystrokes: it queues a `StateEvent` built from the
device's current state with one control overwritten (so other keys, the mouse position and the other
stick survive), runs `InputSystem.Update()`, then pumps the player loop
(`EditorApplication.QueuePlayerLoopUpdate` plus a repaint) so game code sees the change in the same
call. Positional shorthand works (`unity-cli input key Space press` — the key and mode are taken by
content, so either order reads the same) and flags always win over positional values. Only `state`
and `reset` work outside play mode.

Injected events are visible to the **new Input System** (`UnityEngine.InputSystem`, `InputAction`s on
it) and never to legacy `UnityEngine.Input` or IMGUI `Event` code; every injection response repeats
that in `legacy_input_note`.

Response fields:

| Field | Meaning |
|-------|---------|
| `consumed` | The device state now matches the request — `isPressed`/`value` read back after the pump |
| `fresh_press_frames`, `fresh_release_frames` | How many input updates reported the control as freshly pressed/released. This is the view game code gets in `Update()` via `wasPressedThisFrame`/`wasReleasedThisFrame` |
| `held_state`, `state` | The control read back while held, and after the final pump |
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

# No caps at all
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

`entries` is oldest-first and holds the newest entries the caps allowed; `entries_total` counts the
entries matching `--type`, before any cap. The list being read is the Console window's own list: with
the window's Log/Warning toggles off those entries are not in it at all, so `--type log` answers empty
in an editor that logged plenty.

Before the caps release `console` returned a bare array of
all matching entries and `stacktrace=user` filtered with a hardcoded list of names that Burst and
Entities traces slipped through, which is what a single call costing 35 KB came from.

### Execute C# Code

Run arbitrary C# code inside the Unity Editor at runtime. This is the most powerful command — it gives you full access to UnityEngine, UnityEditor, ECS, and every loaded assembly. No need to write a custom tool for one-off queries or mutations.

Use `return` to get output. Common namespaces are included by default. Add `--usings` only for project-specific types (e.g. `Unity.Entities`). The csc compiler and dotnet runtime are auto-detected; if detection fails, specify manually with `--csc <path>` or `--dotnet <path>`.

```bash
unity-cli exec "return Application.dataPath;"
unity-cli exec "return EditorSceneManager.GetActiveScene().name;"
unity-cli exec "return World.All.Count;" --usings Unity.Entities

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

Because `exec` compiles and runs real C#, it can do anything a custom tool can — inspect ECS entities, modify assets, call internal APIs, run editor utilities. For AI agents, this means **zero-friction access to Unity's entire runtime** without writing a single line of tool code. Piping via stdin avoids shell escaping headaches with complex code.

### Menu Items

```bash
# Execute any Unity menu item by path
unity-cli menu "File/Save Project"
unity-cli menu "Assets/Refresh"
unity-cli menu "Window/General/Console"
```

Note: `File/Quit` is blocked for safety.

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

This is what makes text-based asset editing safe. Without it, a single misplaced YAML field can break a prefab with no visible error until runtime. With it, **AI agents can confidently modify any Unity asset through plain text** — add components to prefabs, adjust scene hierarchies, change material properties — and know the result will load correctly.

### Burst Cache

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

# Filter by test name (substring match)
unity-cli test --filter MyTestClass

# Only tests in these assemblies (comma-separated)
unity-cli test --assembly MyGame.Tests.Editor
```

Requires the Unity Test Framework package. Tests are blocked by default when any open scene has unsaved changes. Use `--auto-save-scenes` to save them first, or `--allow-dirty-scenes` to run anyway. PlayMode tests trigger a domain reload — the CLI polls for results automatically.

### Verify

Check that the project compiles and its tests pass in one call, with output brief enough for an agent's context.

```bash
# Compile, list errors in your code, then run EditMode tests
unity-cli verify

# Stop after the compile
unity-cli verify --no-tests

# Only some tests (also --filter, --mode PlayMode)
unity-cli verify --assembly MyGame.Tests.Editor

# One JSON object instead of text
unity-cli verify --json
```

```
compile: 1 error(s)
  Assets/Scripts/Player.cs:42:13 CS0103 The name 'speed' does not exist in the current context
```

`verify` clears the console first (a Burst error stays there after its code changes; Burst logs a live one again on the next compile; `--keep-console` skips the clear), refreshes and compiles, and lists this compile's errors from the connector's `compile_diagnostics` tool, leaving out `Library/` and `PackageCache/` unless nothing else failed. It then saves dirty open scenes and runs the tests, printing `passed/total` and the first line of each failure (`--max` caps both lists, default 20). Exit code 0 means it compiles and every test passed; 1 means compile errors, failing tests or no test ran; 2 means the editor couldn't answer: not running, timed out, or blocked by a modal dialog, which is named along with the command that answers it. Connectors before 0.7.0 have no `compile_diagnostics` and ignore `--assembly`; `verify` then lists console errors and says so.

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

When no heartbeat answers but the project's `Temp/UnityLockfile` (or a heartbeat that went silent)
says an editor is open, every command reports the state `editor_starting_or_blocked` instead of
"no Unity instance found", naming the evidence and its age:

```
editor_starting_or_blocked: Unity appears open for /Users/me/Work/MyGame (Temp/UnityLockfile, age 42s)
but the connector has not loaded — it is still importing/compiling, or a startup dialog (license,
safe mode, "project already open", package resolution) is waiting.
  Lockfile: /Users/me/Work/MyGame/Temp/UnityLockfile (written 2026-11-04T04:26:32Z)
  Next:     run 'unity-cli dialogs' on the macOS host to read the dialog's title and buttons,
            or ask the user to look at the editor.
```

Windows are narrowed to the requested project by matching the project folder name in the window
title; when nothing matches, every Unity window is listed, because startup dialogs rarely name the
project. If Accessibility is missing, the error names the exact grant (System Settings → Privacy &
Security → Accessibility, for `/usr/bin/osascript` and the app running the command). Button matching
is exact-title first, then case- and apostrophe-insensitive, and an ambiguous title is reported
instead of clicking a possibly-wrong dialog.

Windows Unity draws itself (IMGUI panels, some Safe Mode prompts) expose no native buttons to
Accessibility: `dialogs` lists their title and texts, but there is nothing to click, and only a
human can answer them. Static texts are capped at 12 per window (`texts_total` reports the real
count) so an editor window does not flood the output.

### List Tools

```bash
# Show all available tools (built-in + project custom) with parameter schemas
unity-cli list
```

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
#   PID:     12345
```

The CLI also checks Unity's state automatically before sending any command. If Unity is busy (compiling, reloading), it waits for Unity to become responsive. If a modal dialog is open (state `modal`), it fails immediately and shows the dialog; see [Modal Dialogs](#modal-dialogs-macos). If a player build has the main thread (state `building`), the job id, phase and elapsed time are printed with it; see [Build a Player](#build-a-player-long-running). If nothing answers but the project looks open (`Temp/UnityLockfile`, or a heartbeat that went silent), the state is `editor_starting_or_blocked`; see [Startup Dialogs](#startup-dialogs-macos-host).

## Global Options

| Flag | Description | Default |
|------|-------------|---------|
| `--port <N>` | Select Unity instance by active heartbeat port | auto |
| `--project <path>` | Select Unity instance by project path | latest |
| `--timeout <ms>` | HTTP request timeout | 120000 |
| `--ignore-version-mismatch` | Run even when CLI and connector versions differ | false |

```bash
# Select an active Unity instance by heartbeat port
unity-cli --port 8091 editor play

# Select by project path when multiple Unity instances are open
unity-cli --project MyGame editor stop

# Run anyway when the installed connector has a different release version
unity-cli --ignore-version-mismatch status
```

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
