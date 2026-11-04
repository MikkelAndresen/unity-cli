package cmd

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"os"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// compileReportMaxDiags caps the diagnostics embedded in the refresh --compile report,
// keeping the JSON small enough for an agent's context.
const compileReportMaxDiags = 20

// editorCmd controls Unity play mode and asset database.
// resolve is needed for compile polling so it can follow the current project instance.
func editorCmd(args []string, send sendFn, resolve instanceResolver) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli editor <play|stop|pause|refresh|quit|restart>")
	}

	action := args[0]
	flags, err := parseSubFlags(args[1:])
	if err != nil {
		return nil, err
	}

	switch action {
	case "play":
		_, wait := flags["wait"]
		return send("manage_editor", map[string]interface{}{
			"action":              "play",
			"wait_for_completion": wait,
		})

	case "stop":
		return send("manage_editor", map[string]interface{}{"action": "stop"})

	case "pause":
		return send("manage_editor", map[string]interface{}{"action": "pause"})

	case "refresh":
		_, compile := flags["compile"]
		_, force := flags["force"]
		_, keepConsole := flags["keep-console"]
		params := map[string]interface{}{}
		if force {
			params["force"] = true
			params["mode"] = "force"
		}
		if compile {
			params["compile"] = "request"
			return editorRefreshCompile(send, resolve, params, keepConsole, os.Stderr)
		}
		return send("refresh_unity", params)

	default:
		return nil, fmt.Errorf("unknown editor action: %s\nAvailable: play, stop, pause, refresh, quit, restart", action)
	}
}

// editorRefreshCompile requests a refresh plus script compilation, waits for the compile to
// finish, and answers with one report that says what the compile did. It returns the report even
// when the compile failed or timed out, so the JSON is printed before the non-zero exit.
func editorRefreshCompile(send sendFn, resolve instanceResolver, params map[string]interface{}, keepConsole bool, logw io.Writer) (*client.CommandResponse, error) {
	// The connector closes its connection across the domain reload the compile triggers, which Go's
	// HTTP client logs as an "Unsolicited response" line — noise next to the report. Same as verify.
	original := log.Writer()
	log.SetOutput(&suppressWriter{w: original, suppress: "Unsolicited response received on idle HTTP channel"})
	defer log.SetOutput(original)

	// A Burst error stays in the console after its code changes, so a stale one would be reported
	// as this compile's failure; clear it first and let the compile log a live one. Same as verify,
	// including the --keep-console opt-out.
	if !keepConsole {
		if _, err := send("console", map[string]interface{}{"clear": true}); err != nil {
			return nil, err
		}
	}

	resp, err := send("refresh_unity", params)
	if err != nil {
		return nil, err
	}
	if !resp.Success {
		return resp, nil
	}

	// The reply carries the connector's own compile clock at request time, which is the only
	// race-free baseline. The heartbeat fills it in for connectors that do not echo it, and gives
	// the process id used to notice an editor restart while waiting.
	baseline := compileBaseline{}
	trackedReply := false
	if v, ok := reportFieldInt(resp.Data, "compile_finished_at"); ok {
		baseline.FinishedAt, trackedReply = v, true
	}
	if current, rerr := resolve(); rerr == nil && current != nil {
		if !trackedReply {
			baseline.FinishedAt = current.CompileFinishedAt
		}
		baseline.PID = current.PID
	}

	outcome, err := waitForCompileTo(resolve, logw, "editor refresh --compile", baseline)
	if err != nil {
		return nil, err
	}

	findings, err := compileReportDiagnostics(send, outcome.HasErrors)
	if err != nil {
		return nil, fmt.Errorf("compile: %w", err)
	}
	total := findings.Total

	succeeded := !outcome.TimedOut && !outcome.HasErrors && total == 0
	notes := compileReportNotes(outcome, findings.Source, findings.Provenance)
	if err := attachCompileReport(resp, outcome, succeeded, findings, notes); err != nil {
		return nil, err
	}

	switch {
	case outcome.TimedOut:
		resp.Message = fmt.Sprintf("Refresh done; compilation did not finish within %s.", compileWaitTimeout)
		return resp, fmt.Errorf("compilation did not finish within %s (still compiling, or the connector never reported it)", compileWaitTimeout)
	case !succeeded:
		if total > 0 {
			resp.Message = fmt.Sprintf("Refresh done; compilation failed with %d error(s).", total)
		} else {
			resp.Message = "Refresh done; compilation failed."
		}
		return resp, fmt.Errorf("compilation failed: %s", compileFailureLine(total))
	case outcome.NothingToCompile:
		resp.Message = "Refresh done; no script compilation was needed."
	default:
		resp.Message = "Refresh and compilation completed."
	}
	return resp, nil
}

// compileReportDiagnostics reads this compile's errors, retrying while the editor is still back
// from the domain reload the compile triggered: its HTTP connection drops across the reload, so a
// single failed call proves nothing.
func compileReportDiagnostics(send sendFn, hasErrors bool) (compileFindings, error) {
	deadline := time.Now().Add(30 * time.Second)
	for {
		findings, err := verifyDiagnostics(send, compileReportMaxDiags, hasErrors)
		if err == nil || !isConnectionFailure(err) || time.Now().After(deadline) {
			return findings, err
		}
		time.Sleep(statusPollInterval)
	}
}

// isConnectionFailure reports an editor that was momentarily unreachable (mid-reload) rather than
// one that answered with a failure, or one the CLI proved dead: retrying a crashed editor for the
// whole deadline is how an agent spends ten minutes learning Unity is not coming back.
func isConnectionFailure(err error) bool {
	if err == nil {
		return false
	}
	var failure *client.ConnectionFailure
	if errors.As(err, &failure) && failure.Crashed() {
		return false
	}
	msg := err.Error()
	return strings.Contains(msg, "cannot connect to Unity") || strings.Contains(msg, "connection refused") ||
		strings.Contains(msg, "EOF") || strings.Contains(msg, "connection reset")
}

// compileReportNotes explains any part of the result the numbers alone cannot: a compile that was
// never seen to run, a timed-out wait, a restarted editor, or an old connector's console fallback.
func compileReportNotes(outcome compileOutcome, diagSource string, prov *compileProvenance) []string {
	var notes []string
	switch {
	case outcome.TimedOut:
		notes = append(notes, fmt.Sprintf("compilation did not finish within %s; the reported errors are the state at timeout", compileWaitTimeout))
	case outcome.NothingToCompile:
		notes = append(notes, "no script compilation was observed after the request; compile_succeeded is the editor's current compile state")
	case outcome.Restarted:
		notes = append(notes, "the Unity editor restarted while waiting; its startup compile state is reported")
	}
	if outcome.Untracked {
		notes = append(notes, "this connector does not report when a compile finished; the result comes from the heartbeat compileErrors flag")
	}
	if diagSource == "console" {
		notes = append(notes, "connector has no compile_diagnostics; errors were read from the console")
	}
	if prov != nil && prov.StaleCount > 0 {
		notes = append(notes, fmt.Sprintf("%d source file(s) were written after this compile and are not in this result (listed: %s)",
			prov.StaleCount, strings.Join(prov.StaleFiles, ", ")))
	}
	return notes
}

// compileFailureLine words the failure for stderr. total == 0 means the editor flagged a failed
// compile without offering a diagnostic, since a timeout takes the earlier branch.
func compileFailureLine(total int) string {
	if total == 0 {
		return "the editor reported a failed compile but no diagnostic came back; see unity-cli console"
	}
	return fmt.Sprintf("%d error(s), listed in the report", total)
}

// attachCompileReport merges the compile outcome into the connector's refresh payload so the
// command answers with a single self-describing object.
func attachCompileReport(resp *client.CommandResponse, outcome compileOutcome, succeeded bool,
	findings compileFindings, notes []string) error {
	if len(findings.Diags) == 0 {
		findings.Diags = []verifyDiag{}
	}
	data := map[string]interface{}{}
	if len(resp.Data) > 0 && string(resp.Data) != "null" {
		if err := json.Unmarshal(resp.Data, &data); err != nil {
			return fmt.Errorf("unreadable refresh reply: %w", err)
		}
	}

	data["compile_requested"] = true
	data["compile_succeeded"] = succeeded
	data["nothing_to_compile"] = outcome.NothingToCompile
	data["error_count"] = findings.Total
	data["errors"] = findings.Diags
	data["timed_out"] = outcome.TimedOut
	data["elapsed_ms"] = outcome.Elapsed.Milliseconds()
	if len(findings.ProvenanceJSON) > 0 {
		data["provenance"] = findings.ProvenanceJSON
		if findings.Provenance != nil {
			data["provenance_line"] = findings.Provenance.ProvenanceLine
			if findings.Provenance.StaleLine != "" {
				data["stale_line"] = findings.Provenance.StaleLine
			}
		}
	}
	if len(notes) > 0 {
		data["notes"] = notes
	}

	body, err := json.Marshal(data)
	if err != nil {
		return fmt.Errorf("failed to build the compile report: %w", err)
	}
	resp.Data = body
	return nil
}

// reportFieldInt reads one integer from a tool response's data. It reports whether the key was
// present, so an absent key (older connectors) is told apart from a zero value.
func reportFieldInt(data json.RawMessage, key string) (int64, bool) {
	if len(data) == 0 {
		return 0, false
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(data, &fields); err != nil {
		return 0, false
	}
	raw, ok := fields[key]
	if !ok {
		return 0, false
	}
	var n int64
	if err := json.Unmarshal(raw, &n); err != nil {
		return 0, false
	}
	return n, true
}
