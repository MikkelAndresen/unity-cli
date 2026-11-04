package cmd

import (
	"bytes"
	"encoding/json"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func jobData(fields map[string]interface{}) json.RawMessage {
	data, err := json.Marshal(fields)
	if err != nil {
		panic(err)
	}
	return data
}

func queuedJob(id string) json.RawMessage {
	return jobData(map[string]interface{}{
		"id":              id,
		"phase":           "queued",
		"profile":         "macOS_dev",
		"output":          "/proj/Temp/App.app",
		"elapsed_seconds": 0,
	})
}

func TestCheckBuildFlags(t *testing.T) {
	tests := []struct {
		name    string
		flags   map[string]string
		wantErr string
	}{
		{"profile and output", map[string]string{"profile": "macOS_dev", "output": "B/App.app"}, ""},
		{"target and output", map[string]string{"target": "StandaloneOSX", "output": "B/App"}, ""},
		{"output_path is accepted", map[string]string{"profile": "macOS", "output_path": "B/App.app"}, ""},
		{"neither source", map[string]string{"output": "B/App.app"}, "--profile"},
		{"both sources", map[string]string{"profile": "macOS", "target": "StandaloneOSX", "output": "B"}, "not both"},
		{"no output", map[string]string{"profile": "macOS"}, "--output"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := checkBuildFlags(tt.flags)
			switch {
			case tt.wantErr == "" && err != nil:
				t.Errorf("checkBuildFlags(%v) = %v, want nil", tt.flags, err)
			case tt.wantErr != "" && err == nil:
				t.Errorf("checkBuildFlags(%v) = nil, want an error mentioning %q", tt.flags, tt.wantErr)
			case tt.wantErr != "" && !strings.Contains(err.Error(), tt.wantErr):
				t.Errorf("checkBuildFlags(%v) = %v, want it to mention %q", tt.flags, err, tt.wantErr)
			}
		})
	}
}

func TestBuildCmd_StartsJobAndSendsParams(t *testing.T) {
	var gotCmd string
	var gotParams map[string]interface{}
	send := func(command string, params interface{}) (*client.CommandResponse, error) {
		gotCmd = command
		gotParams, _ = params.(map[string]interface{})
		return &client.CommandResponse{Success: true, Data: queuedJob("build-1")}, nil
	}
	poll := func(string, interface{}) (*client.CommandResponse, error) {
		t.Error("build_status polled despite --no-wait")
		return nil, nil
	}

	resp, err := buildCmd([]string{"--profile", "macOS_dev", "--output", "Temp/App.app",
		"--development", "--scenes", "Assets/A.unity,Assets/B.unity", "--no-wait"}, send, poll, readyResolve, &bytes.Buffer{})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !resp.Success {
		t.Fatalf("expected a successful start, got %+v", resp)
	}
	if gotCmd != "build" {
		t.Errorf("command = %q, want build", gotCmd)
	}
	if gotParams["profile"] != "macOS_dev" || gotParams["output"] != "Temp/App.app" {
		t.Errorf("params = %v, want profile and output passed through", gotParams)
	}
	if gotParams["development"] != true {
		t.Errorf("development = %v, want true", gotParams["development"])
	}
	if gotParams["scenes"] != "Assets/A.unity,Assets/B.unity" {
		t.Errorf("scenes = %v, want the comma list forwarded", gotParams["scenes"])
	}
	if _, sent := gotParams["no-wait"]; sent {
		t.Errorf("--no-wait must stay CLI-side, got params %v", gotParams)
	}
}

func TestBuildCmd_RejectsBadFlagsBeforeAskingTheEditor(t *testing.T) {
	send := func(string, interface{}) (*client.CommandResponse, error) {
		t.Error("send called despite a bad flag set")
		return nil, nil
	}
	if _, err := buildCmd([]string{"--profile", "macOS"}, send, failingPoll, readyResolve, &bytes.Buffer{}); err == nil {
		t.Fatal("expected an error for a missing --output")
	}
}

func failingPoll(string, interface{}) (*client.CommandResponse, error) {
	return nil, fmt.Errorf("cannot connect to Unity at port 8090")
}

func TestBuildCmd_WaitsAndPrintsProgress(t *testing.T) {
	restore := shrinkBuildPoll(t)
	defer restore()

	var calls int
	logw := &bytes.Buffer{}
	poll := func(command string, params interface{}) (*client.CommandResponse, error) {
		if command != "build_status" {
			t.Errorf("poll command = %q, want build_status", command)
		}
		calls++
		switch calls {
		case 1:
			return &client.CommandResponse{Success: true, Data: jobData(map[string]interface{}{
				"id": "build-1", "phase": "building", "elapsed_seconds": 12,
				"progress_description": "Compiling PlayerAssemblies", "progress_percent": 0.23,
			})}, nil
		case 2:
			return &client.CommandResponse{Success: true, Data: jobData(map[string]interface{}{
				"id": "build-1", "phase": "building", "elapsed_seconds": 205,
				"progress_description": "Compiling PlayerAssemblies", "progress_percent": 0.61,
			})}, nil
		default:
			return &client.CommandResponse{Success: true, Data: jobData(map[string]interface{}{
				"id": "build-1", "phase": "succeeded", "result": "Succeeded", "elapsed_seconds": 302,
				"total_bytes": 138_000_000, "error_count": 0, "warning_count": 4,
				"output": "/proj/Builds/Mac/App.app",
			})}, nil
		}
	}
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true, Data: queuedJob("build-1")}, nil
	}

	resp, err := buildCmd([]string{"--profile", "macOS_dev", "--output", "Builds/Mac/App.app"},
		send, poll, readyResolve, logw)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	out := logw.String()
	for _, want := range []string{
		"build job build-1 queued (macOS_dev -> /proj/Temp/App.app)",
		"building 12s  Compiling PlayerAssemblies 23%",
		"building 3m25s  Compiling PlayerAssemblies 61%",
		"build succeeded in 5m02s — 131.6 MB, 0 error(s), 4 warning(s) -> /proj/Builds/Mac/App.app",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("progress output is missing %q:\n%s", want, out)
		}
	}
	if !strings.Contains(string(resp.Data), `"phase":"succeeded"`) {
		t.Errorf("final payload should be the terminal job report, got %s", string(resp.Data))
	}
}

func TestBuildCmd_FailedBuildReturnsReportAndError(t *testing.T) {
	restore := shrinkBuildPoll(t)
	defer restore()

	logw := &bytes.Buffer{}
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true, Data: queuedJob("build-2")}, nil
	}
	poll := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true, Data: jobData(map[string]interface{}{
			"id": "build-2", "phase": "failed", "result": "Failed", "elapsed_seconds": 40,
			"error_count": 2, "warning_count": 1, "message": "BuildPipeline failed.",
			"errors": []string{"Compilation: boom", "Postprocess: bang"},
		})}, nil
	}

	resp, err := buildCmd([]string{"--target", "StandaloneOSX", "--output", "Builds/App"}, send, poll, readyResolve, logw)
	if err == nil {
		t.Fatal("a failed build must exit non-zero")
	}
	if resp == nil || !resp.Success {
		t.Fatalf("the report must still be printed, got %+v", resp)
	}
	if !strings.Contains(err.Error(), "build job build-2 failed: BuildPipeline failed.") {
		t.Errorf("error = %v", err)
	}
	out := logw.String()
	if !strings.Contains(out, "error: Compilation: boom") {
		t.Errorf("stderr should quote the report's first errors:\n%s", out)
	}
	if !strings.Contains(out, "build failed in 40s, 2 error(s), 1 warning(s)") {
		t.Errorf("summary line missing:\n%s", out)
	}
}

func TestBuildCmd_ConnectionDropDoesNotAbortTheWait(t *testing.T) {
	restore := shrinkBuildPoll(t)
	defer restore()

	var calls int
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true, Data: queuedJob("build-3")}, nil
	}
	poll := func(string, interface{}) (*client.CommandResponse, error) {
		calls++
		// A reload at the end of a build takes the HTTP listener with it; the wait must ride that
		// out instead of declaring the build lost.
		if calls <= 2 {
			return nil, fmt.Errorf("cannot connect to Unity at port 8090: connection refused")
		}
		return &client.CommandResponse{Success: true, Data: jobData(map[string]interface{}{
			"id": "build-3", "phase": "succeeded", "elapsed_seconds": 10, "total_bytes": 10,
		})}, nil
	}

	resp, err := buildCmd([]string{"--profile", "macOS", "--output", "Builds/App"}, send, poll, readyResolve, &bytes.Buffer{})
	if err != nil {
		t.Fatalf("connection drops must not abort the wait: %v", err)
	}
	if calls < 3 {
		t.Fatalf("expected the poll to retry past the drops, got %d calls", calls)
	}
	if !resp.Success {
		t.Errorf("expected success, got %+v", resp)
	}
}

func TestBuildCmd_GivesUpAfterLosingContact(t *testing.T) {
	restore := shrinkBuildPoll(t)
	defer restore()

	send := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true, Data: queuedJob("build-4")}, nil
	}

	resp, err := buildCmd([]string{"--profile", "macOS", "--output", "Builds/App"}, send, failingPoll, readyResolve, &bytes.Buffer{})
	if err == nil {
		t.Fatal("expected the wait to give up on an editor that never answers")
	}
	if !strings.Contains(err.Error(), "lost contact") || !strings.Contains(err.Error(), "has no report yet") {
		t.Errorf("error = %v, want it to say contact was lost and that no report came", err)
	}
	if resp != nil {
		t.Errorf("there is no report to return, got %s", string(resp.Data))
	}
}

func TestBuildCmd_ReportsTheLastKnownJobWhenContactIsLost(t *testing.T) {
	restore := shrinkBuildPoll(t)
	defer restore()

	var calls int
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true, Data: queuedJob("build-5")}, nil
	}
	poll := func(string, interface{}) (*client.CommandResponse, error) {
		calls++
		if calls > 1 {
			return nil, fmt.Errorf("cannot connect to Unity at port 8090: connection refused")
		}
		return &client.CommandResponse{Success: true, Data: jobData(map[string]interface{}{
			"id": "build-5", "phase": "building", "elapsed_seconds": 15,
		})}, nil
	}

	resp, err := buildCmd([]string{"--profile", "macOS", "--output", "Builds/App"}, send, poll, readyResolve, &bytes.Buffer{})
	if err == nil {
		t.Fatal("expected the wait to give up")
	}
	if !strings.Contains(err.Error(), "was last building") {
		t.Errorf("error = %v, want it to report the last known phase", err)
	}
	if resp == nil || !strings.Contains(string(resp.Data), `"phase":"building"`) {
		t.Fatalf("the last known report should still be printed, got %+v", resp)
	}
}

func TestBuildStatusCmd(t *testing.T) {
	tests := []struct {
		name     string
		args     []string
		data     json.RawMessage
		wantID   interface{}
		wantErr  string
		wantCall bool
	}{
		{
			name:   "in progress is not a failure",
			data:   jobData(map[string]interface{}{"id": "build-9", "phase": "building", "elapsed_seconds": 3}),
			wantID: nil,
		},
		{
			name:   "latest by default",
			args:   nil,
			data:   jobData(map[string]interface{}{"id": "build-9", "phase": "succeeded"}),
			wantID: nil,
		},
		{
			name:   "id forwarded",
			args:   []string{"build-7"},
			data:   jobData(map[string]interface{}{"id": "build-7", "phase": "building"}),
			wantID: "build-7",
		},
		{
			name:    "interrupted exits non-zero",
			args:    []string{"build-8"},
			data:    jobData(map[string]interface{}{"id": "build-8", "phase": "interrupted", "message": "domain reload"}),
			wantID:  "build-8",
			wantErr: "interrupted",
		},
		{
			name:    "cancelled exits non-zero",
			data:    jobData(map[string]interface{}{"id": "build-9", "phase": "cancelled"}),
			wantErr: "cancelled",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var gotParams map[string]interface{}
			poll := func(command string, params interface{}) (*client.CommandResponse, error) {
				if command != "build_status" {
					t.Errorf("command = %q, want build_status", command)
				}
				gotParams, _ = params.(map[string]interface{})
				return &client.CommandResponse{Success: true, Data: tt.data}, nil
			}

			resp, err := buildStatusCmd(tt.args, poll)
			if resp == nil {
				t.Fatal("expected the report to be returned so it gets printed")
			}
			if tt.wantErr == "" && err != nil {
				t.Errorf("unexpected error: %v", err)
			}
			if tt.wantErr != "" {
				if err == nil {
					t.Fatalf("expected an error mentioning %q", tt.wantErr)
				}
				if !strings.Contains(err.Error(), tt.wantErr) {
					t.Errorf("error = %v, want it to mention %q", err, tt.wantErr)
				}
			}
			if gotParams["id"] != tt.wantID {
				t.Errorf("id param = %v, want %v", gotParams["id"], tt.wantID)
			}
		})
	}
}

func TestBuildStatusCmd_ReportFailureIsNotASuccess(t *testing.T) {
	poll := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: false, Message: "No build jobs in this editor session."}, nil
	}
	resp, err := buildStatusCmd(nil, poll)
	if err != nil {
		t.Fatalf("the connector's own error is reported through the response: %v", err)
	}
	if resp == nil || resp.Success {
		t.Fatalf("expected the failed report back, got %+v", resp)
	}
}

func TestJobFromResponse_RejectsUnusableReports(t *testing.T) {
	tests := []struct {
		name string
		data json.RawMessage
		want string
	}{
		{"no data", nil, "without a build job report"},
		{"null data", json.RawMessage("null"), "without a build job report"},
		{"not json", json.RawMessage("oops"), "unreadable build report"},
		{"missing phase", json.RawMessage(`{"id":"build-1"}`), "missing its id or phase"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, err := jobFromResponse(&client.CommandResponse{Success: true, Data: tt.data})
			if err == nil {
				t.Fatalf("expected an error mentioning %q", tt.want)
			}
			if !strings.Contains(err.Error(), tt.want) {
				t.Errorf("error = %v, want it to mention %q", err, tt.want)
			}
		})
	}
}

func TestBuildPhasesAreClassified(t *testing.T) {
	terminal := []string{"succeeded", "failed", "cancelled", "interrupted"}
	for _, phase := range terminal {
		job := &buildJob{Phase: phase}
		if !job.terminal() {
			t.Errorf("%q must end the wait", phase)
		}
		if job.succeeded() != (phase == "succeeded") {
			t.Errorf("%q must only be succeeded when it is succeeded", phase)
		}
	}
	for _, phase := range []string{"queued", "building"} {
		if (&buildJob{Phase: phase}).terminal() {
			t.Errorf("%q must keep the wait going", phase)
		}
	}
}

func TestProgressText(t *testing.T) {
	percent := 0.615
	tests := []struct {
		name string
		text string
		want string
	}{
		{"no progress reported", progressText("building", "", "", 37, nil), "building 37s"},
		{"percent from Unity", progressText("building", "Compiling", "", 205, &percent),
			"building 3m25s  Compiling 61%"},
		{"indefinite step has no percent", progressText("building", "Linking", "", 5, ptr(-1)),
			"building 5s  Linking"},
		{"output appended", progressText("building", "", "/b/App.app", 3, nil),
			"building 3s -> /b/App.app"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if tt.text != tt.want {
				t.Errorf("progressText = %q, want %q", tt.text, tt.want)
			}
		})
	}
}

func TestDescribeBuildUsesTheHeartbeatSnapshot(t *testing.T) {
	percent := 0.5
	line := describeBuild(&client.BuildJob{
		ID:                  "build-1",
		Phase:               "building",
		ElapsedSeconds:      41,
		ProgressPercent:     &percent,
		ProgressDescription: "Compiling scripts",
		Output:              "/p/App.app",
	})
	want := "  Build:   build-1  building 41s  Compiling scripts 50% -> /p/App.app\n"
	if line != want {
		t.Errorf("describeBuild = %q, want %q", line, want)
	}
}

func TestFormatBytes(t *testing.T) {
	tests := []struct {
		in   int64
		want string
	}{
		{0, "0 B"},
		{999, "999 B"},
		{2048, "2.0 KB"},
		{138_000_000, "131.6 MB"},
		{5_000_000_000, "4.7 GB"},
	}
	for _, tt := range tests {
		if got := formatBytes(tt.in); got != tt.want {
			t.Errorf("formatBytes(%d) = %q, want %q", tt.in, got, tt.want)
		}
	}
}

func TestFormatElapsed(t *testing.T) {
	tests := []struct {
		in   float64
		want string
	}{
		{0, "0s"},
		{9.4, "9s"},
		{59.6, "1m00s"},
		{205.4, "3m25s"},
	}
	for _, tt := range tests {
		if got := formatElapsed(tt.in); got != tt.want {
			t.Errorf("formatElapsed(%v) = %q, want %q", tt.in, got, tt.want)
		}
	}
}

// shrinkBuildPoll keeps the wait tests fast: production polls every 2s and gives up after 3 minutes
// of no answer, which is what the connection-drop tests are actually about.
func shrinkBuildPoll(t *testing.T) func() {
	t.Helper()

	originalInterval, originalContact, originalEvery := buildPollInterval, buildContactTimeout, buildProgressEvery
	buildPollInterval = 5 * time.Millisecond
	buildContactTimeout = 60 * time.Millisecond
	buildProgressEvery = time.Millisecond

	return func() {
		buildPollInterval, buildContactTimeout, buildProgressEvery = originalInterval, originalContact, originalEvery
	}
}

func ptr(v float64) *float64 { return &v }

func readyResolve() (*client.Instance, error) {
	return &client.Instance{State: "ready", Port: 8090}, nil
}

func TestBuildCmd_RefusesASecondBuildWhileTheEditorIsBuilding(t *testing.T) {
	send := func(string, interface{}) (*client.CommandResponse, error) {
		t.Error("the connector was asked to start a build although the heartbeat says one is running")
		return &client.CommandResponse{Success: true}, nil
	}
	buildingResolve := func() (*client.Instance, error) {
		return &client.Instance{
			State: "building",
			Port:  8090,
			Build: &client.BuildJob{ID: "build-1", Phase: "building", Profile: "macOS_dev", ElapsedSeconds: 120},
		}, nil
	}

	_, err := buildCmd([]string{"--profile", "macOS", "--output", "Builds/App"}, send, failingPoll,
		buildingResolve, &bytes.Buffer{})
	if err == nil {
		t.Fatal("a second build during a running one must fail fast, not queue behind it")
	}
	out := err.Error()
	for _, want := range []string{"already building build-1 (macOS_dev)", "2m00s in", "unity-cli build status build-1"} {
		if !strings.Contains(out, want) {
			t.Errorf("error %q is missing %q", out, want)
		}
	}
}

func TestBuildCmd_SendsWhenNoHeartbeatIsReadable(t *testing.T) {
	// Nothing to judge by: the connector's own "already running" check still guards the job.
	var asked int
	send := func(string, interface{}) (*client.CommandResponse, error) {
		asked++
		return &client.CommandResponse{Success: true, Data: queuedJob("build-1")}, nil
	}
	noHeartbeat := func() (*client.Instance, error) { return nil, fmt.Errorf("no active instance on port 8090") }

	if _, err := buildCmd([]string{"--profile", "macOS", "--output", "Builds/App", "--no-wait"},
		send, failingPoll, noHeartbeat, &bytes.Buffer{}); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if asked != 1 {
		t.Errorf("build was sent %d times, want once", asked)
	}
}
