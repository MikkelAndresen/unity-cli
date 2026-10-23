package editor

import (
	"errors"
	"fmt"
	"os/exec"
	"path/filepath"
)

// Launch starts the Unity editor for projectPath as a detached process and
// returns its PID. The editor does not inherit the CLI's stdio and is not
// killed when the CLI exits.
func Launch(exe, projectPath string) (int, error) {
	if exe == "" {
		return 0, errors.New("unity editor executable path is empty")
	}
	if projectPath == "" {
		return 0, errors.New("project path is empty")
	}

	projectAbs, err := filepath.Abs(projectPath)
	if err != nil {
		return 0, fmt.Errorf("cannot resolve project path %q: %w", projectPath, err)
	}

	cmd := exec.Command(exe, "-projectPath", projectAbs)
	// Leaving Std* nil sends them to the null device instead of the CLI's pipes.
	if err := startDetached(cmd); err != nil {
		return 0, fmt.Errorf("failed to launch Unity (%s): %w", exe, err)
	}

	pid := cmd.Process.Pid
	// Release instead of Wait: the editor outlives this process, so there is
	// nothing to reap while the CLI is alive.
	_ = cmd.Process.Release()
	return pid, nil
}
