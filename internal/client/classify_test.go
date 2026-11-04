package client

import (
	"errors"
	"io"
	"net"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// freePort returns a local port nothing is listening on, so a request to it fails the way a dead
// editor's does: connection refused.
func freePort(t *testing.T) int {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("could not reserve a port: %v", err)
	}
	port := ln.Addr().(*net.TCPAddr).Port
	_ = ln.Close()
	return port
}

// stubProcessTable replaces the OS process table for one test, the way isProcessDead is replaced.
// A nil table means "this CLI cannot read one at all".
func stubProcessTable(t *testing.T, table map[int]string) {
	t.Helper()
	orig := processTable
	processTable = func() map[int]string { return table }
	t.Cleanup(func() { processTable = orig })
}

// containerTable is the process table an agent-sandbox container sees when the editor runs on another
// machine: plenty of processes, none of them Unity, and the connector's pid missing.
func containerTable() map[int]string {
	return map[int]string{1: "init", 42: "node", os.Getpid(): "go"}
}

func TestJudgePID(t *testing.T) {
	tests := []struct {
		name        string
		pid         int
		table       map[int]string
		want        pidProof
		wantNoteHas string
	}{
		{
			name:        "no pid recorded",
			pid:         0,
			table:       containerTable(),
			want:        pidUnproven,
			wantNoteHas: "records no pid",
		},
		{
			name:        "no readable process table",
			pid:         4321,
			table:       nil,
			want:        pidUnproven,
			wantNoteHas: "no process table",
		},
		{
			name:        "pid runs Unity here",
			pid:         4321,
			table:       map[int]string{4321: "/Applications/Unity/Hub/Editor/6000.4.4f1/Unity.app/Contents/MacOS/Unity"},
			want:        pidAlive,
			wantNoteHas: "is running",
		},
		{
			name:        "windows spelling",
			pid:         4321,
			table:       map[int]string{4321: `C:\Program Files\Unity\Hub\Editor\Editor\Unity.exe`},
			want:        pidAlive,
			wantNoteHas: "",
		},
		{
			name:        "pid taken over by another program",
			pid:         4321,
			table:       map[int]string{4321: "nginx", 5555: "Unity"},
			want:        pidUnproven,
			wantNoteHas: "not Unity",
		},
		{
			name:        "row exists but the name is unreadable",
			pid:         4321,
			table:       map[int]string{4321: "", 5555: "Unity"},
			want:        pidUnproven,
			wantNoteHas: "unreadable",
		},
		{
			name:        "pid gone while other Unity processes are listed",
			pid:         4321,
			table:       map[int]string{5555: "Unity", 5556: "Unity"},
			want:        pidGone,
			wantNoteHas: "still lists Unity as pid",
		},
		{
			name:        "container cannot see the editor's machine",
			pid:         4321,
			table:       containerTable(),
			want:        pidUnproven,
			wantNoteHas: "proves nothing",
		},
		{
			name:        "Unity Hub alone does not prove visibility",
			pid:         4321,
			table:       map[int]string{777: "Unity Hub", 778: "UnityShaderCompiler"},
			want:        pidUnproven,
			wantNoteHas: "no Unity process is visible",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			stubProcessTable(t, tc.table)

			proof, note := judgePID(tc.pid)
			if proof != tc.want {
				t.Errorf("proof: got %v, want %v (note: %s)", proof, tc.want, note)
			}
			if tc.wantNoteHas != "" && !strings.Contains(note, tc.wantNoteHas) {
				t.Errorf("note: got %q, want it to contain %q", note, tc.wantNoteHas)
			}
		})
	}
}

func TestAssessSilence(t *testing.T) {
	const beatAge = 30 * time.Second

	tests := []struct {
		name        string
		state       string
		beatAge     time.Duration
		pid         int
		artifactAge time.Duration // >0 writes a mono_crash that old
		advanced    bool          // the connector beat again after the baseline
		table       map[int]string
		wantCrashed bool
		wantClean   bool
		wantStale   bool
	}{
		{
			name:        "clean shutdown is never a crash, artifact or not",
			state:       "stopped",
			beatAge:     beatAge,
			pid:         65279,
			artifactAge: 5 * time.Second,
			table:       map[int]string{5555: "Unity"},
			wantClean:   true,
			wantStale:   true,
		},
		{
			name:    "heartbeat still beating: something else dropped the socket",
			state:   "ready",
			beatAge: 1 * time.Second, // fresher than BeatSilenceAge
			pid:     65279,
			table:   containerTable(),
		},
		{
			name:      "silent heartbeat, no artifact, pid invisible: not responding, not crashed",
			state:     "ready",
			beatAge:   beatAge,
			pid:       65279,
			table:     containerTable(),
			wantStale: true,
		},
		{
			name:        "silent heartbeat plus a crash dump",
			state:       "ready",
			beatAge:     beatAge,
			pid:         65279,
			artifactAge: 20 * time.Second,
			table:       containerTable(),
			wantCrashed: true,
			wantStale:   true,
		},
		{
			name:        "pid provably gone needs no artifact",
			state:       "ready",
			beatAge:     4 * time.Second,
			pid:         65279,
			table:       map[int]string{5555: "Unity"},
			wantCrashed: true,
			wantStale:   true,
		},
		{
			name:        "artifact from before the last beat belongs to an earlier crash",
			state:       "ready",
			beatAge:     beatAge,
			pid:         65279,
			artifactAge: 10 * time.Minute,
			table:       containerTable(),
			wantStale:   true,
		},
		{
			name:      "compiling heartbeat without crash wording stays a reload",
			state:     "compiling",
			beatAge:   beatAge,
			pid:       65279,
			table:     containerTable(),
			wantStale: true,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			stubProcessTable(t, tc.table)
			project := t.TempDir()
			beat := time.Now().Add(-tc.beatAge)
			if tc.artifactAge > 0 {
				writeCrashFile(t, project, "mono_crash.1b6a4e0a.0.json", tc.artifactAge, monoCrashFixture)
			}
			baseline := beat
			if tc.advanced {
				beat = beat.Add(time.Second)
			}
			writeInstanceFiles(t, map[string]Instance{
				"one.json": {State: tc.state, ProjectPath: project, Port: 8090, PID: tc.pid, Timestamp: beat.UnixMilli()},
			})
			v := AssessSilence(NewestHeartbeat(project, 0), baseline, baseline.UnixMilli())
			if v.Crashed != tc.wantCrashed {
				t.Errorf("Crashed: got %v, want %v (reason: %s)", v.Crashed, tc.wantCrashed, v.Reason())
			}
			if v.Clean != tc.wantClean {
				t.Errorf("Clean: got %v, want %v", v.Clean, tc.wantClean)
			}
			if v.Stale != tc.wantStale {
				t.Errorf("Stale: got %v, want %v", v.Stale, tc.wantStale)
			}
		})
	}
}

// TestExplainDroppedConnection_Crash covers the wiring: the bare EOF the connector's death produces
// must come back saying the editor died, with the artifact and its first line.
func TestExplainDroppedConnection_Crash(t *testing.T) {
	stubProcessTable(t, containerTable())
	project := t.TempDir()
	artifact := writeCrashFile(t, project, "mono_crash.1b6a4e0a.0.json", 20*time.Second, monoCrashFixture)
	beat := time.Now().Add(-30 * time.Second)
	writeInstanceFiles(t, map[string]Instance{
		"one.json": {State: "ready", ProjectPath: project, Port: 8090, PID: 65279, ConnectorVersion: "0.7.0", Timestamp: beat.UnixMilli()},
	})

	inst := NewestHeartbeat(project, 0)
	err := explainDroppedConnection("exec", inst, time.Now().Add(-25*time.Second), io.EOF)

	var failure *ConnectionFailure
	if !errors.As(err, &failure) {
		t.Fatalf("expected a ConnectionFailure, got %T: %v", err, err)
	}
	if !failure.Crashed() {
		t.Fatalf("expected the crash class, got %v", failure.Class)
	}
	msg := err.Error()
	for _, want := range []string{
		"editor process 65279 exited during the request (crash)",
		"cannot connect to Unity at port 8090",
		artifact,
		"System.NullReferenceException: Object reference not set to an instance of an object",
	} {
		if !strings.Contains(msg, want) {
			t.Errorf("message must contain %q, got:\n%s", want, msg)
		}
	}
	if !errors.Is(err, io.EOF) {
		t.Error("the transport error must stay reachable through Unwrap")
	}
}

// TestExplainDroppedConnection_Reload keeps a domain reload from being reported as a crash: the
// heartbeat is still arriving, on a new port.
func TestExplainDroppedConnection_Reload(t *testing.T) {
	stubProcessTable(t, containerTable())
	project := t.TempDir()
	writeInstanceFiles(t, map[string]Instance{
		"new.json": {State: "compiling", ProjectPath: project, Port: 8091, PID: 65279, Timestamp: time.Now().Add(-2 * time.Second).UnixMilli()},
	})

	inst := &Instance{State: "ready", ProjectPath: project, Port: 8090, PID: 65279, Timestamp: time.Now().Add(-10 * time.Second).UnixMilli()}
	err := explainDroppedConnection("test", inst, time.Now().Add(-5*time.Second), io.EOF)

	var failure *ConnectionFailure
	if !errors.As(err, &failure) {
		t.Fatalf("expected a ConnectionFailure, got %T: %v", err, err)
	}
	if failure.Class != FailureReload {
		t.Fatalf("expected the reload class, got %v: %s", failure.Class, err)
	}
	if strings.Contains(err.Error(), "crash") && !strings.Contains(err.Error(), "not a crash") {
		t.Errorf("must not read as a crash: %s", err)
	}
	if !strings.Contains(err.Error(), "port 8091") {
		t.Errorf("expected the new port in the message, got:\n%s", err)
	}
}

// TestExplainDroppedConnection_DumpArrivesLate: Mono's crash handler writes its dump a moment after the
// socket dies, so the first reading can be one artifact short of the verdict. The second reading is what
// keeps the reported error from being the bare EOF the issue is about.
func TestExplainDroppedConnection_DumpArrivesLate(t *testing.T) {
	stubProcessTable(t, containerTable())
	origGrace := crashArtifactGrace
	crashArtifactGrace = 30 * time.Millisecond
	t.Cleanup(func() { crashArtifactGrace = origGrace })

	project := t.TempDir()
	writeInstanceFiles(t, map[string]Instance{
		"one.json": {State: "ready", ProjectPath: project, Port: 8090, PID: 65279, Timestamp: time.Now().UnixMilli()},
	})
	inst := NewestHeartbeat(project, 0)

	dump := make(chan string, 1)
	go func() {
		time.Sleep(10 * time.Millisecond)
		path := filepath.Join(project, "mono_crash.late.0.json")
		if writeErr := os.WriteFile(path, []byte(monoCrashFixture), 0o644); writeErr != nil {
			dump <- ""
			return
		}
		dump <- path
	}()

	err := explainDroppedConnection("exec", inst, time.Now().Add(-time.Second), io.EOF)
	dumpPath := <-dump
	if dumpPath == "" {
		t.Fatal("the goroutine failed to write the dump fixture")
	}
	var failure *ConnectionFailure
	if !errors.As(err, &failure) || !failure.Crashed() {
		t.Fatalf("expected the crash class after the grace, got %T: %v", err, err)
	}
	if !strings.Contains(err.Error(), "exited during the request (crash)") {
		t.Errorf("unexpected message:\n%s", err)
	}
}

// TestExplainDroppedConnection_LiveEditorPaysNoGrace: a pid this machine proves alive means the editor
// is up, so the second reading is skipped and the answer comes back immediately.
func TestExplainDroppedConnection_LiveEditorPaysNoGrace(t *testing.T) {
	stubProcessTable(t, map[int]string{65279: "/Applications/Unity/Hub/Editor/6000.4.4f1/Unity.app/Contents/MacOS/Unity"})
	origGrace := crashArtifactGrace
	crashArtifactGrace = 2 * time.Second
	t.Cleanup(func() { crashArtifactGrace = origGrace })

	project := t.TempDir()
	writeInstanceFiles(t, map[string]Instance{
		"one.json": {State: "compiling", ProjectPath: project, Port: 8090, PID: 65279, Timestamp: time.Now().Add(-2 * time.Second).UnixMilli()},
	})

	start := time.Now()
	err := explainDroppedConnection("exec", NewestHeartbeat(project, 0), start, io.EOF)
	if elapsed := time.Since(start); elapsed > time.Second {
		t.Errorf("a live editor must not pay the crash-dump grace, took %v", elapsed)
	}
	if !strings.Contains(err.Error(), "not a crash") {
		t.Errorf("expected the reload answer, got:\n%s", err)
	}
}

// TestExplainDroppedConnection_UnexplainedSilenceKeepsTheOldText: an editor that is merely wedged gets
// the original transport failure, so nothing is invented about it.
func TestExplainDroppedConnection_UnexplainedSilenceKeepsTheOldText(t *testing.T) {
	stubProcessTable(t, containerTable())
	project := t.TempDir()
	writeInstanceFiles(t, map[string]Instance{
		"one.json": {State: "ready", ProjectPath: project, Port: 8090, PID: 65279, Timestamp: time.Now().Add(-30 * time.Second).UnixMilli()},
	})

	inst := NewestHeartbeat(project, 0)
	err := explainDroppedConnection("exec", inst, time.Now().Add(-25*time.Second), io.EOF)

	var failure *ConnectionFailure
	if errors.As(err, &failure) {
		t.Fatalf("expected the plain transport error, got a classified one: %v", err)
	}
	want := "cannot connect to Unity at port 8090: EOF"
	if err.Error() != want {
		t.Errorf("got %q, want %q", err.Error(), want)
	}
}

// TestExplainDroppedConnection_TimeoutIsNotACrash: a slow editor must never be called dead.
func TestExplainDroppedConnection_TimeoutIsNotACrash(t *testing.T) {
	stubProcessTable(t, map[int]string{5555: "Unity"})
	project := t.TempDir()
	writeCrashFile(t, project, "mono_crash.1b6a4e0a.0.json", 5*time.Second, monoCrashFixture)
	writeInstanceFiles(t, map[string]Instance{
		"one.json": {State: "ready", ProjectPath: project, Port: 8090, PID: 65279, Timestamp: time.Now().Add(-30 * time.Second).UnixMilli()},
	})

	inst := NewestHeartbeat(project, 0)
	err := explainDroppedConnection("exec", inst, time.Now().Add(-time.Minute),
		&timeoutLikeError{msg: "Post \"http://127.0.0.1:8090/command\": context deadline exceeded"})
	if strings.Contains(err.Error(), "crash") {
		t.Errorf("a timeout must not be reported as a crash: %s", err)
	}
}

// timeoutLikeError is what an http.Client reports when its own timeout expires: a slow editor, not a
// dead one.
type timeoutLikeError struct{ msg string }

func (e *timeoutLikeError) Error() string { return e.msg }
func (e *timeoutLikeError) Timeout() bool { return true }

// TestSend_DroppedConnectionIsClassified drives the real Send path against a port nothing listens on,
// which is how an editor that died mid-request presents itself.
func TestSend_DroppedConnectionIsClassified(t *testing.T) {
	stubProcessTable(t, containerTable())
	project := t.TempDir()
	artifact := writeCrashFile(t, project, "mono_crash.1b6a4e0a.0.json", 20*time.Second, monoCrashFixture)
	deadPort := freePort(t)
	writeInstanceFiles(t, map[string]Instance{
		"one.json": {State: "ready", ProjectPath: project, Port: deadPort, PID: 65279, Timestamp: time.Now().Add(-30 * time.Second).UnixMilli()},
	})

	_, err := Send(NewestHeartbeat(project, 0), "exec", nil, 5000)
	if err == nil {
		t.Fatal("expected the request to fail")
	}
	var failure *ConnectionFailure
	if !errors.As(err, &failure) || !failure.Crashed() {
		t.Fatalf("expected a crash-classified failure, got %T: %v", err, err)
	}
	for _, want := range []string{"exited during the request (crash)", artifact} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("message must contain %q, got:\n%s", want, err)
		}
	}
}
