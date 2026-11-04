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
}

// Found reports whether anything on disk suggests an open editor.
func (e *EditorEvidence) Found() bool {
	return e != nil && (e.LockfilePath != "" || e.StaleHeartbeat != nil)
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
