package editor

import (
	"crypto/md5"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"testing"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// setTestHome isolates ~/.unity-cli from the real user profile.
func setTestHome(t *testing.T) string {
	t.Helper()
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	return home
}

func writeHeartbeat(t *testing.T, projectPath string, inst client.Instance) string {
	t.Helper()
	path, err := HeartbeatFilePath(projectPath)
	if err != nil {
		t.Fatalf("HeartbeatFilePath: %v", err)
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatalf("failed to create heartbeat dir: %v", err)
	}
	data, err := json.Marshal(inst)
	if err != nil {
		t.Fatalf("failed to marshal heartbeat: %v", err)
	}
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatalf("failed to write heartbeat: %v", err)
	}
	return path
}

func TestHeartbeatFilePath_MatchesConnectorNaming(t *testing.T) {
	home := setTestHome(t)
	project := filepath.Join(home, "Work", "FoW")

	wantSum := md5.Sum([]byte(filepath.ToSlash(project)))
	want := filepath.Join(home, ".unity-cli", "instances", hex.EncodeToString(wantSum[:])[:16]+".json")

	got, err := HeartbeatFilePath(project)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != want {
		t.Fatalf("path: got %q, want %q", got, want)
	}
}

func TestHeartbeatFilePath_NormalizesSeparatorsAndTrailingSlash(t *testing.T) {
	setTestHome(t)

	base, err := HeartbeatFilePath("/work/FoW")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	for _, variant := range []string{
		"/work/FoW/",
		`\work\FoW`,
		`\work\FoW\`,
	} {
		got, err := HeartbeatFilePath(variant)
		if err != nil {
			t.Fatalf("HeartbeatFilePath(%q): %v", variant, err)
		}
		if got != base {
			t.Errorf("HeartbeatFilePath(%q) = %q, want %q", variant, got, base)
		}
	}
}

func TestHeartbeatFilePath_RejectsEmptyPath(t *testing.T) {
	setTestHome(t)
	if _, err := HeartbeatFilePath(""); err == nil {
		t.Fatal("expected error for empty project path")
	}
	if _, err := HeartbeatFilePath("/"); err == nil {
		t.Fatal("expected error for root path")
	}
}

func TestReadHeartbeat_DoesNotDeleteStoppedInstance(t *testing.T) {
	setTestHome(t)
	project := "/work/FoW"
	path := writeHeartbeat(t, project, client.Instance{
		State:       "stopped",
		ProjectPath: project,
		PID:         999999,
		Timestamp:   1234,
	})

	got, err := ReadHeartbeat(project)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.State != "stopped" {
		t.Errorf("state: got %q, want %q", got.State, "stopped")
	}
	if got.ProjectPath != project {
		t.Errorf("project path: got %q, want %q", got.ProjectPath, project)
	}
	if _, err := os.Stat(path); err != nil {
		t.Errorf("heartbeat file should not be deleted: %v", err)
	}
}

func TestReadHeartbeat_Missing(t *testing.T) {
	setTestHome(t)
	_, err := ReadHeartbeat("/work/does-not-exist")
	if !errors.Is(err, fs.ErrNotExist) {
		t.Fatalf("error should wrap fs.ErrNotExist, got %v", err)
	}
}

func TestReadHeartbeat_Malformed(t *testing.T) {
	setTestHome(t)
	project := "/work/FoW"
	path, err := HeartbeatFilePath(project)
	if err != nil {
		t.Fatalf("HeartbeatFilePath: %v", err)
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatalf("failed to create heartbeat dir: %v", err)
	}
	if err := os.WriteFile(path, []byte("{not json"), 0o644); err != nil {
		t.Fatalf("failed to write heartbeat: %v", err)
	}

	if _, err := ReadHeartbeat(project); err == nil {
		t.Fatal("expected error for malformed heartbeat")
	}
}
