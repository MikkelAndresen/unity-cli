package cmd

import (
	"os"
	"testing"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// stubProcessTable pins the OS process table for one test. These tests build fake heartbeats whose
// pid is not this machine's; a developer's machine usually has a real Unity running, and judgePID
// would then call the fake pid gone instead of unprovable.
func stubProcessTable(t *testing.T, table map[int]client.ProcessEntry) {
	t.Helper()
	orig := client.ProcessTable
	client.ProcessTable = func() map[int]client.ProcessEntry { return table }
	t.Cleanup(func() { client.ProcessTable = orig })
}

// containerProcessTable is what a container sees when the editor runs on the host: its own processes
// and no Unity at all, so a heartbeat pid proves nothing and the artifact route decides.
func containerProcessTable() map[int]client.ProcessEntry {
	return map[int]client.ProcessEntry{1: {Name: "init"}, os.Getpid(): {Name: "go"}}
}
