package client

import (
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

// EditorExecutable maps the connector's editorPath (EditorApplication.applicationPath)
// to the binary to exec. On macOS that path is the .app bundle, which cannot be exec'd.
func EditorExecutable(editorPath string) string {
	if strings.HasSuffix(strings.TrimRight(editorPath, "/"), ".app") {
		return filepath.Join(editorPath, "Contents", "MacOS", "Unity")
	}
	return editorPath
}

// StartDetached starts exe in its own session so it outlives the CLI process,
// and returns its PID without waiting for it.
func StartDetached(exe string, args []string) (int, error) {
	cmd := exec.Command(exe, args...)
	cmd.SysProcAttr = detachedProcAttr()
	devNull, err := os.OpenFile(os.DevNull, os.O_RDWR, 0)
	if err != nil {
		return 0, err
	}
	defer devNull.Close()
	cmd.Stdin, cmd.Stdout, cmd.Stderr = devNull, devNull, devNull
	if err := cmd.Start(); err != nil {
		return 0, err
	}
	// Reap it if it exits while the CLI is still running; an unreaped zombie
	// still answers signal 0, so ProcessDead would never report it gone.
	go func() { _ = cmd.Wait() }()
	return cmd.Process.Pid, nil
}

// KillProcess forcibly terminates pid.
func KillProcess(pid int) error {
	proc, err := os.FindProcess(pid)
	if err != nil {
		return err
	}
	return proc.Kill()
}
