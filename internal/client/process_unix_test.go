//go:build !windows

package client

import (
	"os"
	"os/exec"
	"testing"
)

func TestCheckProcessDead_LiveProcess(t *testing.T) {
	if checkProcessDead(os.Getpid()) {
		t.Fatal("own process reported dead")
	}
}

func TestCheckProcessDead_ExitedProcess(t *testing.T) {
	cmd := exec.Command("true")
	if err := cmd.Run(); err != nil {
		t.Skipf("cannot run helper process: %v", err)
	}
	// Run reaped it, so the PID no longer exists (barring immediate reuse).
	if !checkProcessDead(cmd.Process.Pid) {
		t.Fatalf("exited pid %d reported alive", cmd.Process.Pid)
	}
}
