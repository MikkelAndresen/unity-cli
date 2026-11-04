package cmd

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// fakeWait feeds wait a scripted sequence of heartbeats and one compile_status answer. The last
// heartbeat repeats, so a test only lists what changes.
type fakeWait struct {
	t          *testing.T
	states     []client.Instance
	prov       *compileProvenance // nil: the connector has no compile_status tool
	provCalls  int
	calls      []string
	resolveErr error
}

func (f *fakeWait) resolve() (*client.Instance, error) {
	if f.resolveErr != nil {
		return nil, f.resolveErr
	}
	inst := f.states[0]
	if len(f.states) > 1 {
		f.states = f.states[1:]
	}
	if inst.Timestamp == 0 {
		inst.Timestamp = time.Now().UnixMilli()
	}
	if inst.Port == 0 {
		inst.Port = 8090
	}
	return &inst, nil
}

func (f *fakeWait) send(command string, _ interface{}) (*client.CommandResponse, error) {
	f.calls = append(f.calls, command)
	if command != "compile_status" {
		f.t.Fatalf("unexpected command %q", command)
	}
	f.provCalls++
	if f.prov == nil {
		return &client.CommandResponse{Success: false, Message: "Unknown command: compile_status"}, nil
	}
	data, _ := json.Marshal(f.prov)
	return &client.CommandResponse{Success: true, Data: data}, nil
}

func (f *fakeWait) run(args ...string) (int, string) {
	f.t.Helper()
	origPoll := statusPollInterval
	origGrace := waitStaleGrace
	f.t.Cleanup(func() {
		statusPollInterval = origPoll
		waitStaleGrace = origGrace
	})
	statusPollInterval = time.Millisecond
	waitStaleGrace = 20 * time.Millisecond

	var out bytes.Buffer
	return waitCmd(context.Background(), args, f.send, f.resolve, &out), out.String()
}

func prov(line string, staleCount int, staleFiles ...string) *compileProvenance {
	return &compileProvenance{
		CompiledAtMs:   1_793_782_952_102,
		ScanComplete:   true,
		ProvenanceLine: line,
		StaleCount:     staleCount,
		StaleFiles:     staleFiles,
		StaleLine:      staleLineFor(staleCount, staleFiles),
	}
}

func staleLineFor(count int, files []string) string {
	if count == 0 {
		return ""
	}
	return "stale: " + strings.Join(files, ", ") + " modified after this compile — run verify again"
}

const provLine = "compiled 14:02:11 (3 s ago, took 2.2 s) · 1 source file changed since previous compile · rebuilt: Game"

func TestWait_IdleEditorAnswersAtOnceWithProvenance(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{{State: "ready", CompileFinishedAt: 1000}}, prov: prov(provLine, 0)}
	code, out := f.run()
	if code != waitIdle {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	want := "Unity (port 8090): ready\n" + provLine + "\n"
	if out != want {
		t.Errorf("output = %q, want %q", out, want)
	}
	if strings.Join(f.calls, ",") != "compile_status" {
		t.Errorf("calls = %v, want just compile_status", f.calls)
	}
}

func TestWait_BlocksUntilTheEditorStopsWorking(t *testing.T) {
	f := &fakeWait{t: t, prov: prov(provLine, 0), states: []client.Instance{
		{State: "compiling", CompileFinishedAt: 1000},
		{State: "reloading", CompileFinishedAt: 1000},
		{State: "building", CompileFinishedAt: 1000},
		{State: "ready", CompileFinishedAt: 1000},
	}}
	code, out := f.run()
	if code != waitIdle {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if !strings.HasPrefix(out, "Unity (port 8090): ready\n") {
		t.Errorf("output = %q, want the settled state first", out)
	}
}

func TestWait_CompileIsProvenByTheCompileClock(t *testing.T) {
	f := &fakeWait{t: t, prov: prov(provLine, 0), states: []client.Instance{
		{State: "ready", CompileFinishedAt: 1000},
		{State: "compiling", CompileFinishedAt: 1000},
		{State: "ready", CompileFinishedAt: 2000},
	}}
	code, out := f.run("--compile")
	if code != waitIdle {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if strings.Contains(out, "stale") {
		t.Errorf("output = %q, want the compile clock to end the wait without a staleness note", out)
	}
}

func TestWait_AnEditorThatRestartedDoesNotCountAsItsOwnEvidence(t *testing.T) {
	f := &fakeWait{t: t, prov: prov(provLine, 0), states: []client.Instance{
		{State: "compiling", CompileFinishedAt: 1000, PID: 111},
		{State: "ready", CompileFinishedAt: 2000, PID: 222},
	}}
	code, out := f.run("--compile")
	if code != waitIdle {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if !strings.Contains(out, "restarted while waiting") {
		t.Errorf("output = %q, want the note that the clock moved because the editor came back, not because it compiled", out)
	}
}

func TestWait_CompileAcceptsAnUnchangedSourceTreeAsProof(t *testing.T) {
	f := &fakeWait{t: t, prov: prov(provLine, 0), states: []client.Instance{{State: "ready", CompileFinishedAt: 1000}}}
	code, out := f.run("--compile")
	if code != waitIdle {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if !strings.Contains(out, "no compilation ran while waiting and no source file is newer") {
		t.Errorf("output = %q, want the note that says which proof ended the wait", out)
	}
}

func TestWait_CompileGivesUpWhenAStaleEditorNeverCompiles(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{{State: "ready", CompileFinishedAt: 1000}},
		prov: prov(provLine, 1, "Assets/Foo.cs")}
	code, out := f.run("--compile", "--timeout", "5")
	if code != waitTimedOut {
		t.Fatalf("exit = %d, want 1; output:\n%s", code, out)
	}
	for _, want := range []string{"no compilation started within", "Unity only auto-refreshes while it has focus",
		"stale: Assets/Foo.cs modified after this compile"} {
		if !strings.Contains(out, want) {
			t.Errorf("output = %q, want it to contain %q", out, want)
		}
	}
	if f.provCalls < 2 {
		t.Errorf("compile_status called %d times; the provenance printed at the end must be re-asked, "+
			"or its \"(3 s ago)\" is stale", f.provCalls)
	}
}

func TestWait_CompileOnAConnectorWithoutACompileClockSaysItCannotProveIt(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{{State: "ready"}}, prov: prov(provLine, 0)}
	code, out := f.run("--compile")
	if code != waitIdle {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if !strings.Contains(out, "reports no compile clock") {
		t.Errorf("output = %q, want the unproven warning", out)
	}
}

func TestWait_CompileOnAConnectorWithoutCompileStatusSaysSo(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{{State: "ready", CompileFinishedAt: 1000}}}
	code, out := f.run("--compile")
	if code != waitIdle {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if !strings.Contains(out, "compile_status did not answer") {
		t.Errorf("output = %q, want the no-provenance note", out)
	}
}

func TestWait_ModalDialogExitsTwoAndNamesIt(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{{State: "modal", CompileFinishedAt: 1000, Modal: &client.ModalDialog{
		ID: 7, Kind: "native", Title: "Save changes?", Texts: []string{"Save before closing?"},
		Buttons: []client.ModalButton{{Index: 0, Title: "Save"}},
	}}}}
	code, out := f.run()
	if code != waitNoReply {
		t.Fatalf("exit = %d, want 2; output:\n%s", code, out)
	}
	for _, want := range []string{"Save changes?", "unity-cli modal answer"} {
		if !strings.Contains(out, want) {
			t.Errorf("output = %q, want it to contain %q", out, want)
		}
	}
}

func TestWait_SilentHeartbeatExitsTwo(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{{
		State: "ready", ProjectPath: "/nonexistent/unity-cli-test-project", Timestamp: time.Now().Add(-time.Minute).UnixMilli(),
	}}}
	code, out := f.run()
	if code != waitNoReply {
		t.Fatalf("exit = %d, want 2; output:\n%s", code, out)
	}
	if !strings.Contains(out, "not responding") {
		t.Errorf("output = %q, want the unresponsive report", out)
	}
}

// A heartbeat that goes quiet mid-wait means the editor wedged or restarted; sitting out the whole
// --timeout would look like a busy editor instead.
func TestWait_HeartbeatGoesQuietMidWaitExitsTwo(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{
		{State: "ready", CompileFinishedAt: 1000},
		{State: "reloading", CompileFinishedAt: 1000},
		{State: "reloading", CompileFinishedAt: 1000, ProjectPath: "/nonexistent/unity-cli-test-project",
			Timestamp: time.Now().Add(-time.Minute).UnixMilli()},
	}}
	code, out := f.run("--compile", "--timeout", "30")
	if code != waitNoReply || !strings.Contains(out, "not responding") {
		t.Fatalf("exit = %d, output = %q; want 2 with the unresponsive report", code, out)
	}
}

func TestWait_LosingTheEditorExitsTwo(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{{State: "ready"}},
		resolveErr: errors.New("no instance on port 8090")}
	code, out := f.run()
	if code != waitNoReply || !strings.Contains(out, "no instance") {
		t.Fatalf("exit = %d, output = %q; want 2 naming the lost instance", code, out)
	}
}

func TestWait_RunningOutOfTimeExitsOne(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{{State: "compiling", CompileFinishedAt: 1000}}, prov: prov(provLine, 0)}
	code, out := f.run("--timeout", "1")
	if code != waitTimedOut {
		t.Fatalf("exit = %d, want 1; output:\n%s", code, out)
	}
	if !strings.Contains(out, "gave up after") || !strings.Contains(out, "compiling") {
		t.Errorf("output = %q, want the state it gave up on", out)
	}
}

func TestWait_BadTimeoutIsRejected(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{{State: "ready"}}}
	code, out := f.run("--timeout", "soon")
	if code != waitTimedOut || !strings.Contains(out, "whole number of seconds") {
		t.Errorf("exit = %d, output = %q; want a usage error", code, out)
	}
}

func TestWait_JSONReportsTheProvenanceObject(t *testing.T) {
	f := &fakeWait{t: t, states: []client.Instance{{State: "ready", CompileFinishedAt: 1000}}, prov: prov(provLine, 0)}
	code, out := f.run("--compile", "--json")
	if code != waitIdle {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	var r waitResult
	if err := json.Unmarshal([]byte(out), &r); err != nil {
		t.Fatalf("output is not the JSON object: %v (%s)", err, out)
	}
	if !r.Idle || !r.CompileWanted {
		t.Errorf("result = %+v, want idle with --compile recorded", r)
	}
	if len(r.Provenance) == 0 || !strings.Contains(string(r.Provenance), provLine) {
		t.Errorf("provenance = %s, want the connector's object", r.Provenance)
	}
}

func TestIsIdleState(t *testing.T) {
	tests := map[string]bool{
		"ready":             true,
		"playing":           true,
		"paused":            true,
		"compiling":         false,
		"refreshing":        false,
		"reloading":         false,
		"building":          false,
		"modal":             false,
		"entering_playmode": false,
		"stopped":           false,
	}
	for state, want := range tests {
		if got := isIdleState(state); got != want {
			t.Errorf("isIdleState(%q) = %v, want %v", state, got, want)
		}
	}
}
