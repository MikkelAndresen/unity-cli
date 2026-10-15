package cmd

import (
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func fastPolling(t *testing.T) {
	t.Helper()
	orig := statusPollInterval
	statusPollInterval = 5 * time.Millisecond
	t.Cleanup(func() { statusPollInterval = orig })
}

func modalInstance() *client.Instance {
	return &client.Instance{
		State: "modal",
		Modal: &client.ModalDialog{
			ID:    42,
			Kind:  "alert",
			Texts: []string{"Scene(s) Have Been Modified", "Save changes?"},
			Buttons: []client.ModalButton{
				{Index: 0, Title: "Save", IsDefault: true, Enabled: true},
				{Index: 1, Title: "Don’t Save", Enabled: true},
				{Index: 2, Title: "Cancel", IsCancel: true, Enabled: true},
			},
		},
	}
}

func TestSendWatchingModal_PassesThroughResponse(t *testing.T) {
	fastPolling(t)
	send, _ := mockSend("exec", t)
	resolve := func() (*client.Instance, error) { return &client.Instance{State: "ready"}, nil }

	resp, err := sendWatchingModal(send, resolve)("exec", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !resp.Success {
		t.Errorf("expected success response, got %+v", resp)
	}
}

func TestSendWatchingModal_ModalWhileInFlightReturnsError(t *testing.T) {
	fastPolling(t)
	release := make(chan struct{})
	defer close(release)
	send := func(string, interface{}) (*client.CommandResponse, error) {
		<-release
		return &client.CommandResponse{Success: true}, nil
	}
	resolve := func() (*client.Instance, error) { return modalInstance(), nil }

	_, err := sendWatchingModal(send, resolve)("refresh_unity", nil)
	if err == nil {
		t.Fatal("expected modal error")
	}
	for _, want := range []string{"still pending", "Scene(s) Have Been Modified", `[1] "Don’t Save"`, "unity-cli modal answer"} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("error missing %q:\n%s", want, err)
		}
	}
}

func TestSendWatchingModal_ModalCommandIsNotWatched(t *testing.T) {
	fastPolling(t)
	send, _ := mockSend("modal", t)
	resolve := func() (*client.Instance, error) {
		t.Error("resolve should not be polled for the modal command")
		return modalInstance(), nil
	}

	if _, err := sendWatchingModal(send, resolve)("modal", nil); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestModalBlockedError_NotSent(t *testing.T) {
	err := modalBlockedError(modalInstance(), "exec", false)
	if !strings.Contains(err.Error(), `"exec" was not sent`) {
		t.Errorf("expected not-sent wording, got:\n%s", err)
	}
}

func TestDescribeModal_TagsButtons(t *testing.T) {
	got := describeModal(modalInstance().Modal)
	want := `Buttons: [0] "Save" (default), [1] "Don’t Save", [2] "Cancel" (cancel)`
	if !strings.Contains(got, want) {
		t.Errorf("describeModal missing %q:\n%s", want, got)
	}
}

func TestDescribeModal_NoButtons(t *testing.T) {
	got := describeModal(&client.ModalDialog{ID: 7, Kind: "window", Title: "Package Manager"})
	if !strings.Contains(got, "cannot be answered") {
		t.Errorf("expected no-buttons note, got:\n%s", got)
	}
}

func TestWaitForReady_ModalReturnsError(t *testing.T) {
	fastPolling(t)
	resolve := func() (*client.Instance, error) { return modalInstance(), nil }

	if _, err := waitForReady(resolve); err == nil {
		t.Fatal("expected modal error from waitForReady")
	}
}

func TestReadStatus_ParsesModal(t *testing.T) {
	inst := *modalInstance()
	inst.Port = 8090
	inst.Timestamp = 1
	writeInstanceFile(t, inst)

	got, err := readStatus(8090)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !isModal(got) || got.Modal == nil || len(got.Modal.Buttons) != 3 || got.Modal.Buttons[2].Title != "Cancel" {
		t.Errorf("modal not parsed: %+v", got.Modal)
	}
}
