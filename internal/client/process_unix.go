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

// readProcessTable lists the processes this OS can see, as pid -> entry, or nil when no table can be
// read at all. nil is deliberately not the same as an empty map: judgePID may treat a missing row as
// proof of death only when the table itself was readable.
func readProcessTable() map[int]ProcessEntry {
	if table, ok := procfsTable(); ok {
		return table
	}
	return psTable()
}

// procfsTable reads /proc, present on Linux and in any Linux container. Name and state come from
// /proc/<pid>/stat: the kernel truncates the name to 15 characters, which the editor's is short
// enough to survive, and the state is the one field that tells a running editor from a zombie.
func procfsTable() (map[int]ProcessEntry, bool) {
	entries, err := os.ReadDir("/proc")
	if err != nil {
		return nil, false
	}

	table := map[int]ProcessEntry{}
	for _, e := range entries {
		pid := atoi(e.Name())
		if pid == 0 {
			continue
		}
		stat, err := os.ReadFile(filepath.Join("/proc", e.Name(), "stat"))
		if err != nil {
			// The process is listed but its stat is unreadable (it exited between the listing and
			// the read, or it is a kernel thread): keep the row, leave the name blank.
			table[pid] = ProcessEntry{}
			continue
		}
		entry, ok := parseProcStat(string(stat))
		if !ok {
			table[pid] = ProcessEntry{}
			continue
		}
		table[pid] = entry
	}
	return table, len(table) > 0
}

// parseProcStat reads the name and state out of a /proc/<pid>/stat line. The name is wrapped in
// parentheses and may itself contain spaces or parentheses, so the split is at the *last* ')'.
// The state is the first field after it; Z means the process has exited but still has a pid.
func parseProcStat(line string) (ProcessEntry, bool) {
	open := strings.Index(line, "(")
	close := strings.LastIndex(line, ")")
	if open < 0 || close <= open || close+2 >= len(line) {
		return ProcessEntry{}, false
	}
	return ProcessEntry{
		Name:   line[open+1 : close],
		Zombie: line[close+2] == 'Z',
	}, true
}

// parsePSLine reads one "pid stat comm" row. comm is the executable's full path and may contain
// spaces, so only the first two fields are split off; the stat field's leading Z marks a zombie.
func parsePSLine(line string) (int, ProcessEntry, bool) {
	line = strings.TrimLeft(line, " \t")
	fields := strings.Fields(line)
	if len(fields) < 3 {
		return 0, ProcessEntry{}, false
	}
	pid := atoi(fields[0])
	if pid == 0 {
		return 0, ProcessEntry{}, false
	}
	rest := strings.TrimLeft(line[len(fields[0]):], " \t")
	gap := strings.IndexAny(rest, " \t")
	if gap < 0 {
		return 0, ProcessEntry{}, false
	}
	return pid, ProcessEntry{
		Name:   strings.TrimSpace(rest[gap:]),
		Zombie: strings.HasPrefix(rest[:gap], "Z"),
	}, true
}

// psTable is the fallback for systems without procfs — macOS has no /proc, and that is where the
// editor this CLI drives usually runs. ps prints the executable's full path in comm there, which
// isUnityExecutable trims back to a base name, and the stat column's Z flag marks a zombie.
func psTable() map[int]ProcessEntry {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	out, err := exec.CommandContext(ctx, "ps", "-axo", "pid=,stat=,comm=").Output()
	if err != nil {
		return nil
	}

	table := map[int]ProcessEntry{}
	for _, line := range strings.Split(string(out), "\n") {
		if pid, entry, ok := parsePSLine(line); ok {
			table[pid] = entry
		}
	}
	if len(table) == 0 {
		return nil
	}
	return table
}
