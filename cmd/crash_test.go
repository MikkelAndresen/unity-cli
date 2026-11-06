package cmd

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// monoCrashSample is the exception line a Mono dump records, as it must reach the report.
const monoCrashSample = `{
  "signal": 11,
  "payload": { "mono_version": "Mono: 5.0.17 (Vue)" },
  "managed_stack_trace": "System.NullReferenceException: Object reference not set to an instance of an object\n  at FoW.FogOfWarManager.PrepareAllTeams ()",
  "threads": {}
}`

// crashedProjectFixture builds the shape a crash leaves on disk: a heartbeat that stopped mid-sentence
// for a project holding a fresh mono_crash dump. beatAgeAgo is how long ago the last beat was written,
// artifactAgeAgo how long ago the dump was; artifactAgeAgo of -1 writes no dump.
func crashedProjectFixture(t *testing.T, state string, beatAgeAgo, artifactAgeAgo time.Duration) string {
	t.Helper()

	project := t.TempDir()
	writeInstanceFile(t, client.Instance{
		State:            state,
		ProjectPath:      project,
		Port:             8090,
		PID:              65279,
		UnityVersion:     "6000.4.4f1",
		ConnectorVersion: "0.7.0",
		Timestamp:        time.Now().Add(-beatAgeAgo).UnixMilli(),
	})
	if artifactAgeAgo >= 0 {
		writeCrashFileIn(t, project, "mono_crash.1b6a4e0a.0.json", artifactAgeAgo, monoCrashSample)
	}
	return project
}

func writeCrashFileIn(t *testing.T, dir, name string, ageAgo time.Duration, body string) string {
	t.Helper()
	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
		t.Fatalf("failed to write %s: %v", path, err)
	}
	stamp := time.Now().Add(-ageAgo)
	if err := os.Chtimes(path, stamp, stamp); err != nil {
		t.Fatalf("failed to set mtime on %s: %v", path, err)
	}
	return path
}

func TestAssessCrash_SilentHeartbeatWithDumpIsACrash(t *testing.T) {
	project := crashedProjectFixture(t, "ready", 30*time.Second, 20*time.Second)

	v := assessCrash(client.NewestHeartbeat(project, 8090))
	if !v.Crashed {
		t.Fatalf("expected a crash verdict, got %+v", v)
	}
	if len(v.Artifacts) != 1 || !strings.Contains(v.Artifacts[0].Detail, "System.NullReferenceException") {
		t.Errorf("expected the dump and its exception line, got %+v", v.Artifacts)
	}
	if reason := v.Reason(); !strings.Contains(reason, "no \"stopped\" marker") {
		t.Errorf("Reason: %q", reason)
	}
}

// TestAssessCrash_DumpOlderThanTheBeatIsAnotherCrash: the editor recovered from an earlier crash and
// beat happily until now, so the silence is not that dump's fault.
func TestAssessCrash_DumpOlderThanTheBeatIsAnotherCrash(t *testing.T) {
	stubProcessTable(t, containerProcessTable())
	project := crashedProjectFixture(t, "ready", 10*time.Second, 2*time.Hour)

	if v := assessCrash(client.NewestHeartbeat(project, 8090)); v.Crashed {
		t.Fatalf("expected no crash verdict, got %s", v.Reason())
	}
}

// TestAssessCrash_StoppedMarkerStands: a clean quit is not a crash even with a dump in the project.
func TestAssessCrash_StoppedMarkerStands(t *testing.T) {
	project := crashedProjectFixture(t, "stopped", 30*time.Second, 5*time.Second)

	v := assessCrash(client.NewestHeartbeat(project, 8090))
	if v.Crashed {
		t.Fatalf("a stopped marker must not read as a crash: %s", v.Reason())
	}
	if !v.Clean {
		t.Error("expected the Clean flag")
	}
}

func TestStalledEditorReport_CrashVersusStarting(t *testing.T) {
	stubProcessTable(t, containerProcessTable())
	// The fixture helper resets HOME for the instance files, so the first heartbeat is read before the
	// second fixture replaces it.
	crashedProject := crashedProjectFixture(t, "ready", 30*time.Second, 20*time.Second)
	crashed := client.NewestHeartbeat(crashedProject, 8090)

	report := stalledEditorReport(crashed, 30*time.Second, assessCrash(crashed))
	if !strings.HasPrefix(report, "crashed: the Unity editor died for "+crashedProject) {
		t.Errorf("first line:\n%s", report)
	}
	if !strings.Contains(report, filepath.Join(crashedProject, "mono_crash.1b6a4e0a.0.json")) {
		t.Errorf("expected the artifact path, got:\n%s", report)
	}
	if !strings.Contains(report, "System.NullReferenceException: Object reference not set to an instance of an object") {
		t.Errorf("expected the exception line, got:\n%s", report)
	}
	if !strings.Contains(report, "Next:") {
		t.Errorf("expected the next step, got:\n%s", report)
	}
	if strings.Contains(report, editorStartingState) {
		t.Errorf("a crash must not read as %s:\n%s", editorStartingState, report)
	}

	// The same silence without a dump stays the old answer: an editor that may still come back.
	quietProject := crashedProjectFixture(t, "ready", 30*time.Second, -1)
	quiet := client.NewestHeartbeat(quietProject, 8090)
	quietReport := stalledEditorReport(quiet, 30*time.Second, assessCrash(quiet))
	if !strings.HasPrefix(quietReport, "Unity (port 8090): not responding (last heartbeat 30s ago)") {
		t.Errorf("expected the old text, got:\n%s", quietReport)
	}
}

// TestCrashReport_NoDumpSaysWhereItLooked: "crashed" without a dump is still actionable, so the report
// names the places it checked.
func TestCrashReport_NoDumpSaysWhereItLooked(t *testing.T) {
	project := crashedProjectFixture(t, "ready", 30*time.Second, -1)
	inst := client.NewestHeartbeat(project, 8090)

	report := crashedReport(inst, 30*time.Second, assessCrash(inst), errors.New("no Unity instances running"))
	if !strings.Contains(report, "Crash log: none") {
		t.Errorf("expected the missing-dump line, got:\n%s", report)
	}
	if !strings.Contains(report, "mono_crash.*.json") {
		t.Errorf("expected the pattern it searched for, got:\n%s", report)
	}
	if !strings.Contains(report, "Lookup:    no Unity instances running") {
		t.Errorf("expected the lookup failure to stay visible, got:\n%s", report)
	}
}

// TestAnnotateNoEditor_PrefersCrash: after a crash the instance scan can drop the heartbeat file, so
// the "no instance" path is where the verdict has to surface — and it outranks the
// editor_starting_or_blocked reading, because a crashed editor is not worth waiting for.
func TestAnnotateNoEditor_PrefersCrash(t *testing.T) {
	project := crashedProjectFixture(t, "ready", 30*time.Second, 20*time.Second)

	err := annotateNoEditor(errors.New("no Unity instance found for project: "+project), project, 0)
	if err == nil {
		t.Fatal("expected the crash annotation")
	}
	if !strings.Contains(err.Error(), crashState) {
		t.Errorf("expected %s, got:\n%s", crashState, err)
	}
	if strings.Contains(err.Error(), editorStartingState) {
		t.Errorf("must not also read as %s:\n%s", editorStartingState, err)
	}
}

// TestDiscoverStatusInstance_CrashHeartbeatSurvivesTheScan drives the port-less status entry point with
// the pre-scan heartbeat the real discovery returns, proving the fallback does not re-read a file the
// scan has already deleted.
func TestDiscoverStatusInstance_CrashHeartbeatSurvivesTheScan(t *testing.T) {
	project := t.TempDir()
	writeCrashFileIn(t, project, "mono_crash.1b6a4e0a.0.json", 20*time.Second, monoCrashSample)
	beat := &client.Instance{
		State:       "ready",
		ProjectPath: project,
		Port:        8090,
		PID:         65279,
		Timestamp:   time.Now().Add(-30 * time.Second).UnixMilli(),
	}

	orig := discoverInstanceKeepingEvidence
	discoverInstanceKeepingEvidence = func(string, int) (*client.Instance, *client.Instance, error) {
		return nil, beat, errors.New("no Unity instances running")
	}
	t.Cleanup(func() { discoverInstanceKeepingEvidence = orig })

	got, err := discoverStatusInstance(project, 0)
	if err != nil {
		t.Fatalf("expected the crashed heartbeat, got %v", err)
	}
	if got != beat {
		t.Fatalf("got %+v, want the heartbeat read before the scan", got)
	}
}

// TestAnnotateNoEditorWith_PrefersCrashAfterTheScan: commands outside status discover first, and the
// scan can already have deleted the heartbeat; the pre-scan record must still win over startup evidence.
func TestAnnotateNoEditorWith_PrefersCrashAfterTheScan(t *testing.T) {
	project := t.TempDir()
	writeCrashFileIn(t, project, "mono_crash.1b6a4e0a.0.json", 20*time.Second, monoCrashSample)
	beat := &client.Instance{
		State:       "ready",
		ProjectPath: project,
		Port:        8090,
		PID:         65279,
		Timestamp:   time.Now().Add(-30 * time.Second).UnixMilli(),
	}

	err := annotateNoEditorWith(beat, errors.New("no Unity instances running"), project, 0)
	if err == nil || !strings.Contains(err.Error(), crashState) {
		t.Fatalf("expected the crash report from the pre-scan heartbeat, got %v", err)
	}
	if strings.Contains(err.Error(), editorStartingState) {
		t.Errorf("must not fall back to %s:\n%s", editorStartingState, err)
	}
}

// TestAnnotateNoEditor_WithoutDumpKeepsEvidence: the starting-or-blocked answer must survive untouched.
func TestAnnotateNoEditor_WithoutDumpKeepsEvidence(t *testing.T) {
	dir := openUnityProjectDir(t, 45*time.Second)

	err := annotateNoEditor(errors.New("no Unity instance found"), dir, 0)
	if err == nil || !strings.Contains(err.Error(), editorStartingState) {
		t.Fatalf("expected the evidence annotation, got %v", err)
	}
}

// TestReadStatus_KeepsADeadPIDsHeartbeat: the instance scan judges pids and drops files it deems dead.
// status must read the file regardless, or the crashed editor it exists to report has already been
// deleted from view.
func TestReadStatus_KeepsADeadPIDsHeartbeat(t *testing.T) {
	writeInstanceFile(t, client.Instance{
		State:       "ready",
		ProjectPath: t.TempDir(),
		Port:        8090,
		PID:         999999999,
		Timestamp:   time.Now().Add(-time.Minute).UnixMilli(),
	})

	got, err := readStatus(8090)
	if err != nil {
		t.Fatalf("expected the raw heartbeat file, got %v", err)
	}
	if got.PID != 999999999 || got.State != "ready" {
		t.Errorf("unexpected instance: %+v", got)
	}
	if got.SourceFile == "" {
		t.Error("expected the file the heartbeat was read from")
	}
}

// TestStatusCmd_CrashExitsNonZero: "not responding" is a state an agent can wait out; a dead editor is
// a failed command.
func TestStatusCmd_CrashExitsNonZero(t *testing.T) {
	crashedProjectFixture(t, "ready", 30*time.Second, 20*time.Second)
	inst, err := readStatus(8090)
	if err != nil {
		t.Fatalf("readStatus: %v", err)
	}

	err = statusCmd(inst)
	if err == nil {
		t.Fatal("expected a non-nil error so the CLI exits non-zero")
	}
	if !strings.Contains(err.Error(), crashState) || !strings.Contains(err.Error(), "port 8090") {
		t.Errorf("unexpected error: %v", err)
	}
}

// TestStatusCmd_QuietHeartbeatKeepsExitZero pins the difference: the same silence with no dump is still
// reported and still exits 0, as it did before crash detection.
func TestStatusCmd_QuietHeartbeatKeepsExitZero(t *testing.T) {
	stubProcessTable(t, containerProcessTable())
	crashedProjectFixture(t, "ready", 30*time.Second, -1)
	inst, err := readStatus(8090)
	if err != nil {
		t.Fatalf("readStatus: %v", err)
	}

	if err := statusCmd(inst); err != nil {
		t.Errorf("expected no error for a merely silent heartbeat, got %v", err)
	}
}

// TestWaitForAlive_StopsOnACrashVerdict: the wait loop is for an editor that may come back. A heartbeat
// the disk says is dead ends it with the crash report instead of a 120 s timeout.
func TestWaitForAlive_StopsOnACrashVerdict(t *testing.T) {
	project := crashedProjectFixture(t, "ready", 30*time.Second, 20*time.Second)
	resolve := resolveKeepingEvidence(project, 8090)

	start := time.Now()
	_, err := waitForAliveContext(context.Background(), resolve, 30000)
	if err == nil {
		t.Fatal("expected the crash report")
	}
	if elapsed := time.Since(start); elapsed > 2*time.Second {
		t.Errorf("expected an immediate verdict, took %v", elapsed)
	}
	if !strings.Contains(err.Error(), crashState) || !strings.Contains(err.Error(), "mono_crash.1b6a4e0a.0.json") {
		t.Errorf("unexpected error:\n%v", err)
	}
}

// TestWaitForAlive_CrashSurvivesTheDeletedHeartbeat: the scan can delete the dead-pid file while the
// wait is polling; the resolver must keep the record read before it, or the wait runs to its timeout.
func TestWaitForAlive_CrashSurvivesTheDeletedHeartbeat(t *testing.T) {
	project := crashedProjectFixture(t, "ready", 30*time.Second, 20*time.Second)
	beat := client.NewestHeartbeat(project, 8090)

	orig := discoverInstanceKeepingEvidence
	discoverInstanceKeepingEvidence = func(string, int) (*client.Instance, *client.Instance, error) {
		return nil, beat, errors.New("no Unity instances running")
	}
	t.Cleanup(func() { discoverInstanceKeepingEvidence = orig })

	start := time.Now()
	_, err := waitForAliveContext(context.Background(), resolveKeepingEvidence(project, 8090), 30000)
	if err == nil || !strings.Contains(err.Error(), crashState) {
		t.Fatalf("expected the crash report, got %v after %v", err, time.Since(start))
	}
	if elapsed := time.Since(start); elapsed > 2*time.Second {
		t.Errorf("expected an immediate verdict, took %v", elapsed)
	}
}

// TestWaitForAlive_SilentHeartbeatStillWaits: without crash evidence the old patience stands.
func TestWaitForAlive_SilentHeartbeatStillWaits(t *testing.T) {
	stubProcessTable(t, containerProcessTable())
	project := crashedProjectFixture(t, "ready", 30*time.Second, -1)
	resolve := func() (*client.Instance, error) { return client.NewestHeartbeat(project, 8090), nil }

	_, err := waitForAliveContext(context.Background(), resolve, 200)
	if err == nil || !strings.Contains(err.Error(), "timed out") {
		t.Fatalf("expected the old timeout, got %v", err)
	}
}

func TestIsConnectionFailure_CrashIsNotRetryable(t *testing.T) {
	crash := &client.ConnectionFailure{
		Class:     client.FailureCrash,
		Transport: errors.New("cannot connect to Unity at port 8090: EOF"),
	}
	if isConnectionFailure(crash) {
		t.Error("a proven crash must not keep a retry loop polling a dead editor")
	}

	reload := &client.ConnectionFailure{
		Class:     client.FailureReload,
		Transport: errors.New("cannot connect to Unity at port 8090: EOF"),
	}
	if !isConnectionFailure(reload) {
		t.Error("an editor mid-reload is still momentarily unreachable")
	}

	if !isConnectionFailure(errors.New("cannot connect to Unity at port 8090: connection refused")) {
		t.Error("unclassified connection failures must keep their old behaviour")
	}
}

// TestIsConnectionFailure_MatchesEOFOnlyAsATransportEnd: "EOF" inside unrelated text (a JSON parser
// complaining about an EOF marker, say) is not a dropped connection.
func TestIsConnectionFailure_MatchesEOFOnlyAsATransportEnd(t *testing.T) {
	tests := []struct {
		err  string
		want bool
	}{
		{"cannot connect to Unity at port 8090: EOF", true},
		{"EOF", true},
		{"unexpected EOF", true},
		{"cannot connect to Unity at port 8090: connection reset by peer", true},
		{"decode error: EOF marker found in archive", false},
		{"compile_diagnostics: invalid character 'E' looking for beginning of value", false},
	}
	for _, tt := range tests {
		if got := isConnectionFailure(errors.New(tt.err)); got != tt.want {
			t.Errorf("isConnectionFailure(%q) = %v, want %v", tt.err, got, tt.want)
		}
	}
}
