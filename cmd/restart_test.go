package cmd

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
	"github.com/youngwoocho02/unity-cli/internal/editor"
)

type restartHeartbeatStep struct {
	inst *client.Instance
	err  error
}

type restartHeartbeatScript struct {
	steps []restartHeartbeatStep
	calls int
}

func (s *restartHeartbeatScript) read(string) (*client.Instance, error) {
	index := s.calls
	s.calls++
	if index >= len(s.steps) {
		index = len(s.steps) - 1
	}
	step := s.steps[index]
	if step.err != nil {
		return nil, step.err
	}
	copied := *step.inst
	return &copied, nil
}

// restartTestDeps starts from production defaults so every seam is non-nil,
// then makes polling fast and captures stderr. Tests override the seams they
// exercise.
func restartTestDeps(t *testing.T, stderr *bytes.Buffer) restartDeps {
	t.Helper()
	origInterval := restartPollInterval
	restartPollInterval = time.Millisecond
	t.Cleanup(func() { restartPollInterval = origInterval })

	deps := newRestartDeps(false)
	deps.project = ""
	deps.port = 0
	deps.timeoutMs = 2000
	deps.timeoutSet = true
	deps.stderr = stderr
	deps.getwd = func() (string, error) { return t.TempDir(), nil }
	return deps
}

func decodeRestartResult(t *testing.T, resp *client.CommandResponse) restartResult {
	t.Helper()
	if resp == nil || !resp.Success {
		t.Fatalf("expected successful response, got %+v", resp)
	}
	var result restartResult
	if err := json.Unmarshal(resp.Data, &result); err != nil {
		t.Fatalf("cannot decode restart result %s: %v", resp.Data, err)
	}
	return result
}

func TestRestartCmd_RunningQuitCleanLaunchReady(t *testing.T) {
	var stderr bytes.Buffer
	deps := restartTestDeps(t, &stderr)
	project := t.TempDir()
	live := &client.Instance{
		State:        "ready",
		ProjectPath:  project,
		PID:          100,
		UnityVersion: "6000.1.0f1",
	}
	deps.discover = func(projectArg string, port int) (*client.Instance, error) {
		if projectArg != "" || port != 0 {
			t.Errorf("discover called with project=%q port=%d, want defaults", projectArg, port)
		}
		return live, nil
	}

	var events []string
	var sentCommand string
	var sentParams map[string]interface{}
	deps.send = func(_ *client.Instance, command string, params interface{}, _ int) (*client.CommandResponse, error) {
		events = append(events, "quit")
		sentCommand = command
		sentParams, _ = params.(map[string]interface{})
		return &client.CommandResponse{Success: true}, nil
	}
	var waited editor.WaitForExitOptions
	deps.waitForExit = func(opts editor.WaitForExitOptions) error {
		events = append(events, "wait-exit")
		waited = opts
		return nil
	}
	var cleanedPath string
	deps.cleanProject = func(path string) ([]string, error) {
		events = append(events, "clean")
		cleanedPath = path
		return []string{path + "/Library", path + "/Temp"}, nil
	}
	deps.validateProject = func(string) error { return nil }
	// No ProjectVersion.txt in the fake project: the heartbeat version is the fallback.
	deps.readVersion = func(string) (string, error) { return "", errors.New("missing ProjectVersion.txt") }
	var gotVersion, gotOverride string
	deps.findExecutable = func(version, override string) (string, error) {
		gotVersion = version
		gotOverride = override
		return "/Unity/Editor/Unity", nil
	}
	var launchedExe, launchedProject string
	deps.launch = func(exe, projectPath string) (int, error) {
		events = append(events, "launch")
		launchedExe = exe
		launchedProject = projectPath
		return 200, nil
	}
	heartbeats := &restartHeartbeatScript{steps: []restartHeartbeatStep{
		{inst: &client.Instance{State: "stopped", PID: 100, ProjectPath: project}},
		{inst: &client.Instance{State: "refreshing", PID: 200, ProjectPath: project}},
		{inst: &client.Instance{State: "ready", PID: 200, ProjectPath: project}},
	}}
	deps.readHeartbeat = heartbeats.read

	resp, err := restartCmd([]string{"--clean", "--save", "--editor-path", "/custom/Unity"}, deps)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if sentCommand != "quit_editor" {
		t.Errorf("quit command = %q, want quit_editor", sentCommand)
	}
	if sentParams["save"] != true {
		t.Errorf("quit save = %v, want true", sentParams["save"])
	}
	if waited.PID != 100 || waited.ProjectPath != project {
		t.Errorf("wait options = %+v, want pid 100 project %q", waited, project)
	}
	if waited.Timeout != 2*time.Second {
		t.Errorf("wait timeout = %s, want --timeout override 2s", waited.Timeout)
	}
	if cleanedPath != project {
		t.Errorf("cleaned path = %q, want %q", cleanedPath, project)
	}
	if gotVersion != "6000.1.0f1" {
		t.Errorf("resolved version = %q, want heartbeat fallback", gotVersion)
	}
	if gotOverride != "/custom/Unity" {
		t.Errorf("editor override = %q, want /custom/Unity", gotOverride)
	}
	if launchedExe != "/Unity/Editor/Unity" || launchedProject != project {
		t.Errorf("launched %q for %q, want /Unity/Editor/Unity for %q", launchedExe, launchedProject, project)
	}
	if strings.Join(events, ",") != "quit,wait-exit,clean,launch" {
		t.Errorf("events = %v, want quit,wait-exit,clean,launch", events)
	}

	result := decodeRestartResult(t, resp)
	if result.ProjectPath != project || result.OldPID != 100 || result.NewPID != 200 {
		t.Errorf("result = %+v, want project %q old 100 new 200", result, project)
	}
	if len(result.Cleaned) != 2 {
		t.Errorf("cleaned = %v, want Library and Temp", result.Cleaned)
	}
	if result.Elapsed == "" {
		t.Error("expected elapsed to be reported")
	}
	if !strings.Contains(stderr.String(), "Removed "+project+"/Library") {
		t.Errorf("stderr should list removed paths, got:\n%s", stderr.String())
	}
	if !strings.Contains(stderr.String(), "state: refreshing") {
		t.Errorf("stderr should report state changes, got:\n%s", stderr.String())
	}
}

func TestRestartCmd_NoEditorRunningLaunchesFromProject(t *testing.T) {
	var stderr bytes.Buffer
	deps := restartTestDeps(t, &stderr)
	project := t.TempDir()
	deps.project = project
	deps.discover = func(string, int) (*client.Instance, error) {
		return nil, errors.New("no Unity instances running")
	}
	deps.validateProject = func(path string) error {
		if path != project {
			t.Errorf("validated %q, want %q", path, project)
		}
		return nil
	}
	heartbeats := &restartHeartbeatScript{steps: []restartHeartbeatStep{
		{err: errors.New("no heartbeat file")},
		{inst: &client.Instance{State: "ready", PID: 200, ProjectPath: project}},
	}}
	deps.readHeartbeat = heartbeats.read
	deps.send = func(*client.Instance, string, interface{}, int) (*client.CommandResponse, error) {
		t.Fatal("quit must not be sent when no editor is running")
		return nil, nil
	}
	deps.waitForExit = func(editor.WaitForExitOptions) error {
		t.Fatal("must not wait for an editor that was not running")
		return nil
	}
	deps.cleanProject = func(string) ([]string, error) {
		t.Fatal("clean must not run without --clean")
		return nil, nil
	}
	deps.readVersion = func(string) (string, error) { return "6000.1.0f1", nil }
	deps.findExecutable = func(version, override string) (string, error) {
		if version != "6000.1.0f1" || override != "" {
			t.Errorf("find executable args = %q, %q", version, override)
		}
		return "/Unity", nil
	}
	deps.launch = func(exe, projectPath string) (int, error) {
		if exe != "/Unity" || projectPath != project {
			t.Errorf("launch args = %q, %q", exe, projectPath)
		}
		return 200, nil
	}

	resp, err := restartCmd(nil, deps)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	result := decodeRestartResult(t, resp)
	if result.OldPID != 0 || result.NewPID != 200 {
		t.Errorf("result = %+v, want old 0 new 200", result)
	}
	if len(result.Cleaned) != 0 {
		t.Errorf("cleaned = %v, want none", result.Cleaned)
	}
}

func TestRestartCmd_ModalBlocksQuit(t *testing.T) {
	var stderr bytes.Buffer
	deps := restartTestDeps(t, &stderr)
	deps.discover = func(string, int) (*client.Instance, error) {
		return &client.Instance{
			State:       "modal",
			ProjectPath: "/work/Proj",
			PID:         100,
			Modal: &client.ModalDialog{
				ID:      7,
				Kind:    "dialog",
				Texts:   []string{"Unsaved changes will be lost"},
				Buttons: []client.ModalButton{{Index: 0, Title: "Save"}},
			},
		}, nil
	}
	deps.send = func(*client.Instance, string, interface{}, int) (*client.CommandResponse, error) {
		t.Fatal("quit must not be sent while a modal blocks the editor")
		return nil, nil
	}
	deps.waitForExit = func(editor.WaitForExitOptions) error {
		t.Fatal("must not wait for exit while a modal blocks the editor")
		return nil
	}

	_, err := restartCmd(nil, deps)
	if err == nil {
		t.Fatal("expected modal error")
	}
	if !strings.Contains(err.Error(), "modal dialog") || !strings.Contains(err.Error(), "Unsaved changes will be lost") {
		t.Fatalf("error should describe the blocking dialog, got: %v", err)
	}
}

func TestRestartCmd_ModalDuringQuitFails(t *testing.T) {
	var stderr bytes.Buffer
	deps := restartTestDeps(t, &stderr)
	project := "/work/Proj"
	deps.discover = func(string, int) (*client.Instance, error) {
		return &client.Instance{State: "ready", ProjectPath: project, PID: 100}, nil
	}
	deps.send = func(*client.Instance, string, interface{}, int) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true}, nil
	}
	deps.waitForExit = func(editor.WaitForExitOptions) error {
		return fmt.Errorf("still running: %w", editor.ErrEditorModal)
	}
	deps.readHeartbeat = func(string) (*client.Instance, error) {
		return &client.Instance{
			State:       "modal",
			ProjectPath: project,
			PID:         100,
			Modal:       &client.ModalDialog{ID: 9, Kind: "dialog", Texts: []string{"Restore backup?"}},
		}, nil
	}
	deps.cleanProject = func(string) ([]string, error) {
		t.Fatal("clean must not run when the editor did not exit")
		return nil, nil
	}
	deps.launch = func(string, string) (int, error) {
		t.Fatal("launch must not run when the editor did not exit")
		return 0, nil
	}

	_, err := restartCmd([]string{"--clean"}, deps)
	if err == nil {
		t.Fatal("expected modal error")
	}
	if !strings.Contains(err.Error(), "modal dialog blocks it") || !strings.Contains(err.Error(), "Restore backup?") {
		t.Fatalf("error should describe the blocking dialog, got: %v", err)
	}
}

func TestRestartCmd_WaitReadyTimeoutMentionsStartupModal(t *testing.T) {
	var stderr bytes.Buffer
	deps := restartTestDeps(t, &stderr)
	deps.timeoutMs = 30
	deps.discover = func(string, int) (*client.Instance, error) {
		return nil, errors.New("no Unity instances running")
	}
	deps.project = t.TempDir()
	deps.validateProject = func(string) error { return nil }
	deps.readHeartbeat = func(string) (*client.Instance, error) {
		return nil, errors.New("no heartbeat yet")
	}
	deps.readVersion = func(string) (string, error) { return "6000.1.0f1", nil }
	deps.findExecutable = func(string, string) (string, error) { return "/Unity", nil }
	deps.launch = func(string, string) (int, error) { return 200, nil }

	_, err := restartCmd(nil, deps)
	if err == nil {
		t.Fatal("expected timeout error")
	}
	for _, want := range []string{"timed out after", "startup dialog", "safe mode", "restore backup", "version upgrade"} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("timeout error missing %q:\n%v", want, err)
		}
	}
}

func TestRestartCmd_CleanRefusesNonProjectPath(t *testing.T) {
	var stderr bytes.Buffer
	deps := restartTestDeps(t, &stderr)
	deps.project = t.TempDir()
	deps.discover = func(string, int) (*client.Instance, error) {
		return nil, errors.New("no Unity instances running")
	}
	deps.validateProject = editor.ValidateProject
	deps.cleanProject = editor.CleanProject
	deps.launch = func(string, string) (int, error) {
		t.Fatal("launch must not run for a non-project path")
		return 0, nil
	}

	_, err := restartCmd([]string{"--clean"}, deps)
	if err == nil {
		t.Fatal("expected error for non-project path")
	}
	if !strings.Contains(err.Error(), "not a Unity project root") {
		t.Fatalf("error should explain the path is not a Unity project, got: %v", err)
	}
}
