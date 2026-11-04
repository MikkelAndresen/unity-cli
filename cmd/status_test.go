package cmd

import (
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func writeInstanceFile(t *testing.T, inst client.Instance) string {
	t.Helper()
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	dir := filepath.Join(home, ".unity-cli", "instances")
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatalf("failed to create instances dir: %v", err)
	}
	data, err := json.Marshal(inst)
	if err != nil {
		t.Fatalf("failed to marshal instance: %v", err)
	}
	// Use a fixed filename for testing
	path := filepath.Join(dir, "test.json")
	if err := os.WriteFile(path, data, 0644); err != nil {
		t.Fatalf("failed to write instance file: %v", err)
	}
	return home
}

func TestReadStatus_ValidFile(t *testing.T) {
	want := client.Instance{
		State:            "ready",
		ProjectPath:      "/home/user/MyProject",
		Port:             8090,
		PID:              os.Getpid(),
		UnityVersion:     "6000.3.10f1",
		ConnectorVersion: "0.3.10",
		Timestamp:        1000000,
	}

	writeInstanceFile(t, want)

	got, err := readStatus(8090)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.State != want.State {
		t.Errorf("State: got %q, want %q", got.State, want.State)
	}
	if got.Port != want.Port {
		t.Errorf("Port: got %d, want %d", got.Port, want.Port)
	}
	if got.ProjectPath != want.ProjectPath {
		t.Errorf("ProjectPath: got %q, want %q", got.ProjectPath, want.ProjectPath)
	}
	if got.ConnectorVersion != want.ConnectorVersion {
		t.Errorf("ConnectorVersion: got %q, want %q", got.ConnectorVersion, want.ConnectorVersion)
	}
}

func TestReadStatus_MissingFile(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	_, err := readStatus(9999)
	if err == nil {
		t.Error("expected error for missing status file")
	}
}

func TestReadStatus_InvalidJSON(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	dir := filepath.Join(home, ".unity-cli", "instances")
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatalf("failed to create dir: %v", err)
	}
	if err := os.WriteFile(filepath.Join(dir, "test.json"), []byte("not json"), 0644); err != nil {
		t.Fatalf("failed to write file: %v", err)
	}

	_, err := readStatus(8090)
	if err == nil {
		t.Error("expected error for invalid JSON")
	}
}

func TestDiscoverStatusInstance_PortAllowsStoppedInstance(t *testing.T) {
	want := client.Instance{
		State:       "stopped",
		ProjectPath: "/home/user/MyProject",
		Port:        8090,
		PID:         os.Getpid(),
		Timestamp:   1000000,
	}

	writeInstanceFile(t, want)

	got, err := discoverStatusInstance("", 8090)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.State != "stopped" {
		t.Errorf("State: got %q, want stopped", got.State)
	}
	if got.Port != 8090 {
		t.Errorf("Port: got %d, want 8090", got.Port)
	}
}

func TestStatusCmd_PatchDriftSucceeds(t *testing.T) {
	origVersion := Version
	Version = "0.7.1"
	t.Cleanup(func() { Version = origVersion })

	writeInstanceFile(t, client.Instance{
		State:            "ready",
		ProjectPath:      "/home/user/MyProject",
		Port:             8090,
		PID:              os.Getpid(),
		UnityVersion:     "6000.3.10f1",
		ConnectorVersion: "0.7.0",
		Timestamp:        time.Now().UnixMilli(),
	})

	out := captureStdout(t, func() {
		if err := statusCmd(&client.Instance{Port: 8090}); err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
	})
	if !strings.Contains(out, "CLI:     0.7.1") || !strings.Contains(out, "Connector: 0.7.0") {
		t.Fatalf("status must show both versions, got:\n%s", out)
	}
	if !strings.Contains(out, "Compatibility: compatible") {
		t.Fatalf("status must show the compatibility verdict, got:\n%s", out)
	}
}

func TestStatusCmd_DoesNotGateAnOldConnector(t *testing.T) {
	origVersion := Version
	Version = "0.8.0"
	t.Cleanup(func() { Version = origVersion })

	writeInstanceFile(t, client.Instance{
		State:            "ready",
		ProjectPath:      "/home/user/MyProject",
		Port:             8090,
		PID:              os.Getpid(),
		UnityVersion:     "6000.3.10f1",
		ConnectorVersion: "0.6.0",
		Timestamp:        time.Now().UnixMilli(),
	})

	out := captureStdout(t, func() {
		if err := statusCmd(&client.Instance{Port: 8090}); err != nil {
			t.Fatalf("status must not fail on a version mismatch: %v", err)
		}
	})
	if !strings.Contains(out, "Compatibility: connector 0.6.0 is older than the minimum 0.7.0") {
		t.Fatalf("expected the too-old verdict, got:\n%s", out)
	}
}

func TestStatusCmd_StaleHeartbeatDoesNotGateVersion(t *testing.T) {
	origVersion := Version
	Version = "0.8.0"
	t.Cleanup(func() { Version = origVersion })

	writeInstanceFile(t, client.Instance{
		State:            "ready",
		ProjectPath:      "/home/user/MyProject",
		Port:             8090,
		PID:              os.Getpid(),
		UnityVersion:     "6000.3.10f1",
		ConnectorVersion: "0.6.0",
		Timestamp:        time.Now().Add(-10 * time.Second).UnixMilli(),
	})

	errOut := captureStderr(t, func() {
		if err := statusCmd(&client.Instance{Port: 8090}); err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
	})
	if !strings.Contains(errOut, "Compatibility: connector 0.6.0 is older than the minimum 0.7.0") {
		t.Fatalf("expected the compatibility verdict on a stale heartbeat, got:\n%s", errOut)
	}
}

func TestWaitForAlive_FollowsResolverPortChange(t *testing.T) {
	origPollInterval := statusPollInterval
	statusPollInterval = 5 * time.Millisecond
	t.Cleanup(func() { statusPollInterval = origPollInterval })

	project := "C:/WorkSpace/ProjectMaid"
	callCount := 0
	resolve := func() (*client.Instance, error) {
		callCount++
		if callCount == 1 {
			return &client.Instance{
				State:       "reloading",
				ProjectPath: project,
				Port:        8090,
				Timestamp:   time.Now().Add(-5 * time.Second).UnixMilli(),
			}, nil
		}
		return &client.Instance{
			State:       "ready",
			ProjectPath: project,
			Port:        8091,
			Timestamp:   time.Now().UnixMilli(),
		}, nil
	}

	inst, err := waitForAlive(resolve, 100)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if inst.Port != 8091 {
		t.Fatalf("expected updated port 8091, got %d", inst.Port)
	}
	if callCount < 2 {
		t.Fatalf("expected resolver to be called multiple times, got %d", callCount)
	}
}

func TestWaitForAlive_DoesNotReturnWhileTheConnectorIsStarting(t *testing.T) {
	origPollInterval := statusPollInterval
	statusPollInterval = 5 * time.Millisecond
	t.Cleanup(func() { statusPollInterval = origPollInterval })

	project := "/work/FoW"
	callCount := 0
	resolve := func() (*client.Instance, error) {
		callCount++
		state := "starting"
		if callCount >= 3 {
			state = "ready"
		}
		return &client.Instance{
			State:       state,
			ProjectPath: project,
			Port:        8090,
			// The startup beater refreshes the timestamp while the state stays "starting".
			Timestamp: time.Now().Add(time.Duration(callCount) * time.Millisecond).UnixMilli(),
		}, nil
	}

	inst, err := waitForAlive(resolve, 200)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if inst.State != "ready" {
		t.Fatalf("state = %q, want ready", inst.State)
	}
	if callCount < 3 {
		t.Fatalf("expected to wait through the starting beats, got %d call(s)", callCount)
	}
}

func TestWaitForCompile_ReadyHeartbeatIsNotEvidence(t *testing.T) {
	compileTestBudgets(t)
	// The heartbeat was already "ready" and its compile clock never moves: polling must not
	// report a compile it never saw finish.
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "ready", CompileFinishedAt: 1_000}, nil
	}

	out, err := waitForCompileTo(resolve, io.Discard, "editor refresh --compile", compileBaseline{FinishedAt: 1_000})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if out.Evidence || !out.NothingToCompile || out.TimedOut {
		t.Errorf("outcome = %+v, want nothing_to_compile without evidence", out)
	}
}

func TestWaitForCompile_ClockAdvanceAfterRequestIsEvidence(t *testing.T) {
	compileTestBudgets(t)
	// The compile can finish before the first poll; the connector's clock is what proves it ran.
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "ready", CompileFinishedAt: 1_001, CompileErrors: true}, nil
	}

	out, err := waitForCompileTo(resolve, io.Discard, "editor refresh --compile", compileBaseline{FinishedAt: 1_000})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !out.Evidence || out.NothingToCompile || !out.HasErrors {
		t.Errorf("outcome = %+v, want evidence of a compile that ended in errors", out)
	}
}

func TestWaitForCompile_KeepsWaitingWhileCompiling(t *testing.T) {
	compileTestBudgets(t)
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "compiling", CompileFinishedAt: 1_000}, nil
	}

	out, err := waitForCompileTo(resolve, io.Discard, "editor refresh --compile", compileBaseline{FinishedAt: 1_000})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !out.TimedOut || out.NothingToCompile {
		t.Errorf("outcome = %+v, want a timeout while the editor never left 'compiling'", out)
	}
}
