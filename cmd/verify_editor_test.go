package cmd

import (
	"bytes"
	"context"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// editorVerifyFake answers the connector's verify tool with queued replies and records each request.
type editorVerifyFake struct {
	t        *testing.T
	replies  []*client.CommandResponse
	sent     []map[string]interface{}
	wrongJob bool // answer without echoing the job id, like a result kept from another job
}

func (f *editorVerifyFake) send(command string, params interface{}) (*client.CommandResponse, error) {
	if command != "verify" {
		f.t.Fatalf("unexpected command %q: the in-editor verify runs every step itself", command)
	}
	f.sent = append(f.sent, params.(map[string]interface{}))
	if len(f.replies) == 0 {
		f.t.Fatalf("verify sent %d times, more than expected", len(f.sent))
	}
	r := f.replies[0]
	f.replies = f.replies[1:]
	if r.Success && !f.wrongJob {
		var data map[string]interface{}
		if err := json.Unmarshal(r.Data, &data); err == nil {
			data["job"] = params.(map[string]interface{})["job"]
			b, _ := json.Marshal(data)
			r = &client.CommandResponse{Success: r.Success, Message: r.Message, Data: b}
		}
	}
	return r, nil
}

func (f *editorVerifyFake) resolve() (*client.Instance, error) {
	return &client.Instance{State: "ready", Timestamp: time.Now().UnixMilli()}, nil
}

func (f *editorVerifyFake) run(args ...string) (int, string) {
	f.t.Helper()
	orig := statusPollInterval
	statusPollInterval = time.Millisecond
	defer func() { statusPollInterval = orig }()
	var out bytes.Buffer
	code := verifyCmd(context.Background(), args, f.send, f.send, f.resolve, 0, &out)
	return code, out.String()
}

func editorReply(t *testing.T, v map[string]interface{}) *client.CommandResponse {
	t.Helper()
	b, err := json.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	return &client.CommandResponse{Success: true, Message: "verify finished", Data: b}
}

func cleanDiagnostics() map[string]interface{} {
	return map[string]interface{}{"compiling": false, "total": 0, "diagnostics": []interface{}{}}
}

func TestEditorVerify_OneRequestForCompileAndTests(t *testing.T) {
	f := &editorVerifyFake{t: t, replies: []*client.CommandResponse{editorReply(t, map[string]interface{}{
		"compile":     map[string]interface{}{"has_errors": false},
		"diagnostics": cleanDiagnostics(),
		"tests":       map[string]interface{}{"success": true, "message": "All 3 test(s) passed.", "data": passing(3)},
	})}}
	code, out := f.run("--assembly", "Game.Tests")
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if !strings.Contains(out, "compile: ok") || !strings.Contains(out, "tests: 3/3 passed") {
		t.Errorf("output = %q, want the compile and test lines", out)
	}
	if len(f.sent) != 1 || f.sent[0]["assembly"] != "Game.Tests" || f.sent[0]["job"] == "" {
		t.Errorf("requests = %v, want one verify carrying the job id and the selection", f.sent)
	}
}

func TestEditorVerify_CompileErrorsStopBeforeTests(t *testing.T) {
	f := &editorVerifyFake{t: t, replies: []*client.CommandResponse{editorReply(t, map[string]interface{}{
		"compile": map[string]interface{}{"has_errors": true},
		"diagnostics": map[string]interface{}{"compiling": false, "total": 1, "diagnostics": []interface{}{
			map[string]interface{}{"file": "Assets/A.cs", "line": 3, "col": 5, "severity": "error", "code": "CS1002", "message": "; expected"},
		}},
		"stopped": "compile",
	})}}
	code, out := f.run()
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1; output:\n%s", code, out)
	}
	if !strings.Contains(out, "compile: 1 error(s)") || !strings.Contains(out, "Assets/A.cs:3:5 CS1002 ; expected") {
		t.Errorf("output = %q, want the error listed", out)
	}
	if strings.Contains(out, "tests:") {
		t.Errorf("output = %q, want no test line after a failed compile", out)
	}
}

// A successful compile reloads the domain and drops the request; the job keeps running in the
// editor, so the CLI asks again with the same job id.
func TestEditorVerify_ReattachesToItsJobAfterAReload(t *testing.T) {
	lost := &client.CommandResponse{Success: false, Message: "domain reload", Data: json.RawMessage(`{"code":"reload_during_command"}`)}
	f := &editorVerifyFake{t: t, replies: []*client.CommandResponse{lost, editorReply(t, map[string]interface{}{
		"compile":     map[string]interface{}{"has_errors": false},
		"diagnostics": cleanDiagnostics(),
		"tests":       map[string]interface{}{"success": true, "data": passing(2)},
	})}}
	code, out := f.run()
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if len(f.sent) != 2 || f.sent[0]["job"] != f.sent[1]["job"] {
		t.Errorf("requests = %v, want the same job sent twice", f.sent)
	}
}

// The tests run while Burst finishes, so a Burst error is reported next to the test result.
func TestEditorVerify_BurstErrorFailsEvenWhenTestsPass(t *testing.T) {
	f := &editorVerifyFake{t: t, replies: []*client.CommandResponse{editorReply(t, map[string]interface{}{
		"compile":     map[string]interface{}{"has_errors": false},
		"diagnostics": cleanDiagnostics(),
		"burst": map[string]interface{}{"found": true, "success": true, "data": map[string]interface{}{
			"compiled": 2, "error_count": 1,
			"errors": []interface{}{map[string]interface{}{"code": "BC1016", "message": "managed call"}},
		}},
		"tests": map[string]interface{}{"success": true, "data": passing(4)},
	})}}
	code, out := f.run()
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1; output:\n%s", code, out)
	}
	if !strings.Contains(out, "burst: 2 targets compiled, 1 error(s)") || !strings.Contains(out, "tests: 4/4 passed") {
		t.Errorf("output = %q, want both the Burst error and the test result", out)
	}
}

func TestEditorVerify_JobErrorIsNoReply(t *testing.T) {
	f := &editorVerifyFake{t: t, replies: []*client.CommandResponse{editorReply(t, map[string]interface{}{
		"error": "compile: still compiling after 5m0s", "error_exit": 2,
	})}}
	code, out := f.run()
	if code != verifyNoReply || !strings.Contains(out, "still compiling") {
		t.Errorf("exit = %d, output = %q; want 2 with the job's error", code, out)
	}
}

func TestEditorVerify_StaleAssemblyRefusesTests(t *testing.T) {
	diag := cleanDiagnostics()
	diag["provenance"] = map[string]interface{}{
		"compiled_at_ms": 1, "stale_assembly_count": 1,
		"stale_assemblies":    []interface{}{map[string]interface{}{"name": "Game.Tests"}},
		"stale_assembly_line": "Game.Tests.dll is older than its sources",
	}
	f := &editorVerifyFake{t: t, replies: []*client.CommandResponse{editorReply(t, map[string]interface{}{
		"compile": map[string]interface{}{"has_errors": false}, "diagnostics": diag, "stopped": "stale",
	})}}
	code, out := f.run("--assembly", "Game.Tests")
	if code != verifyFailed || !strings.Contains(out, "tests: refused (1 stale assembly(s))") {
		t.Errorf("exit = %d, output = %q; want the stale refusal", code, out)
	}
}

// A result that belongs to another job must not pass for this one's.
func TestEditorVerify_AnotherJobsResultIsRejected(t *testing.T) {
	f := &editorVerifyFake{t: t, wrongJob: true, replies: []*client.CommandResponse{editorReply(t, map[string]interface{}{
		"job": "someone-else", "compile": map[string]interface{}{"has_errors": false}, "diagnostics": cleanDiagnostics(),
		"tests": map[string]interface{}{"success": true, "data": passing(1)},
	})}}
	code, out := f.run()
	if code != verifyNoReply || !strings.Contains(out, "not this one") {
		t.Errorf("exit = %d, output = %q; want the mismatch reported", code, out)
	}
}
