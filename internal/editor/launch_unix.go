//go:build !windows

package editor

import (
	"os/exec"
	"syscall"
)

// startDetached puts the editor in its own session so the CLI's terminal
// signals and process-group teardown do not reach it.
func startDetached(cmd *exec.Cmd) error {
	cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true}
	return cmd.Start()
}
