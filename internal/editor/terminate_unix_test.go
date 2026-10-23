//go:build !windows

package editor

import (
	"errors"
	"strings"
	"testing"
)

func stubProcessCommand(t *testing.T, comm string, err error) {
	t.Helper()
	orig := processCommand
	processCommand = func(int) (string, error) { return comm, err }
	t.Cleanup(func() { processCommand = orig })
}

func TestTerminate_RefusesNonUnityProcess(t *testing.T) {
	stubProcessCommand(t, "bash", nil)

	err := Terminate(12345)
	if err == nil || !strings.Contains(err.Error(), "not a Unity editor") {
		t.Fatalf("expected refusal for a non-Unity process, got %v", err)
	}
}

func TestTerminate_RefusesInvisiblePID(t *testing.T) {
	stubProcessCommand(t, "", errors.New("exit status 1"))

	err := Terminate(12345)
	if err == nil || !strings.Contains(err.Error(), "not visible") {
		t.Fatalf("expected not-visible error, got %v", err)
	}
}

func TestTerminate_RejectsInvalidPID(t *testing.T) {
	if err := Terminate(0); err == nil {
		t.Fatal("expected error for pid 0")
	}
}

func TestVerifyUnityProcess_AcceptsUnityBinary(t *testing.T) {
	for _, comm := range []string{"Unity", "/Applications/Unity/Hub/Editor/6000.4.4f1/Unity.app/Contents/MacOS/Unity"} {
		stubProcessCommand(t, comm, nil)
		if err := verifyUnityProcess(1); err != nil {
			t.Errorf("%q should be accepted: %v", comm, err)
		}
	}
}
