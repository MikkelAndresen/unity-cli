package editor

import (
	"crypto/md5"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// HeartbeatFilePath returns the connector's heartbeat file for a project:
// ~/.unity-cli/instances/<first 16 hex chars of md5(projectPath)>.json.
//
// The naming is owned by the connector (unity-connector/Editor/Heartbeat.cs:92-104),
// which hashes Application.dataPath with "/Assets" removed. That string uses
// forward slashes and has no trailing separator, so normalize the supplied path
// the same way and never resolve symlinks or absolute-ize it — a different
// string means a different hash and the heartbeat is never found.
func HeartbeatFilePath(projectPath string) (string, error) {
	normalized, err := normalizeProjectPath(projectPath)
	if err != nil {
		return "", err
	}

	sum := md5.Sum([]byte(normalized)) //nolint:gosec // filename hash, not a security boundary
	name := hex.EncodeToString(sum[:])[:16] + ".json"

	homeDir, err := os.UserHomeDir()
	if err != nil {
		return "", fmt.Errorf("cannot determine home directory: %w", err)
	}
	return filepath.Join(homeDir, ".unity-cli", "instances", name), nil
}

func normalizeProjectPath(projectPath string) (string, error) {
	if projectPath == "" {
		return "", errors.New("project path is empty")
	}
	normalized := strings.TrimRight(strings.ReplaceAll(projectPath, `\`, "/"), "/")
	if normalized == "" {
		return "", fmt.Errorf("invalid project path %q", projectPath)
	}
	return normalized, nil
}

// ReadHeartbeat reads the heartbeat for projectPath without deleting it.
//
// This intentionally does not go through client.ScanInstances, which prunes
// files whose PID it considers dead: during a restart the heartbeat is the only
// remaining record of the project path. A missing file is reported with an
// error wrapping fs.ErrNotExist.
func ReadHeartbeat(projectPath string) (*client.Instance, error) {
	path, err := HeartbeatFilePath(projectPath)
	if err != nil {
		return nil, err
	}

	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}

	var inst client.Instance
	if err := json.Unmarshal(data, &inst); err != nil {
		return nil, fmt.Errorf("cannot parse Unity heartbeat %s: %w", path, err)
	}
	return &inst, nil
}
