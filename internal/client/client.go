package client

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
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
// If project is set, matches by project path substring.
// Otherwise returns the most recently active instance.
func DiscoverInstance(project string, port int) (*Instance, error) {
	if port > 0 {
		return FindActiveByPort(port)
	}

	instances, err := ScanInstances()
	if err != nil {
		return nil, fmt.Errorf("no Unity instances found.\nIs Unity running with the Connector package?\nExpected: %s", instancesDir())
	}

	// Filter out stopped instances
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
		for _, inst := range alive {
			if projectMatches(inst.ProjectPath, project) {
				return &inst, nil
			}
		}
		return nil, fmt.Errorf("no Unity instance found for project: %s", project)
	}

	// Try to match by current working directory before falling back to timestamp
	if cwd, err := os.Getwd(); err == nil {
		cwdNorm := filepath.ToSlash(cwd)
		for _, inst := range alive {
			projNorm := filepath.ToSlash(inst.ProjectPath)
			if cwdNorm == projNorm || strings.HasPrefix(cwdNorm, projNorm+"/") {
				return &inst, nil
			}
		}
	}

	// Return the most recently updated
	best := alive[0]
	for _, inst := range alive[1:] {
		if inst.Timestamp > best.Timestamp {
			best = inst
		}
	}
	return &best, nil
}

func Send(inst *Instance, command string, params interface{}, timeoutMs int) (*CommandResponse, error) {
	if params == nil {
		params = map[string]interface{}{}
	}

	body, err := json.Marshal(CommandRequest{Command: command, Params: params})
	if err != nil {
		return nil, err
	}

	url := fmt.Sprintf("http://127.0.0.1:%d/command", inst.Port)
	httpClient := &http.Client{Timeout: time.Duration(timeoutMs) * time.Millisecond}
	// The moment the request left, not now: crash artifacts are matched against it so that a dump from
	// an earlier crash is not blamed on this request.
	start := time.Now()

	resp, err := httpClient.Post(url, "application/json", bytes.NewReader(body))
	if err != nil {
		return nil, explainDroppedConnection(command, inst, start, err)
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
