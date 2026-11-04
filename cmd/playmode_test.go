package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestAnswerLostToReload(t *testing.T) {
	reloadAnswer := &client.CommandResponse{
		Success: false,
		Message: "exec had already started when Unity stopped answering",
		Data:    json.RawMessage(`{"code":"reload_during_command","started":true}`),
	}
	neverRanAnswer := &client.CommandResponse{
		Success: false,
		Message: "exec never ran: Unity stopped answering before the main thread picked it up",
		Data:    json.RawMessage(`{"code":"domain_reload","started":false}`),
	}

	tests := []struct {
		name string
		resp *client.CommandResponse
		err  error
		want bool
	}{
		{"answered normally", &client.CommandResponse{Success: true, Data: json.RawMessage(`1`)}, nil, false},
		{"interrupted command", reloadAnswer, nil, true},
		{"never ran", neverRanAnswer, nil, false},
		{"ordinary failure", &client.CommandResponse{Success: false, Message: "bad params"}, nil, false},
		{"dropped by a reload", nil, &client.ConnectionFailure{Class: client.FailureReload, Transport: errors.New("EOF")}, true},
		{"crashed editor", nil, &client.ConnectionFailure{Class: client.FailureCrash, Transport: errors.New("EOF")}, false},
		{"transport error alone", nil, errors.New("cannot connect to Unity at port 8090: EOF"), false},
		{"nothing at all", nil, nil, false},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := answerLostToReload(tc.resp, tc.err); got != tc.want {
				t.Errorf("answerLostToReload = %v, want %v", got, tc.want)
			}
		})
	}
}

func TestEditorStateLabel(t *testing.T) {
	tests := []struct{ state, want string }{
		{"ready", "Edit mode"},
		{"playing", "Play mode"},
		{"paused", "Play mode (paused)"},
		{"reloading", "a domain reload"},
		{"entering_playmode", "entering Play mode"},
		{"modal", "a modal dialog"},
		{"something-new", "something-new"},
	}
	for _, tc := range tests {
		if got := editorStateLabel(tc.state); got != tc.want {
			t.Errorf("editorStateLabel(%q) = %q, want %q", tc.state, got, tc.want)
		}
	}
}

func TestWaitForEditorState_StopsAtTheFirstWantedState(t *testing.T) {
	shrinkPlayModeWait(t)
	reads := 0
	resolve := func() (*client.Instance, error) {
		reads++
		state := "reloading"
		if reads >= 3 {
			state = "ready"
		}
		return &client.Instance{State: state, Port: 8090}, nil
	}

	inst, reached, err := waitForEditorState(context.Background(), resolve, []string{"ready"}, 5*time.Second, "editor stop")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !reached || inst.State != "ready" {
		t.Errorf("expected to reach ready, got %+v reached=%v", inst, reached)
	}
	if reads < 3 {
		t.Errorf("expected to keep polling until the state changed, got %d reads", reads)
	}
}

func TestWaitForEditorState_ReportsTheStateItGotStuckOn(t *testing.T) {
	shrinkPlayModeWait(t)
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "compiling", Port: 8090}, nil
	}

	inst, reached, err := waitForEditorState(context.Background(), resolve, []string{"ready"}, 30*time.Millisecond, "editor stop")
	if err != nil {
		t.Fatalf("a state that never arrives is not an error: %v", err)
	}
	if reached {
		t.Error("expected the wait to report it never reached ready")
	}
	if inst == nil || inst.State != "compiling" {
		t.Errorf("expected the last heartbeat back so the caller can name the state, got %+v", inst)
	}
}

func TestWaitForEditorState_LeavesAModalDialogEarly(t *testing.T) {
	shrinkPlayModeWait(t)
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "modal", Port: 8090, Modal: &client.ModalDialog{Title: "Save scene?"}}, nil
	}

	_, _, err := waitForEditorState(context.Background(), resolve, []string{"ready"}, 5*time.Second, "editor stop")
	if err == nil {
		t.Fatal("a modal dialog is not a state worth polling out")
	}
	if !strings.Contains(err.Error(), "Save scene?") {
		t.Errorf("expected the dialog to be named, got %v", err)
	}
}

func TestStateAfterReloadNote_NamesTheStateTheEditorCameBackIn(t *testing.T) {
	shrinkPlayModeWait(t)
	old := reloadStateWait
	reloadStateWait = 5 * time.Second
	t.Cleanup(func() { reloadStateWait = old })

	line := stateAfterReloadNote(context.Background(), func() (*client.Instance, error) {
		return &client.Instance{State: "ready", Port: 8091}, nil
	})
	if !strings.Contains(line, "Unity is back: Edit mode") || !strings.Contains(line, "8091") {
		t.Errorf("expected the state and port, got %q", line)
	}
}

func TestStateAfterReloadNote_SaysWhenTheEditorNeverCameBack(t *testing.T) {
	shrinkPlayModeWait(t)
	old := reloadStateWait
	reloadStateWait = 30 * time.Millisecond
	t.Cleanup(func() { reloadStateWait = old })

	line := stateAfterReloadNote(context.Background(), func() (*client.Instance, error) {
		return &client.Instance{State: "reloading", Port: 8090}, nil
	})
	if !strings.Contains(line, "still in a domain reload") || !strings.Contains(line, "unity-cli wait") {
		t.Errorf("expected the stuck state and the next step, got %q", line)
	}
}

func TestStateNoteAppliesSkipsCommandsThatWaitForThemselves(t *testing.T) {
	for _, category := range []string{"editor", "test", "verify", "build", "wait"} {
		if stateNoteApplies(category) {
			t.Errorf("%s reports its own reload wait; the note would print it twice", category)
		}
	}
	for _, category := range []string{"exec", "scene_get", "refresh_unity"} {
		if !stateNoteApplies(category) {
			t.Errorf("%s should get the state note after a reload took its answer", category)
		}
	}
}
