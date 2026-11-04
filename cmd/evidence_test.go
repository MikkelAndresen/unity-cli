package cmd

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// isolatedHome points the instance lookup at an empty directory so tests never read the
// developer's real ~/.unity-cli/instances.
func isolatedHome(t *testing.T) string {
	t.Helper()
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	return home
}

// openUnityProjectDir makes a project directory whose Temp/UnityLockfile was written mtimeAgo:
// an editor holding the project open without a connector heartbeat.
func openUnityProjectDir(t *testing.T, mtimeAgo time.Duration) string {
	t.Helper()
	dir := t.TempDir()
	temp := filepath.Join(dir, "Temp")
	if err := os.MkdirAll(temp, 0755); err != nil {
		t.Fatalf("failed to create Temp: %v", err)
	}
	lock := filepath.Join(temp, "UnityLockfile")
	if err := os.WriteFile(lock, nil, 0644); err != nil {
		t.Fatalf("failed to write lockfile: %v", err)
	}
	stamp := time.Now().Add(-mtimeAgo)
	if err := os.Chtimes(lock, stamp, stamp); err != nil {
		t.Fatalf("failed to set lockfile mtime: %v", err)
	}
	return dir
}

func TestAnnotateNoEditor_NilError(t *testing.T) {
	if err := annotateNoEditor(nil, "/anything", 0); err != nil {
		t.Fatalf("got %v, want nil", err)
	}
}

// TestAnnotateNoEditor_NoEvidenceKeepsOriginal keeps the old message for a project that simply is
// not open: inventing editor_starting_or_blocked would send agents hunting for a dialog.
func TestAnnotateNoEditor_NoEvidenceKeepsOriginal(t *testing.T) {
	isolatedHome(t)
	dir := t.TempDir()

	err := annotateNoEditor(errors.New("no Unity instance found for project: x"), dir, 0)
	if err == nil {
		t.Fatal("expected the original error back")
	}
	if err.Error() != "no Unity instance found for project: x" {
		t.Fatalf("got %q, want the original message unchanged", err.Error())
	}
}

func TestAnnotateNoEditor_LockfileEvidence(t *testing.T) {
	isolatedHome(t)
	dir := openUnityProjectDir(t, 42*time.Second)

	err := annotateNoEditor(errors.New("no Unity instance found for project: "+dir), dir, 0)
	if err == nil {
		t.Fatal("expected an evidence-based error")
	}
	msg := err.Error()
	for _, want := range []string{
		"editor_starting_or_blocked",
		"Unity appears open for " + dir,
		"Temp/UnityLockfile, age 42s",
		"connector has not loaded",
		"unity-cli dialogs",
		"Lookup:   no Unity instance found for project:",
	} {
		if !strings.Contains(msg, want) {
			t.Errorf("message missing %q:\n%s", want, msg)
		}
	}
	if lines := strings.Count(msg, "\n"); lines > 4 {
		t.Errorf("message is %d lines long, keep it brief:\n%s", lines+1, msg)
	}
}

func TestAnnotateNoEditor_StaleLockfileIsFlagged(t *testing.T) {
	isolatedHome(t)
	dir := openUnityProjectDir(t, 6*time.Hour)

	msg := annotateNoEditor(errors.New("no Unity instance found"), dir, 0).Error()
	if !strings.Contains(msg, "crashed") {
		t.Errorf("expected a crash hint for a 6h old lockfile:\n%s", msg)
	}
}

func TestAnnotateNoEditor_StaleHeartbeatWithoutLockfile(t *testing.T) {
	project := t.TempDir()
	writeInstanceFile(t, client.Instance{
		State:       "compiling",
		ProjectPath: project,
		Port:        8090,
		PID:         os.Getpid(),
		Timestamp:   time.Now().Add(-4 * time.Minute).UnixMilli(),
	})

	msg := annotateNoEditor(errors.New("no active instance on port 8090"), "", 8090).Error()
	for _, want := range []string{
		"editor_starting_or_blocked",
		"connector stopped answering",
		"heartbeat last seen 4m0s ago",
		"no active instance on port 8090",
	} {
		if !strings.Contains(msg, want) {
			t.Errorf("message missing %q:\n%s", want, msg)
		}
	}
	if !strings.Contains(msg, "none at "+filepath.Join(project, "Temp", "UnityLockfile")) {
		t.Errorf("expected the missing lockfile path:\n%s", msg)
	}
}

func TestEvidenceHint_IsOneLine(t *testing.T) {
	isolatedHome(t)
	dir := openUnityProjectDir(t, 90*time.Second)

	ev := client.FindEditorEvidence(dir, 0)
	if !ev.Found() {
		t.Fatal("expected evidence")
	}
	hint := evidenceHint(ev)
	if strings.Contains(hint, "\n") {
		t.Errorf("hint must be one line:\n%s", hint)
	}
	if !strings.Contains(hint, "editor_starting_or_blocked") || !strings.Contains(hint, "unity-cli dialogs") {
		t.Errorf("unexpected hint: %q", hint)
	}
}

func TestStaleHeartbeatReport_WithoutEvidenceKeepsOldText(t *testing.T) {
	got := staleHeartbeatReport(&client.Instance{Port: 8090}, 10*time.Second, nil)
	want := "Unity (port 8090): not responding (last heartbeat 10s ago)\n"
	if got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestStaleHeartbeatReport_AddsEvidenceLine(t *testing.T) {
	isolatedHome(t)
	dir := openUnityProjectDir(t, 45*time.Second)
	ev := client.FindEditorEvidence(dir, 8090)

	got := staleHeartbeatReport(&client.Instance{Port: 8090, ProjectPath: dir}, 12*time.Second, ev)
	if !strings.HasPrefix(got, "Unity (port 8090): not responding (last heartbeat 12s ago)\n") {
		t.Errorf("first line changed:\n%s", got)
	}
	if !strings.Contains(got, "editor_starting_or_blocked") {
		t.Errorf("expected the evidence line:\n%s", got)
	}
}
