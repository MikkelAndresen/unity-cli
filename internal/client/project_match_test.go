package client

import (
	"errors"
	"os"
	"path/filepath"
	"testing"
)

// siblingEditors reproduces #67: three live editors in sibling checkouts sharing a name prefix, the
// prefix-sharing sibling with the newest heartbeat (it was compiling, so it beat most often).
func siblingEditors(t *testing.T) (work string) {
	t.Helper()
	stubIsProcessDead(t, map[int]bool{})
	work = t.TempDir()
	for _, name := range []string{"ovrshot-remake", "ovrshot-remake-player-controller", "ovrshot-remake-issue-11"} {
		if err := os.MkdirAll(filepath.Join(work, name, "Assets"), 0755); err != nil {
			t.Fatal(err)
		}
	}
	writeInstanceFiles(t, map[string]Instance{
		"a.json": {State: "compiling", ProjectPath: filepath.Join(work, "ovrshot-remake-player-controller"), Port: 8090, PID: 1, Timestamp: 3000, CompileErrors: true},
		"b.json": {State: "ready", ProjectPath: filepath.Join(work, "ovrshot-remake-issue-11"), Port: 8091, PID: 2, Timestamp: 2000},
		"c.json": {State: "ready", ProjectPath: filepath.Join(work, "ovrshot-remake"), Port: 8092, PID: 3, Timestamp: 1000},
		"d.json": {State: "stopped", ProjectPath: filepath.Join(work, "old-benchmark"), Port: 8090, PID: 4, Timestamp: 4000},
	})
	return work
}

func TestDiscoverInstance_ProjectPathDoesNotMatchPrefixSibling(t *testing.T) {
	work := siblingEditors(t)

	got, err := DiscoverInstance(filepath.Join(work, "ovrshot-remake"), 0)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.Port != 8092 {
		t.Fatalf("got port %d (%s), want 8092", got.Port, got.ProjectPath)
	}
}

func TestDiscoverInstance_ProjectPathInsideProjectSelectsIt(t *testing.T) {
	work := siblingEditors(t)

	got, err := DiscoverInstance(filepath.Join(work, "ovrshot-remake", "Assets"), 0)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.Port != 8092 {
		t.Fatalf("got port %d, want 8092", got.Port)
	}
}

func TestDiscoverInstance_ProjectPathWithNoEditorListsRunningOnes(t *testing.T) {
	work := siblingEditors(t)

	_, err := DiscoverInstance(filepath.Join(work, "elsewhere"), 0)
	if err == nil {
		t.Fatal("expected an error for a path no editor has open")
	}
}

func TestDiscoverInstance_AmbiguousFragmentFails(t *testing.T) {
	siblingEditors(t)

	_, err := DiscoverInstance("remake-", 0)
	var amb *AmbiguousInstanceError
	if !errors.As(err, &amb) {
		t.Fatalf("got %v, want an AmbiguousInstanceError", err)
	}
	if len(amb.Candidates) != 2 {
		t.Fatalf("got %d candidates, want 2", len(amb.Candidates))
	}
}

func TestDiscoverInstance_FragmentNamingOneFolderSelectsIt(t *testing.T) {
	siblingEditors(t)

	got, err := DiscoverInstance("ovrshot-remake", 0)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.Port != 8092 {
		t.Fatalf("got port %d, want 8092", got.Port)
	}
}

func TestDiscoverInstance_CwdInsideProjectSelectsIt(t *testing.T) {
	work := siblingEditors(t)
	t.Chdir(filepath.Join(work, "ovrshot-remake", "Assets"))

	got, err := DiscoverInstance("", 0)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.Port != 8092 {
		t.Fatalf("got port %d, want 8092", got.Port)
	}
}

func TestDiscoverInstance_CwdInNoProjectWithSeveralEditorsFails(t *testing.T) {
	siblingEditors(t)
	t.Chdir(t.TempDir())

	_, err := DiscoverInstance("", 0)
	var amb *AmbiguousInstanceError
	if !errors.As(err, &amb) {
		t.Fatalf("got %v, want an AmbiguousInstanceError", err)
	}
	if len(amb.Candidates) != 3 {
		t.Fatalf("got %d candidates, want the 3 live editors", len(amb.Candidates))
	}
}

func TestDiscoverInstance_CwdAboveOneProjectSelectsIt(t *testing.T) {
	stubIsProcessDead(t, map[int]bool{})
	repo := t.TempDir()
	other := t.TempDir()
	writeInstanceFiles(t, map[string]Instance{
		"a.json": {State: "ready", ProjectPath: filepath.Join(repo, "UnityProject"), Port: 8090, PID: 1, Timestamp: 1000},
		"b.json": {State: "ready", ProjectPath: other, Port: 8091, PID: 2, Timestamp: 2000},
	})
	t.Chdir(repo)

	got, err := DiscoverInstance("", 0)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.Port != 8090 {
		t.Fatalf("got port %d, want 8090", got.Port)
	}
}

func TestDiscoverInstance_SingleEditorIsUsedFromAnyCwd(t *testing.T) {
	stubIsProcessDead(t, map[int]bool{})
	writeInstanceFiles(t, map[string]Instance{
		"a.json": {State: "ready", ProjectPath: "/projects/only", Port: 8090, PID: 1, Timestamp: 1000},
	})
	t.Chdir(t.TempDir())

	got, err := DiscoverInstance("", 0)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.Port != 8090 {
		t.Fatalf("got port %d, want 8090", got.Port)
	}
}

// The wait and retry resolvers re-select by the first answer's ProjectPath, and crash verdicts read
// the heartbeat the same way; a prefix sibling there is the mixed routing #67 reported.
func TestNewestHeartbeat_ProjectPathDoesNotMatchPrefixSibling(t *testing.T) {
	work := siblingEditors(t)

	got := NewestHeartbeat(filepath.Join(work, "ovrshot-remake"), 0)
	if got == nil || got.Port != 8092 {
		t.Fatalf("got %+v, want the heartbeat on 8092", got)
	}
}
