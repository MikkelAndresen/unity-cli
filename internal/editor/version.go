package editor

import (
	"bufio"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// ReadProjectVersion returns the Unity editor version declared by the project's
// ProjectSettings/ProjectVersion.txt (for example "6000.4.4f1").
func ReadProjectVersion(projectPath string) (string, error) {
	if projectPath == "" {
		return "", errors.New("project path is empty")
	}

	path := filepath.Join(projectPath, "ProjectSettings", "ProjectVersion.txt")
	data, err := os.ReadFile(path)
	if err != nil {
		return "", fmt.Errorf("cannot read %s: %w", path, err)
	}

	version, err := parseProjectVersion(string(data))
	if err != nil {
		return "", fmt.Errorf("%s: %w", path, err)
	}
	return version, nil
}

// parseProjectVersion accepts both the m_EditorVersion line and, as a fallback,
// the first token of m_EditorVersionWithRevision ("6000.4.4f1 (0c5f9b2b1e0e)").
func parseProjectVersion(content string) (string, error) {
	var version, revision string

	scanner := bufio.NewScanner(strings.NewReader(content))
	for scanner.Scan() {
		line := strings.TrimSpace(strings.TrimPrefix(scanner.Text(), "\ufeff"))
		key, value, ok := strings.Cut(line, ":")
		if !ok {
			continue
		}
		value = strings.TrimSpace(value)
		switch strings.TrimSpace(key) {
		case "m_EditorVersion":
			version = value
		case "m_EditorVersionWithRevision":
			revision = value
		}
	}
	if err := scanner.Err(); err != nil {
		return "", err
	}

	if version != "" {
		return version, nil
	}
	if fields := strings.Fields(revision); len(fields) > 0 {
		return fields[0], nil
	}
	return "", errors.New("no m_EditorVersion entry found")
}
