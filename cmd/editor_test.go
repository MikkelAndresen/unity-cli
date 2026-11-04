package cmd

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestEditorCmd_Play(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"play"}, send, resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "play" {
		t.Errorf("expected action=play, got %v", (*params)["action"])
	}
	if (*params)["wait_for_completion"] != false {
		t.Errorf("expected wait_for_completion=false, got %v", (*params)["wait_for_completion"])
	}
}

func TestEditorCmd_PlayWait(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"play", "--wait"}, send, resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["wait_for_completion"] != true {
		t.Errorf("expected wait_for_completion=true, got %v", (*params)["wait_for_completion"])
	}
}

func TestEditorCmd_Stop(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"stop"}, send, resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "stop" {
		t.Errorf("expected action=stop, got %v", (*params)["action"])
	}
}

func TestEditorCmd_Pause(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"pause"}, send, resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "pause" {
		t.Errorf("expected action=pause, got %v", (*params)["action"])
	}
}

func TestEditorCmd_Refresh(t *testing.T) {
	send, _ := mockSend("refresh_unity", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"refresh"}, send, resolve); err != nil {
		t.Errorf("unexpected error: %v", err)
	}
}

func TestEditorCmd_RefreshForce(t *testing.T) {
	send, params := mockSend("refresh_unity", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"refresh", "--force"}, send, resolve); err != nil {
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

	resp, err := editorCmd([]string{"refresh", "--compile", "--force"}, f.send, f.resolve)
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

	resp, err := editorCmd([]string{"refresh", "--compile"}, f.send, f.resolve)
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

	resp, err := editorCmd([]string{"refresh", "--compile"}, f.send, f.resolve)
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

	resp, err := editorCmd([]string{"refresh", "--compile"}, f.send, f.resolve)
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

	resp, err := editorCmd([]string{"refresh", "--compile"}, f.send, f.resolve)
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

	resp, err := editorCmd([]string{"refresh", "--compile"}, f.send, f.resolve)
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

	resp, err := editorCmd([]string{"refresh", "--compile"}, f.send, f.resolve)
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

	if _, err := editorCmd([]string{"refresh", "--compile", "--keep-console"}, f.send, f.resolve); err != nil {
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

	resp, err := editorCmd([]string{"refresh", "--compile"}, f.send, f.resolve)
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

	resp, err := editorCmd([]string{"refresh", "--compile"}, f.send, resolve)
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
	resp, err := editorCmd([]string{"refresh"}, f.send, f.resolve)
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
	RefreshTriggered bool               `json:"refresh_triggered"`
	CompileRequested bool               `json:"compile_requested"`
	CompileSucceeded bool               `json:"compile_succeeded"`
	NothingToCompile bool               `json:"nothing_to_compile"`
	ErrorCount       int                `json:"error_count"`
	Errors           []verifyDiag       `json:"errors"`
	TimedOut         bool               `json:"timed_out"`
	ElapsedMs        int64              `json:"elapsed_ms"`
	Notes            []string           `json:"notes"`
	ProvenanceLine   string             `json:"provenance_line"`
	StaleLine        string             `json:"stale_line"`
	Provenance       *compileProvenance `json:"provenance"`
}

// The refresh report has to say which compile it answers with, or a clean report from a stale
// editor looks identical to one that covers the caller's edit.
func TestEditorCmd_RefreshCompileReportCarriesProvenance(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{t: t, diagnostics: clean(), echoFinished: &base, finishedAt: 2_000,
		provenance: map[string]interface{}{"provenance_line": provLine, "compiled_at_ms": 2_000,
			"rebuilt_assemblies": []string{"Game"}}}

	resp, err := editorCmd([]string{"refresh", "--compile"}, f.send, f.resolve)
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

	resp, err := editorCmd([]string{"refresh", "--compile"}, f.send, f.resolve)
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

	resp, err := editorCmd([]string{"refresh", "--compile"}, send, resolve)
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
	_, err := editorCmd(nil, send, resolve)
	if err == nil {
		t.Error("expected error for empty args")
	}
}

func TestEditorCmd_UnknownAction(t *testing.T) {
	send, _ := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	_, err := editorCmd([]string{"fly"}, send, resolve)
	if err == nil {
		t.Error("expected error for unknown action")
	}
}
