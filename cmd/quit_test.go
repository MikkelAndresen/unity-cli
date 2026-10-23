package cmd

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"testing"

	"github.com/youngwoocho02/unity-cli/internal/client"
	"github.com/youngwoocho02/unity-cli/internal/editor"
)

func decodeQuitResult(t *testing.T, resp *client.CommandResponse) quitResult {
	t.Helper()
	if resp == nil || !resp.Success {
		t.Fatalf("expected successful response, got %+v", resp)
	}
	var result quitResult
	if err := json.Unmarshal(resp.Data, &result); err != nil {
		t.Fatalf("cannot decode quit result %s: %v", resp.Data, err)
	}
	return result
}

func quitTestDeps(t *testing.T, stderr *bytes.Buffer, inst *client.Instance) restartDeps {
	t.Helper()
	deps := restartTestDeps(t, stderr)
	deps.discover = func(string, int) (*client.Instance, error) {
		copied := *inst
		return &copied, nil
	}
	deps.terminate = func(int) error {
		t.Fatal("terminate must not be called")
		return nil
	}
	deps.launch = func(string, string) (int, error) {
		t.Fatal("quit must never launch Unity")
		return 0, nil
	}
	return deps
}

func TestQuitCmd_GracefulSendsSaveAndWaitsForExit(t *testing.T) {
	var stderr bytes.Buffer
	deps := quitTestDeps(t, &stderr, &client.Instance{State: "ready", ProjectPath: "/work/Proj", PID: 100})

	var sentParams map[string]interface{}
	deps.send = func(_ *client.Instance, command string, params interface{}, _ int) (*client.CommandResponse, error) {
		if command != "quit_editor" {
			t.Fatalf("expected quit_editor, got %q", command)
		}
		sentParams = params.(map[string]interface{})
		return &client.CommandResponse{Success: true}, nil
	}
	var waited editor.WaitForExitOptions
	deps.waitForExit = func(opts editor.WaitForExitOptions) error {
		waited = opts
		return nil
	}

	resp, err := quitCmd([]string{"--save"}, deps)
	if err != nil {
		t.Fatal(err)
	}
	result := decodeQuitResult(t, resp)
	if result.Method != stopMethodGraceful || result.PID != 100 || result.ProjectPath != "/work/Proj" {
		t.Fatalf("unexpected result %+v", result)
	}
	if sentParams["save"] != true {
		t.Fatalf("expected save=true, got %v", sentParams)
	}
	if waited.PID != 100 || waited.ProjectPath != "/work/Proj" || waited.IgnoreModal {
		t.Fatalf("unexpected wait options %+v", waited)
	}
}

func TestQuitCmd_NoEditorRunning(t *testing.T) {
	var stderr bytes.Buffer
	deps := restartTestDeps(t, &stderr)
	deps.discover = func(string, int) (*client.Instance, error) {
		return nil, errors.New("no Unity instances running")
	}

	_, err := quitCmd(nil, deps)
	if err == nil || !strings.Contains(err.Error(), "no running Unity editor to quit") {
		t.Fatalf("expected no-editor error, got %v", err)
	}
}

func TestQuitCmd_IgnoredQuitWithoutForceSuggestsForce(t *testing.T) {
	var stderr bytes.Buffer
	deps := quitTestDeps(t, &stderr, &client.Instance{State: "ready", ProjectPath: "/work/Proj", PID: 100})
	deps.send = func(*client.Instance, string, interface{}, int) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true}, nil
	}
	deps.waitForExit = func(editor.WaitForExitOptions) error {
		return errors.New("timed out after 2s waiting for Unity (pid 100) to exit")
	}

	_, err := quitCmd(nil, deps)
	if err == nil || !strings.Contains(err.Error(), "timed out") || !strings.Contains(err.Error(), "--force") {
		t.Fatalf("expected timeout error suggesting --force, got %v", err)
	}
}

func TestQuitCmd_IgnoredQuitWithForceTerminates(t *testing.T) {
	var stderr bytes.Buffer
	deps := quitTestDeps(t, &stderr, &client.Instance{State: "ready", ProjectPath: "/work/Proj", PID: 100})
	deps.send = func(*client.Instance, string, interface{}, int) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true}, nil
	}
	var waits []editor.WaitForExitOptions
	deps.waitForExit = func(opts editor.WaitForExitOptions) error {
		waits = append(waits, opts)
		if len(waits) == 1 {
			return errors.New("timed out")
		}
		return nil
	}
	terminated := 0
	deps.terminate = func(pid int) error {
		terminated = pid
		return nil
	}

	resp, err := quitCmd([]string{"--force"}, deps)
	if err != nil {
		t.Fatal(err)
	}
	if result := decodeQuitResult(t, resp); result.Method != stopMethodTerminate {
		t.Fatalf("expected terminate method, got %+v", result)
	}
	if terminated != 100 {
		t.Fatalf("expected pid 100 terminated, got %d", terminated)
	}
	if len(waits) != 2 || !waits[1].IgnoreModal {
		t.Fatalf("expected a second wait ignoring modal state, got %+v", waits)
	}
}

func TestQuitCmd_ModalWithoutForceDoesNotSend(t *testing.T) {
	var stderr bytes.Buffer
	deps := quitTestDeps(t, &stderr, &client.Instance{
		State:       "modal",
		ProjectPath: "/work/Proj",
		PID:         100,
		Modal:       &client.ModalDialog{ID: 3, Kind: "dialog", Texts: []string{"Save changes?"}},
	})
	deps.send = func(*client.Instance, string, interface{}, int) (*client.CommandResponse, error) {
		t.Fatal("quit must not be sent while a modal blocks the editor")
		return nil, nil
	}

	_, err := quitCmd(nil, deps)
	if err == nil || !strings.Contains(err.Error(), "Save changes?") || !strings.Contains(err.Error(), "editor quit") {
		t.Fatalf("expected modal error naming the dialog, got %v", err)
	}
}

func TestQuitCmd_ModalWithForceTerminatesWithoutSending(t *testing.T) {
	var stderr bytes.Buffer
	deps := quitTestDeps(t, &stderr, &client.Instance{State: "modal", ProjectPath: "/work/Proj", PID: 100})
	deps.send = func(*client.Instance, string, interface{}, int) (*client.CommandResponse, error) {
		t.Fatal("a parked main thread cannot run quit_editor; it must not be sent")
		return nil, nil
	}
	deps.waitForExit = func(opts editor.WaitForExitOptions) error {
		if !opts.IgnoreModal {
			t.Fatal("wait after termination must ignore the stale modal heartbeat")
		}
		return nil
	}
	terminated := false
	deps.terminate = func(int) error {
		terminated = true
		return nil
	}

	resp, err := quitCmd([]string{"--force"}, deps)
	if err != nil {
		t.Fatal(err)
	}
	if result := decodeQuitResult(t, resp); result.Method != stopMethodTerminate || !terminated {
		t.Fatalf("expected termination, got %+v", result)
	}
}

func TestQuitCmd_ModalDuringWaitWithForceTerminates(t *testing.T) {
	var stderr bytes.Buffer
	deps := quitTestDeps(t, &stderr, &client.Instance{State: "ready", ProjectPath: "/work/Proj", PID: 100})
	deps.send = func(*client.Instance, string, interface{}, int) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true}, nil
	}
	calls := 0
	deps.waitForExit = func(editor.WaitForExitOptions) error {
		calls++
		if calls == 1 {
			return fmt.Errorf("quit prompt: %w", editor.ErrEditorModal)
		}
		return nil
	}
	deps.terminate = func(int) error { return nil }

	resp, err := quitCmd([]string{"--force"}, deps)
	if err != nil {
		t.Fatal(err)
	}
	if result := decodeQuitResult(t, resp); result.Method != stopMethodTerminate {
		t.Fatalf("expected terminate method, got %+v", result)
	}
}

func TestQuitCmd_TerminateFailureIsReported(t *testing.T) {
	var stderr bytes.Buffer
	deps := quitTestDeps(t, &stderr, &client.Instance{State: "modal", ProjectPath: "/work/Proj", PID: 100})
	deps.terminate = func(int) error { return errors.New("pid 100 is not visible from this process") }
	deps.waitForExit = func(editor.WaitForExitOptions) error {
		t.Fatal("must not wait when termination failed")
		return nil
	}

	_, err := quitCmd([]string{"--force"}, deps)
	if err == nil || !strings.Contains(err.Error(), "cannot terminate Unity (pid 100)") {
		t.Fatalf("expected termination error, got %v", err)
	}
}

func TestQuitCmd_RefusedByUnity(t *testing.T) {
	var stderr bytes.Buffer
	deps := quitTestDeps(t, &stderr, &client.Instance{State: "ready", ProjectPath: "/work/Proj", PID: 100})
	deps.send = func(*client.Instance, string, interface{}, int) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: false, Message: "nope"}, nil
	}

	_, err := quitCmd([]string{"--force"}, deps)
	if err == nil || !strings.Contains(err.Error(), "unity refused to quit: nope") {
		t.Fatalf("expected refusal error, got %v", err)
	}
}
