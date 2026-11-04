//go:build !windows

package client

import (
	"context"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"
)

// checkProcessDead returns true only when the process is confirmed to not exist (ESRCH).
// Returns false for permission errors (EPERM) or any other transient failure,
// meaning the instance file will be preserved.
func checkProcessDead(pid int) bool {
	proc, err := os.FindProcess(pid)
	if err != nil {
		return true
	}
	err = proc.Signal(syscall.Signal(0))
	if err == nil {
		return false // process exists and we have permission
	}
	// EPERM means the process exists but we lack permission to signal it
	if errors.Is(err, syscall.EPERM) {
		return false
	}
	// ESRCH means the process does not exist
	if errors.Is(err, syscall.ESRCH) {
		return true
	}
	// Unknown error — be conservative, assume alive
	return false
}

// readProcessTable lists the processes this OS can see, as pid -> executable name, or nil when no
// table can be read at all. nil is deliberately not the same as an empty map: judgePID may treat a
// missing row as proof of death only when the table itself was readable.
func readProcessTable() map[int]string {
	if table, ok := procfsTable(); ok {
		return table
	}
	return psTable()
}

// procfsTable reads /proc, present on Linux and in any Linux container. Names come from comm, which
// the kernel truncates to 15 characters; the editor's is short enough to survive that.
func procfsTable() (map[int]string, bool) {
	entries, err := os.ReadDir("/proc")
	if err != nil {
		return nil, false
	}

	table := map[int]string{}
	for _, e := range entries {
		pid := atoi(e.Name())
		if pid == 0 {
			continue
		}
		name, err := os.ReadFile(filepath.Join("/proc", e.Name(), "comm"))
		if err != nil {
			// The process is listed but its name is unreadable (it exited between the listing and the
			// read, or it is a kernel thread): keep the row, leave the name blank.
			table[pid] = ""
			continue
		}
		table[pid] = strings.TrimSpace(string(name))
	}
	return table, len(table) > 0
}

// psTable is the fallback for systems without procfs — macOS has no /proc, and that is where the
// editor this CLI drives usually runs. ps prints the executable's full path in comm there, which
// isUnityExecutable trims back to a base name.
func psTable() map[int]string {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	out, err := exec.CommandContext(ctx, "ps", "-axo", "pid=,comm=").Output()
	if err != nil {
		return nil
	}

	table := map[int]string{}
	for _, line := range strings.Split(string(out), "\n") {
		fields := strings.Fields(line)
		if len(fields) < 2 {
			continue
		}
		pid := atoi(fields[0])
		if pid == 0 {
			continue
		}
		table[pid] = strings.TrimSpace(strings.TrimPrefix(line, fields[0]))
	}
	if len(table) == 0 {
		return nil
	}
	return table
}
