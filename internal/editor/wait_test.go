package editor

import (
	"encoding/json"
	"errors"
	"os"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func stubProcessAlive(t *testing.T, alive bool) {
	t.Helper()
	orig := processAlive
	processAlive = func(int) bool { return alive }
	t.Cleanup(func() { processAlive = orig })
}

func stubReadHeartbeat(t *testing.T, fn func(string) (*client.Instance, error)) {
	t.Helper()
	orig := readHeartbeat
	readHeartbeat = fn
	t.Cleanup(func() { readHeartbeat = orig })
}

func TestWaitForExit_StoppedHeartbeat(t *testing.T) {
	setTestHome(t)
	project := "/work/FoW"
	writeHeartbeat(t, project, client.Instance{
		State:     "stopped",
		PID:       4242,
		Timestamp: 1000,
	})
	stubProcessAlive(t, true)

	start := time.Now()
	err := WaitForExit(WaitForExitOptions{
		PID:          4242,
		ProjectPath:  project,
		Timeout:      2 * time.Second,
		PollInterval: 10 * time.Millisecond,
		StaleAfter:   5 * time.Second,
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if elapsed := time.Since(start); elapsed > time.Second {
		t.Fatalf("stopped heartbeat should return immediately, took %s", elapsed)
	}
}

func TestWaitForExit_StoppedHeartbeatWithDifferentPIDDoesNotEndWait(t *testing.T) {
	setTestHome(t)
	project := "/work/FoW"
	writeHeartbeat(t, project, client.Instance{
		State:     "stopped",
		PID:       1111, // previous editor session, not the one being waited on
		Timestamp: 1000,
	})
	stubProcessAlive(t, true)

	err := WaitForExit(WaitForExitOptions{
		PID:          4242,
		ProjectPath:  project,
		Timeout:      80 * time.Millisecond,
		PollInterval: 10 * time.Millisecond,
		StaleAfter:   20 * time.Millisecond,
	})
	if err == nil || !strings.Contains(err.Error(), "timed out") {
		t.Fatalf("expected timeout because the stopped heartbeat belongs to another PID, got %v", err)
	}
}

func TestWaitForExit_StaleHeartbeatWithDifferentPIDDoesNotEndWait(t *testing.T) {
	setTestHome(t)
	project := "/work/FoW"
	writeHeartbeat(t, project, client.Instance{
		State:     "ready",
		PID:       1111,
		Timestamp: 1000,
	})
	stubProcessAlive(t, true)

	err := WaitForExit(WaitForExitOptions{
		PID:          4242,
		ProjectPath:  project,
		Timeout:      80 * time.Millisecond,
		PollInterval: 10 * time.Millisecond,
		StaleAfter:   20 * time.Millisecond,
	})
	if err == nil || !strings.Contains(err.Error(), "timed out") {
		t.Fatalf("expected timeout because the stale heartbeat belongs to another PID, got %v", err)
	}
}

func TestWaitForExit_UnknownPIDAcceptsStoppedHeartbeat(t *testing.T) {
	setTestHome(t)
	project := "/work/FoW"
	writeHeartbeat(t, project, client.Instance{
		State:     "stopped",
		PID:       4242,
		Timestamp: 1000,
	})

	err := WaitForExit(WaitForExitOptions{
		ProjectPath:  project,
		Timeout:      2 * time.Second,
		PollInterval: 10 * time.Millisecond,
		StaleAfter:   5 * time.Second,
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestWaitForExit_StaleHeartbeat(t *testing.T) {
	setTestHome(t)
	project := "/work/FoW"
	writeHeartbeat(t, project, client.Instance{
		State:     "ready",
		PID:       4242,
		Timestamp: 1000,
	})
	stubProcessAlive(t, true)

	const staleAfter = 80 * time.Millisecond
	start := time.Now()
	err := WaitForExit(WaitForExitOptions{
		PID:          4242,
		ProjectPath:  project,
		Timeout:      2 * time.Second,
		PollInterval: 10 * time.Millisecond,
		StaleAfter:   staleAfter,
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	elapsed := time.Since(start)
	if elapsed < staleAfter {
		t.Fatalf("returned after %s, before the %s stale threshold", elapsed, staleAfter)
	}
	if elapsed > time.Second {
		t.Fatalf("stale detection took too long: %s", elapsed)
	}
}

func TestWaitForExit_DeadPID(t *testing.T) {
	stubProcessAlive(t, false)

	start := time.Now()
	err := WaitForExit(WaitForExitOptions{
		PID:          4242,
		Timeout:      5 * time.Second,
		PollInterval: 10 * time.Millisecond,
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if elapsed := time.Since(start); elapsed > time.Second {
		t.Fatalf("dead PID should return immediately, took %s", elapsed)
	}
}

func TestWaitForExit_MissingHeartbeatTimesOut(t *testing.T) {
	setTestHome(t)
	stubProcessAlive(t, true)

	err := WaitForExit(WaitForExitOptions{
		PID:          4242,
		ProjectPath:  "/work/FoW",
		Timeout:      80 * time.Millisecond,
		PollInterval: 10 * time.Millisecond,
		StaleAfter:   20 * time.Millisecond,
	})
	if err == nil || !strings.Contains(err.Error(), "timed out") {
		t.Fatalf("expected timeout error, got %v", err)
	}
}

func TestWaitForExit_AdvancingHeartbeatTimesOut(t *testing.T) {
	var counter atomic.Int64
	stubProcessAlive(t, true)
	stubReadHeartbeat(t, func(string) (*client.Instance, error) {
		return &client.Instance{State: "ready", Timestamp: counter.Add(1)}, nil
	})

	err := WaitForExit(WaitForExitOptions{
		PID:          4242,
		ProjectPath:  "/work/FoW",
		Timeout:      100 * time.Millisecond,
		PollInterval: 5 * time.Millisecond,
		StaleAfter:   10 * time.Millisecond,
	})
	if err == nil || !strings.Contains(err.Error(), "timed out") {
		t.Fatalf("expected timeout error, got %v", err)
	}
}

func TestWaitForExit_RejectsInvalidOptions(t *testing.T) {
	if err := WaitForExit(WaitForExitOptions{Timeout: 0, PID: 1}); err == nil {
		t.Fatal("expected error for zero timeout")
	}
	if err := WaitForExit(WaitForExitOptions{Timeout: time.Second}); err == nil {
		t.Fatal("expected error when both PID and project path are unset")
	}
}

func TestWaitForExit_StoppedAppearsAfterStart(t *testing.T) {
	setTestHome(t)
	project := "/work/FoW"
	path := writeHeartbeat(t, project, client.Instance{State: "ready", PID: 4242, Timestamp: 1000})
	stubProcessAlive(t, true)

	go func() {
		time.Sleep(30 * time.Millisecond)
		data, err := json.Marshal(client.Instance{State: "stopped", PID: 4242, Timestamp: 1500})
		if err == nil {
			_ = os.WriteFile(path, data, 0o644)
		}
	}()

	err := WaitForExit(WaitForExitOptions{
		PID:          4242,
		ProjectPath:  project,
		Timeout:      2 * time.Second,
		PollInterval: 10 * time.Millisecond,
		StaleAfter:   5 * time.Second,
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestLaunch_RejectsEmptyInputs(t *testing.T) {
	if _, err := Launch("", t.TempDir()); err == nil {
		t.Fatal("expected error for empty executable")
	}
	if _, err := Launch(os.Args[0], ""); err == nil {
		t.Fatal("expected error for empty project path")
	}
}

func TestLaunch_MissingExecutable(t *testing.T) {
	exe := t.TempDir() + "/does-not-exist/Unity"
	if _, err := Launch(exe, t.TempDir()); err == nil {
		t.Fatal("expected error for missing executable")
	}
}

func TestWaitForExit_ModalBlocksExit(t *testing.T) {
	setTestHome(t)
	project := "/work/FoW"
	writeHeartbeat(t, project, client.Instance{
		State:     "modal",
		PID:       4242,
		Timestamp: 1000,
	})
	stubProcessAlive(t, true)

	err := WaitForExit(WaitForExitOptions{
		PID:          4242,
		ProjectPath:  project,
		Timeout:      2 * time.Second,
		PollInterval: 10 * time.Millisecond,
		StaleAfter:   5 * time.Second,
	})
	if !errors.Is(err, ErrEditorModal) {
		t.Fatalf("expected ErrEditorModal, got %v", err)
	}
}
