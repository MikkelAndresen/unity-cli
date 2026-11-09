package cmd

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestTestCmd_ForwardsDirtySceneOptions(t *testing.T) {
	var captured map[string]interface{}
	send := func(cmd string, params interface{}) (*client.CommandResponse, error) {
		if cmd != "run_tests" {
			t.Fatalf("send called with command %q, want run_tests", cmd)
		}
		var ok bool
		captured, ok = params.(map[string]interface{})
		if !ok {
			t.Fatalf("params type = %T, want map[string]interface{}", params)
		}
		return &client.CommandResponse{Success: true}, nil
	}

	resp, err := testCmd(context.Background(), []string{"--allow-dirty-scenes", "--auto-save-scenes"}, send, 0)
	if err != nil {
		t.Fatalf("testCmd returned error: %v", err)
	}
	if resp == nil || !resp.Success {
		t.Fatalf("testCmd response = %#v, want success", resp)
	}
	if captured["allowDirtyScenes"] != true {
		t.Errorf("allowDirtyScenes = %v, want true", captured["allowDirtyScenes"])
	}
	if captured["autoSaveScenes"] != true {
		t.Errorf("autoSaveScenes = %v, want true", captured["autoSaveScenes"])
	}
}

// The connector stamps the results file with the port that ran the tests. A resolve before the send
// can name an older port than the one the send (or the compile before it) lands on, so the poll must
// follow the reply; polling the pre-send port here would leave this test hanging until its deadline.
func TestTestCmd_PlayModePollsThePortFromTheRunReply(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	statusDir := filepath.Join(home, ".unity-cli", "status")
	if err := os.MkdirAll(statusDir, 0o755); err != nil {
		t.Fatalf("create status dir: %v", err)
	}
	runPort := freeLocalPort(t)
	stalePort := freeLocalPort(t)
	resultsPath := filepath.Join(statusDir, fmt.Sprintf("test-results-%d.json", runPort))
	if err := os.WriteFile(resultsPath, []byte(`{"success":true,"message":"All 1 test(s) passed.","data":{"total":1,"passed":1}}`), 0o644); err != nil {
		t.Fatalf("write results: %v", err)
	}

	send := func(cmd string, params interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{
			Success: true,
			Message: "running",
			Data:    []byte(fmt.Sprintf(`{"port":%d}`, runPort)),
		}, nil
	}

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	resp, err := testCmd(ctx, []string{"--mode", "PlayMode"}, send, stalePort)
	if err != nil {
		t.Fatalf("testCmd did not poll the run's port: %v", err)
	}
	if resp.Message != "All 1 test(s) passed." {
		t.Fatalf("unexpected response: %+v", resp)
	}
}

// zeroTestReply is a successful run_tests reply that ran nothing.
func zeroTestReply() *client.CommandResponse {
	return &client.CommandResponse{
		Success: true,
		Message: "All 0 test(s) passed.",
		Data:    []byte(`{"total":0,"passed":0,"failed":0,"skipped":0,"failures":[],"passes":[]}`),
	}
}

// TestTestCmd_NoTestsMatchedIsNotSuccess: `test --filter NoSuchTest` used to exit 0 with
// {"total":0,...}, which reads as a passing run (F4).
func TestTestCmd_NoTestsMatchedIsNotSuccess(t *testing.T) {
	tests := []struct {
		name string
		args []string
		want string
	}{
		{
			name: "filter matched nothing",
			args: []string{"--filter", "NoSuchTestXyz"},
			want: "no tests matched (filter NoSuchTestXyz)",
		},
		{
			name: "unknown assembly",
			args: []string{"--assembly", "NoSuchAssemblyXyz"},
			want: "no tests matched (assembly NoSuchAssemblyXyz)",
		},
		{
			name: "both",
			args: []string{"--assembly", "A.Tests", "--filter", "Nope"},
			want: "no tests matched (filter Nope, assembly A.Tests)",
		},
		{
			name: "nothing selected even without a filter",
			args: nil,
			want: "no tests matched: the run selected no tests in this mode",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			resp := zeroTestReply()
			flags, err := parseSubFlags(tt.args)
			if err != nil {
				t.Fatalf("parseSubFlags: %v", err)
			}
			applyTestVerdict(resp, flags)
			if resp.Success {
				t.Fatalf("resp.Success = true, want the empty run to fail")
			}
			if resp.Message != tt.want {
				t.Errorf("message = %q, want %q", resp.Message, tt.want)
			}
		})
	}
}

func TestTestCmd_APassingRunKeepsItsVerdict(t *testing.T) {
	resp := &client.CommandResponse{
		Success: true,
		Data:    []byte(`{"total":3,"passed":3,"failed":0,"skipped":0}`),
	}
	applyTestVerdict(resp, map[string]string{"filter": "Mine"})
	if !resp.Success {
		t.Error("resp.Success = false, want a run that executed tests to keep its verdict")
	}
}

func TestTestCmd_AFailureThatRanNothingKeepsItsOwnMessage(t *testing.T) {
	resp := &client.CommandResponse{
		Success: false,
		Message: "Open scenes have unsaved changes.",
		Data:    []byte(`{"blocked":"dirty-scenes","total":0}`),
	}
	applyTestVerdict(resp, nil)
	if resp.Message != "Open scenes have unsaved changes." {
		t.Errorf("message = %q, want the connector's refusal left alone", resp.Message)
	}
}

// TestTestCmd_StaleAssemblyOutsideAssemblyIsNoted: verify prints a note when a stale assembly sits
// outside --assembly; test runs the same scoped selection, so it must say the same thing instead of
// printing nothing.
func TestTestCmd_StaleAssemblyOutsideAssemblyIsNoted(t *testing.T) {
	stale := "Game.Tests.dll (built 14:01:50) is older than Assets/Game/Tests.cs (14:04:02)"
	provJSON := fmt.Sprintf(`{
		"compiled_at_ms": 1, "stale_assembly_count": 1,
		"stale_assemblies": [{"name":"Game.Tests","newest_source":"Assets/Game/Tests.cs"}],
		"stale_assembly_line": %q
	}`, stale)
	send := func(command string, _ interface{}) (*client.CommandResponse, error) {
		if command != "compile_status" {
			t.Fatalf("unexpected command %q", command)
		}
		return &client.CommandResponse{Success: true, Data: []byte(provJSON)}, nil
	}

	note := staleOutsideSelectionNote(send, "Other.Tests.Editor")
	want := "1 stale assembly(ies) are outside --assembly; the tests will not load them"
	if note != want {
		t.Errorf("note = %q, want %q", note, want)
	}
}

func TestTestCmd_StaleAssemblyUnderTestNeedsNoNote(t *testing.T) {
	provJSON := `{
		"compiled_at_ms": 1, "stale_assembly_count": 1,
		"stale_assemblies": [{"name":"Game.Tests","newest_source":"Assets/Game/Tests.cs"}]
	}`
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true, Data: []byte(provJSON)}, nil
	}
	if note := staleOutsideSelectionNote(send, "Game.Tests"); note != "" {
		t.Errorf("note = %q, want none when the stale assembly is the one under test", note)
	}
	if note := staleOutsideSelectionNote(send, ""); note != "" {
		t.Errorf("note = %q, want none for an unscoped run", note)
	}
}
