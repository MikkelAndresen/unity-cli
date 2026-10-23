package cmd

import (
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
	"github.com/youngwoocho02/unity-cli/internal/editor"
)

const (
	stopMethodGraceful  = "graceful"
	stopMethodTerminate = "terminate"
)

type quitResult struct {
	ProjectPath string `json:"projectPath"`
	PID         int    `json:"pid"`
	// Method is "graceful" when quit_editor shut Unity down, or "terminate" when
	// --force had to signal the process (SIGTERM on unix).
	Method  string `json:"method"`
	Elapsed string `json:"elapsed"`
}

// quitCmd stops the running editor and waits until it is gone. Unlike
// `exec "EditorApplication.Exit(0)"`, it returns only after the heartbeat or the
// process confirms the exit, so a quit that Unity ignored is reported as an error.
func quitCmd(args []string, deps restartDeps) (*client.CommandResponse, error) {
	start := time.Now()
	flags := parseSubFlags(args)

	inst, err := deps.discover(deps.project, deps.port)
	if err != nil {
		return nil, fmt.Errorf("no running Unity editor to quit: %w", err)
	}
	target := targetFromInstance(inst)

	quitTimeout, _ := restartTimeouts(deps, false)
	method, err := stopEditor(deps, target, subFlagEnabled(flags, "save"), subFlagEnabled(flags, "force"), quitTimeout, "editor quit")
	if err != nil {
		return nil, err
	}

	data, err := json.Marshal(quitResult{
		ProjectPath: target.projectPath,
		PID:         target.pid,
		Method:      method,
		Elapsed:     time.Since(start).Round(time.Millisecond).String(),
	})
	if err != nil {
		return nil, err
	}
	return &client.CommandResponse{Success: true, Data: data}, nil
}

// stopEditor asks the live editor to quit and waits for it to exit. With force,
// an editor that ignores the request or is parked on a modal dialog is
// terminated instead, because only the native signal path skips Unity's
// managed quit handling.
func stopEditor(deps restartDeps, target *restartTarget, save, force bool, timeout time.Duration, command string) (string, error) {
	waitOpts := editor.WaitForExitOptions{
		PID:         target.pid,
		ProjectPath: target.projectPath,
		Timeout:     timeout,
	}

	var gracefulErr error
	if isModal(target.inst) {
		if !force {
			return "", modalBlockedError(target.inst, command, false)
		}
		gracefulErr = editor.ErrEditorModal
	} else {
		restartLog(deps.stderr, "Stopping Unity (pid %d) for %s...\n", target.pid, target.projectPath)
		resp, sendErr := deps.send(target.inst, "quit_editor", map[string]interface{}{"save": save}, quitRequestTimeoutMs)
		if sendErr != nil {
			// The editor can tear down its HTTP server before the reply reaches us.
			restartLog(deps.stderr, "Warning: quit request failed (%v); waiting for Unity to exit anyway.\n", sendErr)
		} else if resp != nil && !resp.Success {
			return "", fmt.Errorf("unity refused to quit: %s", resp.Message)
		}

		restartLog(deps.stderr, "Waiting for Unity to exit...\n")
		gracefulErr = deps.waitForExit(waitOpts)
		if gracefulErr == nil {
			return stopMethodGraceful, nil
		}
		if !force {
			if errors.Is(gracefulErr, editor.ErrEditorModal) {
				return "", stopModalError(deps, target.projectPath, command)
			}
			return "", fmt.Errorf("%w\nIf a modal dialog is open, answer it with: unity-cli modal answer --button \"<title>\"\nor pass --force to terminate the process", gracefulErr)
		}
	}

	restartLog(deps.stderr, "Unity did not quit gracefully (%v); terminating pid %d.\n", gracefulErr, target.pid)
	if err := deps.terminate(target.pid); err != nil {
		return "", fmt.Errorf("cannot terminate Unity (pid %d): %w", target.pid, err)
	}
	// A modal heartbeat can outlive the kill until it goes stale; only the
	// process or heartbeat staleness can confirm exit now.
	waitOpts.IgnoreModal = true
	if err := deps.waitForExit(waitOpts); err != nil {
		return "", fmt.Errorf("unity (pid %d) is still running after termination: %w", target.pid, err)
	}
	return stopMethodTerminate, nil
}
