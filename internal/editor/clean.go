package editor

import (
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
)

// cleanTargets are regenerable directories. Deleting them forces Unity to
// reimport the project and rebuild its import database.
var cleanTargets = []string{"Library", "Temp"}

// CleanProject deletes Library/ and Temp/ under projectPath and returns the
// paths that were removed. Missing directories are not an error.
//
// The caller is responsible for ensuring the editor has exited; this function
// only guards against obviously wrong paths. A Unity project root must be an
// absolute path containing both Assets/ and ProjectSettings/ directories.
func CleanProject(projectPath string) ([]string, error) {
	root, err := projectRoot(projectPath)
	if err != nil {
		return nil, err
	}

	var removed []string
	for _, name := range cleanTargets {
		target := filepath.Join(root, name)
		info, err := os.Lstat(target)
		if errors.Is(err, fs.ErrNotExist) {
			continue
		}
		if err != nil {
			return removed, fmt.Errorf("cannot inspect %s: %w", target, err)
		}
		// RemoveAll on a symlink removes the link, not its target, so symlinked
		// Library/Temp folders are safe to delete.
		if info.Mode()&os.ModeSymlink == 0 && !info.IsDir() {
			return removed, fmt.Errorf("%s is not a directory; refusing to delete it", target)
		}
		if err := os.RemoveAll(target); err != nil {
			return removed, fmt.Errorf("failed to delete %s: %w (is Unity still running or holding files?)", target, err)
		}
		removed = append(removed, target)
	}
	return removed, nil
}

// LockfileHeld reports whether Temp/UnityLockfile exists. Unity keeps the file
// mapped while an editor has the project open, and a held lock on Windows also
// makes deletion fail. Existence alone is not proof that an editor is running,
// so callers must establish that separately before cleaning.
func LockfileHeld(projectPath string) (bool, error) {
	if projectPath == "" {
		return false, errors.New("project path is empty")
	}
	path := filepath.Join(projectPath, "Temp", "UnityLockfile")
	_, err := os.Stat(path)
	switch {
	case err == nil:
		return true, nil
	case errors.Is(err, fs.ErrNotExist):
		return false, nil
	default:
		return false, fmt.Errorf("cannot check Unity lockfile %s: %w", path, err)
	}
}

// ValidateProject checks that projectPath is an absolute Unity project root.
// It is the same guard CleanProject uses, exposed so a launch path can reject a
// non-project directory without deleting anything.
func ValidateProject(projectPath string) error {
	_, err := projectRoot(projectPath)
	return err
}

// projectRoot validates that projectPath looks like a Unity project before any
// recursive deletion happens.
func projectRoot(projectPath string) (string, error) {
	if projectPath == "" {
		return "", errors.New("project path is empty")
	}
	if !filepath.IsAbs(projectPath) {
		return "", fmt.Errorf("project path must be absolute: %q", projectPath)
	}

	root := filepath.Clean(projectPath)
	for _, required := range []string{"Assets", "ProjectSettings"} {
		requiredPath := filepath.Join(root, required)
		info, err := os.Stat(requiredPath)
		if err != nil {
			return "", fmt.Errorf("%s is not a Unity project root: missing %s/: %w", root, required, err)
		}
		if !info.IsDir() {
			return "", fmt.Errorf("%s is not a Unity project root: %s is not a directory", root, required)
		}
	}
	return root, nil
}
