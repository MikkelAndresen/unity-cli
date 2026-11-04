package client

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// heartbeatStaleAge is how old a heartbeat may be before it stops describing what the editor
// is doing right now. The connector writes one about every second, so anything older means the
// connector stopped answering: unloaded, still compiling, or wedged behind a startup dialog.
const heartbeatStaleAge = 10 * time.Second

// unityLockfileName is the file Unity holds while a project is open. It lives in <project>/Temp,
// which Unity removes on a clean quit — so finding it is the only sign of a live editor when no
// connector heartbeat exists, which is exactly the window where startup dialogs (license, safe
// mode, "project already open", package resolution) appear and no in-editor modal handler exists.
const unityLockfileName = "UnityLockfile"

// EditorEvidence is on-disk proof that an editor may be open for a project even though nothing
// answers on HTTP. It is built from files only, so it works with no connector and no host access.
type EditorEvidence struct {
	ProjectPath   string
	LockfilePath  string
	LockfileAge   time.Duration
	LockfileMTime time.Time
	// StaleHeartbeat is the newest instance file for the project whose beats stopped arriving,
	// and StaleFile the file it came from. Its PID is not trusted on its own: a container that
	// mounts the host's ~/.unity-cli cannot see host PIDs at all, so beat freshness decides.
	StaleHeartbeat *Instance
	StaleFile      string
	StaleBeatAge   time.Duration
	// SessionHeartbeat is the newest heartbeat for the project whatever its state or freshness,
	// including a cleanly stopped one. It answers whether the connector ever loaded this session;
	// StaleHeartbeat is the subset that went silent and drives the unresponsive report.
	SessionHeartbeat *Instance
}

// sessionStartedAfterBeat reports whether Temp/UnityLockfile is newer than the heartbeat, which
// makes the heartbeat the record of a previous editor session. Unity rewrites the lockfile when it
// opens a project, so this is a restart in progress, not a crash of the editor that is starting.
func sessionStartedAfterBeat(inst *Instance) bool {
	if inst == nil || inst.ProjectPath == "" || inst.Timestamp <= 0 {
		return false
	}
	info, err := os.Stat(filepath.Join(expandHome(inst.ProjectPath), "Temp", unityLockfileName))
	if err != nil {
		return false
	}
	return info.ModTime().After(time.UnixMilli(inst.Timestamp))
}

// Found reports whether anything on disk suggests an open editor.
func (e *EditorEvidence) Found() bool {
	return e != nil && (e.LockfilePath != "" || e.StaleHeartbeat != nil)
}

func (e *EditorEvidence) heartbeatAfterLockfile(inst *Instance) bool {
	if e == nil || inst == nil {
		return false
	}
	if e.LockfilePath == "" {
		return true
	}
	return !time.UnixMilli(inst.Timestamp).Before(e.LockfileMTime)
}

// HeartbeatThisSession reports whether any heartbeat for the project postdates the lockfile.
// Temp/UnityLockfile is written once when Unity opens the project, so such a heartbeat proves the
// connector loaded this session — even if it has since stopped, gone silent, or shut down cleanly.
// Without a lockfile there is nothing to compare, and the heartbeat itself is proof enough.
func (e *EditorEvidence) HeartbeatThisSession() bool {
	if e == nil {
		return false
	}
	if e.SessionHeartbeat != nil {
		return e.heartbeatAfterLockfile(e.SessionHeartbeat)
	}
	return e.heartbeatAfterLockfile(e.StaleHeartbeat)
}

// SilentHeartbeatThisSession narrows the same question to the silent heartbeat the report is about:
// only then is "stopped answering" the right reading rather than "never started".
func (e *EditorEvidence) SilentHeartbeatThisSession() bool {
	if e == nil {
		return false
	}
	return e.heartbeatAfterLockfile(e.StaleHeartbeat)
}

// UnresponsiveHeartbeat returns the heartbeat that proves the connector loaded and then stopped
// answering, with how long it has been silent. The stale record covers anything past
// heartbeatStaleAge; the session heartbeat also covers the gap down to BeatSilenceAge, where
// `status` already calls the editor unresponsive, so an ordinary domain reload is not reported as
// "the connector never loaded".
func (e *EditorEvidence) UnresponsiveHeartbeat() (*Instance, time.Duration) {
	if e == nil {
		return nil, 0
	}
	if e.SilentHeartbeatThisSession() {
		return e.StaleHeartbeat, e.StaleBeatAge
	}
	beat := e.SessionHeartbeat
	if beat == nil || beat.State == "stopped" || !e.HeartbeatThisSession() {
		return nil, 0
	}
	age := time.Since(time.UnixMilli(beat.Timestamp))
	if age < BeatSilenceAge {
		return nil, 0
	}
	return beat, age
}

// Summary is the compact reason an editor looks open, e.g. "Temp/UnityLockfile, age 42s".
func (e *EditorEvidence) Summary() string {
	if e == nil {
		return ""
	}
	var parts []string
	if e.LockfilePath != "" {
		parts = append(parts, fmt.Sprintf("Temp/%s, age %s", unityLockfileName, FormatAge(e.LockfileAge)))
	}
	if e.StaleHeartbeat != nil {
		suffix := ""
		if e.StaleHeartbeat.PID > 0 {
			suffix = fmt.Sprintf(", pid %d", e.StaleHeartbeat.PID)
		}
		parts = append(parts, fmt.Sprintf("heartbeat last seen %s ago%s", FormatAge(e.StaleBeatAge), suffix))
	}
	return strings.Join(parts, "; ")
}

// FindEditorEvidence looks for an editor that is open for project/port but not answering.
// project is the --project value (path or substring; "" means the current directory), port the
// --port value (0 when unset). Returns nil when nothing on disk suggests an open editor.
func FindEditorEvidence(project string, port int) *EditorEvidence {
	ev := &EditorEvidence{}

	if stale, file := findStaleHeartbeat(project, port); stale != nil {
		ev.StaleHeartbeat = stale
		ev.StaleFile = file
		ev.StaleBeatAge = time.Since(time.UnixMilli(stale.Timestamp))
		ev.ProjectPath = stale.ProjectPath
	}

	for _, dir := range projectCandidates(project, port) {
		lock := filepath.Join(dir, "Temp", unityLockfileName)
		info, err := os.Stat(lock)
		if err != nil {
			continue
		}
		ev.ProjectPath = dir
		ev.LockfilePath = lock
		ev.LockfileMTime = info.ModTime()
		ev.LockfileAge = time.Since(info.ModTime())
		break
	}

	// Scope the session heartbeat to the project the rest of the evidence is about, so an
	// unrelated instance cannot suppress this project's lockfile crash hint.
	scope := ev.ProjectPath
	if scope == "" {
		scope = project
	}
	ev.SessionHeartbeat = NewestHeartbeat(scope, port)

	if !ev.Found() {
		return nil
	}
	return ev
}

// projectCandidates lists directories that could hold the requested project on this machine.
// --project is a substring match against heartbeat paths, and a sandbox mounts only some of
// those paths, so the literal argument and every matching heartbeat path are both probed.
func projectCandidates(project string, port int) []string {
	var dirs []string
	seen := map[string]bool{}
	add := func(dir string) {
		dir = expandHome(strings.TrimRight(dir, `/\`))
		if dir == "" || seen[dir] {
			return
		}
		seen[dir] = true
		dirs = append(dirs, dir)
	}

	if project != "" {
		if info, err := os.Stat(expandHome(project)); err == nil && info.IsDir() {
			add(project)
		}
	}

	files, _ := rawInstanceFiles()
	for _, f := range files {
		if port > 0 && f.Instance.Port != port {
			continue
		}
		if f.Instance.ProjectPath == "" {
			continue
		}
		if project != "" && !projectMatches(f.Instance.ProjectPath, project) {
			continue
		}
		add(f.Instance.ProjectPath)
	}

	if project == "" && port == 0 {
		if cwd, err := os.Getwd(); err == nil {
			add(cwd)
		}
	}
	return dirs
}

// NewestHeartbeat returns the newest instance file for the target whatever its state is, and without
// pruning anything. ScanInstances drops files whose pid is gone, which is exactly what a crash leaves
// behind; the crash verdict and `status` need that record, so they read the disk through here.
// project is the --project value (path or substring, "" for any) and port the --port value (0 when
// unset).
func NewestHeartbeat(project string, port int) *Instance {
	return newestMatching(rawInstances(), func(inst Instance) bool {
		if port > 0 && inst.Port != port {
			return false
		}
		return project == "" || projectMatches(inst.ProjectPath, project)
	})
}

// rawInstances is rawInstanceFiles as plain records, so the newest-record rule lives in
// newestMatching rather than being repeated by every caller that reads past the scan.
func rawInstances() []Instance {
	files, _ := rawInstanceFiles()
	out := make([]Instance, 0, len(files))
	for _, f := range files {
		out = append(out, f.Instance)
	}
	return out
}

// findStaleHeartbeat returns the newest heartbeat for the target that stopped arriving.
// Files the connector marked "stopped" are skipped: that is a clean shutdown, not a silent one.
func findStaleHeartbeat(project string, port int) (*Instance, string) {
	var best *Instance
	var bestFile string
	now := time.Now()

	files, _ := rawInstanceFiles()
	for _, f := range files {
		inst := f.Instance
		if inst.State == "stopped" || inst.Timestamp <= 0 {
			continue
		}
		if port > 0 && inst.Port != port {
			continue
		}
		if project != "" && !projectMatches(inst.ProjectPath, project) {
			continue
		}
		if now.Sub(time.UnixMilli(inst.Timestamp)) < heartbeatStaleAge {
			continue
		}
		if best == nil || inst.Timestamp > best.Timestamp {
			beat := inst
			best = &beat
			bestFile = f.Path
		}
	}
	return best, bestFile
}

// ReconnectInstance returns the heartbeat a request should be retried against after a reload or port
// change: the newest non-stopped record for the same project (or port when the project is unknown).
// It reads past ScanInstances so a heartbeat the scan is about to prune is still visible as the old
// record, never as the retry target. A newer beat wins; an equal timestamp keeps the first record,
// because the caller refuses to retry unless the beat actually advanced.
func ReconnectInstance(old *Instance) *Instance {
	if old == nil {
		return nil
	}
	return newestMatching(rawInstances(), func(inst Instance) bool {
		if inst.State == "stopped" || inst.Timestamp <= 0 {
			return false
		}
		if old.ProjectPath != "" {
			return projectMatches(inst.ProjectPath, old.ProjectPath)
		}
		return inst.Port == old.Port
	})
}

func projectMatches(projectPath, needle string) bool {
	return strings.Contains(filepath.ToSlash(projectPath), filepath.ToSlash(needle))
}

// expandHome resolves a leading "~" so an agent can pass the same path shape it sees in shell.
func expandHome(path string) string {
	if path != "~" && !strings.HasPrefix(path, "~/") && !strings.HasPrefix(path, `~\`) {
		return path
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return path
	}
	return filepath.Join(home, path[1:])
}

// FormatAge renders an age the way the CLI renders heartbeat ages: seconds under a minute.
func FormatAge(d time.Duration) string {
	if d < 0 {
		d = 0
	}
	if d < time.Minute {
		return fmt.Sprintf("%ds", int(d.Seconds()))
	}
	return d.Truncate(time.Second).String()
}
