package editor

import (
	"fmt"
	"os"
	"strings"
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

// isUnityProcessName reads a process listing line (ps comm on unix, a tasklist row on Windows) and
// reports whether it names the Unity editor. Shared so the two platform guards cannot disagree
// about what may be terminated.
func isUnityProcessName(line string) bool {
	image := strings.TrimSpace(line)
	if strings.HasPrefix(image, `"`) {
		// tasklist /FO CSV: the image name is the first quoted field.
		image = strings.SplitN(strings.TrimPrefix(image, `"`), `"`, 2)[0]
	}
	if i := strings.LastIndexAny(image, `/\`); i >= 0 {
		image = image[i+1:]
	}
	image = strings.TrimSuffix(strings.ToLower(image), ".exe")
	return image == "unity"
}
