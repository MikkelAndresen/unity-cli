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
