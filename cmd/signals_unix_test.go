//go:build !windows

package cmd

import (
	"bytes"
	"errors"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"syscall"
	"testing"
	"time"
)

// TestSignalHelperProcess is not a real test: it is the child process that blocks with the signal
// handler installed, so the parent can prove an external alarm actually kills unity-cli.
func TestSignalHelperProcess(t *testing.T) {
	if os.Getenv("UNITY_CLI_SIGNAL_HELPER") != "1" {
		t.Skip("helper process only")
	}
	_, _ = installSignalCancellation()
	select {}
}

func TestInstallSignalCancellation_ExitsWith128PlusSignal(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("POSIX signals")
	}
	tests := []struct {
		name string
		sig  syscall.Signal
	}{
		{"SIGALRM", syscall.SIGALRM},
		{"SIGTERM", syscall.SIGTERM},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cmd := exec.Command(os.Args[0], "-test.run=TestSignalHelperProcess")
			cmd.Env = append(os.Environ(), "UNITY_CLI_SIGNAL_HELPER=1")
			var stderr bytes.Buffer
			cmd.Stderr = &stderr
			if err := cmd.Start(); err != nil {
				t.Fatalf("start helper: %v", err)
			}

			time.Sleep(300 * time.Millisecond)
			start := time.Now()
			if err := cmd.Process.Signal(tt.sig); err != nil {
				t.Fatalf("signal helper: %v", err)
			}
			err := cmd.Wait()
			var exitErr *exec.ExitError
			if !errors.As(err, &exitErr) {
				t.Fatalf("expected a non-zero exit, got %v", err)
			}
			if got, want := exitErr.ExitCode(), 128+int(tt.sig); got != want {
				t.Errorf("exit code = %d, want %d (stderr: %s)", got, want, stderr.String())
			}
			if elapsed := time.Since(start); elapsed > 2*time.Second {
				t.Errorf("the process did not exit promptly: %v", elapsed)
			}
			if !strings.Contains(stderr.String(), "interrupted by") {
				t.Errorf("expected an interrupt line on stderr, got: %s", stderr.String())
			}
		})
	}
}
