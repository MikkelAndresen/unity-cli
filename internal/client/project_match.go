package client

import (
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
)

// ProjectEnvVar pins the target project for every call that passes neither --project nor --port,
// so an agent sandbox can select its editor once per session.
const ProjectEnvVar = "UNITY_CLI_PROJECT"

// AmbiguousInstanceError is a selection that fits more than one live editor. Guessing here is what
// sent one agent's play-mode toggles and recompiles into another agent's editor (#67).
type AmbiguousInstanceError struct {
	// Selector is the --project value, or "" when nothing selected and the cwd is in no project.
	Selector   string
	Candidates []Instance
}

func (e *AmbiguousInstanceError) Error() string {
	var b strings.Builder
	if e.Selector == "" {
		cwd, _ := os.Getwd()
		fmt.Fprintf(&b, "%d Unity editors are running and the current directory (%s) is in none of their projects", len(e.Candidates), cwd)
	} else {
		fmt.Fprintf(&b, "--project %q matches %d running Unity editors", e.Selector, len(e.Candidates))
	}
	b.WriteString("; pick one with --project <path> or --port <port>, or set " + ProjectEnvVar + ":")
	for _, c := range e.Candidates {
		fmt.Fprintf(&b, "\n  --port %d  %s (%s)", c.Port, c.ProjectPath, c.State)
	}
	return b.String()
}

// isPathSelector reports whether a --project value names a location rather than a fragment of one.
// A location matches by path identity; a fragment keeps the older substring match.
func isPathSelector(needle string) bool {
	if needle == "" {
		return false
	}
	if filepath.IsAbs(needle) || needle == "~" || needle == "." || needle == ".." {
		return true
	}
	for _, prefix := range []string{"~/", `~\`, "./", `.\`, "../", `..\`} {
		if strings.HasPrefix(needle, prefix) {
			return true
		}
	}
	return false
}

// canonicalPath is the form two paths are compared in. Symlinks are resolved through the longest
// existing ancestor, because macOS hands out /var and /private/var for the same directory and a
// heartbeat's project path need not exist on disk to be compared with the cwd.
func canonicalPath(path string) string {
	path = expandHome(path)
	if abs, err := filepath.Abs(path); err == nil {
		path = abs
	}
	path = resolveExistingPrefix(filepath.Clean(path))
	path = strings.TrimRight(filepath.ToSlash(path), "/")
	if runtime.GOOS == "windows" {
		path = strings.ToLower(path)
	}
	return path
}

func resolveExistingPrefix(path string) string {
	var rest []string
	for {
		if resolved, err := filepath.EvalSymlinks(path); err == nil {
			for i := len(rest) - 1; i >= 0; i-- {
				resolved = filepath.Join(resolved, rest[i])
			}
			return resolved
		}
		parent := filepath.Dir(path)
		if parent == path {
			break
		}
		rest = append(rest, filepath.Base(path))
		path = parent
	}
	for i := len(rest) - 1; i >= 0; i-- {
		path = filepath.Join(path, rest[i])
	}
	return path
}

// pathWithin reports whether path is root or lies below it. A plain prefix test would put
// /work/Game2 inside /work/Game.
func pathWithin(path, root string) bool {
	if root == "" {
		return false
	}
	return path == root || strings.HasPrefix(path, root+"/")
}

// projectMatches answers whether a heartbeat's project fits a --project value: by path identity
// (the project root or a path inside it) for a location, by substring for a bare fragment.
func projectMatches(projectPath, needle string) bool {
	if isPathSelector(needle) {
		return pathWithin(canonicalPath(needle), canonicalPath(projectPath))
	}
	return strings.Contains(filepath.ToSlash(projectPath), filepath.ToSlash(needle))
}

// newestPerProject keeps the newest heartbeat of each distinct project, ordered by project path.
func newestPerProject(instances []Instance) []Instance {
	byProject := map[string]Instance{}
	for _, inst := range instances {
		key := canonicalPath(inst.ProjectPath)
		if cur, ok := byProject[key]; !ok || inst.Timestamp > cur.Timestamp {
			byProject[key] = inst
		}
	}
	out := make([]Instance, 0, len(byProject))
	for _, inst := range byProject {
		out = append(out, inst)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ProjectPath < out[j].ProjectPath })
	return out
}

// deepestContaining returns the editor whose project holds path, preferring the innermost project
// when one Unity project is nested in another's folder.
func deepestContaining(projects []Instance, path string) *Instance {
	var best *Instance
	bestLen := -1
	for i := range projects {
		root := canonicalPath(projects[i].ProjectPath)
		if pathWithin(path, root) && len(root) > bestLen {
			best = &projects[i]
			bestLen = len(root)
		}
	}
	return best
}

// selectByProject picks the editor for an explicit --project value.
func selectByProject(alive []Instance, needle string) (*Instance, error) {
	projects := newestPerProject(alive)
	if isPathSelector(needle) {
		if best := deepestContaining(projects, canonicalPath(needle)); best != nil {
			return best, nil
		}
		return nil, noProjectError(needle, projects)
	}

	var matched []Instance
	for _, p := range projects {
		if projectMatches(p.ProjectPath, needle) {
			matched = append(matched, p)
		}
	}
	if len(matched) > 1 {
		// A fragment that is the whole folder name (or trailing path components) of exactly one
		// project names that project; "Game" must not be ambiguous with "Game-old".
		tail := "/" + strings.Trim(filepath.ToSlash(needle), "/")
		var exact []Instance
		for _, p := range matched {
			if strings.HasSuffix(strings.TrimRight(filepath.ToSlash(p.ProjectPath), "/"), tail) {
				exact = append(exact, p)
			}
		}
		if len(exact) == 1 {
			return &exact[0], nil
		}
		return nil, &AmbiguousInstanceError{Selector: needle, Candidates: matched}
	}
	if len(matched) == 0 {
		return nil, noProjectError(needle, projects)
	}
	return &matched[0], nil
}

// selectByWorkingDir picks the editor when nothing was selected: the project holding the cwd, else
// the only project below it (a repo whose Unity project is a subfolder), else the only editor.
func selectByWorkingDir(alive []Instance) (*Instance, error) {
	projects := newestPerProject(alive)
	if cwd, err := os.Getwd(); err == nil {
		here := canonicalPath(cwd)
		if best := deepestContaining(projects, here); best != nil {
			return best, nil
		}
		var below []Instance
		for _, p := range projects {
			if pathWithin(canonicalPath(p.ProjectPath), here) {
				below = append(below, p)
			}
		}
		if len(below) == 1 {
			return &below[0], nil
		}
	}
	if len(projects) == 1 {
		return &projects[0], nil
	}
	return nil, &AmbiguousInstanceError{Candidates: projects}
}

func noProjectError(needle string, projects []Instance) error {
	var b strings.Builder
	fmt.Fprintf(&b, "no Unity instance found for project: %s", needle)
	if len(projects) > 0 {
		b.WriteString("\nrunning editors:")
		for _, p := range projects {
			fmt.Fprintf(&b, "\n  --port %d  %s (%s)", p.Port, p.ProjectPath, p.State)
		}
	}
	return fmt.Errorf("%s", b.String())
}

// LiveProjectCount is how many distinct projects have a live editor. Callers print which editor
// answered when it is more than one, so a call that reached the wrong editor is visible.
func LiveProjectCount() int {
	instances, err := ScanInstances()
	if err != nil {
		return 0
	}
	var alive []Instance
	for _, inst := range instances {
		if isActiveInstance(inst) {
			alive = append(alive, inst)
		}
	}
	return len(newestPerProject(alive))
}
