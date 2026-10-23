package editor

import (
	"errors"
	"fmt"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// ErrEditorModal reports that the editor is alive but a modal dialog has parked
// its main thread, so waiting for it to exit cannot succeed until someone answers.
var ErrEditorModal = errors.New("unity editor is blocked by a modal dialog")

const (
	defaultPollInterval = 100 * time.Millisecond
	// A running editor rewrites its heartbeat every 0.5s, so a multi-second
	// freeze means it is gone (or hung) even when its PID is not visible.
	defaultStaleAfter = 3 * time.Second
)

// WaitForExitOptions configures WaitForExit.
type WaitForExitOptions struct {
	// PID is the editor process. Values <= 0 disable the liveness probe.
	PID int
	// ProjectPath selects the heartbeat file used for the stopped/stale signals.
	// Empty disables heartbeat polling.
	ProjectPath string
	// Timeout bounds the whole wait and must be positive.
	Timeout time.Duration
	// PollInterval defaults to 100ms when <= 0.
	PollInterval time.Duration
	// StaleAfter defaults to 3s when <= 0.
	StaleAfter time.Duration
	// IgnoreModal keeps waiting through a "modal" heartbeat instead of returning
	// ErrEditorModal. Set it after terminating the editor: the dead process
	// leaves its last modal heartbeat behind until it goes stale.
	IgnoreModal bool
}

// WaitForExit blocks until the editor is gone. It returns nil when any of the
// available signals says so:
//
//   - the PID is confirmed dead (definitive ESRCH/exit code only);
//   - the project heartbeat reports state "stopped" (graceful quit);
//   - the heartbeat timestamp has not advanced for StaleAfter. This is the
//     fallback for containers where the host editor's PID is invisible and a
//     hard kill leaves a stale "ready" heartbeat behind.
//
// The two heartbeat signals are only trusted when the heartbeat's PID matches
// opts.PID, or when opts.PID is 0 (unknown). A heartbeat belonging to another
// process is a leftover from an earlier session and must not end this wait.
//
// A matching heartbeat reporting state "modal" returns ErrEditorModal instead
// of waiting: the editor is alive and only a dialog can unblock it. Other errors
// are returned for timeout or invalid options.
func WaitForExit(opts WaitForExitOptions) error {
	if opts.Timeout <= 0 {
		return errors.New("wait timeout must be greater than zero")
	}
	if opts.PID <= 0 && opts.ProjectPath == "" {
		return errors.New("nothing to wait for: both PID and project path are unset")
	}

	pollInterval := opts.PollInterval
	if pollInterval <= 0 {
		pollInterval = defaultPollInterval
	}
	staleAfter := opts.StaleAfter
	if staleAfter <= 0 {
		staleAfter = defaultStaleAfter
	}

	deadline := time.Now().Add(opts.Timeout)
	var lastTimestamp int64
	var haveTimestamp bool
	var lastAdvance time.Time

	for {
		if opts.PID > 0 && !processAlive(opts.PID) {
			return nil
		}

		if opts.ProjectPath != "" {
			// Parse failures during a heartbeat rewrite are expected; they carry
			// no exit signal and must not restart the stale timer.
			if inst, err := readHeartbeat(opts.ProjectPath); err == nil && heartbeatMatchesPID(inst, opts.PID) {
				if inst.State == "modal" && !opts.IgnoreModal {
					return ErrEditorModal
				}
				if inst.State == "stopped" {
					return nil
				}
				now := time.Now()
				if !haveTimestamp || inst.Timestamp != lastTimestamp {
					haveTimestamp = true
					lastTimestamp = inst.Timestamp
					lastAdvance = now
				} else if now.Sub(lastAdvance) >= staleAfter {
					return nil
				}
			}
		}

		if !time.Now().Before(deadline) {
			return fmt.Errorf("timed out after %s waiting for Unity (pid %d) to exit", opts.Timeout, opts.PID)
		}
		time.Sleep(pollInterval)
	}
}

// heartbeatMatchesPID reports whether heartbeat exit signals can be attributed
// to the requested editor. pid <= 0 means the caller does not know the PID, so
// any heartbeat for the project is accepted.
func heartbeatMatchesPID(inst *client.Instance, pid int) bool {
	return pid <= 0 || inst.PID == pid
}

// Indirection so tests can exercise the wait loop without a real editor.
var (
	processAlive  = client.IsProcessAlive
	readHeartbeat = ReadHeartbeat
)
