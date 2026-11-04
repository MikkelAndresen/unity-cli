//go:build windows

package editor

import (
	"fmt"
	"os"
	"os/exec"
	"strings"
)

// Indirection so tests can check the name guard without a Unity process.
var processCommand = func(pid int) (string, error) {
	out, err := exec.Command("tasklist", "/FI", fmt.Sprintf("PID eq %d", pid), "/FO", "CSV", "/NH").Output()
	return strings.TrimSpace(string(out)), err
}

// verifyUnityProcess guards against signalling the wrong process: a recycled pid can name an
// unrelated program. tasklist is the Windows equivalent of ps -o comm=.
func verifyUnityProcess(pid int) error {
	line, err := processCommand(pid)
	if err != nil || line == "" {
		return fmt.Errorf("pid %d is not visible from this process; if the CLI runs in a container, stop Unity from the host", pid)
	}
	if !isUnityProcessName(line) {
		return fmt.Errorf("pid %d is %q, not a Unity editor; refusing to terminate it", pid, line)
	}
	return nil
}

func terminateProcess(proc *os.Process) error {
	return proc.Kill()
}
