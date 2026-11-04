package cmd

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// playModeConfirmTimeout bounds how long `editor play|stop` waits for the heartbeat to report the
// state it asked for. A domain reload is part of that change in most projects, so the budget has to
// outlast one; it is still bounded, because a play-mode entry that failed leaves the editor "ready"
// forever and the caller must be told rather than left watching.
var playModeConfirmTimeout = 90 * time.Second

// reloadStateWait bounds how long any command whose answer was lost to a reload waits to report the
// state the editor ended in. Variables so tests can shrink them.
var reloadStateWait = 60 * time.Second

// playModePollInterval is the heartbeat polling step for both waits.
var playModePollInterval = 500 * time.Millisecond

// editorRestedStates are the heartbeat states in which the domain change is over and the editor can
// answer again. "starting" is deliberately absent: the listener is up but no update tick has run.
var editorRestedStates = []string{"ready", "playing", "paused", "modal"}

// answerLostToReload reports whether a call ended without the connector's verdict while its effect
// may still have been applied: the connector said a reload interrupted a command that had already
// started, or the connection dropped while the heartbeat says Unity reloaded rather than crashed.
// A `domain_reload` answer means the opposite (the command never ran), so it is not lost work.
func answerLostToReload(resp *client.CommandResponse, err error) bool {
	if err != nil {
		var failure *client.ConnectionFailure
		return errors.As(err, &failure) && failure.Class == client.FailureReload
	}
	return resp != nil && !resp.Success &&
		client.ConnectorErrorCode(resp) == codeReloadDuringCommand
}

// editorStateLabel turns a heartbeat state into what the editor window would be showing.
func editorStateLabel(state string) string {
	switch state {
	case "playing":
		return "Play mode"
	case "paused":
		return "Play mode (paused)"
	case "ready":
		return "Edit mode"
	case "entering_playmode":
		return "entering Play mode"
	case "exiting_playmode":
		return "leaving Play mode"
	case "reloading":
		return "a domain reload"
	case "compiling":
		return "a script compile"
	case "refreshing":
		return "an asset refresh"
	case "starting":
		return "startup"
	case "modal":
		return "a modal dialog"
	case "stopped":
		return "stopped"
	case "building":
		return "a player build"
	default:
		return state
	}
}

// waitForEditorState polls the heartbeat until it reports one of want. It stops early on a modal
// dialog, which nothing here can answer, and never outruns ctx or budget. The last heartbeat read is
// returned even when no wanted state appeared, so the caller can say what the editor is doing
// instead of only that the wait failed.
func waitForEditorState(ctx context.Context, resolve instanceResolver, want []string, budget time.Duration,
	command string) (*client.Instance, bool, error) {

	deadline := time.Now().Add(budget)
	if d, ok := ctx.Deadline(); ok && d.Before(deadline) {
		deadline = d
	}

	var last *client.Instance
	for {
		inst, err := resolve()
		if err == nil {
			last = inst
			if isModal(inst) {
				return inst, false, modalBlockedError(inst, command, true)
			}
			for _, state := range want {
				if inst.State == state {
					return inst, true, nil
				}
			}
		}

		remaining := time.Until(deadline)
		if remaining <= 0 {
			return last, false, nil
		}
		step := playModePollInterval
		if step > remaining {
			step = remaining
		}
		select {
		case <-ctx.Done():
			if ctx.Err() == context.DeadlineExceeded {
				return last, false, nil
			}
			return last, false, fmt.Errorf("waiting for Unity's %s cancelled: %w", command, ctx.Err())
		case <-time.After(step):
		}
	}
}

// stateAfterReloadNote answers the question a reload-interrupted command leaves behind: did the
// command's change land, and is the editor answering again? Only the heartbeat can say, and only once
// the reload is over, so this waits (bounded by the caller's remaining budget) and returns the line
// that reports what it saw. "" means there is nothing to add.
func stateAfterReloadNote(ctx context.Context, resolve instanceResolver) string {
	budget := cappedBudget(ctx, reloadStateWait)
	inst, rested, err := waitForEditorState(ctx, resolve, editorRestedStates, budget, "state check")
	if err != nil {
		// A modal dialog or an interrupt: that report is the caller's business, not this note's.
		return ""
	}
	if inst == nil {
		return "  state:     no heartbeat from Unity; run 'unity-cli status' to see if the editor is still up\n"
	}
	if rested {
		return fmt.Sprintf("  state:     Unity is back: %s (heartbeat %q, port %d)\n",
			editorStateLabel(inst.State), inst.State, inst.Port)
	}
	return fmt.Sprintf("  state:     Unity is still in %s after %s (heartbeat %q, port %d); run 'unity-cli wait'\n",
		editorStateLabel(inst.State), budget.Truncate(time.Second), inst.State, inst.Port)
}
