package cmd

import (
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

var Version = "dev"

var (
	flagPort                  int
	flagProject               string
	flagTimeout               int
	flagIgnoreVersionMismatch bool
)

func Execute() error {
	flag.IntVar(&flagPort, "port", 0, "Select Unity instance by active heartbeat port")
	flag.StringVar(&flagProject, "project", "", "Select Unity instance by project path")
	flag.IntVar(&flagTimeout, "timeout", 120000, "Request timeout in milliseconds")
	flag.BoolVar(&flagIgnoreVersionMismatch, "ignore-version-mismatch", false, "Run even when CLI and connector versions differ")

	flag.Usage = func() { printHelp() }

	args := os.Args[1:]
	flagArgs, cmdArgs := splitArgs(args)
	if err := flag.CommandLine.Parse(flagArgs); err != nil {
		fmt.Fprintf(os.Stderr, "flag parse error: %v\n", err)
		os.Exit(1)
	}

	if len(cmdArgs) == 0 {
		printHelp()
		return nil
	}

	category := cmdArgs[0]
	subArgs := cmdArgs[1:]

	// --help / -h on any command
	for _, a := range subArgs {
		if a == "--help" || a == "-h" {
			printTopicHelp(category)
			return nil
		}
	}

	switch category {
	case "help", "--help", "-h":
		if len(subArgs) > 0 {
			printTopicHelp(subArgs[0])
		} else {
			printHelp()
		}
		return nil
	case "version", "--version", "-v":
		fmt.Println("unity-cli " + Version)
		return nil
	case "update":
		return updateCmd(subArgs)
	case "status":
		inst, err := discoverStatusInstance(flagProject, flagPort)
		if err != nil {
			return annotateNoEditor(err, flagProject, flagPort)
		}
		return statusCmd(inst)
	case "dialogs":
		// Runs with no connector and no heartbeat at all: startup dialogs are exactly the
		// window where neither exists yet.
		return dialogsCmd(subArgs)
	}

	// verify reports an editor that can't answer as exit code 2, not as a CLI error.
	noEditor := func(err error) error {
		if category == "verify" {
			os.Exit(verifyExit(os.Stdout, err))
		}
		return err
	}

	inst, err := client.DiscoverInstance(flagProject, flagPort)
	if err != nil {
		return noEditor(annotateNoEditor(err, flagProject, flagPort))
	}

	targetProject := flagProject
	if flagPort == 0 && targetProject == "" {
		targetProject = inst.ProjectPath
	}

	resolve := func() (*client.Instance, error) {
		if flagPort > 0 {
			return client.DiscoverInstance("", flagPort)
		}
		return client.DiscoverInstance(targetProject, 0)
	}

	alive, err := waitForAlive(resolve, flagTimeout)
	if err != nil {
		return noEditor(annotateNoEditor(err, targetProject, flagPort))
	}
	if err := checkConnectorVersion(alive, Version, flagIgnoreVersionMismatch); err != nil {
		return noEditor(err)
	}
	if category != modalCommand && isModal(alive) {
		return noEditor(modalBlockedError(alive, category, false))
	}

	timeout := flagTimeout
	send := sendWatchingModal(func(command string, params interface{}) (*client.CommandResponse, error) {
		inst, err := resolve()
		if err != nil {
			return nil, err
		}
		if err := checkConnectorVersion(inst, Version, flagIgnoreVersionMismatch); err != nil {
			return nil, err
		}
		return client.Send(inst, command, params, timeout)
	}, resolve)

	var resp *client.CommandResponse

	switch category {
	case "editor":
		resp, err = editorCmd(subArgs, send, resolve)
	case "test":
		currentInst, resolveErr := resolve()
		if resolveErr != nil {
			return resolveErr
		}
		if err := checkConnectorVersion(currentInst, Version, flagIgnoreVersionMismatch); err != nil {
			return err
		}
		testSend := sendWatchingModal(func(command string, params interface{}) (*client.CommandResponse, error) {
			return client.Send(currentInst, command, params, 0)
		}, resolve)
		resp, err = testCmd(subArgs, testSend, currentInst.Port)
	case "build":
		// Polling a build must survive the build itself: the main thread is inside BuildPlayer for
		// the whole run, so the wait runs on build_status (served off the main thread) with its own
		// short request timeout rather than the global --timeout.
		buildPollSend := sendWatchingModal(func(command string, params interface{}) (*client.CommandResponse, error) {
			currentInst, err := resolve()
			if err != nil {
				return nil, err
			}
			return client.Send(currentInst, command, params, buildStatusRequestMs)
		}, resolve)
		resp, err = buildCmd(subArgs, send, buildPollSend, resolve, os.Stderr)
	case "verify":
		currentInst, resolveErr := resolve()
		if resolveErr != nil {
			return noEditor(resolveErr)
		}
		testSend := sendWatchingModal(func(command string, params interface{}) (*client.CommandResponse, error) {
			return client.Send(currentInst, command, params, 0)
		}, resolve)
		os.Exit(verifyCmd(subArgs, send, testSend, resolve, currentInst.Port, os.Stdout))
	case "exec":
		subArgs = readStdinIfPiped(subArgs)
		var params map[string]interface{}
		params, err = buildParams(subArgs, nil)
		if err == nil {
			// The connector waits out --await_timeout before answering. Give it the same
			// budget plus compile slack so the client is never the one that gives up
			// first — otherwise a long await reports a Go deadline instead of the
			// connector's timed_out marker.
			if t := execRequestTimeout(flagTimeout, params); t > timeout {
				timeout = t
			}
			resp, err = send("exec", params)
		}
	default:
		var params map[string]interface{}
		params, err = buildParams(subArgs, nil)
		if err == nil {
			resp, err = send(category, params)
		}
	}

	if err != nil {
		// A command may answer with a report and still fail (editor refresh --compile prints its
		// compile report, then exits non-zero), so a failing exit code must not hide the JSON.
		if resp != nil {
			printResponse(resp)
		}
		return err
	}

	printResponse(resp)

	if !resp.Success {
		os.Exit(1)
	}

	return nil
}

// sendFn is the function signature for sending a command to Unity.
// Injected into each command function so they can be tested without a real Unity connection.
type sendFn func(command string, params interface{}) (*client.CommandResponse, error)

func printResponse(resp *client.CommandResponse) {
	if !resp.Success {
		msg := resp.Message
		if msg == "" {
			msg = "unknown error"
		}
		if len(resp.Data) > 0 && string(resp.Data) != "null" {
			fmt.Fprintf(os.Stderr, "Error: %s\nDetails: %s\n", msg, string(resp.Data))
		} else {
			fmt.Fprintf(os.Stderr, "Error: %s\n", msg)
		}
		return
	}

	if len(resp.Data) > 0 && string(resp.Data) != "null" {
		var pretty interface{}
		if json.Unmarshal(resp.Data, &pretty) == nil {
			// If data is a plain string, print it raw (preserves newlines for tree output etc.)
			if s, ok := pretty.(string); ok {
				fmt.Println(s)
			} else {
				b, _ := json.MarshalIndent(pretty, "", "  ")
				fmt.Println(string(b))
			}
		} else {
			fmt.Println(string(resp.Data))
		}
	} else if resp.Message != "" {
		fmt.Println(resp.Message)
	}
}

// parseSubFlags parses --key value and --flag (boolean) pairs from subcommand args.
// Non-flag args (no "--" prefix) are silently ignored.
func parseSubFlags(args []string) map[string]string {
	flags := map[string]string{}
	for i := 0; i < len(args); i++ {
		a := args[i]
		if strings.HasPrefix(a, "--") {
			key := a[2:]
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
				flags[key] = args[i+1]
				i++
			} else {
				flags[key] = "true"
			}
		}
	}
	return flags
}

// execRequestTimeout widens the HTTP request timeout so it outlasts the connector's
// own await budget (--await_timeout). A budget of 0 or less means the connector waits
// forever, which no client-side number can cover — leave the flag timeout alone.
func execRequestTimeout(flagTimeoutMs int, params map[string]interface{}) int {
	raw, ok := params["await_timeout"]
	if !ok {
		raw, ok = params["awaitTimeout"]
	}
	if !ok {
		return flagTimeoutMs
	}

	ms := 0
	switch v := raw.(type) {
	case int:
		ms = v
	case float64:
		ms = int(v)
	case string:
		if n, convErr := strconv.Atoi(v); convErr == nil {
			ms = n
		}
	}
	if ms <= 0 || flagTimeoutMs <= 0 {
		// 0 or less means "no client-side deadline" on either side.
		return flagTimeoutMs
	}

	const compileSlackMs = 30000
	if want := ms + compileSlackMs; want > flagTimeoutMs {
		return want
	}
	return flagTimeoutMs
}

// buildParams parses --flag value pairs and positional args from args and merges with base params.
func buildParams(args []string, base map[string]interface{}) (map[string]interface{}, error) {
	params := map[string]interface{}{}
	for k, v := range base {
		params[k] = v
	}

	var positional []string
	flags := map[string]string{}
	for i := 0; i < len(args); i++ {
		a := args[i]
		if strings.HasPrefix(a, "--") {
			key := a[2:]
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
				flags[key] = args[i+1]
				i++
			} else {
				flags[key] = "true"
			}
		} else {
			positional = append(positional, a)
		}
	}

	if raw, ok := flags["params"]; ok {
		if jsonErr := json.Unmarshal([]byte(raw), &params); jsonErr != nil {
			return nil, fmt.Errorf("invalid JSON in --params: %w", jsonErr)
		}
	}
	for k, v := range flags {
		if k == "params" {
			continue
		}
		if _, exists := params[k]; exists {
			continue
		}
		if n, err := strconv.Atoi(v); err == nil {
			params[k] = n
		} else if v == "true" {
			params[k] = true
		} else if v == "false" {
			params[k] = false
		} else {
			params[k] = v
		}
	}

	if len(positional) > 0 {
		params["args"] = positional
	}

	return params, nil
}

// readStdinIfPiped reads stdin when piped and prepends it as the first positional arg.
func readStdinIfPiped(args []string) []string {
	info, err := os.Stdin.Stat()
	if err != nil {
		return args
	}
	if info.Mode()&os.ModeCharDevice != 0 {
		return args // interactive terminal, not piped
	}
	data, err := io.ReadAll(os.Stdin)
	if err != nil || len(data) == 0 {
		return args
	}
	code := strings.TrimRight(string(data), "\n\r")
	return append([]string{code}, args...)
}

// splitArgs separates global flags from subcommand args.
// Global flags must be parsed by flag.CommandLine before the subcommand runs.
func splitArgs(args []string) (flags, commands []string) {
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--ignore-version-mismatch":
			flags = append(flags, args[i])
		case "--port", "--project", "--timeout":
			flags = append(flags, args[i])
			if i+1 < len(args) {
				i++
				flags = append(flags, args[i])
			}
		default:
			commands = append(commands, args[i])
		}
	}
	return
}

func printHelp() {
	fmt.Print(`unity-cli ` + Version + ` — Control Unity Editor from the command line

Usage: unity-cli <command> [subcommand] [options]

Editor Control:
  editor play [--wait]          Enter play mode (--wait blocks until fully entered)
  editor stop                   Exit play mode
  editor pause                  Toggle pause/resume (play mode only)
  editor refresh [--force]      Refresh asset database (blocked in play mode unless forced)
  editor refresh --compile      Recompile scripts and wait until done

Input Injection (Play mode, new Input System only):
  input key <name> [press|release|tap]   e.g. input key Space press
  input mouse --x 100 --y 200            Game view pixels; --button left --mode click
  input gamepad|axis --control leftStick --x 1 --y 0
  input touch --x 64 --y 128 --mode tap
  input state                     Keys/mouse/gamepads as the Input System currently sees them
  input reset                     Release injected state, drop virtual devices, restore settings

Console:
  console                       Newest 50 entries, capped stack traces (default)
  console --lines 20            Newest N entries
  console --type error,warning,log   Filter by log types (comma-separated)
  console --stacktrace full     Stack trace: none, user (default), full
  console --full                No caps: every entry, frame and character
  console --clear               Clear console

Execute C#:
  exec "<code>"                 Run C# code in Unity (return required for output)
  echo '<code>' | exec          Pipe code via stdin (avoids shell escaping)
  exec "<code>" --usings x,y    Add extra using directives
  exec "<code>" --await_timeout 60000   Wait longer for a Task/coroutine to finish

  Examples:
    exec "Time.time"
    exec "GameObject.FindObjectsOfType<Camera>().Length"
    exec "var go = new GameObject(\"Test\"); return go.name;"
    exec "await Task.Delay(100); return EditorApplication.timeSinceStartup;"

Menu:
  menu "<path>"                 Execute Unity menu item by path

  Examples:
    menu "File/Save Project"
    menu "Assets/Refresh"

Screenshot:
  screenshot                          Capture scene view (default)
  screenshot --view game              Capture game view
  screenshot --output_path <path>     Custom output path

Reserialize:
  reserialize [path...]          Force reserialize (no args = entire project)

  Examples:
    reserialize                                                    Reserialize entire project
    reserialize Assets/Scenes/Main.unity
    reserialize Assets/Prefabs/A.prefab Assets/Prefabs/B.prefab

Burst:
  clear_burst_cache              Delete Library/BurstCache (Burst recompiles on next use)
  clear_burst_cache --scope jit  Only the JIT library cache, keep AOT settings hashes
  clear_burst_cache --dry_run    List files and size without deleting
  burst_status                   Is Burst done compiling, what compiled, what failed (BCxxxx)
  burst_status --wait 60         Wait up to 60s for Burst to finish, then report

  Examples:
    clear_burst_cache
    clear_burst_cache --dry_run
    clear_burst_cache --scope jit
    burst_status --wait 60
    burst_status --assembly MyGame.Runtime --list

Tests:
  test                            Run EditMode tests (default)
  test --mode PlayMode            Run PlayMode tests
  test --filter <name>            Filter by namespace, class, or full test name
  test --allow-dirty-scenes       Run even when open scenes have unsaved changes
  test --auto-save-scenes         Save dirty open scenes before running tests
  test --assembly <a,b>           Only tests in these assemblies

Verify (compile + tests, brief output for agents):
  verify                          Compile, list errors in your code, run EditMode tests
  verify --no-tests               Stop after the compile
  verify --assembly <a,b>         Only these test assemblies (also --filter, --mode)
  verify --no-burst               Skip the Burst step

Build (long-running; the editor stays observable while it runs):
  build --profile <name> --output <path>      Start a player build job, watch progress, exit 0/1
  build --target StandaloneOSX --output <p>   Build by BuildTarget instead of a BuildProfile
  build --no-wait ...                         Return the job id right away
  build status [id]                           Poll one job once (default: the newest)

  Examples:
    build --profile macOS_dev --output Builds/Mac/App.app
    build --profile macOS --output Builds/Mac/App.app --development
    build --target StandaloneLinux64 --output Builds/Linux/App --scenes Assets/Main.unity
    build status

Profiler:
  profiler hierarchy              Top-level profiler samples (last frame)
  profiler hierarchy --depth 5    Recursive drill-down (0=unlimited)
  profiler hierarchy --root Name  Set root by name (substring match)
  profiler hierarchy --frames 30  Average over last 30 frames
  profiler hierarchy --parent 5   Drill into item by ID
  profiler hierarchy --min 0.5    Filter items below 0.5ms
  profiler hierarchy --sort self  Sort by self time
  profiler enable                Start profiler recording
  profiler disable               Stop profiler recording
  profiler status                Show profiler state
  profiler clear                 Clear all captured frames

Input Injection (Play mode):
  input key <name> [press|release|tap]   Keyboard key by Key enum name (Space, W, LeftShift)
  input mouse [--x --y | --dx --dy] [--button left|right|middle|forward|back] [--mode ...]
  input gamepad|axis --control <name> [--x --y | --value] [--mode set|press|release|tap]
  input touch [--x --y] [--mode began|moved|stationary|ended|tap] [--index N]
  input state                      Report pressed keys, mouse position, gamepads, focus, settings
  input reset                      Release everything, remove virtual devices, restore settings

  Injected state reaches the NEW Input System only; UnityEngine.Input and IMGUI Event code
  will not see it. Requires play mode except for state/reset. See 'unity-cli input --help'.

Modal Dialogs (macOS):
  modal                         Show the modal dialog blocking the editor
  modal answer --button <title> Click a button by title (or --index <n>)

Startup Dialogs (macOS host, no connector needed):
  dialogs                       List Unity's windows, their texts and button names (JSON)
  dialogs --click "<title>"    Press a button by title (--window "<title>" to pick the window)
  Use this when status says editor_starting_or_blocked: the editor is open but its connector
  has not loaded, so 'modal' cannot see anything. Needs macOS + Accessibility permission.

Custom Tools:
  list                          List all registered tools with parameter schemas
  <name>                        Call a custom tool directly
  <name> --params '{"k":"v"}'   Call with JSON parameters

Status:
  status                        Show Unity Editor state (ready, compiling, etc.)
                                editor_starting_or_blocked means Unity is open but the connector
                                has not loaded; see "unity-cli dialogs --help"

Update:
  update                        Update to the latest version
  update --check                Check for updates without installing

Global Options:
  --port <N>          Select Unity instance by active heartbeat port
  --project <path>    Select Unity instance by project path
  --timeout <ms>      Request timeout in ms (default: 120000)
  --ignore-version-mismatch
                      Run even when CLI and connector versions differ

Use "unity-cli <command> --help" for more information about a command.

Notes:
  - Unity must be open with the Connector package installed
  - Multiple Unity instances: use --port or --project to select
  - Custom tools: any [UnityCliTool] class is auto-discovered
  - Run 'list' to see all available tools
`)
}

func printTopicHelp(topic string) {
	switch topic {
	case "input":
		fmt.Print(`Usage: unity-cli input <key|mouse|gamepad|axis|touch|state|reset> [options]

Injects device state into the new Input System (com.unity.inputsystem) and pumps the player loop
so the events are consumed inside the same call. No OS keystrokes are involved.

Actions:
  key <name> [press|release|tap]   Key enum name: Space, W, A, LeftShift, Enter, Digit1 …
                                   Aliases: ctrl, alt, shift, cmd, esc, return, up, down.
                                   Default mode: press. --hold_ms / --hold_frames tune a tap.
  mouse                            --x/--y (Game view pixels, 0,0 = top-left), --dx/--dy,
                                   --button left|right|middle|forward|back, --scroll_x/--scroll_y,
                                   --mode press|release|tap|click|move|set.
  gamepad / axis                   --control leftStick|rightStick|dpad|leftTrigger|buttonSouth|…
                                   --x/--y for sticks, --value for buttons/triggers (default 1),
                                   --mode set|press|release|tap. Adds a virtual Gamepad on demand.
  touch                            --x/--y, --mode began|moved|stationary|ended|tap, --index N.
                                   Adds a virtual Touchscreen on demand.
  state                            Read-only: pressed keys, mouse position/buttons/scroll,
                                   gamepad axes, touch phase, focus flags and InputSettings.
  reset                            Release injected state, remove the virtual devices the tool
                                   added, restore the InputSettings it changed.

Response fields:
  consumed                The device state now matches the request (isPressed / value read back).
  fresh_press_frames      Input updates that saw the control as freshly pressed — what game code
                          in Update() sees through wasPressedThisFrame. The was_pressed_this_frame
                          field of 'state' is read after the tool's own input update, so it is
                          usually false.
  held_state / state      The control read back while held and after the final pump.
  focus.*                game_view_focused (a Game View window has editor focus),
                          application_is_focused (what the Input System's own focus gating uses),
                          editor_focused (Unity is the frontmost application).
  settings.*              InputSettings in effect, the values saved before the override, and
                          held_controls — what is still keeping the override in place.

Injected events are visible to the new Input System only. Code reading UnityEngine.Input or
IMGUI Event sees nothing; every injection response repeats this in legacy_input_note.
key/mouse/gamepad (or its alias axis)/touch require play mode; state and reset work any time. While
the tool holds injected state it sets editorInputBehaviorInPlayMode=AllDeviceInputAlwaysGoesToGameView
and backgroundBehavior=IgnoreFocus, because with the defaults the Input System defers or drops
keyboard/pointer events while the Game View lacks focus. The values are restored when nothing is
held anymore, on 'input reset', and when play mode exits.

Examples:
  unity-cli editor play --wait
  unity-cli input key Space press
  unity-cli input state
  unity-cli input key Space release
  unity-cli input key W tap --hold_ms 200
  unity-cli input mouse --x 100 --y 200
  unity-cli input mouse --button left --mode click
  unity-cli input gamepad --control leftStick --x 1 --y 0
  unity-cli input reset
  unity-cli editor stop
`)
	case "editor":
		fmt.Print(`Usage: unity-cli editor <play|stop|pause|refresh> [options]

Subcommands:
  play [--wait]       Enter play mode
                      --wait blocks until Unity fully enters play mode.
                      Without --wait, returns immediately after requesting.
  stop                Exit play mode. No effect if not playing.
  pause               Toggle pause. Only works during play mode.
  refresh             Refresh AssetDatabase (reimport changed assets).
                      Blocked in play mode unless --force is set.
    --compile         Recompile scripts and wait until compilation finishes.
                      Answers with one report: compile_requested, compile_succeeded,
                      error_count, errors[], timed_out, elapsed_ms, plus nothing_to_compile
                      when the scripts were already up to date. Exit 1 when the compile
                      failed or timed out; the report is still printed.
    --keep-console    With --compile, don't clear the console first (the default clear keeps
                      stale errors from a previous compile out of the report).
    --force           Allow refresh during play mode and force asset update.

Examples:
  unity-cli editor play --wait
  unity-cli editor stop
  unity-cli editor refresh --compile
  unity-cli editor refresh --force
`)
	case "console":
		fmt.Print(`Usage: unity-cli console [options]

Read Unity console log entries.

Small by default so a chatty editor fits in one tool output: the newest 50 entries, at most 6
stack frames each, at most 1000 characters per entry and 15 KB of entry text in total. The reply
says what was cut (truncated, entries_total/entries_returned/entries_omitted, frames_omitted).

Options:
  --lines <N>          Newest N entries (default 50; 0 = no limit)
  --type <types>       Comma-separated log types: error, warning, log (default: error,warning,log)
  --stacktrace <mode>  none: first line only
                        user: user-code frames only, Unity/runtime frames collapsed (default)
                        full: every frame, still capped by --frames unless --full
  --frames <N>         Stack frames kept per entry (default 6; 0 = none, -1 = all)
  --entry_chars <N>    Max characters per entry (default 1000; 0 = no limit)
  --max_bytes <N>      Max total bytes of entry text (default 15360; 0 = no limit)
  --full               Remove every cap
  --clear              Clear console

Examples:
  unity-cli console
  unity-cli console --lines 20 --type error,warning,log
  unity-cli console --stacktrace none --type error
  unity-cli console --type error --stacktrace full --frames -1
  unity-cli console --full
  unity-cli console --clear
`)
	case "exec":
		fmt.Print(`Usage: unity-cli exec "<code>" [options]

Execute C# code inside Unity Editor. Full access to UnityEngine,
UnityEditor, and all loaded assemblies.

Use 'return' to get output. Add --usings for types outside default namespaces.

Options:
  --usings <ns1,ns2>   Add extra using directives
  --await_timeout <ms> Max ms to await a returned Task / pump a returned coroutine
                       (default 30000; 0 or negative waits forever)
  --csc <path>         Path to csc compiler (csc.dll or csc.exe). Auto-detected if omitted.
  --dotnet <path>      Path to dotnet runtime. Auto-detected if omitted.

Default usings: System, System.Collections, System.Collections.Generic,
  System.IO, System.Linq, System.Reflection, System.Threading.Tasks, UnityEngine,
  UnityEngine.SceneManagement, UnityEditor, UnityEditor.SceneManagement,
  UnityEditorInternal

Examples:
  unity-cli exec "return 1+1;"
  unity-cli exec "return Application.dataPath;"
  echo 'return EditorSceneManager.GetActiveScene().name;' | unity-cli exec
  echo 'Debug.Log("hello"); return null;' | unity-cli exec
  unity-cli exec "return World.All.Count;" --usings Unity.Entities
  unity-cli exec "await Task.Delay(100); return 1;"
  unity-cli exec "return Task.Run(() => AssetDatabase.Refresh());" --await_timeout 60000

Async:
  'await' works in the body, and a Task you return is awaited for you — the response
  carries its result, not the Task object. Nothing blocks the editor's main thread,
  so work that needs the editor to keep ticking still completes.

  A body using 'yield return' runs as a coroutine: 'yield return null' waits one
  editor update, a yielded Task is awaited, and any other yielded value is collected
  and returned as a list. Unity YieldInstructions (WaitForSeconds, WaitForEndOfFrame)
  are rejected rather than ignored — use 'await Task.Delay(ms)' to wait for time.

  If the wait outlives --await_timeout the response is an error carrying
  {"timed_out": true, "status": …}; the operation keeps running in the editor and its
  result is not observable from that response. A domain reload drops a pending result
  silently — park it in a static field or ScriptableObject and read it back.

Stdin:
  Pipe code via stdin to avoid shell escaping issues.
  echo '<code>' | unity-cli exec [--usings ns1,ns2]

Notes:
  - Use 'return' for output, 'return null;' for void operations
`)
	case "menu":
		fmt.Print(`Usage: unity-cli menu "<path>"

Execute a Unity menu item by its path.

Examples:
  unity-cli menu "File/Save Project"
  unity-cli menu "Assets/Refresh"
  unity-cli menu "Window/General/Console"

Note: File/Quit is blocked for safety.
`)
	case "screenshot":
		fmt.Print(`Usage: unity-cli screenshot [options]

Capture a screenshot of the Unity editor.

Options:
  --view <mode>      scene (default), game
  --width <N>        Image width in pixels (default: 1920)
  --height <N>       Image height in pixels (default: 1080)
  --output_path <path>  Output path, absolute or relative to project root
                        (default: Screenshots/screenshot.png)

Examples:
  unity-cli screenshot
  unity-cli screenshot --view game
  unity-cli screenshot --view scene --width 3840 --height 2160
  unity-cli screenshot --output_path captures/my_scene.png
`)
	case "reserialize":
		fmt.Print(`Usage: unity-cli reserialize [path...]

Force Unity to reserialize assets through its own YAML serializer.
Run after editing .prefab, .unity, .asset, or .mat files as text.
No arguments = reserialize the entire project.

Examples:
  unity-cli reserialize
  unity-cli reserialize Assets/Prefabs/Player.prefab
  unity-cli reserialize Assets/Scenes/Main.unity Assets/Scenes/Lobby.unity
`)
	case "clear_burst_cache":
		fmt.Print(`Usage: unity-cli clear_burst_cache [options]

Delete the project's Burst compilation cache (<project>/Library/BurstCache) and
report how many files and bytes were removed.

Burst rebuilds the cache on its next compile, so this is safe to run whenever
JIT-compiled jobs look stale. Only paths inside Library/BurstCache are touched —
no path argument is accepted.

Options:
  --scope <all|jit>   all (default): the whole Library/BurstCache
                      jit: only Library/BurstCache/JIT, keeping AotSettings*.hash
  --dry_run           List what would be deleted without deleting anything

Locked files are reported per path instead of aborting the whole clear; on
Windows the JIT cache can stay mapped until the editor quits.

Examples:
  unity-cli clear_burst_cache
  unity-cli clear_burst_cache --dry_run
  unity-cli clear_burst_cache --scope jit
`)
	case "burst_status":
		fmt.Print(`Usage: unity-cli burst_status [options]

Ask Burst what it has compiled. Burst compiles outside Unity's script compilation,
so a clean C# compile and a quiet console do not prove a [BurstCompile] job was
compiled — and a Burst (BCxxxx) error can appear after the compile finished.

Reports whether Burst's own queue is idle, and the [BurstCompile] targets in the
project's own assemblies (package assemblies are left out unless --include_packages)
with status compiled / failed / not-compiled-yet / unknown. Default output is short:
counts plus the Burst errors, each as file:line:col CODE message.

A filter (--type, --namespace, --assembly) turns the listing on: the matching targets
with their status.

How status is decided: Burst eager-compiles every target it knows when an assembly
finishes compiling, so "its queue is idle" plus "no error names this target" is what
compiled means here. --compile forces a Burst recompile now, so the answer comes from
this call (method: forced_recompile) instead of the last script compile (method: eager).

Needs com.unity.burst installed: without it the tool is not built and the command is
unknown. verify runs this step automatically; see unity-cli help verify.

Options:
  --wait <seconds>        Wait for Burst to go idle (default 0 = answer immediately;
                          with --compile the default is 60)
  --compile               Force Burst to recompile its targets now
  --type <name>           Only targets whose type or target name contains this
  --namespace <ns>        Only targets in this namespace or below
  --assembly <a,b>        Only targets in these assemblies (comma-separated)
  --list                  List matched targets even without a filter
  --include_packages      Include targets in package assemblies too
  --limit <n>             Max errors and targets to list (default 20)

Examples:
  unity-cli burst_status
  unity-cli burst_status --wait 60
  unity-cli burst_status --type ScaleValuesJob
  unity-cli burst_status --assembly MyGame.Runtime --list
  unity-cli burst_status --compile --wait 120 --timeout 130000
`)
	case "profiler":
		fmt.Print(`Usage: unity-cli profiler <subcommand> [options]

Subcommands:
  hierarchy             Top-level profiler samples (last frame)
    --depth <N>         Recursive depth (0=unlimited, default: 1)
    --root <name>       Set root by name (substring match, searches full tree)
    --frames <N>        Average over last N frames (flat output, sorted by time)
    --from <N>          Start frame index for range average
    --to <N>            End frame index for range average
    --parent <ID>       Drill into item by ID
    --min <ms>          Filter items below threshold
    --sort <col>        Sort by: total (default), self, calls
    --max <N>           Max children per level (default: 30)
    --frame <N>         Specific frame index
    --thread <N>        Thread index (0=main)
  enable                Start profiler recording
  disable               Stop profiler recording
  status                Show profiler state
  clear                 Clear all captured frames

Examples:
  unity-cli profiler hierarchy --depth 3
  unity-cli profiler hierarchy --root SimulationSystem --depth 3
  unity-cli profiler hierarchy --frames 30 --min 0.5 --sort self
  unity-cli profiler enable
`)
	case "test":
		fmt.Print(`Usage: unity-cli test [options]

Run Unity tests via the Test Runner API.

Options:
  --mode <EditMode|PlayMode>    Test mode (default: EditMode)
  --filter <name>               Filter by namespace, class, or full test name
                                Must be the full path (e.g. MyNamespace.MyClass)
  --assembly <a,b>              Only tests in these assemblies (comma-separated)
  --allow-dirty-scenes          Run even when open scenes have unsaved changes
  --auto-save-scenes            Save dirty open scenes before running tests

EditMode tests hold the connection open and return results directly.
PlayMode tests return immediately and poll a results file (domain reload safe).
By default, tests are blocked when any open scene has unsaved changes.

Requires the Unity Test Framework package (com.unity.test-framework).

Examples:
  unity-cli test
  unity-cli test --mode PlayMode
  unity-cli test --auto-save-scenes
  unity-cli test --filter MyNamespace.MyTests
  unity-cli test --assembly MyGame.Tests.Editor
  unity-cli test --mode EditMode --filter MyNamespace.MyTests.SpecificTest
`)
	case "verify":
		fmt.Print(`Usage: unity-cli verify [options]

Check that the project compiles and its tests pass, in one call, with output
brief enough for an agent's context:

  1. Clears the console (a Burst error stays there after its code changes;
     Burst logs a live one again on the next compile). --keep-console skips it.
  2. Refreshes assets, compiles, and waits for it to finish.
  3. Lists this compile's errors in your code as file:line:col CODE message.
     Errors in Library/ or PackageCache/ are listed only when nothing else is.
  4. Asks Burst (burst_status) whether it is still compiling, what compiled, and
     what failed, waiting up to --burst-wait seconds. Burst errors print the same
     way as compile errors and fail verify. --no-burst skips the step; projects
     without Burst print nothing.
  5. Unless --no-tests: saves dirty open scenes and runs the tests, then
     prints passed/total and the first line of each failure.

Options:
  --no-tests                    Stop after the compile
  --no-burst                    Skip the Burst step
  --burst-wait <seconds>        How long to wait for Burst to go idle (default 60)
  --assembly <a,b>              Only tests in these assemblies (comma-separated)
  --filter <name>               Filter by namespace, class, or full test name
  --mode <EditMode|PlayMode>    Test mode (default: EditMode)
  --max <n>                     List at most n errors and n failures (default 20)
  --keep-console                Don't clear the console first
  --json                        One JSON object instead of text

Exit codes:
  0  compiles, Burst is quiet, and every test passes
  1  compile errors, Burst errors, failing tests, or no test ran
  2  the editor could not answer: not running, timed out, or blocked by a
     modal dialog (named, with the command that answers it)

A Burst check that cannot run (older connector, editor busy) is printed as a note
and does not change the exit code; keep --timeout above --burst-wait.

Needs connector 0.7.0 for live compile errors; older connectors fall back to
console errors. The burst step needs the connector's Burst tool, which is built
only when com.unity.burst is installed.

Examples:
  unity-cli verify
  unity-cli verify --no-tests
  unity-cli verify --no-burst
  unity-cli verify --assembly MyGame.Tests.Editor
  unity-cli verify --json
`)
	case "modal":
		fmt.Print(`Usage: unity-cli modal [list|answer] [options]

Inspect or answer a modal dialog that blocks the Unity Editor (macOS only).

A modal dialog (DisplayDialog, save prompts, file panels) freezes Unity's
main thread. While one is open, every other command fails immediately with
the dialog's text and buttons instead of hanging; a command that opened the
dialog itself resumes inside Unity once the dialog is answered.

Subcommands:
  list                List the open dialog: id, kind, texts, buttons (default)
  answer              Click one of the dialog's buttons
    --button <title>  Button title; falls back to case- and apostrophe-insensitive match
    --index <n>       Button index as reported by list
    --id <id>         Only click if the open dialog still has this id

Answering happens inside the Unity process; no Accessibility permission is
used and only Unity's own windows are reachable. IMGUI modal windows have no
native buttons and cannot be answered.

Examples:
  unity-cli modal
  unity-cli modal answer --button "Don't Save"
  unity-cli modal answer --index 0 --id 27186
`)
	case "dialogs":
		fmt.Print(`Usage: unity-cli dialogs [--click "<button title>"] [--window "<window title>"]

List — and optionally answer — the windows of the Unity processes on this machine,
read from OUTSIDE the editor with AppleScript + System Events (Accessibility).

This is the only way to see a dialog that appears before the connector assembly loads:
license / sign-in prompts, safe mode, "project already open", package resolution.
status reports that window as "editor_starting_or_blocked".

macOS only: on other systems the command explains that it must run on the macOS host.
The agent sandbox is granted no Accessibility permission, so run it in a Terminal on
the host.

Options:
  --click "<title>"  Press the button. Exact title first, then a case-insensitive
                     substring match against the titles just listed.
  --window "<title>" Only click in a window whose title contains this. Also used to
                     break a tie when the same button title is on several windows.

Windows are narrowed to the requested project (--project, or the running instance) by
matching the project folder name in the window title; when nothing matches, every
Unity window is listed instead, because startup dialogs rarely name the project.

Output is JSON: pid, window title, role, button titles, and the window's static
texts (capped at 12 per window; texts_total reports the real count).

If Accessibility permission is missing, the error names the exact grant to make in
System Settings -> Privacy & Security -> Accessibility (for /usr/bin/osascript and the
app running this command).

Windows Unity draws itself (IMGUI panels, some Safe Mode prompts) expose no native
buttons to Accessibility: they are listed with their title and texts, but there is
nothing to click. Then only a human can answer them.

Examples:
  unity-cli --project ~/Work/MyProject dialogs
  unity-cli dialogs
  unity-cli dialogs --click "Continue"
  unity-cli dialogs --click "Don't Save" --window "Save Changes"

See also: 'unity-cli modal' — the in-editor equivalent, available once the connector loads.
`)
	case "build":
		fmt.Print(`Usage: unity-cli build [--profile <name> | --target <BuildTarget>] --output <path> [options]
       unity-cli build status [id]

Starts a player build as a job. The connector validates the parameters, records the job, and returns
its id; the build itself starts on a later editor update tick. BuildPipeline.BuildPlayer blocks
Unity's main thread for the whole build no matter what, so the point of the job is that the editor
stays observable while it runs:

  - the heartbeat reports state "building" (with the job id and elapsed time) instead of "ready",
    so waiters do not mistake a building editor for a wedged one;
  - build_status answers on a thread that does not wait for the main thread, so 'unity-cli build
    status' works during the build;
  - the wait keeps polling through dropped connections and the domain reload a build can trigger,
    instead of aborting on the first failed request.

Job options:
  --profile <name|path>   BuildProfile asset: a name (looked up under Assets/Settings/Build
                          Profiles/<name>.asset, then by name anywhere) or an asset path.
                          The profile's platform must be the editor's active build target.
  --target <BuildTarget>  StandaloneOSX, StandaloneWindows64, StandaloneLinux64, iOS, Android, ...
                          Refused up front when the platform module is not installed.
  --output <path>         Required. Absolute, or relative to the project root. Not inside Assets/.
  --scenes <a,b>          Comma-separated scene paths. Defaults to the profile's scenes, else the
                          enabled scenes in Build Settings.
  --development           BuildOptions.Development.
  --no-wait               Return the job id and exit; poll it later with 'build status <id>'.

Progress on stderr:
  build job build-20240101-114213-9f0ac1 queued (macOS_dev -> /proj/Builds/Mac/App.app)
  building 12s  Compiling PlayerAssemblies 23%
  building 3m27s  Compiling PlayerAssemblies 61%
  build succeeded in 5m02s — 132.4 MB, 0 error(s), 4 warning(s) -> /proj/Builds/Mac/App.app

The percent comes from Unity's Progress window and is only shown when the build pumps the editor
update loop; when it does not, the lines carry elapsed time only. The final JSON on stdout is the job
record: phase, result, output path, size, duration, error/warning counts, and the first error
messages from the BuildReport.

Phases: queued, building, succeeded, failed, cancelled, interrupted. "interrupted" means the editor
stopped reporting the job while it was building (domain reload or editor exit), so the outcome of
that build is unknown.

While one build is running, a second 'build' is refused at once from the heartbeat ("Unity is already
building ..."); asking the connector instead would queue the request behind the running build and
look like a hang. Validation that needs the editor (unknown build profile, scenes that no longer exist
in the project, a target whose module is not installed) fails before the job starts.

Exit codes:
  0  the build succeeded, or a status poll of a job that has not failed
  1  failed, cancelled, interrupted, or a validation error before the job started

Job state lives in <project>/Temp/unity-cli/build-jobs.json, so it survives the domain reload a build
can trigger. Quitting the editor clears Temp, and with it the job history.

Examples:
  unity-cli build --profile macOS_dev --output Builds/Mac/App.app
  unity-cli build --profile macOS --output Builds/Mac/App.app --development
  unity-cli build --target StandaloneOSX --output Builds/Mac/App.app --scenes Assets/Main.unity
  unity-cli build --no-wait --profile macOS_dev --output Temp/App.app
  unity-cli build status
  unity-cli build status build-20240101-114213-9f0ac1
  unity-cli status                 # shows: Unity (port 8090): building
`)
	case "list":
		fmt.Print(`Usage: unity-cli list

List all registered tools (built-in + custom) with parameter schemas.

Example:
  unity-cli list
`)
	case "status":
		fmt.Print(`Usage: unity-cli status

Show the current Unity Editor state: port, project path, version, PID.
Reports "not responding" if heartbeat is older than 3 seconds.
State "modal" means a dialog blocks the editor; its text and buttons are
printed (see "unity-cli modal --help"). State "building" means a player build job holds the main
thread; the job id, phase and elapsed time are printed with it (see "unity-cli build --help").

When no heartbeat answers but the project's Temp/UnityLockfile (or a heartbeat that
went silent) says an editor is open, status reports "editor_starting_or_blocked": the
connector has not loaded yet, or a startup dialog is waiting. That dialog is invisible
to the connector — read it with "unity-cli dialogs" on the macOS host.

Example:
  unity-cli status
`)
	case "update":
		fmt.Print(`Usage: unity-cli update [options]

Update the CLI binary to the latest release from GitHub.

Options:
  --check              Check for updates without installing

Examples:
  unity-cli update
  unity-cli update --check
`)
	case "custom-tools", "custom", "tools":
		fmt.Print(`How to write custom tools for unity-cli

Custom tools are C# classes that run inside Unity Editor. The CLI
discovers them automatically via reflection.

Create a static class with [UnityCliTool] in any Editor assembly:

    using UnityCliConnector;
    using Newtonsoft.Json.Linq;

    [UnityCliTool(Description = "Spawn an enemy at a position")]
    public static class SpawnEnemy
    {
        public class Parameters
        {
            [ToolParameter("X world position", Required = true)]
            public float X { get; set; }
        }

        public static object HandleCommand(JObject parameters)
        {
            float x = parameters["x"]?.Value<float>() ?? 0;
            var go = Object.Instantiate(prefab, new Vector3(x, 0, 0), Quaternion.identity);
            return new SuccessResponse("Spawned", new { name = go.name });
        }
    }

Rules:
  - Class must be static
  - Must have: public static object HandleCommand(JObject parameters)
  - Return SuccessResponse(message, data) or ErrorResponse(message)
  - Add Parameters class with [ToolParameter] for discoverability
  - Class name auto-converts to snake_case (SpawnEnemy → spawn_enemy)
  - Override name: [UnityCliTool(Name = "my_name")]
  - Runs on Unity main thread — all Unity APIs are safe
  - Discovered on Editor start and after every script recompilation
  - Duplicate tool names are detected and logged as errors (first wins)
`)
	case "setup", "install":
		fmt.Print(`Installation and Unity setup

CLI Installation:
  # Linux / macOS
  curl -fsSL https://raw.githubusercontent.com/MikkelAndresen/unity-cli/main/install.sh | sh

  # Windows (PowerShell)
  irm https://raw.githubusercontent.com/MikkelAndresen/unity-cli/main/install.ps1 | iex

  # Go install (any platform)
  go install github.com/youngwoocho02/unity-cli@latest

Unity Setup:
  1. Window → Package Manager → + → Add package from git URL
  2. Paste: https://github.com/MikkelAndresen/unity-cli.git?path=unity-connector
  The Connector starts automatically when Unity opens.

Verify:
  unity-cli list
`)
	default:
		fmt.Printf("Unknown help topic: %s\n\nUse \"unity-cli --help\" for available commands.\n", topic)
	}
}
