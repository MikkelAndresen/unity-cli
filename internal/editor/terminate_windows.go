//go:build windows

package editor

import "os"

// Windows PIDs are system-wide, so the heartbeat PID cannot name a process in
// another namespace the way it can inside a unix container.
func verifyUnityProcess(int) error {
	return nil
}

func terminateProcess(proc *os.Process) error {
	return proc.Kill()
}
