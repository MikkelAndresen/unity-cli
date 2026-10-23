package cmd

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
	"github.com/youngwoocho02/unity-cli/internal/editor"
)

const (
	defaultQuitTimeout       = 60 * time.Second
	defaultReadyTimeout      = 5 * time.Minute
	defaultCleanReadyTimeout = 15 * time.Minute
	// The quit handler answers before the editor exits, so this only bounds the
	// HTTP round-trip, not the shutdown itself.
	quitRequestTimeoutMs = 30000
)

// Overridden in tests so polling does not add seconds to every case.
var restartPollInterval = 250 * time.Millisecond

// restartDeps is the seam for restart orchestration. Production values come from
// newRestartDeps; tests replace the functions that touch Unity or the filesystem.
type restartDeps struct {
	discover        func(project string, port int) (*client.Instance, error)
	send            func(inst *client.Instance, command string, params interface{}, timeoutMs int) (*client.CommandResponse, error)
	waitForExit     func(editor.WaitForExitOptions) error
	terminate       func(pid int) error
	cleanProject    func(string) ([]string, error)
	validateProject func(string) error
	readVersion     func(string) (string, error)
	findExecutable  func(version, override string) (string, error)
	launch          func(exe, projectPath string) (int, error)
	readHeartbeat   func(string) (*client.Instance, error)
	getwd           func() (string, error)
	stderr          io.Writer

	project    string
	port       int
	timeoutMs  int
	timeoutSet bool
}

func newRestartDeps(timeoutSet bool) restartDeps {
	return restartDeps{
		discover:        client.DiscoverInstance,
		send:            client.Send,
		waitForExit:     editor.WaitForExit,
		terminate:       editor.Terminate,
		cleanProject:    editor.CleanProject,
		validateProject: editor.ValidateProject,
		readVersion:     editor.ReadProjectVersion,
		findExecutable:  editor.FindEditorExecutable,
		launch:          editor.Launch,
		readHeartbeat:   editor.ReadHeartbeat,
		getwd:           os.Getwd,
		stderr:          os.Stderr,
		project:         flagProject,
		port:            flagPort,
		timeoutMs:       flagTimeout,
		timeoutSet:      timeoutSet,
	}
}

type restartTarget struct {
	projectPath string
	// pid is the live editor being stopped, or 0 when restart is a plain launch.
	pid int
	// baselinePID is the PID in the heartbeat before launch. Waiting for a
	// different PID avoids mistaking a leftover "stopped" file for the new editor.
	baselinePID  int
	unityVersion string
	inst         *client.Instance
}

type restartResult struct {
	ProjectPath string   `json:"projectPath"`
	OldPID      int      `json:"oldPid"`
	NewPID      int      `json:"newPid"`
	Cleaned     []string `json:"cleaned"`
	Elapsed     string   `json:"elapsed"`
}

// restartCmd quits the target editor (if running), optionally deletes Library/
// and Temp/, relaunches the same project, and waits for the new editor to be ready.
// It deliberately bypasses the normal discovery/health pipeline in root.go: it
// must run when no editor is alive, and it must not wait for a heartbeat that is
// about to disappear.
func restartCmd(args []string, deps restartDeps) (*client.CommandResponse, error) {
	start := time.Now()
	flags := parseSubFlags(args)
	clean := subFlagEnabled(flags, "clean")
	save := subFlagEnabled(flags, "save")

	target, err := resolveRestartTarget(deps)
	if err != nil {
		return nil, err
	}

	quitTimeout, readyTimeout := restartTimeouts(deps, clean)
	cleaned := []string{}

	if target.pid > 0 {
		if _, err := stopEditor(deps, target, save, subFlagEnabled(flags, "force"), quitTimeout, "editor restart"); err != nil {
			return nil, err
		}
	}

	if clean {
		restartLog(deps.stderr, "Cleaning generated folders...\n")
		removed, err := deps.cleanProject(target.projectPath)
		if err != nil {
			return nil, err
		}
		cleaned = removed
		if len(removed) == 0 {
			restartLog(deps.stderr, "Nothing to remove (Library/ and Temp/ are already absent).\n")
		}
		for _, path := range removed {
			restartLog(deps.stderr, "Removed %s\n", path)
		}
	}

	version, versionErr := deps.readVersion(target.projectPath)
	if versionErr != nil {
		if target.unityVersion != "" {
			version = target.unityVersion
		} else {
			restartLog(deps.stderr, "Warning: %v\n", versionErr)
		}
	}

	exe, err := deps.findExecutable(version, flags["editor-path"])
	if err != nil {
		return nil, err
	}

	restartLog(deps.stderr, "Launching %s...\n", exe)
	launchedPID, err := deps.launch(exe, target.projectPath)
	if err != nil {
		return nil, err
	}

	ready, err := waitForRestartReady(deps, target.projectPath, target.baselinePID, readyTimeout)
	if err != nil {
		return nil, err
	}
	newPID := ready.PID
	if newPID == 0 {
		newPID = launchedPID
	}

	data, err := json.Marshal(restartResult{
		ProjectPath: target.projectPath,
		OldPID:      target.pid,
		NewPID:      newPID,
		Cleaned:     cleaned,
		Elapsed:     time.Since(start).Round(time.Millisecond).String(),
	})
	if err != nil {
		return nil, err
	}
	return &client.CommandResponse{Success: true, Data: data}, nil
}

func subFlagEnabled(flags map[string]string, name string) bool {
	value, ok := flags[name]
	return ok && value != "false"
}

func restartTimeouts(deps restartDeps, clean bool) (quit, ready time.Duration) {
	quit = defaultQuitTimeout
	ready = defaultReadyTimeout
	if clean {
		ready = defaultCleanReadyTimeout
	}
	if deps.timeoutSet && deps.timeoutMs > 0 {
		timeout := time.Duration(deps.timeoutMs) * time.Millisecond
		quit = timeout
		ready = timeout
	}
	return quit, ready
}

func resolveRestartTarget(deps restartDeps) (*restartTarget, error) {
	if deps.port > 0 {
		inst, err := deps.discover("", deps.port)
		if err != nil {
			return nil, fmt.Errorf("no Unity instance on port %d: %w", deps.port, err)
		}
		return targetFromInstance(inst), nil
	}

	if inst, err := deps.discover(deps.project, 0); err == nil {
		return targetFromInstance(inst), nil
	}

	projectPath := deps.project
	if projectPath == "" {
		cwd, err := deps.getwd()
		if err != nil {
			return nil, fmt.Errorf("no Unity editor is running and the working directory is unavailable: %w", err)
		}
		projectPath = cwd
	}

	abs, err := filepath.Abs(projectPath)
	if err != nil {
		return nil, fmt.Errorf("cannot resolve project path %q: %w", projectPath, err)
	}
	abs = filepath.Clean(abs)
	if err := deps.validateProject(abs); err != nil {
		return nil, err
	}

	target := &restartTarget{projectPath: abs}
	if inst, err := deps.readHeartbeat(abs); err == nil {
		target.baselinePID = inst.PID
		target.unityVersion = inst.UnityVersion
	}
	return target, nil
}

func targetFromInstance(inst *client.Instance) *restartTarget {
	return &restartTarget{
		projectPath:  inst.ProjectPath,
		pid:          inst.PID,
		baselinePID:  inst.PID,
		unityVersion: inst.UnityVersion,
		inst:         inst,
	}
}

func waitForRestartReady(deps restartDeps, projectPath string, oldPID int, timeout time.Duration) (*client.Instance, error) {
	if timeout <= 0 {
		timeout = defaultReadyTimeout
	}
	deadline := time.Now().Add(timeout)
	var lastState string
	sawNewEditor := false

	for {
		if inst, err := deps.readHeartbeat(projectPath); err == nil && inst.PID > 0 && inst.PID != oldPID {
			if !sawNewEditor {
				sawNewEditor = true
				restartLog(deps.stderr, "Unity started (pid %d). Waiting for it to become ready...\n", inst.PID)
			}
			if isModal(inst) {
				return nil, fmt.Errorf("%s", "unity started but is blocked by a modal dialog before becoming ready.\n"+
					describeModal(inst.Modal)+"answer it with: unity-cli modal answer --button \"<title>\" or in the Unity window.")
			}
			if inst.State != lastState {
				lastState = inst.State
				restartLog(deps.stderr, "  state: %s\n", inst.State)
			}
			if inst.State == "ready" {
				restartLog(deps.stderr, "Unity is ready.\n")
				return inst, nil
			}
		}

		if !time.Now().Before(deadline) {
			return nil, fmt.Errorf("timed out after %s waiting for Unity to become ready; a startup dialog may be blocking the editor (safe mode, restore backup, or version upgrade), so answer it manually in the Unity window", timeout)
		}
		time.Sleep(restartPollInterval)
	}
}

func stopModalError(deps restartDeps, projectPath, command string) error {
	if inst, err := deps.readHeartbeat(projectPath); err == nil && isModal(inst) {
		return fmt.Errorf("unity is still running because a modal dialog blocks it; %q did not complete.\n%sanswer it with: unity-cli modal answer --button \"<title>\", or pass --force to terminate the process",
			command, describeModal(inst.Modal))
	}
	return fmt.Errorf("unity is still running because a modal dialog blocks it; %q did not complete, so answer the dialog in the Unity window and retry, or pass --force", command)
}

func restartLog(w io.Writer, format string, args ...interface{}) {
	_, _ = fmt.Fprintf(w, format, args...)
}
