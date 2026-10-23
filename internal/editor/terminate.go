package editor

import (
	"fmt"
	"os"
)

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
