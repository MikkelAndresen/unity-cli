package cmd

import (
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// editorStartingState names the case the connector cannot report: an editor is open for the
// project but nothing answers on HTTP because its assembly has not loaded yet. Startup dialogs
// (license, safe mode, "project already open", package resolution) land in this window, and the
// in-editor modal handler is not running yet, so the CLI must speak from on-disk evidence.
const editorStartingState = "editor_starting_or_blocked"

// startupDialogCauses is what an agent is told to consider, in the order an agent can act on them.
const startupDialogCauses = `a startup dialog (license, safe mode, "project already open", package resolution) is waiting`

// staleLockfileAge is when a Temp/UnityLockfile stops reading as "Unity is starting" and starts
// reading as "Unity died". A cold import of a large project stays well under this.
const staleLockfileAge = 2 * time.Hour

// annotateNoEditor turns a "no Unity instance" failure into editor_starting_or_blocked when the
// disk shows an editor open for the target. Without that evidence the original message stands:
// it is the right answer for a project that is simply not open.
//
// A crash is checked first because it outranks the starting-or-blocked reading: both have a silent
// heartbeat, and only one of them is worth waiting for.
func annotateNoEditor(err error, project string, port int) error {
	if err == nil {
		return nil
	}
	var already *crashError
	if errors.As(err, &already) {
		return err
	}
	if inst := client.NewestHeartbeat(project, port); inst != nil {
		if verdict := assessCrash(inst); verdict.Crashed {
			return crashedError(inst, verdict, err)
		}
	}
	ev := client.FindEditorEvidence(project, port)
	if !ev.Found() {
		return err
	}
	return fmt.Errorf("%s", evidenceReport(ev, err))
}

// evidenceSentence is the one-line verdict: which state, which project, which evidence.
func evidenceSentence(ev *client.EditorEvidence) string {
	project := ev.ProjectPath
	if project == "" {
		project = "this project"
	}
	if ev.LockfilePath != "" {
		return fmt.Sprintf("%s: Unity appears open for %s (%s) but the connector has not loaded — it is still importing/compiling, or %s.",
			editorStartingState, project, ev.Summary(), startupDialogCauses)
	}
	return fmt.Sprintf("%s: Unity's connector stopped answering for %s (%s) and no fresher heartbeat exists — the editor may be restarting, still importing/compiling, or %s.",
		editorStartingState, project, ev.Summary(), startupDialogCauses)
}

// evidenceHint is the one-line form, for paths that already print their own message.
func evidenceHint(ev *client.EditorEvidence) string {
	return fmt.Sprintf("%s Run 'unity-cli dialogs' on the macOS host, or ask the user.", evidenceSentence(ev))
}

// evidenceReport is the whole actionable report for an editor that is open but silent.
// discovery is the lookup failure it replaces, kept so the original reason stays visible.
func evidenceReport(ev *client.EditorEvidence, discovery error) string {
	var b strings.Builder

	fmt.Fprintf(&b, "%s\n", evidenceSentence(ev))

	if ev.LockfilePath != "" {
		fmt.Fprintf(&b, "  Lockfile: %s (written %s)\n", ev.LockfilePath, ev.LockfileMTime.UTC().Format(time.RFC3339))
		if ev.LockfileAge > staleLockfileAge {
			fmt.Fprintf(&b, "            older than %s: Unity probably crashed instead of quitting\n", staleLockfileAge)
		}
	} else if ev.ProjectPath != "" {
		fmt.Fprintf(&b, "  Lockfile: none at %s/Temp/UnityLockfile, so Unity may not be holding this project open\n", ev.ProjectPath)
	}

	if ev.StaleHeartbeat != nil {
		fmt.Fprintf(&b, "  Heartbeat: %s (state %q, pid %d, last beat %s ago)\n",
			ev.StaleFile, ev.StaleHeartbeat.State, ev.StaleHeartbeat.PID, client.FormatAge(ev.StaleBeatAge))
	}
	if discovery != nil {
		fmt.Fprintf(&b, "  Lookup:   %s\n", strings.ReplaceAll(discovery.Error(), "\n", " "))
	}
	b.WriteString("  Next:     run 'unity-cli dialogs' on the macOS host to read the dialog's title and buttons, or ask the user to look at the editor.")
	return b.String()
}
