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

// editorUnresponsiveState names a different failure from startup: the connector loaded this
// session, then stopped answering. The distinction decides whether an agent waits for a startup
// dialog or looks for a wedged main thread.
const editorUnresponsiveState = "editor_unresponsive"

// startupDialogCauses is what an agent is told to consider, in the order an agent can act on them.
const startupDialogCauses = `a startup dialog (license, safe mode, "project already open", package resolution) is waiting`

// staleLockfileAge is when a Temp/UnityLockfile stops reading as "Unity is starting" and starts
// reading as "Unity died". A cold import of a large project stays well under this. The lockfile is
// written once at editor start, so its age is the session age; this only applies when no heartbeat
// this session proves the connector loaded.
const staleLockfileAge = 2 * time.Hour

// annotateNoEditor turns a "no Unity instance" failure into editor_starting_or_blocked (or
// editor_unresponsive after a connector loaded this session) when the disk shows an editor open
// for the target. Without that evidence the original message stands: it is the right answer for a
// project that is simply not open.
//
// A crash is checked first because it outranks the starting-or-blocked reading: both have a silent
// heartbeat, and only one of them is worth waiting for.
func annotateNoEditor(err error, project string, port int) error {
	return annotateNoEditorWith(client.NewestHeartbeat(project, port), err, project, port)
}

// annotateNoEditorWith is annotateNoEditor for a caller that already discovered an instance: beat is
// the heartbeat read before that discovery, whose scan deletes the dead-pid file the crash verdict
// needs. It is nil when there was none to read.
func annotateNoEditorWith(beat *client.Instance, err error, project string, port int) error {
	if err == nil {
		return nil
	}
	var already *crashError
	if errors.As(err, &already) {
		return err
	}
	if beat != nil {
		if verdict := assessCrash(beat); verdict.Crashed {
			return crashedError(beat, verdict, err)
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
	if beat, age := ev.UnresponsiveHeartbeat(); beat != nil {
		pid := ""
		if beat.PID > 0 {
			pid = fmt.Sprintf(", pid %d", beat.PID)
		}
		if ev.LockfilePath == "" {
			return fmt.Sprintf("%s: Unity's connector for %s stopped answering %s ago%s (last state %q), and no Temp/UnityLockfile is held — the editor has probably exited.",
				editorUnresponsiveState, project, client.FormatAge(age), pid, beat.State)
		}
		return fmt.Sprintf("%s: Unity's connector for %s stopped answering %s ago%s (last state %q) — the main thread is likely blocked (long exec/build/import), a modal dialog is open, or the editor hung.",
			editorUnresponsiveState, project, client.FormatAge(age), pid, beat.State)
	}
	if ev.LockfilePath != "" {
		// The lockfile age decides what the sentence may claim: a session that has been "starting"
		// for hours is not still importing.
		if ev.LockfileAge > staleLockfileAge && !ev.HeartbeatThisSession() {
			return fmt.Sprintf("%s: Unity's Temp/UnityLockfile for %s is %s old and no heartbeat from this session exists — the editor probably crashed instead of quitting, rather than still starting.",
				editorStartingState, project, client.FormatAge(ev.LockfileAge))
		}
		return fmt.Sprintf("%s: Unity appears open for %s (%s) but the connector has not loaded — it is still importing/compiling, or %s.",
			editorStartingState, project, ev.Summary(), startupDialogCauses)
	}
	return fmt.Sprintf("%s: Unity's connector stopped answering for %s (%s) and no fresher heartbeat exists — the editor may be restarting, still importing/compiling, or %s.",
		editorStartingState, project, ev.Summary(), startupDialogCauses)
}

// evidenceHint is the one-line form, for paths that already print their own message.
func evidenceHint(ev *client.EditorEvidence) string {
	if beat, _ := ev.UnresponsiveHeartbeat(); beat != nil {
		return fmt.Sprintf("%s Try 'unity-cli dialogs' on the macOS host; if it stays silent, restart the editor ('unity-restart' in agent-sandbox).", evidenceSentence(ev))
	}
	return fmt.Sprintf("%s Run 'unity-cli dialogs' on the macOS host, or ask the user.", evidenceSentence(ev))
}

// evidenceReport is the whole actionable report for an editor that is open but silent.
// discovery is the lookup failure it replaces, kept so the original reason stays visible.
func evidenceReport(ev *client.EditorEvidence, discovery error) string {
	var b strings.Builder

	fmt.Fprintf(&b, "%s\n", evidenceSentence(ev))

	if ev.LockfilePath != "" {
		fmt.Fprintf(&b, "  Lockfile: %s (written %s)\n", ev.LockfilePath, ev.LockfileMTime.UTC().Format(time.RFC3339))
		if ev.LockfileAge > staleLockfileAge && !ev.HeartbeatThisSession() {
			fmt.Fprintf(&b, "            older than %s: Unity probably crashed instead of quitting\n", staleLockfileAge)
		}
	} else if ev.ProjectPath != "" {
		fmt.Fprintf(&b, "  Lockfile: none at %s/Temp/UnityLockfile, so Unity may not be holding this project open\n", ev.ProjectPath)
	}

	if beat, age := ev.UnresponsiveHeartbeat(); beat != nil {
		fmt.Fprintf(&b, "  Heartbeat: %s (state %q, pid %d, last beat %s ago)\n",
			beat.SourceFile, beat.State, beat.PID, client.FormatAge(age))
	} else if ev.StaleHeartbeat != nil {
		fmt.Fprintf(&b, "  Heartbeat: %s (state %q, pid %d, last beat %s ago)\n",
			ev.StaleFile, ev.StaleHeartbeat.State, ev.StaleHeartbeat.PID, client.FormatAge(ev.StaleBeatAge))
	}
	if discovery != nil {
		fmt.Fprintf(&b, "  Lookup:   %s\n", strings.ReplaceAll(discovery.Error(), "\n", " "))
	}
	if beat, _ := ev.UnresponsiveHeartbeat(); beat != nil {
		if ev.LockfilePath == "" {
			b.WriteString("  Next:     the project holds no Temp/UnityLockfile, so the editor has probably exited; relaunch it ('unity-restart' in agent-sandbox) and read its log.")
		} else {
			b.WriteString("  Next:     try 'unity-cli dialogs' on the macOS host; if it stays silent, restart the editor ('unity-restart' in agent-sandbox).")
		}
	} else if ev.LockfilePath != "" && ev.LockfileAge > staleLockfileAge && !ev.HeartbeatThisSession() {
		b.WriteString("  Next:     the editor probably crashed; relaunch it ('unity-restart' in agent-sandbox) and read its crash log or Logs/Editor.log.")
	} else {
		b.WriteString("  Next:     run 'unity-cli dialogs' on the macOS host to read the dialog's title and buttons, or ask the user to look at the editor.")
	}
	return b.String()
}
