package cmd

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

const (
	heartbeatFreshAge     = 3 * time.Second
	restartStaleGrace     = 15 * time.Second
	restartQuitTimeout    = 60 * time.Second
	restartLingerGrace    = 15 * time.Second
	restartKillTimeout    = 10 * time.Second
	restartQuitRequestMs  = 10000
	defaultRestartWaitDur = 30 * time.Minute
)

// restartCleanDirs are deleted by --clean. Unity regenerates both on the next launch.
var restartCleanDirs = []string{"Library", "Temp"}

type restartOptions struct {
	clean       bool
	force       bool
	allowDirty  bool
	autoSave    bool
	noWait      bool
	editorPath  string
	waitTimeout time.Duration
}

// restartDeps isolates every side effect of a restart so the flow can be tested
// without a real editor process.
type restartDeps struct {
	scan        func() ([]client.Instance, error)
	send        func(inst *client.Instance, command string, params interface{}, timeoutMs int) (*client.CommandResponse, error)
	processDead func(pid int) bool
	kill        func(pid int) error
	removeAll   func(path string) error
	isDir       func(path string) bool
	launch      func(exe string, args []string) (int, error)
	now         func() time.Time
	sleep       func(time.Duration)
}

func defaultRestartDeps() restartDeps {
	return restartDeps{
		scan:        client.ScanInstances,
		send:        client.Send,
		processDead: client.ProcessDead,
		kill:        client.KillProcess,
		removeAll:   os.RemoveAll,
		isDir: func(path string) bool {
			info, err := os.Stat(path)
			return err == nil && info.IsDir()
		},
		launch: client.StartDetached,
		now:    time.Now,
		sleep:  time.Sleep,
	}
}

func parseRestartOptions(args []string) (restartOptions, error) {
	flags := parseSubFlags(args)
	_, clean := flags["clean"]
	_, force := flags["force"]
	_, allowDirty := flags["allow-dirty-scenes"]
	_, autoSave := flags["auto-save-scenes"]
	_, noWait := flags["no-wait"]
	opts := restartOptions{
		clean:       clean,
		force:       force,
		allowDirty:  allowDirty,
		autoSave:    autoSave,
		noWait:      noWait,
		editorPath:  flags["editor"],
		waitTimeout: defaultRestartWaitDur,
	}
	if opts.editorPath == "true" {
		return opts, fmt.Errorf("--editor requires a path")
	}
	if raw, ok := flags["wait-timeout"]; ok {
		ms, err := strconv.Atoi(raw)
		if err != nil || ms <= 0 {
			return opts, fmt.Errorf("--wait-timeout must be a positive number of milliseconds, got %q", raw)
		}
		opts.waitTimeout = time.Duration(ms) * time.Millisecond
	}
	return opts, nil
}

func restartFromFlags(args []string) error {
	opts, err := parseRestartOptions(args)
	if err != nil {
		return err
	}
	inst, err := findRestartTarget(flagProject, flagPort)
	if err != nil {
		return err
	}
	if inst.PID > 0 {
		if err := checkConnectorVersion(inst, Version, flagIgnoreVersionMismatch); err != nil {
			return err
		}
	}
	return restartEditor(inst, opts, defaultRestartDeps())
}

// findRestartTarget returns the running instance to restart. When none is running,
// --project naming a Unity project directory yields a PID-less target so the editor
// can be cold-started.
func findRestartTarget(project string, port int) (*client.Instance, error) {
	inst, err := client.DiscoverInstance(project, port)
	if err == nil {
		return inst, nil
	}
	if port > 0 || project == "" {
		return nil, err
	}
	abs, absErr := filepath.Abs(project)
	if absErr != nil || !isUnityProject(abs, defaultRestartDeps().isDir) {
		return nil, err
	}
	return &client.Instance{ProjectPath: abs}, nil
}

// hubEditorForProject returns the default Unity Hub install location for the
// project's m_EditorVersion, or "" when it is unknown or not installed there.
func hubEditorForProject(project string, isDir func(string) bool) string {
	data, err := os.ReadFile(filepath.Join(project, "ProjectSettings", "ProjectVersion.txt"))
	if err != nil {
		return ""
	}
	version := parseEditorVersion(string(data))
	if version == "" {
		return ""
	}
	var path string
	switch runtime.GOOS {
	case "darwin":
		path = filepath.Join("/Applications/Unity/Hub/Editor", version, "Unity.app")
		if !isDir(path) {
			return ""
		}
		return path
	case "windows":
		path = filepath.Join(`C:\Program Files\Unity\Hub\Editor`, version, "Editor", "Unity.exe")
	default:
		home, _ := os.UserHomeDir()
		path = filepath.Join(home, "Unity", "Hub", "Editor", version, "Editor", "Unity")
	}
	if !isDir(filepath.Dir(path)) {
		return ""
	}
	return path
}

func parseEditorVersion(projectVersion string) string {
	for _, line := range strings.Split(projectVersion, "\n") {
		if v, ok := strings.CutPrefix(strings.TrimSpace(line), "m_EditorVersion:"); ok {
			return strings.TrimSpace(v)
		}
	}
	return ""
}

func isUnityProject(path string, isDir func(string) bool) bool {
	return isDir(filepath.Join(path, "Assets")) && isDir(filepath.Join(path, "ProjectSettings"))
}

func restartEditor(inst *client.Instance, opts restartOptions, d restartDeps) error {
	project := inst.ProjectPath
	if project == "" {
		return fmt.Errorf("instance has no project path")
	}
	editorPath := opts.editorPath
	if editorPath == "" {
		editorPath = inst.EditorPath
	}
	if editorPath == "" {
		editorPath = hubEditorForProject(project, d.isDir)
	}
	if editorPath == "" {
		return fmt.Errorf("cannot tell which editor to launch (no editorPath in the heartbeat and no Hub install matching ProjectSettings/ProjectVersion.txt); pass --editor <path to Unity.app or Unity executable>")
	}
	// Checked before quitting so a bad target never leaves the user without an editor.
	if opts.clean && !isUnityProject(project, d.isDir) {
		return fmt.Errorf("refusing to clean %s: it has no Assets and ProjectSettings folders", project)
	}

	if inst.PID > 0 && !d.processDead(inst.PID) {
		if err := stopEditor(inst, opts, d); err != nil {
			return err
		}
	}

	if opts.clean {
		for _, name := range restartCleanDirs {
			dir := filepath.Join(project, name)
			fmt.Fprintf(os.Stderr, "Deleting %s\n", dir)
			if err := d.removeAll(dir); err != nil {
				return fmt.Errorf("delete %s: %w", dir, err)
			}
		}
	}

	exe := client.EditorExecutable(editorPath)
	pid, err := d.launch(exe, []string{"-projectPath", project})
	if err != nil {
		return fmt.Errorf("launch %s: %w", exe, err)
	}
	fmt.Fprintf(os.Stderr, "Launched Unity (pid %d) for %s\n", pid, project)

	if opts.noWait {
		fmt.Println("Unity launched; not waiting for it to become ready.")
		return nil
	}
	return waitForRestart(project, inst.PID, pid, opts.waitTimeout, d)
}

// stopEditor asks the connector to quit and waits for the process to exit. When
// Unity cannot be asked (stale heartbeat, modal dialog) or ignores the request,
// it is killed only with --force. A refusal (dirty scenes) is never overridden.
// An editor that has already run its quit handlers but whose process lingers is
// killed without --force: nothing unsaved is left, and it holds the project lock.
func stopEditor(inst *client.Instance, opts restartOptions, d restartDeps) error {
	inst = awaitFreshHeartbeat(inst, d)

	var reason string
	quitFinished := false
	switch {
	case isModal(inst):
		reason = "blocked by a modal dialog.\n" + describeModal(inst.Modal)
	case !heartbeatFresh(inst, d.now()):
		reason = fmt.Sprintf("not responding (state %q, last heartbeat %s ago)", inst.State,
			d.now().Sub(time.UnixMilli(inst.Timestamp)).Truncate(time.Second))
	default:
		fmt.Fprintf(os.Stderr, "Asking Unity (pid %d) to quit...\n", inst.PID)
		resp, err := d.send(inst, "manage_editor", map[string]interface{}{
			"action":             "quit",
			"allow_dirty_scenes": opts.allowDirty,
			"auto_save_scenes":   opts.autoSave,
		}, restartQuitRequestMs)
		switch {
		case err != nil:
			reason = fmt.Sprintf("unreachable (%v)", err)
		case !resp.Success:
			return quitRefusedError(resp)
		default:
			var exited bool
			exited, quitFinished = waitForQuit(inst, d)
			if exited {
				return nil
			}
			if quitFinished {
				reason = "done quitting, but its process has not exited"
			} else {
				reason = fmt.Sprintf("still running %s after the quit request", restartQuitTimeout)
			}
		}
	}

	if !opts.force && !quitFinished {
		return fmt.Errorf("the editor (pid %d) is %s\nRerun with --force to kill it; unsaved changes are lost", inst.PID, strings.TrimRight(reason, "\n"))
	}
	fmt.Fprintf(os.Stderr, "Killing Unity (pid %d): %s\n", inst.PID, strings.TrimRight(reason, "\n"))
	if err := d.kill(inst.PID); err != nil && !d.processDead(inst.PID) {
		return fmt.Errorf("kill Unity (pid %d): %w", inst.PID, err)
	}
	if !waitForExit(inst.PID, restartKillTimeout, d) {
		return fmt.Errorf("the editor (pid %d) is still running %s after being killed", inst.PID, restartKillTimeout)
	}
	return nil
}

// awaitFreshHeartbeat gives a compiling or reloading editor a short grace period,
// since its heartbeat legitimately stalls while the main thread is busy.
func awaitFreshHeartbeat(inst *client.Instance, d restartDeps) *client.Instance {
	deadline := d.now().Add(restartStaleGrace)
	for !heartbeatFresh(inst, d.now()) && !isModal(inst) && d.now().Before(deadline) {
		d.sleep(statusPollInterval)
		if latest := findProjectInstance(inst.ProjectPath, d.scan, func(i client.Instance) bool { return i.PID == inst.PID }); latest != nil {
			inst = latest
		}
	}
	return inst
}

func heartbeatFresh(inst *client.Instance, now time.Time) bool {
	return inst.Timestamp > 0 && now.Sub(time.UnixMilli(inst.Timestamp)) < heartbeatFreshAge
}

func quitRefusedError(resp *client.CommandResponse) error {
	msg := resp.Message
	if msg == "" {
		msg = "unknown error"
	}
	var b strings.Builder
	fmt.Fprintf(&b, "the editor refused to quit: %s", msg)
	var data struct {
		Blocked string   `json:"blocked"`
		Scenes  []string `json:"scenes"`
	}
	if len(resp.Data) > 0 && json.Unmarshal(resp.Data, &data) == nil && data.Blocked == "dirty-scenes" {
		fmt.Fprintf(&b, "\n  Unsaved scenes: %s\nRerun with --auto-save-scenes to save them, or --allow-dirty-scenes to discard them", strings.Join(data.Scenes, ", "))
	}
	return fmt.Errorf("%s", b.String())
}

// waitForQuit waits for the process to exit after a quit request. stopped reports
// whether the connector marked the instance "stopped" (EditorApplication.quitting
// ran); the wait is then cut to restartLingerGrace, measured from that moment.
func waitForQuit(inst *client.Instance, d restartDeps) (exited, stopped bool) {
	deadline := d.now().Add(restartQuitTimeout)
	var stoppedAt time.Time
	for {
		if d.processDead(inst.PID) {
			return true, !stoppedAt.IsZero()
		}
		if stoppedAt.IsZero() && instanceStopped(inst.ProjectPath, inst.PID, d.scan) {
			stoppedAt = d.now()
		}
		if !stoppedAt.IsZero() && d.now().Sub(stoppedAt) >= restartLingerGrace {
			return false, true
		}
		if !d.now().Before(deadline) {
			return false, !stoppedAt.IsZero()
		}
		d.sleep(statusPollInterval)
	}
}

func instanceStopped(project string, pid int, scan func() ([]client.Instance, error)) bool {
	instances, err := scan()
	if err != nil {
		return false
	}
	for _, inst := range instances {
		if inst.PID == pid && filepath.Clean(inst.ProjectPath) == filepath.Clean(project) {
			return inst.State == "stopped"
		}
	}
	return false
}

func waitForExit(pid int, timeout time.Duration, d restartDeps) bool {
	deadline := d.now().Add(timeout)
	for {
		if d.processDead(pid) {
			return true
		}
		if !d.now().Before(deadline) {
			return false
		}
		d.sleep(statusPollInterval)
	}
}

// waitForRestart polls until the relaunched editor's connector reports ready.
// The old process's instance file can linger, so only a different PID counts.
func waitForRestart(project string, oldPID, launchedPID int, timeout time.Duration, d restartDeps) error {
	fmt.Fprintf(os.Stderr, "Waiting for Unity to start (importing can take a while after --clean)...\n")
	isNew := func(i client.Instance) bool { return oldPID == 0 || i.PID != oldPID }

	deadline := d.now().Add(timeout)
	for d.now().Before(deadline) {
		d.sleep(statusPollInterval)
		inst := findProjectInstance(project, d.scan, isNew)
		if inst == nil {
			if d.processDead(launchedPID) {
				return fmt.Errorf("the editor (pid %d) exited before the connector started; check the Editor.log", launchedPID)
			}
			continue
		}
		if isModal(inst) {
			return fmt.Errorf("the editor (pid %d) started but is blocked by a modal dialog.\n%sAnswer it with: unity-cli modal answer --button \"<title>\"", inst.PID, describeModal(inst.Modal))
		}
		if inst.State != "ready" || !heartbeatFresh(inst, d.now()) {
			continue
		}
		if inst.CompileErrors {
			return fmt.Errorf("the editor (pid %d, port %d) restarted with compile errors (check unity-cli console)", inst.PID, inst.Port)
		}
		fmt.Printf("Unity restarted and ready (pid %d, port %d).\n", inst.PID, inst.Port)
		return nil
	}
	return fmt.Errorf("timed out after %s waiting for Unity to become ready. A startup dialog (e.g. Safe Mode) can block it before the connector loads; check the editor window", timeout)
}

// findProjectInstance returns the most recently updated instance whose project path
// is exactly project and which satisfies accept.
func findProjectInstance(project string, scan func() ([]client.Instance, error), accept func(client.Instance) bool) *client.Instance {
	instances, err := scan()
	if err != nil {
		return nil
	}
	want := filepath.Clean(project)
	var best *client.Instance
	for i, inst := range instances {
		if filepath.Clean(inst.ProjectPath) != want || inst.State == "stopped" || !accept(inst) {
			continue
		}
		if best == nil || inst.Timestamp > best.Timestamp {
			best = &instances[i]
		}
	}
	return best
}
