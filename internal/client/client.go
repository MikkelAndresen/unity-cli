package client

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"
)

// Instance represents a running Unity Editor discovered from ~/.unity-cli/instances/.
type Instance struct {
	State            string `json:"state"`
	ProjectPath      string `json:"projectPath"`
	Port             int    `json:"port"`
	PID              int    `json:"pid"`
	UnityVersion     string `json:"unityVersion,omitempty"`
	ConnectorVersion string `json:"connectorVersion,omitempty"`
	Timestamp        int64  `json:"timestamp,omitempty"`
	CompileErrors    bool   `json:"compileErrors,omitempty"`
	// CompileFinishedAt is the connector's wall-clock ms of the last finished script
	// compilation. The CLI compares it before and after a compile request as evidence that a
	// compile actually ran; 0 means the connector does not report it.
	CompileFinishedAt int64 `json:"compileFinishedAt,omitempty"`
	// StartingSince is the connector's wall-clock ms when its listener came up, published while State
	// is "starting". The startup beater keeps Timestamp fresh through a long import, so this is the
	// only way to see how long the editor has been stuck before its first update tick.
	StartingSince int64 `json:"startingSince,omitempty"`
	// Modal is set while State is "modal": the dialog blocking Unity's main thread.
	Modal *ModalDialog `json:"modal,omitempty"`
	// Build is set while State is "building": the player build job holding Unity's main thread.
	// It is published by the build's own heartbeat writer, which is what keeps the timestamp fresh
	// while BuildPlayer has the main thread for minutes at a time.
	Build *BuildJob `json:"build,omitempty"`
	// SourceFile is where this heartbeat was read from. It is the CLI's own annotation, never a field
	// of the file, and it is what lets a report point at the record of an editor that is gone.
	SourceFile string `json:"-"`
}

// BuildJob mirrors the connector's UnityCliConnector.BuildJobs.BuildHeartbeatSnapshot.
type BuildJob struct {
	ID                  string   `json:"id"`
	Phase               string   `json:"phase"`
	Profile             string   `json:"profile,omitempty"`
	Target              string   `json:"target,omitempty"`
	Output              string   `json:"output,omitempty"`
	ElapsedSeconds      float64  `json:"elapsed_seconds"`
	ProgressPercent     *float64 `json:"progress_percent"`
	ProgressDescription string   `json:"progress_description,omitempty"`
}

// ModalDialog mirrors the connector's UnityCliConnector.Modal.ModalDialog.
type ModalDialog struct {
	ID          int64         `json:"id"`
	Kind        string        `json:"kind"`
	Title       string        `json:"title"`
	Texts       []string      `json:"texts"`
	Buttons     []ModalButton `json:"buttons"`
	WindowClass string        `json:"window_class"`
}

// ModalButton is one clickable button of a ModalDialog.
type ModalButton struct {
	Index     int    `json:"index"`
	Title     string `json:"title"`
	IsDefault bool   `json:"is_default"`
	IsCancel  bool   `json:"is_cancel"`
	Enabled   bool   `json:"enabled"`
}

// CommandRequest is the JSON body sent to Unity's HTTP server.
type CommandRequest struct {
	Command string      `json:"command"`
	Params  interface{} `json:"params"`
	// DeadlineMs is this request's remaining client budget. The connector uses it to answer a
	// queued command before the client gives up (unity-connector RequestTracker.QueueWaitMsFor);
	// 0 means the caller declared no deadline and the connector's own default applies.
	DeadlineMs int `json:"deadline_ms,omitempty"`
}

// CommandResponse is the JSON body returned by Unity.
// Data is raw JSON so callers can unmarshal into any shape.
type CommandResponse struct {
	Success bool            `json:"success"`
	Message string          `json:"message"`
	Data    json.RawMessage `json:"data,omitempty"`
	// Meta is how the call was served rather than what it returned (exec reports
	// compile cache state here). The CLI prints it only with --meta.
	Meta json.RawMessage `json:"meta,omitempty"`
}

// isProcessDead returns true only when the process is confirmed to not exist.
// Permission errors or transient failures return false (not confirmed dead),
// so the instance file is preserved.
// Defaults to the OS-specific implementation; overridden in tests.
var isProcessDead = checkProcessDead

// IsProcessAlive reports whether pid is running. It is conservative: true is
// also returned when liveness cannot be determined (permission denied, or a PID
// from a different namespace), so callers must not treat true as proof of life.
func IsProcessAlive(pid int) bool {
	if pid <= 0 {
		return false
	}
	return !isProcessDead(pid)
}

func instancesDir() string {
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".unity-cli", "instances")
}

// instanceFile is one heartbeat file on disk plus its parsed contents.
type instanceFile struct {
	Path     string
	Instance Instance
}

// rawInstanceFiles reads every instance file without judging or deleting anything, so callers
// that need the dead/stopped ones (evidence of an editor that stopped answering) can see them.
func rawInstanceFiles() ([]instanceFile, error) {
	dir := instancesDir()
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}

	var files []instanceFile
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		fp := filepath.Join(dir, e.Name())
		data, err := os.ReadFile(fp)
		if err != nil {
			continue
		}
		var inst Instance
		if err := json.Unmarshal(data, &inst); err != nil {
			continue
		}
		files = append(files, instanceFile{Path: fp, Instance: inst})
	}
	for i := range files {
		files[i].Instance.SourceFile = files[i].Path
	}
	return files, nil
}

// ScanInstances reads all instance files from ~/.unity-cli/instances/.
// Stale files whose PID is no longer running are automatically removed.
func ScanInstances() ([]Instance, error) {
	files, err := rawInstanceFiles()
	if err != nil {
		return nil, err
	}

	var instances []Instance
	for _, f := range files {
		if f.Instance.PID > 0 && isProcessDead(f.Instance.PID) {
			_ = os.Remove(f.Path)
			continue
		}
		instances = append(instances, f.Instance)
	}
	return instances, nil
}

// FindByPort scans instance files and returns the instance matching the given port.
// If multiple instances share the same port, the one with the most recent timestamp wins.
func FindByPort(port int) (*Instance, error) {
	instances, err := ScanInstances()
	if err != nil {
		return nil, err
	}
	var best *Instance
	for i, inst := range instances {
		if inst.Port != port {
			continue
		}
		if best == nil || inst.Timestamp > best.Timestamp {
			best = &instances[i]
		}
	}
	if best == nil {
		return nil, fmt.Errorf("no instance on port %d", port)
	}
	return best, nil
}

func isActiveInstance(inst Instance) bool {
	return inst.State != "stopped" && inst.Timestamp > 0
}

// FindActiveByPort is like FindByPort but skips stopped or incomplete instances.
// Used by polling paths (waitForAlive, waitForReady) that only care about live instances.
func FindActiveByPort(port int) (*Instance, error) {
	instances, err := ScanInstances()
	if err != nil {
		return nil, err
	}
	var best *Instance
	for i, inst := range instances {
		if inst.Port != port || !isActiveInstance(inst) {
			continue
		}
		if best == nil || inst.Timestamp > best.Timestamp {
			best = &instances[i]
		}
	}
	if best == nil {
		return nil, fmt.Errorf("no active instance on port %d", port)
	}
	return best, nil
}

// DiscoverInstance finds a running Unity instance from ~/.unity-cli/instances/.
// If port > 0, matches an active instance by port.
// If project is set, matches it by path (a location) or substring (a fragment); a fragment that fits
// several projects is an error. Otherwise the cwd decides, and several candidates is an error too:
// guessing routes commands into another project's editor.
func DiscoverInstance(project string, port int) (*Instance, error) {
	if port > 0 {
		return FindActiveByPort(port)
	}

	instances, err := ScanInstances()
	if err != nil {
		return nil, fmt.Errorf("no Unity instances found.\nIs Unity running with the Connector package?\nExpected: %s", instancesDir())
	}

	var alive []Instance
	for _, inst := range instances {
		if !isActiveInstance(inst) {
			continue
		}
		alive = append(alive, inst)
	}

	if len(alive) == 0 {
		return nil, fmt.Errorf("no Unity instances running")
	}

	if project != "" {
		return selectByProject(alive, project)
	}
	return selectByWorkingDir(alive)
}

// DiscoverInstanceKeepingEvidence is DiscoverInstance plus the newest heartbeat read before the
// instance scan. ScanInstances deletes dead-pid files, which is exactly the record a crash verdict
// needs, so callers that report a crash must read it first.
func DiscoverInstanceKeepingEvidence(project string, port int) (*Instance, *Instance, error) {
	beat := NewestHeartbeat(project, port)
	inst, err := DiscoverInstance(project, port)
	return inst, beat, err
}

// newestMatching returns the newest heartbeat matching keep, or nil when none does. Readiness is
// decided by the caller; a stale file with a newer write must not hide the heartbeat that replaced it.
func newestMatching(instances []Instance, keep func(Instance) bool) *Instance {
	var best *Instance
	for i := range instances {
		inst := &instances[i]
		if !keep(*inst) {
			continue
		}
		if best == nil || inst.Timestamp > best.Timestamp {
			best = inst
		}
	}
	return best
}

// RequestNotDelivered reports whether the transport failed before a listener could read the request: a
// dial failure (connection refused, no route) rather than a connection that died after the request was
// written. Only the former is safe to resend whatever the command.
func RequestNotDelivered(err error) bool {
	var opErr *net.OpError
	if errors.As(err, &opErr) && opErr.Op == "dial" {
		return true
	}
	return errors.Is(err, syscall.ECONNREFUSED)
}

// RequestTimeoutError is a client-side deadline expiring. It is deliberately not a connection
// failure: the editor is healthy and may still be running the command, so saying "cannot connect"
// sends the caller into a reconnect loop instead of waiting for the result.
type RequestTimeoutError struct {
	Command string
	Budget  time.Duration
}

func (e *RequestTimeoutError) Error() string {
	return fmt.Sprintf("timed out after %s waiting for Unity to answer %s (the editor may still be running it)", FormatBudget(e.Budget), e.Command)
}

// FormatBudget keeps a context deadline's nanosecond remainder out of the message: callers passed
// a whole number of milliseconds, and "1.947998375s" reads like a bug rather than a budget. Below a
// second the granularity stays at one millisecond, so `--timeout 1` is reported as the 1 ms it was.
func FormatBudget(d time.Duration) string {
	if d <= 0 {
		return "0s"
	}
	if d < time.Second {
		return ceilDuration(d, time.Millisecond).String()
	}
	return strconv.FormatFloat(ceilDuration(d, 100*time.Millisecond).Seconds(), 'f', -1, 64) + "s"
}

// ceilDuration keeps a rounding-down remainder from making a 2000 ms budget read as 1.9s.
func ceilDuration(d, step time.Duration) time.Duration {
	return ((d + step - 1) / step) * step
}

// connectorHTTP is the client every connector request goes through. Its transport keeps no
// connection alive: the editor tears its listener down across a domain reload, and a connection that
// was pooled before the reload reads the old listener's leftover bytes as an unsolicited response.
// Requests here are few and long-lived, so pooling buys nothing; one connection per request is what
// makes a reload a plain new connection instead of a confused one.
var connectorHTTP = &http.Client{
	Transport: &http.Transport{DisableKeepAlives: true},
}

// Send is SendContext without a caller context, for callers that manage their own HTTP timeout
// (the quit/restart path, whose request budget is not the invocation deadline).
func Send(inst *Instance, command string, params interface{}, timeoutMs int) (*CommandResponse, error) {
	return SendContext(context.Background(), inst, command, params, timeoutMs)
}

// SendContext posts one command, bounded by timeoutMs and by the caller's context. A context
// deadline shared by discovery, wait-for-alive and this request is what keeps one invocation from
// spending the budget twice; a canceled context is a caller interrupt, not a timeout or a crash.
func SendContext(ctx context.Context, inst *Instance, command string, params interface{}, timeoutMs int) (*CommandResponse, error) {
	if params == nil {
		params = map[string]interface{}{}
	}

	url := fmt.Sprintf("http://127.0.0.1:%d/command", inst.Port)
	budget := time.Duration(timeoutMs) * time.Millisecond
	if budget > 0 {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, budget)
		defer cancel()
	}
	// The parent (one invocation, several requests) may expire before this request's own budget.
	if deadline, ok := ctx.Deadline(); ok {
		if remaining := time.Until(deadline); remaining > 0 && (budget == 0 || remaining < budget) {
			budget = remaining
		}
	}

	// Told to the connector so it answers a queued command itself instead of the connection sitting
	// unanswered until the client hangs up.
	body, err := json.Marshal(CommandRequest{
		Command:    command,
		Params:     params,
		DeadlineMs: int(budget / time.Millisecond),
	})
	if err != nil {
		return nil, err
	}

	// The moment the request left, not now: crash artifacts are matched against it so that a dump from
	// an earlier crash is not blamed on this request.
	start := time.Now()

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, url, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	resp, err := connectorHTTP.Do(req)
	if err != nil {
		if ctx.Err() == context.Canceled {
			return nil, fmt.Errorf("request for %s cancelled: %w", command, ctx.Err())
		}
		if ctx.Err() == context.DeadlineExceeded || isTimeoutError(err) {
			return nil, &RequestTimeoutError{Command: command, Budget: budget}
		}
		return nil, explainDroppedConnection(inst, start, err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		var body []byte
		body, _ = io.ReadAll(resp.Body)
		if len(body) > 0 {
			return nil, fmt.Errorf("HTTP %d from Unity: %s", resp.StatusCode, string(body))
		}
		return nil, fmt.Errorf("HTTP %d from Unity (command: %s)", resp.StatusCode, command)
	}

	respBody, err := io.ReadAll(resp.Body)
	if err != nil || len(respBody) == 0 {
		// Some commands (e.g. play mode entry) close the connection before responding.
		return &CommandResponse{
			Success: true,
			Message: fmt.Sprintf("%s sent (connection closed before response)", command),
		}, nil
	}

	var result CommandResponse
	if err := json.Unmarshal(respBody, &result); err != nil {
		// Unity sent a non-JSON body — treat as plain message.
		return &CommandResponse{
			Success: true,
			Message: string(respBody),
		}, nil
	}

	return &result, nil
}

// isTimeoutError reports a transport deadline without swallowing a canceled request: cancellation
// is decided by the context first, in SendContext.
func isTimeoutError(err error) bool {
	if errors.Is(err, context.DeadlineExceeded) {
		return true
	}
	var timeout interface{ Timeout() bool }
	return errors.As(err, &timeout) && timeout.Timeout()
}

// StartingFor is how long the editor has been in its "starting" state, measured on the connector's
// own clock (Timestamp - StartingSince) so a CLI on another machine needs no agreeing clock. ok is
// false when the editor is not starting or its connector does not publish StartingSince.
func (inst *Instance) StartingFor() (time.Duration, bool) {
	if inst == nil || inst.State != "starting" || inst.StartingSince <= 0 || inst.Timestamp < inst.StartingSince {
		return 0, false
	}
	return time.Duration(inst.Timestamp-inst.StartingSince) * time.Millisecond, true
}
