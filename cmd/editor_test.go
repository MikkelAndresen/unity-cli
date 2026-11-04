package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestEditorCmd_Play(t *testing.T) {
	shrinkPlayModeWait(t)
	send, params := mockSend("manage_editor", t)
	resp, err := editorCmd(context.Background(), []string{"play"}, send, stateResolver("playing"))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "play" {
		t.Errorf("expected action=play, got %v", (*params)["action"])
	}
	if wait := (*params)["wait_for_completion"]; wait != nil && wait != false {
		t.Errorf("expected no wait_for_completion request, got %v", wait)
	}
	report := playModeReportOf(t, resp)
	if !report.Confirmed || report.EditorState != "playing" {
		t.Errorf("expected confirmation of the playing state, got %+v", report)
	}
}

func TestEditorCmd_PlayWait(t *testing.T) {
	shrinkPlayModeWait(t)
	send, params := mockSend("manage_editor", t)
	if _, err := editorCmd(context.Background(), []string{"play", "--wait"}, send, stateResolver("playing")); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["wait_for_completion"] != true {
		t.Errorf("expected wait_for_completion=true, got %v", (*params)["wait_for_completion"])
	}
}

func TestEditorCmd_Stop(t *testing.T) {
	shrinkPlayModeWait(t)
	send, params := mockSend("manage_editor", t)
	resp, err := editorCmd(context.Background(), []string{"stop"}, send, stateResolver("playing", "ready"))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "stop" {
		t.Errorf("expected action=stop, got %v", (*params)["action"])
	}
	report := playModeReportOf(t, resp)
	if !report.Confirmed || report.EditorState != "ready" {
		t.Errorf("expected the editor to be confirmed back in Edit mode, got %+v", report)
	}
}

// The whole point of #10: leaving Play mode reloads the domain in most projects, and the command
// that asked for it loses its answer to that reload. The heartbeat decides the exit code.
func TestEditorCmd_StopAnswerLostToReloadStillConfirms(t *testing.T) {
	shrinkPlayModeWait(t)
	send := func(command string, params interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{
			Success: false,
			Message: "manage_editor had already started when Unity stopped answering",
			Data:    json.RawMessage(`{"code":"reload_during_command","started":true}`),
		}, nil
	}
	resp, err := editorCmd(context.Background(), []string{"stop"}, send, stateResolver("reloading", "ready"))
	if err != nil {
		t.Fatalf("a play-mode change that reached its state must exit 0, got %v", err)
	}
	if !resp.Success {
		t.Errorf("expected success, got %+v", resp)
	}
	report := playModeReportOf(t, resp)
	if !report.AnswerLost || !report.Confirmed {
		t.Errorf("expected a lost answer confirmed by the heartbeat, got %+v", report)
	}
	if !strings.Contains(resp.Message, "Edit mode") {
		t.Errorf("message must say what state the editor is in now, got %q", resp.Message)
	}
}

func TestEditorCmd_PlayConnectionDroppedByReload(t *testing.T) {
	shrinkPlayModeWait(t)
	send := func(command string, params interface{}) (*client.CommandResponse, error) {
		return nil, &client.ConnectionFailure{
			Class:     client.FailureReload,
			Transport: errors.New("EOF"),
		}
	}
	resp, err := editorCmd(context.Background(), []string{"play"}, send, stateResolver("reloading", "playing"))
	if err != nil {
		t.Fatalf("expected the state check to settle the call, got %v", err)
	}
	report := playModeReportOf(t, resp)
	if !report.AnswerLost || !report.Confirmed {
		t.Errorf("expected a dropped answer confirmed by the heartbeat, got %+v", report)
	}
}

func TestEditorCmd_StopNeverReachesEditMode(t *testing.T) {
	shrinkPlayModeWait(t)
	send, _ := mockSend("manage_editor", t)
	resp, err := editorCmd(context.Background(), []string{"stop"}, send, stateResolver("playing"))
	if err == nil {
		t.Fatal("staying in Play mode must fail the command")
	}
	if resp == nil || resp.Success {
		t.Fatalf("expected a failed report, got %+v", resp)
	}
	report := playModeReportOf(t, resp)
	if report.Confirmed || report.WantedState != "Edit mode" {
		t.Errorf("expected an unconfirmed report, got %+v", report)
	}
}

func TestEditorCmd_NoWaitReturnsImmediately(t *testing.T) {
	resolves := 0
	send, _ := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) {
		resolves++
		return &client.Instance{State: "reloading"}, nil
	}
	if _, err := editorCmd(context.Background(), []string{"stop", "--no-wait"}, send, resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if resolves != 0 {
		t.Errorf("--no-wait must not poll the heartbeat, got %d reads", resolves)
	}
}

// A refusal that says the command never ran must be reported as the connector worded it; waiting for
// a state it never asked for would hide the "retry it" answer behind a timeout.
func TestEditorCmd_BusyRefusalIsNotWaitedOut(t *testing.T) {
	shrinkPlayModeWait(t)
	send := func(command string, params interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{
			Success: false,
			Message: "manage_editor was queued but the main thread did not pick it up",
			Data:    json.RawMessage(`{"code":"main_thread_busy","started":false}`),
		}, nil
	}
	resolves := 0
	resolve := func() (*client.Instance, error) {
		resolves++
		return &client.Instance{State: "playing"}, nil
	}
	resp, err := editorCmd(context.Background(), []string{"stop"}, send, resolve)
	if err != nil {
		t.Fatalf("the connector answered, so there is no transport error: %v", err)
	}
	if resp.Success {
		t.Errorf("expected the refusal to stand, got %+v", resp)
	}
	if resolves != 0 {
		t.Errorf("a command that never ran must not be waited out, got %d heartbeat reads", resolves)
	}
}

func TestEditorCmd_Pause(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd(context.Background(), []string{"pause"}, send, resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "pause" {
		t.Errorf("expected action=pause, got %v", (*params)["action"])
	}
}

// shrinkPlayModeWait takes the play-mode confirmation wait off the test clock.
func shrinkPlayModeWait(t *testing.T) {
	t.Helper()
	timeout, poll := playModeConfirmTimeout, playModePollInterval
	playModeConfirmTimeout = 60 * time.Millisecond
	playModePollInterval = time.Millisecond
	t.Cleanup(func() { playModeConfirmTimeout, playModePollInterval = timeout, poll })
}

// stateResolver hands back one heartbeat state per read, repeating the last one.
func stateResolver(states ...string) instanceResolver {
	read := 0
	return func() (*client.Instance, error) {
		state := states[read]
		if read < len(states)-1 {
			read++
		}
		return &client.Instance{State: state, Port: 8090, Timestamp: time.Now().UnixMilli()}, nil
	}
}

func playModeReportOf(t *testing.T, resp *client.CommandResponse) playModeReport {
	t.Helper()
	if len(resp.Data) == 0 {
		t.Fatalf("the play mode command answered with no report: %+v", resp)
	}
	var report playModeReport
	if err := json.Unmarshal(resp.Data, &report); err != nil {
		t.Fatalf("unreadable play mode report %s: %v", resp.Data, err)
	}
	return report
}

func TestEditorCmd_Refresh(t *testing.T) {
	send, _ := mockSend("refresh_unity", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd(context.Background(), []string{"refresh"}, send, resolve); err != nil {
		t.Errorf("unexpected error: %v", err)
	}
}

func TestEditorCmd_RefreshForce(t *testing.T) {
	send, params := mockSend("refresh_unity", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd(context.Background(), []string{"refresh", "--force"}, send, resolve); err != nil {
		t.Errorf("unexpected error: %v", err)
	}
	if (*params)["force"] != true {
		t.Errorf("expected force=true, got %v", (*params)["force"])
	}
	if (*params)["mode"] != "force" {
		t.Errorf("expected mode=force, got %v", (*params)["mode"])
	}
}

func TestEditorCmd_RefreshCompileForce(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{t: t, diagnostics: clean(), echoFinished: &base, finishedAt: 2_000}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile", "--force"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got := f.params["refresh_unity"]; got["compile"] != "request" || got["force"] != true || got["mode"] != "force" {
		t.Errorf("refresh params = %v, want compile=request, force=true, mode=force", got)
	}
	report := parseCompileReport(t, resp)
	if !report.CompileSucceeded || report.ErrorCount != 0 || report.TimedOut || report.NothingToCompile {
		t.Errorf("report = %+v, want a clean compiled result", report)
	}
}

func TestEditorCmd_RefreshCompileReportsCleanResult(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{t: t, diagnostics: clean(), echoFinished: &base, finishedAt: 2_000}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	report := parseCompileReport(t, resp)
	if !report.CompileRequested || !report.CompileSucceeded || report.ErrorCount != 0 ||
		report.TimedOut || report.NothingToCompile || report.ElapsedMs < 0 {
		t.Errorf("report = %+v, want compile_requested+compile_succeeded, no errors", report)
	}
	if !f.called("console(clear)") {
		t.Error("expected the console to be cleared before the compile so stale errors are not reported")
	}
}

func TestEditorCmd_RefreshCompileReportsErrorsAndFails(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	errs := []interface{}{map[string]interface{}{
		"file": "Assets/CliScratch/Broken.cs", "line": 1, "col": 15,
		"severity": "error", "code": "CS1525", "message": "Invalid expression term ';'",
	}}
	f := &fakeEditor{
		t: t, compileErrors: true, diagnostics: map[bool][]interface{}{true: errs, false: errs},
		echoFinished: &base, finishedAt: 2_000,
	}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err == nil {
		t.Fatal("expected a non-zero-worthy error when the compile fails")
	}
	if resp == nil {
		t.Fatal("expected the compile report to be returned alongside the error")
	}
	report := parseCompileReport(t, resp)
	if report.CompileSucceeded || report.ErrorCount != 1 || len(report.Errors) != 1 ||
		report.Errors[0].File != "Assets/CliScratch/Broken.cs" || report.TimedOut {
		t.Errorf("report = %+v, want one listed error and compile_succeeded=false", report)
	}
}

// The report's error objects must carry the connector's hint: it is what tells the caller what to
// write, and dropping it here would leave the compile failure without its answer.
func TestEditorCmd_RefreshCompileReportCarriesHints(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	errs := []interface{}{map[string]interface{}{
		"file": "Assets/CliScratch/Hints.cs", "line": 12, "col": 29,
		"severity": "error", "code": "CS0619",
		"message": "'Object.GetInstanceID()' is obsolete: 'Use GetEntityId instead.'",
		"hint":    "use UnityEngine.Object.GetEntityId()",
	}}
	f := &fakeEditor{
		t: t, compileErrors: true, diagnostics: map[bool][]interface{}{true: errs, false: errs},
		echoFinished: &base, finishedAt: 2_000,
	}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err == nil {
		t.Fatal("expected a non-zero-worthy error when the compile fails")
	}
	report := parseCompileReport(t, resp)
	if len(report.Errors) != 1 || report.Errors[0].Hint != "use UnityEngine.Object.GetEntityId()" {
		t.Errorf("errors = %+v, want the hint on the single error", report.Errors)
	}
	if !strings.Contains(string(resp.Data), `"hint":"use UnityEngine.Object.GetEntityId()"`) {
		t.Errorf("report json = %s, want the hint key", string(resp.Data))
	}
}

func TestEditorCmd_RefreshCompileHeartbeatErrorsWithoutDiagnostics(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{t: t, compileErrors: true, diagnostics: clean(), echoFinished: &base, finishedAt: 2_000}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err == nil {
		t.Fatal("expected an error when the heartbeat says the compile failed")
	}
	report := parseCompileReport(t, resp)
	if report.CompileSucceeded || report.TimedOut {
		t.Errorf("report = %+v, want compile_succeeded=false without a timeout", report)
	}
}

func TestEditorCmd_RefreshCompileNothingToCompile(t *testing.T) {
	compileTestBudgets(t)
	// The connector's compile clock never moves: no compilation ran after the request, so the
	// result must say so instead of turning an already-"ready" heartbeat into a success.
	base := int64(1_000)
	f := &fakeEditor{t: t, diagnostics: clean(), echoFinished: &base, finishedAt: base}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	report := parseCompileReport(t, resp)
	if !report.NothingToCompile || !report.CompileSucceeded || report.ErrorCount != 0 {
		t.Errorf("report = %+v, want nothing_to_compile with the current clean state", report)
	}
	if !hasNoteContaining(report.Notes, "no script compilation") {
		t.Errorf("notes = %v, want one explaining no compile was observed", report.Notes)
	}
}

func TestEditorCmd_RefreshCompileDistinguishesTimeout(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{
		t: t, diagnostics: clean(), echoFinished: &base, finishedAt: base,
		stuckState: "compiling",
	}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err == nil {
		t.Fatal("expected an error when the compile never finished")
	}
	report := parseCompileReport(t, resp)
	if !report.TimedOut || report.CompileSucceeded || report.NothingToCompile {
		t.Errorf("report = %+v, want timed_out and compile_succeeded=false", report)
	}
}

func TestEditorCmd_RefreshCompileKeepConsole(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{t: t, diagnostics: clean(), echoFinished: &base, finishedAt: 2_000}

	if _, err := editorCmd(context.Background(), []string{"refresh", "--compile", "--keep-console"}, f.send, f.resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if f.called("console(clear)") {
		t.Error("--keep-console must not clear the console")
	}
}

func TestEditorCmd_RefreshCompileReportsOldConnector(t *testing.T) {
	compileTestBudgets(t)
	// No compile clock anywhere: the wait can only report what the heartbeat says, and must say
	// that it could not prove a compile ran.
	f := &fakeEditor{t: t, diagnostics: clean()}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	report := parseCompileReport(t, resp)
	if !report.CompileSucceeded || report.NothingToCompile || report.TimedOut {
		t.Errorf("report = %+v, want the heartbeat's clean state reported", report)
	}
	if !hasNoteContaining(report.Notes, "does not report when a compile finished") {
		t.Errorf("notes = %v, want one about the missing compile clock", report.Notes)
	}
}

func TestEditorCmd_RefreshCompileEditorRestarted(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{
		t: t, diagnostics: clean(), echoFinished: &base,
		finishedAt: base, pid: os.Getpid(),
	}
	resolveCount := 0
	resolve := func() (*client.Instance, error) {
		inst, err := f.resolve()
		if err != nil {
			return nil, err
		}
		resolveCount++
		if resolveCount > 1 {
			inst.PID = os.Getpid() + 1
			inst.CompileFinishedAt = 0
		}
		return inst, nil
	}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	report := parseCompileReport(t, resp)
	if report.TimedOut || report.NothingToCompile {
		t.Errorf("report = %+v, want the restarted editor's state, not a timeout", report)
	}
	if !hasNoteContaining(report.Notes, "restarted") {
		t.Errorf("notes = %v, want one about the editor restart", report.Notes)
	}
}

func TestEditorCmd_RefreshWithoutCompileDoesNotWaitOrClear(t *testing.T) {
	f := &fakeEditor{t: t}
	resp, err := editorCmd(context.Background(), []string{"refresh"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if resp == nil || !resp.Success {
		t.Fatalf("expected a successful refresh response, got %+v", resp)
	}
	if got := f.params["refresh_unity"]; got["compile"] != nil {
		t.Errorf("refresh params = %v, want no compile", got)
	}
	if f.called("console(clear)") || f.called("compile_diagnostics(source_tree_only=true)") {
		t.Errorf("plain refresh made compile calls: %v", f.calls)
	}
}

func compileTestBudgets(t *testing.T) {
	t.Helper()
	origPoll, origWait, origGrace := statusPollInterval, compileWaitTimeout, compileIdleGrace
	statusPollInterval = time.Millisecond
	compileWaitTimeout = 300 * time.Millisecond
	compileIdleGrace = 20 * time.Millisecond
	t.Cleanup(func() {
		statusPollInterval, compileWaitTimeout, compileIdleGrace = origPoll, origWait, origGrace
	})
}

// compileReport mirrors the JSON `unity-cli editor refresh --compile` answers with.
type compileReport struct {
	RefreshTriggered  bool               `json:"refresh_triggered"`
	CompileRequested  bool               `json:"compile_requested"`
	CompileSucceeded  bool               `json:"compile_succeeded"`
	NothingToCompile  bool               `json:"nothing_to_compile"`
	ErrorCount        int                `json:"error_count"`
	Errors            []verifyDiag       `json:"errors"`
	TimedOut          bool               `json:"timed_out"`
	ElapsedMs         int64              `json:"elapsed_ms"`
	Notes             []string           `json:"notes"`
	ProvenanceLine    string             `json:"provenance_line"`
	StaleLine         string             `json:"stale_line"`
	StaleAfterCompile bool               `json:"stale_after_compile"`
	Provenance        *compileProvenance `json:"provenance"`

	StaleAssemblyCount        int             `json:"stale_assembly_count"`
	StaleAssemblyCoveredCount int             `json:"stale_assembly_covered_count"`
	StaleAssemblyCoveredLine  string          `json:"stale_assembly_covered_line"`
	StaleAssemblies           []staleAssembly `json:"stale_assemblies"`
}

// The refresh report has to say which compile it answers with, or a clean report from a stale
// editor looks identical to one that covers the caller's edit.
func TestEditorCmd_RefreshCompileReportCarriesProvenance(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{t: t, diagnostics: clean(), echoFinished: &base, finishedAt: 2_000,
		provenance: map[string]interface{}{"provenance_line": provLine, "compiled_at_ms": 2_000,
			"rebuilt_assemblies": []string{"Game"}}}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	report := parseCompileReport(t, resp)
	if report.ProvenanceLine != provLine {
		t.Errorf("provenance_line = %q, want the connector's line", report.ProvenanceLine)
	}
	if report.Provenance == nil || report.Provenance.CompiledAtMs != 2_000 ||
		len(report.Provenance.RebuiltAssemblies) != 1 || report.Provenance.RebuiltAssemblies[0] != "Game" {
		t.Errorf("provenance = %+v, want the connector's fields", report.Provenance)
	}
}

func TestEditorCmd_RefreshCompileReportNotesStaleSources(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{t: t, diagnostics: clean(), echoFinished: &base, finishedAt: 2_000,
		provenance: map[string]interface{}{"provenance_line": provLine, "stale_count": 1,
			"stale_files": []string{"Assets/Foo.cs"}, "stale_line": "stale: Assets/Foo.cs modified after this compile"}}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	report := parseCompileReport(t, resp)
	if report.StaleLine == "" {
		t.Errorf("report = %+v, want the stale line echoed", report)
	}
	if !hasNoteContaining(report.Notes, "written after this compile") {
		t.Errorf("notes = %v, want one that names the uncompiled sources", report.Notes)
	}
}

func TestEditorCmd_RefreshCompileRetriesStaleAssemblyWithCleanBuild(t *testing.T) {
	compileTestBudgets(t)
	const staleLine = "stale assembly: Game.dll (built 12:01:00) is older than Assets/Game.cs (12:04:00) — run unity-cli editor refresh --compile"
	f := &fakeEditor{t: t, diagnostics: clean(),
		refreshReplies: []map[string]interface{}{
			{"clean_compile_supported": true, "compile_finished_at": 1000},
			{"clean_compile_supported": true, "compile_finished_at": 2000},
		},
		provenanceSeq: []map[string]interface{}{
			{"provenance_line": provLine, "stale_assembly_count": 1, "stale_assembly_line": staleLine},
			{"provenance_line": provLine, "stale_assembly_count": 0},
		}}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	report := parseCompileReport(t, resp)
	if report.StaleAfterCompile {
		t.Errorf("report = %+v, want stale_after_compile=false after the clean rebuild", report)
	}
	if !hasNoteContaining(report.Notes, "CleanBuildCache rebuild fixed") {
		t.Errorf("notes = %v, want the clean retry explained", report.Notes)
	}
	if !f.called("refresh_unity") || f.params["refresh_unity"]["compile"] != "clean" {
		t.Errorf("refresh params = %v, want the retry to request a clean compile", f.params["refresh_unity"])
	}
}

func TestEditorCmd_RefreshCompileReportsStaleAfterCleanRetry(t *testing.T) {
	compileTestBudgets(t)
	const staleLine = "stale assembly: Game.dll (missing) is older than Assets/Game.cs (12:04:00) — run unity-cli editor refresh --compile"
	f := &fakeEditor{t: t, diagnostics: clean(),
		refreshReplies: []map[string]interface{}{
			{"clean_compile_supported": true, "compile_finished_at": 1000},
			{"clean_compile_supported": true, "compile_finished_at": 2000},
		},
		provenanceSeq: []map[string]interface{}{
			{"provenance_line": provLine, "stale_assembly_count": 1, "stale_assembly_line": staleLine},
			{"provenance_line": provLine, "stale_assembly_count": 1, "stale_assembly_line": staleLine},
		}}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	report := parseCompileReport(t, resp)
	if !report.StaleAfterCompile {
		t.Errorf("report = %+v, want stale_after_compile=true when the clean rebuild did not fix it", report)
	}
	if !hasNoteContaining(report.Notes, "still stale after one CleanBuildCache") {
		t.Errorf("notes = %v, want the persisted staleness explained", report.Notes)
	}
}

// The request-time staleness note tells the caller to run `editor refresh --compile clean`; the CLI
// has to actually send that mode instead of a plain request.
func TestEditorCmd_RefreshCompileCleanDiscardsTheBuildCache(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{t: t, diagnostics: clean(), echoFinished: &base, finishedAt: 2_000}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile", "clean"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got := f.params["refresh_unity"]; got["compile"] != "clean" {
		t.Errorf("refresh params = %v, want compile=clean", got)
	}
	if report := parseCompileReport(t, resp); !report.CompileSucceeded {
		t.Errorf("report = %+v, want the clean compile reported like any other", report)
	}
}

// An unknown compile mode reaches the connector as "don't compile", which would answer a request to
// rebuild with a refresh that rebuilt nothing.
func TestEditorCmd_RefreshCompileRejectsAnUnknownMode(t *testing.T) {
	compileTestBudgets(t)
	f := &fakeEditor{t: t, diagnostics: clean()}

	if _, err := editorCmd(context.Background(), []string{"refresh", "--compile", "sometimes"}, f.send, f.resolve); err == nil {
		t.Fatal("want an error for an unknown --compile value")
	}
	if f.called("refresh_unity") {
		t.Errorf("calls = %v, want nothing sent to the editor", f.calls)
	}
}

// The refresh answer used to carry the staleness it measured before asking for the compile next to
// the post-compile verdict, so a report could say stale_after_compile:false while listing two stale
// assemblies. The post-compile numbers win.
func TestEditorCmd_RefreshCompileRestatesStalenessAfterTheCompile(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{t: t, diagnostics: clean(), echoFinished: &base, finishedAt: 2_000,
		refreshReplies: []map[string]interface{}{{
			"clean_compile_supported": true,
			"compile_finished_at":     1000,
			"stale_assembly_count":    2,
			"stale_assemblies":        []map[string]interface{}{{"name": "Game.Tests"}, {"name": "Game"}},
		}},
		provenance: map[string]interface{}{"provenance_line": provLine, "stale_assembly_count": 0,
			"stale_assembly_covered_count": 2, "stale_assembly_covered_line": "not stale: Game.dll"}}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, f.send, f.resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	report := parseCompileReport(t, resp)
	if report.StaleAfterCompile {
		t.Errorf("report = %+v, want stale_after_compile=false", report)
	}
	if report.StaleAssemblyCount != 0 || len(report.StaleAssemblies) != 0 {
		t.Errorf("report = %+v, want the request-time stale list replaced by the post-compile one", report)
	}
	if report.StaleAssemblyCoveredCount != 2 || report.StaleAssemblyCoveredLine == "" {
		t.Errorf("report = %+v, want the covered count and line the compile reported", report)
	}
}

func parseCompileReport(t *testing.T, resp *client.CommandResponse) compileReport {
	t.Helper()
	if resp == nil || len(resp.Data) == 0 {
		t.Fatalf("response carries no compile report: %+v", resp)
	}
	var r compileReport
	if err := json.Unmarshal(resp.Data, &r); err != nil {
		t.Fatalf("report is not readable: %v\n%s", err, resp.Data)
	}
	if !r.RefreshTriggered {
		t.Errorf("report lost the connector's refresh fields: %s", resp.Data)
	}
	return r
}

func hasNoteContaining(notes []string, want string) bool {
	for _, n := range notes {
		if strings.Contains(n, want) {
			return true
		}
	}
	return false
}

func TestEditorCmd_RefreshCompileFailureDoesNotWait(t *testing.T) {
	compileTestBudgets(t)
	resolveCalled := false
	var calls []string
	send := func(cmd string, params interface{}) (*client.CommandResponse, error) {
		calls = append(calls, cmd)
		if cmd == "console" {
			return &client.CommandResponse{Success: true, Message: "Console cleared."}, nil
		}
		if cmd != "refresh_unity" {
			t.Errorf("send called with command %q, want refresh_unity", cmd)
		}
		return &client.CommandResponse{Success: false, Message: "blocked"}, nil
	}
	resolve := func() (*client.Instance, error) {
		resolveCalled = true
		return &client.Instance{State: "ready"}, nil
	}

	resp, err := editorCmd(context.Background(), []string{"refresh", "--compile"}, send, resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if resp == nil || resp.Success {
		t.Fatalf("expected failed response, got %+v", resp)
	}
	if resolveCalled {
		t.Error("expected refresh failure to skip compilation wait")
	}
	for _, c := range calls {
		if c == "compile_diagnostics" {
			t.Error("expected refresh failure to skip reading compile diagnostics")
		}
	}
}

func TestEditorCmd_EmptyArgs(t *testing.T) {
	send, _ := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	_, err := editorCmd(context.Background(), nil, send, resolve)
	if err == nil {
		t.Error("expected error for empty args")
	}
}

func TestEditorCmd_UnknownAction(t *testing.T) {
	send, _ := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	_, err := editorCmd(context.Background(), []string{"fly"}, send, resolve)
	if err == nil {
		t.Error("expected error for unknown action")
	}
}
