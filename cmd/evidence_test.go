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

// An ambiguous lookup is not "no editor": several answer, and evidence read without a target would
// describe whichever project happens to have the newest lockfile or heartbeat.
func TestAnnotateNoEditor_AmbiguousLookupPassesThrough(t *testing.T) {
	isolatedHome(t)
	dir := openUnityProjectDir(t, 42*time.Second)
	t.Chdir(dir)

	amb := &client.AmbiguousInstanceError{Candidates: []client.Instance{
		{ProjectPath: "/work/a", Port: 8090, State: "ready"},
		{ProjectPath: "/work/b", Port: 8091, State: "ready"},
	}}
	err := annotateNoEditor(amb, "", 0)
	if err != amb {
		t.Fatalf("got %v, want the ambiguity error unchanged", err)
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

func TestAnnotateNoEditor_StaleHeartbeatWithoutLockfileIsUnresponsive(t *testing.T) {
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
		"editor_unresponsive",
		"stopped answering 4m0s ago",
		"no active instance on port 8090",
		"unity-restart",
	} {
		if !strings.Contains(msg, want) {
			t.Errorf("message missing %q:\n%s", want, msg)
		}
	}
	if strings.Contains(msg, "editor_starting_or_blocked") {
		t.Errorf("a loaded connector must not read as startup:\n%s", msg)
	}
	if !strings.Contains(msg, "none at "+filepath.Join(project, "Temp", "UnityLockfile")) {
		t.Errorf("expected the missing lockfile path:\n%s", msg)
	}
	for _, unwanted := range []string{"unity-cli dialogs", "main thread is likely blocked"} {
		if strings.Contains(msg, unwanted) {
			t.Errorf("a gone editor must not be reported as a dialog/wedged main thread (%q):\n%s", unwanted, msg)
		}
	}
}

// TestAnnotateNoEditor_OldLockfileWithSessionHeartbeatIsUnresponsive pins the live misreading: an
// editor open for hours is not a crash, and a heartbeat written after its lockfile proves the
// connector loaded this session and then went silent.
func TestAnnotateNoEditor_OldLockfileWithSessionHeartbeatIsUnresponsive(t *testing.T) {
	stubProcessTable(t, containerProcessTable())
	isolatedHome(t)
	dir := openUnityProjectDir(t, 8*time.Hour)
	writeInstanceFile(t, client.Instance{
		State:       "ready",
		ProjectPath: dir,
		Port:        8090,
		PID:         65279,
		Timestamp:   time.Now().Add(-16 * time.Minute).UnixMilli(),
	})

	msg := annotateNoEditor(errors.New("no active instance on port 8090"), "", 8090).Error()
	for _, want := range []string{
		"editor_unresponsive",
		"stopped answering 16m0s ago",
		"pid 65279",
		`last state "ready"`,
		"unity-cli dialogs",
		"unity-restart",
	} {
		if !strings.Contains(msg, want) {
			t.Errorf("message missing %q:\n%s", want, msg)
		}
	}
	for _, unwanted := range []string{
		"editor_starting_or_blocked",
		"probably crashed",
		"connector has not loaded",
	} {
		if strings.Contains(msg, unwanted) {
			t.Errorf("message must not contain %q for a loaded connector:\n%s", unwanted, msg)
		}
	}
}

// TestAnnotateNoEditor_AnySessionHeartbeatSuppressesCrashHint covers the heartbeats that are not
// "silent": fresh or cleanly stopped, but still written this session. They prove the connector
// loaded, so the old lockfile must not be read as a crash.
func TestAnnotateNoEditor_AnySessionHeartbeatSuppressesCrashHint(t *testing.T) {
	for _, state := range []string{"ready", "stopped"} {
		t.Run(state, func(t *testing.T) {
			isolatedHome(t)
			dir := openUnityProjectDir(t, 6*time.Hour)
			writeInstanceFile(t, client.Instance{
				State:       state,
				ProjectPath: dir,
				Port:        8090,
				PID:         os.Getpid(),
				Timestamp:   time.Now().UnixMilli(),
			})

			msg := annotateNoEditor(errors.New("no active instance on port 8090"), "", 8090).Error()
			if strings.Contains(msg, "probably crashed") {
				t.Errorf("a %q heartbeat this session rules out the lockfile crash hint:\n%s", state, msg)
			}
		})
	}
}

// TestAnnotateNoEditor_OldLockfileWithOlderHeartbeatIsStartup covers a restart: the heartbeat on
// disk belongs to the previous session, so the lockfile age may still hint at a crash.
func TestAnnotateNoEditor_OldLockfileWithOlderHeartbeatIsStartup(t *testing.T) {
	isolatedHome(t)
	dir := openUnityProjectDir(t, 6*time.Hour)
	writeInstanceFile(t, client.Instance{
		State:       "ready",
		ProjectPath: dir,
		Port:        8090,
		PID:         65279,
		Timestamp:   time.Now().Add(-8 * time.Hour).UnixMilli(),
	})

	msg := annotateNoEditor(errors.New("no active instance on port 8090"), "", 8090).Error()
	for _, want := range []string{
		"editor_starting_or_blocked",
		"probably crashed",
		"rather than still starting",
		"unity-restart",
	} {
		if !strings.Contains(msg, want) {
			t.Errorf("message missing %q:\n%s", want, msg)
		}
	}
	if strings.Contains(msg, "connector has not loaded") {
		t.Errorf("a 6h old lockfile must not read as a startup in progress:\n%s", msg)
	}
	if strings.Contains(msg, editorUnresponsiveState) {
		t.Errorf("a heartbeat from before the lockfile must not count as this session:\n%s", msg)
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

func TestStaleHeartbeatReport_AfterSessionHeartbeatSaysUnresponsive(t *testing.T) {
	isolatedHome(t)
	dir := openUnityProjectDir(t, time.Hour)
	writeInstanceFile(t, client.Instance{
		State:       "ready",
		ProjectPath: dir,
		Port:        8090,
		PID:         65279,
		Timestamp:   time.Now().Add(-20 * time.Minute).UnixMilli(),
	})
	ev := client.FindEditorEvidence(dir, 8090)

	got := staleHeartbeatReport(&client.Instance{Port: 8090, ProjectPath: dir}, 20*time.Minute, ev)
	if !strings.Contains(got, editorUnresponsiveState) {
		t.Errorf("expected the unresponsive state:\n%s", got)
	}
	if strings.Contains(got, "probably crashed") {
		t.Errorf("a heartbeat this session rules out the lockfile crash hint:\n%s", got)
	}
}

// TestAnnotateNoEditor_ShortSilenceAfterSessionHeartbeatIsUnresponsive covers the gap between
// status's 3s cut-off and the client's 10s "stale" cut-off: a domain reload pauses the heartbeat
// for a few seconds, and the connector has plainly loaded this session.
func TestAnnotateNoEditor_ShortSilenceAfterSessionHeartbeatIsUnresponsive(t *testing.T) {
	isolatedHome(t)
	dir := openUnityProjectDir(t, 6*time.Hour)
	writeInstanceFile(t, client.Instance{
		State:       "ready",
		ProjectPath: dir,
		Port:        8090,
		PID:         os.Getpid(),
		Timestamp:   time.Now().Add(-5 * time.Second).UnixMilli(),
	})

	msg := annotateNoEditor(errors.New("no active instance on port 8090"), "", 8090).Error()
	for _, want := range []string{
		"editor_unresponsive",
		"stopped answering 5s ago",
		"last state \"ready\"",
	} {
		if !strings.Contains(msg, want) {
			t.Errorf("message missing %q:\n%s", want, msg)
		}
	}
	for _, unwanted := range []string{"editor_starting_or_blocked", "connector has not loaded", "probably crashed"} {
		if strings.Contains(msg, unwanted) {
			t.Errorf("message must not contain %q:\n%s", unwanted, msg)
		}
	}
}

// TestEvidenceHint_NoLockfileDoesNotSendToDialogs: status prints the one-line hint, which once kept
// "try 'unity-cli dialogs'" after concluding the editor had exited.
func TestEvidenceHint_NoLockfileDoesNotSendToDialogs(t *testing.T) {
	project := t.TempDir()
	writeInstanceFile(t, client.Instance{
		State:       "ready",
		ProjectPath: project,
		Port:        8090,
		PID:         os.Getpid(),
		Timestamp:   time.Now().Add(-4 * time.Minute).UnixMilli(),
	})

	ev := client.FindEditorEvidence(project, 0)
	if beat, _ := ev.UnresponsiveHeartbeat(); beat == nil {
		t.Fatalf("expected an unresponsive heartbeat, got %+v", ev)
	}
	hint := evidenceHint(ev)
	if strings.Contains(hint, "dialogs") || !strings.Contains(hint, "probably exited") {
		t.Errorf("hint must agree with the no-lockfile verdict:\n%s", hint)
	}
}
