package client

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// unityProjectDir makes a directory that looks like a Unity project with an editor holding it
// open: Temp/UnityLockfile, written mtimeAgo.
func unityProjectDir(t *testing.T, mtimeAgo time.Duration) string {
	t.Helper()
	dir := t.TempDir()
	temp := filepath.Join(dir, "Temp")
	if err := os.MkdirAll(temp, 0755); err != nil {
		t.Fatalf("failed to create Temp: %v", err)
	}
	lock := filepath.Join(temp, unityLockfileName)
	if err := os.WriteFile(lock, nil, 0644); err != nil {
		t.Fatalf("failed to write lockfile: %v", err)
	}
	stamp := time.Now().Add(-mtimeAgo)
	if err := os.Chtimes(lock, stamp, stamp); err != nil {
		t.Fatalf("failed to set lockfile mtime: %v", err)
	}
	return dir
}

func TestFindEditorEvidence_LockfileOnly(t *testing.T) {
	writeInstanceFiles(t, map[string]Instance{})
	dir := unityProjectDir(t, 42*time.Second)

	ev := FindEditorEvidence(dir, 0)
	if !ev.Found() {
		t.Fatalf("expected evidence in %s, got none", dir)
	}
	if ev.ProjectPath != dir {
		t.Errorf("ProjectPath: got %q, want %q", ev.ProjectPath, dir)
	}
	if ev.LockfilePath != filepath.Join(dir, "Temp", unityLockfileName) {
		t.Errorf("LockfilePath: got %q", ev.LockfilePath)
	}
	if ev.LockfileAge < 41*time.Second || ev.LockfileAge > 2*time.Minute {
		t.Errorf("LockfileAge: got %v, want about 42s", ev.LockfileAge)
	}
	if ev.StaleHeartbeat != nil {
		t.Errorf("StaleHeartbeat: got %+v, want none", ev.StaleHeartbeat)
	}
	if summary := ev.Summary(); !strings.Contains(summary, "Temp/UnityLockfile, age 42s") {
		t.Errorf("Summary: got %q, want the lockfile age", summary)
	}
}

func TestFindEditorEvidence_NoEvidenceReturnsNil(t *testing.T) {
	writeInstanceFiles(t, map[string]Instance{})
	dir := t.TempDir() // a directory that is not an open Unity project

	if ev := FindEditorEvidence(dir, 0); ev != nil {
		t.Fatalf("expected no evidence, got %+v", ev)
	}
}

// TestFindEditorEvidence_FreshHeartbeatIsNotEvidence pins the common case: a live editor is found
// by the instance lookup, so the evidence path must stay silent about it.
func TestFindEditorEvidence_FreshHeartbeatIsNotEvidence(t *testing.T) {
	writeInstanceFiles(t, map[string]Instance{
		"live.json": {
			State:       "ready",
			ProjectPath: t.TempDir(),
			Port:        8090,
			PID:         os.Getpid(),
			Timestamp:   time.Now().UnixMilli(),
		},
	})

	if ev := FindEditorEvidence("", 8090); ev != nil {
		t.Fatalf("expected no evidence for a fresh heartbeat, got %+v", ev)
	}
}

func TestFindEditorEvidence_StaleHeartbeatOnly(t *testing.T) {
	project := t.TempDir()
	writeInstanceFiles(t, map[string]Instance{
		"gone.json": {
			State:       "compiling",
			ProjectPath: project,
			Port:        8090,
			PID:         os.Getpid(),
			Timestamp:   time.Now().Add(-5 * time.Minute).UnixMilli(),
		},
	})

	ev := FindEditorEvidence(project, 0)
	if !ev.Found() {
		t.Fatalf("expected evidence for a heartbeat that went silent, got none")
	}
	if ev.LockfilePath != "" {
		t.Errorf("LockfilePath: got %q, want empty", ev.LockfilePath)
	}
	if ev.StaleHeartbeat == nil || ev.StaleHeartbeat.State != "compiling" {
		t.Fatalf("StaleHeartbeat: got %+v, want the compiling instance", ev.StaleHeartbeat)
	}
	if !strings.Contains(ev.Summary(), "heartbeat last seen 5m0s ago") {
		t.Errorf("Summary: got %q", ev.Summary())
	}
}

// TestFindEditorEvidence_IgnoresCleanShutdowns keeps a project Unity closed on purpose from
// looking like a startup dialog.
func TestFindEditorEvidence_IgnoresCleanShutdowns(t *testing.T) {
	project := t.TempDir()
	writeInstanceFiles(t, map[string]Instance{
		"stopped.json": {
			State:       "stopped",
			ProjectPath: project,
			Port:        8090,
			PID:         os.Getpid(),
			Timestamp:   time.Now().Add(-5 * time.Minute).UnixMilli(),
		},
	})

	if ev := FindEditorEvidence(project, 0); ev != nil {
		t.Fatalf("expected no evidence for a clean shutdown, got %+v", ev)
	}
}

// TestFindEditorEvidence_SubstringProjectUsesHeartbeatPath covers --project as a substring: the
// argument is not a directory the CLI can stat, so the heartbeat's path is what locates Temp/.
func TestFindEditorEvidence_SubstringProjectUsesHeartbeatPath(t *testing.T) {
	dir := unityProjectDir(t, time.Minute)
	writeInstanceFiles(t, map[string]Instance{
		"gone.json": {
			State:       "ready",
			ProjectPath: dir,
			Port:        8090,
			PID:         os.Getpid(),
			Timestamp:   time.Now().Add(-3 * time.Minute).UnixMilli(),
		},
	})

	ev := FindEditorEvidence(filepath.Base(dir), 0)
	if !ev.Found() {
		t.Fatalf("expected evidence via the heartbeat path, got none")
	}
	if ev.ProjectPath != dir {
		t.Errorf("ProjectPath: got %q, want %q", ev.ProjectPath, dir)
	}
}

func TestFindEditorEvidence_HomeRelativeProjectPath(t *testing.T) {
	home := writeInstanceFiles(t, map[string]Instance{})
	dir := filepath.Join(home, "MyProj")
	if err := os.MkdirAll(filepath.Join(dir, "Temp"), 0755); err != nil {
		t.Fatalf("failed to create project Temp: %v", err)
	}
	if err := os.WriteFile(filepath.Join(dir, "Temp", unityLockfileName), nil, 0644); err != nil {
		t.Fatalf("failed to write lockfile: %v", err)
	}

	ev := FindEditorEvidence("~/MyProj", 0)
	if !ev.Found() {
		t.Fatalf("expected evidence for ~/MyProj, got none")
	}
	if ev.ProjectPath != dir {
		t.Errorf("ProjectPath: got %q, want %q", ev.ProjectPath, dir)
	}
}

func TestFindEditorEvidence_PortMatchesHeartbeatProject(t *testing.T) {
	dir := unityProjectDir(t, 30*time.Second)
	writeInstanceFiles(t, map[string]Instance{
		"gone.json": {
			State:       "compiling",
			ProjectPath: dir,
			Port:        8091,
			PID:         os.Getpid(),
			Timestamp:   time.Now().Add(-90 * time.Second).UnixMilli(),
		},
	})

	ev := FindEditorEvidence("", 8091)
	if !ev.Found() {
		t.Fatalf("expected evidence for --port 8091, got none")
	}
	if ev.LockfilePath == "" {
		t.Errorf("LockfilePath: got empty, want the project's lockfile")
	}
	if ev.ProjectPath != dir {
		t.Errorf("ProjectPath: got %q, want %q", ev.ProjectPath, dir)
	}
}

func TestFormatAge(t *testing.T) {
	tests := []struct {
		in   time.Duration
		want string
	}{
		{-5 * time.Second, "0s"},
		{0, "0s"},
		{42 * time.Second, "42s"},
		{59 * time.Second, "59s"},
		{90 * time.Second, "1m30s"},
		{42 * time.Minute, "42m0s"},
	}
	for _, tc := range tests {
		if got := FormatAge(tc.in); got != tc.want {
			t.Errorf("FormatAge(%v): got %q, want %q", tc.in, got, tc.want)
		}
	}
}
