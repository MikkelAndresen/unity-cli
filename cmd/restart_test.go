package cmd

import (
	"encoding/json"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

const (
	testProject = "/work/Game"
	testEditor  = "/Applications/Unity/Hub/Editor/6000.4.4f1/Unity.app"
	oldPID      = 100
	newPID      = 200
)

// fakeEditor simulates one Unity process per PID plus the instance files they write.
// Time only moves when the code under test sleeps.
type fakeEditor struct {
	t         *testing.T
	clock     time.Time
	alive     map[int]bool
	instances map[int]client.Instance

	quitResp    *client.CommandResponse
	quitErr     error
	exitOnQuit  bool
	stopOnQuit  bool // runs quit handlers (state "stopped") but the process lingers
	sentParams  map[string]interface{}
	killed      []int
	removed     []string
	launchedExe string
	launchArgs  []string

	// onLaunch configures the instance the relaunched editor reports; nil means it never comes up.
	onLaunch func(f *fakeEditor)
}

func newFakeEditor(t *testing.T) *fakeEditor {
	f := &fakeEditor{
		t:          t,
		clock:      time.UnixMilli(1_000_000),
		alive:      map[int]bool{oldPID: true},
		instances:  map[int]client.Instance{},
		quitResp:   &client.CommandResponse{Success: true, Message: "Quitting Unity."},
		exitOnQuit: true,
	}
	f.instances[oldPID] = f.instance(oldPID, "ready")
	f.onLaunch = func(f *fakeEditor) { f.instances[newPID] = f.instance(newPID, "ready") }
	return f
}

func (f *fakeEditor) instance(pid int, state string) client.Instance {
	return client.Instance{
		State:       state,
		ProjectPath: testProject,
		Port:        8090,
		PID:         pid,
		EditorPath:  testEditor,
		Timestamp:   f.clock.UnixMilli(),
	}
}

func (f *fakeEditor) deps() restartDeps {
	return restartDeps{
		scan: func() ([]client.Instance, error) {
			var out []client.Instance
			for pid, inst := range f.instances {
				if f.alive[pid] {
					inst.Timestamp = f.clock.UnixMilli()
				}
				out = append(out, inst)
			}
			return out, nil
		},
		send: func(_ *client.Instance, command string, params interface{}, _ int) (*client.CommandResponse, error) {
			if command != "manage_editor" {
				f.t.Fatalf("unexpected command %q", command)
			}
			f.sentParams = params.(map[string]interface{})
			if f.quitErr == nil && f.quitResp.Success && f.exitOnQuit {
				f.alive[oldPID] = false
				delete(f.instances, oldPID)
			}
			if f.quitErr == nil && f.quitResp.Success && f.stopOnQuit {
				f.instances[oldPID] = f.instance(oldPID, "stopped")
			}
			return f.quitResp, f.quitErr
		},
		processDead: func(pid int) bool { return !f.alive[pid] },
		kill: func(pid int) error {
			f.killed = append(f.killed, pid)
			f.alive[pid] = false
			delete(f.instances, pid)
			return nil
		},
		removeAll: func(path string) error {
			f.removed = append(f.removed, path)
			return nil
		},
		isDir: func(path string) bool {
			return path == filepath.Join(testProject, "Assets") || path == filepath.Join(testProject, "ProjectSettings")
		},
		launch: func(exe string, args []string) (int, error) {
			f.launchedExe, f.launchArgs = exe, args
			f.alive[newPID] = true
			if f.onLaunch != nil {
				f.onLaunch(f)
			}
			return newPID, nil
		},
		now:   func() time.Time { return f.clock },
		sleep: func(d time.Duration) { f.clock = f.clock.Add(d) },
	}
}

func (f *fakeEditor) target() *client.Instance {
	inst := f.instances[oldPID]
	return &inst
}

func defaultOpts() restartOptions {
	return restartOptions{waitTimeout: time.Minute}
}

func TestRestart_GracefulCleanRelaunch(t *testing.T) {
	f := newFakeEditor(t)
	opts := defaultOpts()
	opts.clean = true
	opts.autoSave = true

	if err := restartEditor(f.target(), opts, f.deps()); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if f.sentParams["action"] != "quit" || f.sentParams["auto_save_scenes"] != true || f.sentParams["allow_dirty_scenes"] != false {
		t.Errorf("unexpected quit params: %v", f.sentParams)
	}
	if len(f.killed) != 0 {
		t.Errorf("graceful quit must not kill, killed %v", f.killed)
	}
	wantRemoved := []string{filepath.Join(testProject, "Library"), filepath.Join(testProject, "Temp")}
	if strings.Join(f.removed, ",") != strings.Join(wantRemoved, ",") {
		t.Errorf("removed %v, want %v", f.removed, wantRemoved)
	}
	if want := filepath.Join(testEditor, "Contents", "MacOS", "Unity"); f.launchedExe != want {
		t.Errorf("launched %q, want %q", f.launchedExe, want)
	}
	if strings.Join(f.launchArgs, " ") != "-projectPath "+testProject {
		t.Errorf("unexpected launch args %v", f.launchArgs)
	}
}

func TestRestart_WithoutCleanKeepsLibrary(t *testing.T) {
	f := newFakeEditor(t)
	if err := restartEditor(f.target(), defaultOpts(), f.deps()); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(f.removed) != 0 {
		t.Errorf("expected nothing deleted, got %v", f.removed)
	}
}

func TestRestart_DirtyScenesRefusalIsNeverForced(t *testing.T) {
	f := newFakeEditor(t)
	data, _ := json.Marshal(map[string]interface{}{"blocked": "dirty-scenes", "scenes": []string{"Assets/Main.unity"}})
	f.quitResp = &client.CommandResponse{Success: false, Message: "Open scenes have unsaved changes.", Data: data}
	opts := defaultOpts()
	opts.force = true
	opts.clean = true

	err := restartEditor(f.target(), opts, f.deps())
	if err == nil || !strings.Contains(err.Error(), "Assets/Main.unity") || !strings.Contains(err.Error(), "--auto-save-scenes") {
		t.Fatalf("expected dirty-scene refusal naming the scene, got %v", err)
	}
	if len(f.killed) != 0 || len(f.removed) != 0 || f.launchedExe != "" {
		t.Errorf("refusal must stop the restart: killed=%v removed=%v launched=%q", f.killed, f.removed, f.launchedExe)
	}
}

func TestRestart_StaleHeartbeatRequiresForce(t *testing.T) {
	f := newFakeEditor(t)
	inst := f.target()
	f.clock = f.clock.Add(time.Minute)
	stale := *inst
	d := f.deps()
	d.scan = func() ([]client.Instance, error) { return []client.Instance{stale}, nil }

	err := restartEditor(inst, defaultOpts(), d)
	if err == nil || !strings.Contains(err.Error(), "not responding") || !strings.Contains(err.Error(), "--force") {
		t.Fatalf("expected not-responding error suggesting --force, got %v", err)
	}
	if f.sentParams != nil || len(f.killed) != 0 || f.launchedExe != "" {
		t.Errorf("stale editor must not be contacted, killed or relaunched without --force")
	}
}

func TestRestart_StaleHeartbeatForceKills(t *testing.T) {
	f := newFakeEditor(t)
	inst := f.target()
	f.clock = f.clock.Add(time.Minute)
	stale := *inst
	d := f.deps()
	scan := d.scan
	d.scan = func() ([]client.Instance, error) {
		if f.alive[oldPID] {
			return []client.Instance{stale}, nil
		}
		return scan()
	}
	opts := defaultOpts()
	opts.force = true

	if err := restartEditor(inst, opts, d); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(f.killed) != 1 || f.killed[0] != oldPID {
		t.Errorf("expected pid %d killed, got %v", oldPID, f.killed)
	}
	if f.launchedExe == "" {
		t.Error("expected relaunch after kill")
	}
}

func TestRestart_ModalBeforeQuitRequiresForce(t *testing.T) {
	f := newFakeEditor(t)
	modal := f.instance(oldPID, modalState)
	modal.Modal = &client.ModalDialog{ID: 7, Kind: "alert", Title: "Save?", Buttons: []client.ModalButton{{Index: 0, Title: "Save", Enabled: true}}}
	f.instances[oldPID] = modal

	err := restartEditor(f.target(), defaultOpts(), f.deps())
	if err == nil || !strings.Contains(err.Error(), "modal dialog") || !strings.Contains(err.Error(), `"Save"`) {
		t.Fatalf("expected modal description, got %v", err)
	}
	if f.sentParams != nil || len(f.killed) != 0 {
		t.Error("modal-blocked editor must not be sent quit or killed without --force")
	}
}

func TestRestart_IgnoredQuitIsKilledWithForce(t *testing.T) {
	f := newFakeEditor(t)
	f.exitOnQuit = false
	opts := defaultOpts()
	opts.force = true

	if err := restartEditor(f.target(), opts, f.deps()); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(f.killed) != 1 {
		t.Errorf("expected kill after ignored quit, got %v", f.killed)
	}
}

func TestRestart_StartupModalIsReported(t *testing.T) {
	f := newFakeEditor(t)
	f.onLaunch = func(f *fakeEditor) {
		inst := f.instance(newPID, modalState)
		inst.Modal = &client.ModalDialog{ID: 9, Kind: "alert", Texts: []string{"Enter Safe Mode?"}}
		f.instances[newPID] = inst
	}

	err := restartEditor(f.target(), defaultOpts(), f.deps())
	if err == nil || !strings.Contains(err.Error(), "Enter Safe Mode?") || !strings.Contains(err.Error(), "modal answer") {
		t.Fatalf("expected startup modal error, got %v", err)
	}
}

func TestRestart_WaitsThroughNonReadyStates(t *testing.T) {
	f := newFakeEditor(t)
	polls := 0
	f.onLaunch = func(f *fakeEditor) { f.instances[newPID] = f.instance(newPID, "compiling") }
	d := f.deps()
	scan := d.scan
	d.scan = func() ([]client.Instance, error) {
		polls++
		if polls == 3 {
			f.instances[newPID] = f.instance(newPID, "ready")
		}
		return scan()
	}

	if err := restartEditor(f.target(), defaultOpts(), d); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if polls < 3 {
		t.Errorf("returned before the editor was ready (polls=%d)", polls)
	}
}

func TestRestart_CompileErrorsAfterRestartFail(t *testing.T) {
	f := newFakeEditor(t)
	f.onLaunch = func(f *fakeEditor) {
		inst := f.instance(newPID, "ready")
		inst.CompileErrors = true
		f.instances[newPID] = inst
	}
	err := restartEditor(f.target(), defaultOpts(), f.deps())
	if err == nil || !strings.Contains(err.Error(), "compile errors") {
		t.Fatalf("expected compile error failure, got %v", err)
	}
}

func TestRestart_LaunchedEditorDies(t *testing.T) {
	f := newFakeEditor(t)
	f.onLaunch = func(f *fakeEditor) { f.alive[newPID] = false }
	err := restartEditor(f.target(), defaultOpts(), f.deps())
	if err == nil || !strings.Contains(err.Error(), "exited before the connector started") {
		t.Fatalf("expected early-exit error, got %v", err)
	}
}

func TestRestart_TimesOut(t *testing.T) {
	f := newFakeEditor(t)
	f.onLaunch = nil
	err := restartEditor(f.target(), defaultOpts(), f.deps())
	if err == nil || !strings.Contains(err.Error(), "timed out") {
		t.Fatalf("expected timeout, got %v", err)
	}
}

func TestRestart_NoWaitReturnsAfterLaunch(t *testing.T) {
	f := newFakeEditor(t)
	f.onLaunch = nil
	opts := defaultOpts()
	opts.noWait = true
	if err := restartEditor(f.target(), opts, f.deps()); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if f.launchedExe == "" {
		t.Error("expected launch")
	}
}

func TestRestart_CleanRefusesNonProjectBeforeQuitting(t *testing.T) {
	f := newFakeEditor(t)
	d := f.deps()
	d.isDir = func(string) bool { return false }
	opts := defaultOpts()
	opts.clean = true

	err := restartEditor(f.target(), opts, d)
	if err == nil || !strings.Contains(err.Error(), "refusing to clean") {
		t.Fatalf("expected refusal, got %v", err)
	}
	if f.sentParams != nil || len(f.removed) != 0 {
		t.Error("must not quit or delete anything for a non-project path")
	}
}

func TestRestart_MissingEditorPath(t *testing.T) {
	f := newFakeEditor(t)
	inst := f.target()
	inst.EditorPath = ""
	err := restartEditor(inst, defaultOpts(), f.deps())
	if err == nil || !strings.Contains(err.Error(), "--editor") {
		t.Fatalf("expected --editor hint, got %v", err)
	}
}

func TestRestart_ColdStartWithEditorOverride(t *testing.T) {
	f := newFakeEditor(t)
	f.alive[oldPID] = false
	delete(f.instances, oldPID)
	opts := defaultOpts()
	opts.editorPath = "/opt/unity/Editor/Unity"

	if err := restartEditor(&client.Instance{ProjectPath: testProject}, opts, f.deps()); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if f.sentParams != nil {
		t.Error("cold start must not send quit")
	}
	if f.launchedExe != "/opt/unity/Editor/Unity" {
		t.Errorf("launched %q", f.launchedExe)
	}
}

func TestParseRestartOptions(t *testing.T) {
	opts, err := parseRestartOptions([]string{"--clean", "--force", "--auto-save-scenes", "--wait-timeout", "5000", "--editor", "/x/Unity.app"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !opts.clean || !opts.force || !opts.autoSave || opts.allowDirty || opts.noWait {
		t.Errorf("unexpected flags: %+v", opts)
	}
	if opts.waitTimeout != 5*time.Second || opts.editorPath != "/x/Unity.app" {
		t.Errorf("unexpected values: %+v", opts)
	}

	defaults, err := parseRestartOptions(nil)
	if err != nil || defaults.waitTimeout != defaultRestartWaitDur {
		t.Errorf("unexpected defaults: %+v, %v", defaults, err)
	}

	for _, bad := range [][]string{{"--wait-timeout", "abc"}, {"--wait-timeout", "0"}, {"--editor"}} {
		if _, err := parseRestartOptions(bad); err == nil {
			t.Errorf("expected error for %v", bad)
		}
	}
}

func TestQuitRefusedError_GenericMessage(t *testing.T) {
	err := quitRefusedError(&client.CommandResponse{Success: false, Message: "Unknown action: 'quit'."})
	if err == nil || !strings.Contains(err.Error(), "Unknown action") || strings.Contains(err.Error(), "--auto-save-scenes") {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestRestart_LingeringProcessAfterQuitIsKilledWithoutForce(t *testing.T) {
	f := newFakeEditor(t)
	f.exitOnQuit = false
	f.stopOnQuit = true
	start := f.clock

	if err := restartEditor(f.target(), defaultOpts(), f.deps()); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(f.killed) != 1 || f.killed[0] != oldPID {
		t.Errorf("expected lingering pid %d killed, got %v", oldPID, f.killed)
	}
	if waited := f.clock.Sub(start); waited >= restartQuitTimeout {
		t.Errorf("linger kill should not wait the full quit timeout, waited %s", waited)
	}
	if f.launchedExe == "" {
		t.Error("expected relaunch")
	}
}

func TestParseEditorVersion(t *testing.T) {
	in := "m_EditorVersion: 6000.4.4f1\nm_EditorVersionWithRevision: 6000.4.4f1 (abc123)\n"
	if got := parseEditorVersion(in); got != "6000.4.4f1" {
		t.Errorf("got %q", got)
	}
	if got := parseEditorVersion("garbage"); got != "" {
		t.Errorf("expected empty, got %q", got)
	}
}
