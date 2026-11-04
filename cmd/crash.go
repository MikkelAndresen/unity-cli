package cmd

import (
	"fmt"
	"path/filepath"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// crashState names an editor whose process died without quitting: distinct from "not responding",
// which covers an editor that may still come back (still importing, wedged, behind a startup dialog).
// The difference decides whether an agent waits or tells the user.
const crashState = "crashed"

// noBeatCrashWindow bounds how far back an artifact can belong to a heartbeat that never recorded a
// timestamp; without it, a dump from last year would be blamed on the current silence.
const noBeatCrashWindow = 24 * time.Hour

// assessCrash judges an editor whose heartbeat file is still on disk but which has stopped answering.
// Artifacts older than its own last beat belong to an earlier crash, so they are not evidence here.
func assessCrash(inst *client.Instance) client.SilenceVerdict {
	if inst == nil {
		return client.SilenceVerdict{}
	}
	since := time.UnixMilli(inst.Timestamp)
	if inst.Timestamp <= 0 {
		since = time.Now().Add(-noBeatCrashWindow)
	}
	return client.AssessSilence(inst, since, 0)
}

// crashError carries a finished crash report. Carrying it as a type, rather than as an error string,
// is what stops annotateNoEditor from re-deciding the same question and printing the report twice.
type crashError struct{ report string }

func (e *crashError) Error() string { return e.report }

// crashedError builds the report for an editor whose heartbeat file proves it died. discovery is the
// lookup failure the caller ran into, kept so the original reason stays visible.
func crashedError(inst *client.Instance, v client.SilenceVerdict, discovery error) error {
	return &crashError{report: crashedReport(inst, v.BeatAge, v, discovery)}
}

// stalledEditorReport is what any command prints for an editor whose heartbeat went silent: the
// crash report when the disk proves one, the starting-or-blocked report otherwise.
func stalledEditorReport(inst *client.Instance, age time.Duration, v client.SilenceVerdict) string {
	if v.Crashed {
		return crashedReport(inst, age, v, nil)
	}
	return staleHeartbeatReport(inst, age, client.FindEditorEvidence(inst.ProjectPath, inst.Port))
}

// crashedReport is the whole answer for an editor that died: the verdict, the record of the silence,
// the artifact that says why, and what to do next. discovery is the lookup failure the caller ran
// into, kept so the original reason stays visible (status has none).
func crashedReport(inst *client.Instance, age time.Duration, v client.SilenceVerdict, discovery error) string {
	var b strings.Builder

	project := inst.ProjectPath
	if project == "" {
		project = "this project"
	}
	fmt.Fprintf(&b, "%s: the Unity editor died for %s (heartbeat last seen %s ago, pid %d) with no \"stopped\" marker.\n",
		crashState, project, client.FormatAge(age), inst.PID)

	if inst.SourceFile != "" {
		fmt.Fprintf(&b, "  Heartbeat: %s (state %q, pid %d, last beat %s ago)\n",
			inst.SourceFile, inst.State, inst.PID, client.FormatAge(age))
	}
	if v.PIDNote != "" {
		fmt.Fprintf(&b, "  Process:   %s\n", v.PIDNote)
	}

	if len(v.Artifacts) == 0 {
		fmt.Fprintf(&b, "  Crash log: none newer than the last heartbeat in %s (%s), %s, or ~/Library/Logs/Unity and ~/Library/Logs/DiagnosticReports\n",
			project, client.MonoCrashGlob, filepath.Join(project, "Logs"))
	}
	for _, art := range v.Artifacts {
		fmt.Fprintf(&b, "  Crash log: %s (%s, written %s ago)\n", art.Path, art.Kind, art.Age())
		if art.Detail != "" {
			fmt.Fprintf(&b, "             %s\n", art.Detail)
		}
	}

	if discovery != nil {
		fmt.Fprintf(&b, "  Lookup:    %s\n", strings.ReplaceAll(discovery.Error(), "\n", " "))
	}
	b.WriteString("  Next:      Unity has to be relaunched by the user — no CLI call brings it back. Editor.log rotates when it does, so read the crash log above first.")
	return b.String()
}

// oneTrailingNewline normalizes a report for printing: the same string is used as an error message
// (where a trailing newline would print a blank line) and straight to stderr (where a missing one runs
// into the next line of output).
func oneTrailingNewline(s string) string {
	return strings.TrimRight(s, "\n") + "\n"
}

// crashLine is the one-sentence form, for places that already print their own message.
func crashLine(inst *client.Instance, v client.SilenceVerdict) string {
	if inst == nil {
		return crashState
	}
	return fmt.Sprintf("%s (port %d, pid %d): %s", crashState, inst.Port, inst.PID, v.Reason())
}
