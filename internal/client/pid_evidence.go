package client

import (
	"fmt"
	"path/filepath"
	"strconv"
	"strings"
)

// pidProof is what this CLI can prove about a heartbeat's process id from where it runs.
type pidProof int

const (
	// pidUnproven means the process table here cannot answer the question at all: no readable table,
	// or a table that belongs to a different machine than the editor (an agent-sandbox container
	// talking to a connector on a macOS host, where the connector's pid is not in /proc and never
	// will be). Callers must fall back to heartbeat age and crash artifacts.
	pidUnproven pidProof = iota
	// pidAlive means the id is present in this process table and still runs a Unity executable.
	pidAlive
	// pidGone means this table does contain Unity processes, just not this one: the editor that wrote
	// the heartbeat is over and done with.
	pidGone
)

// unityProcessNames are the executable names a Unity editor appears under. Matching is on the base
// name so that Unity Hub, UnityShaderCompiler and license helpers prove *visibility* of the table
// without being mistaken for the editor itself.
func unityProcessNames() []string { return []string{"unity", "unity.exe"} }

// ProcessEntry is one row of a process table. Zombie marks a process that has exited but has not
// been reaped by its parent yet: on a Linux host a crashed Unity child stays listed as "Unity" with
// state Z, and treating that row as a running editor hides the crash.
type ProcessEntry struct {
	Name   string
	Zombie bool
}

// ProcessTable reads this OS's process table. It is a variable so a fake table can be installed by
// tests — including the cmd package's, which cannot reach an unexported seam, and whose fake
// heartbeat would otherwise be judged against a real Unity on the developer's own machine.
var ProcessTable = readProcessTable

// judgePID answers whether the editor process named by pid still exists — but only when this CLI can
// actually see the machine the editor runs on. note says why the answer counts, or why it does not,
// for printing in a report.
func judgePID(pid int) (proof pidProof, note string) {
	if pid <= 0 {
		return pidUnproven, "the heartbeat records no pid"
	}

	table := ProcessTable()
	if table == nil {
		return pidUnproven, "no process table is readable here, so a pid proves nothing"
	}

	if entry, ok := table[pid]; ok {
		switch {
		case isUnityExecutable(entry.Name) && entry.Zombie:
			return pidGone, fmt.Sprintf("pid %d is a zombie Unity process (already exited, not yet reaped), so the editor is over", pid)
		case isUnityExecutable(entry.Name):
			return pidAlive, fmt.Sprintf("pid %d is running %s on this machine", pid, entry.Name)
		case entry.Name == "":
			return pidUnproven, fmt.Sprintf("pid %d exists here but its executable name is unreadable", pid)
		default:
			// The id is taken by something that is not Unity: either the pid was reused after the
			// editor died, or this table is not the editor's. Neither proves the editor died.
			return pidUnproven, fmt.Sprintf("pid %d exists here but runs %q, not Unity", pid, entry.Name)
		}
	}

	if other := firstUnityPID(table); other > 0 {
		return pidGone, fmt.Sprintf("pid %d is not in this machine's process table, which still lists Unity as pid %d", pid, other)
	}
	return pidUnproven, fmt.Sprintf("pid %d is not in this machine's process table, but no Unity process is visible here either, so that proves nothing (a container cannot see the host's processes)", pid)
}

func isUnityExecutable(name string) bool {
	// Names arrive in the host's own spelling; a Windows path read on any OS must still reduce to its
	// base name, so the separator is normalized instead of relying on filepath semantics.
	normalized := strings.ReplaceAll(name, `\`, "/")
	base := strings.ToLower(strings.TrimSpace(filepath.Base(normalized)))
	base = strings.TrimSuffix(base, ".exe")
	for _, unity := range unityProcessNames() {
		if base == strings.TrimSuffix(unity, ".exe") {
			return true
		}
	}
	return false
}

// firstUnityPID returns any live Unity editor pid listed in the table, which is the proof that the
// table describes the machine the editor lives on. A zombie row is a dead process, not proof.
func firstUnityPID(table map[int]ProcessEntry) int {
	for pid, entry := range table {
		if !entry.Zombie && isUnityExecutable(entry.Name) {
			return pid
		}
	}
	return 0
}

// atoi parses a pid field, returning 0 for anything that is not a whole number.
func atoi(s string) int {
	n, err := strconv.Atoi(strings.TrimSpace(s))
	if err != nil {
		return 0
	}
	return n
}
