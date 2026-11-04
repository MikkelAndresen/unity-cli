package editor

import (
	"fmt"
	"os"
)

// CanManageProcess reports whether this process can identify pid as a Unity
// editor. It is used before an operation that needs to signal the editor: a CLI
// inside a container sees the host heartbeat PID but cannot signal it.
func CanManageProcess(pid int) error {
	if pid <= 0 {
		return fmt.Errorf("invalid pid %d", pid)
	}
	return verifyUnityProcess(pid)
}

// Terminate asks the OS to stop the editor process (SIGTERM on unix,
// TerminateProcess on Windows). Unity's native handler for these takes the
// shutdown path without the managed quit prompt that can swallow
// EditorApplication.Exit.
func Terminate(pid int) error {
	if pid <= 0 {
		return fmt.Errorf("invalid pid %d", pid)
	}
	if err := verifyUnityProcess(pid); err != nil {
		return err
	}
	proc, err := os.FindProcess(pid)
	if err != nil {
		return err
	}
	return terminateProcess(proc)
}
