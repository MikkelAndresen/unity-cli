//go:build !windows

package editor

import (
	"fmt"
	"os"
	"os/exec"
	"strings"
	"syscall"
)

// Indirection so tests can check the name guard without a Unity process.
var processCommand = func(pid int) (string, error) {
	out, err := exec.Command("ps", "-p", fmt.Sprint(pid), "-o", "comm=").Output()
	return strings.TrimSpace(string(out)), err
}

// verifyUnityProcess guards against signalling the wrong process: inside a
// container the heartbeat holds the host's PID, which can name an unrelated
// local process here.
func verifyUnityProcess(pid int) error {
	comm, err := processCommand(pid)
	if err != nil || comm == "" {
		return fmt.Errorf("pid %d is not visible from this process; if the CLI runs in a container, stop Unity from the host", pid)
	}
	if !strings.Contains(strings.ToLower(comm), "unity") {
		return fmt.Errorf("pid %d is %q, not a Unity editor; refusing to terminate it", pid, comm)
	}
	return nil
}

func terminateProcess(proc *os.Process) error {
	return proc.Signal(syscall.SIGTERM)
}
