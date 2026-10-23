package editor

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const testVersion = "6000.4.4f1"

func writeTestFile(t *testing.T, path string) string {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatalf("failed to create dir for %s: %v", path, err)
	}
	if err := os.WriteFile(path, []byte("#!/bin/sh\n"), 0o755); err != nil {
		t.Fatalf("failed to write %s: %v", path, err)
	}
	return path
}

func fakeResolveEnv(goos, homeDir string, vars map[string]string) resolveEnv {
	return resolveEnv{
		goos:    goos,
		homeDir: homeDir,
		getenv: func(key string) string {
			return vars[key]
		},
		isFile: isRegularFile,
	}
}

func TestFindEditorExecutable_OverrideWins(t *testing.T) {
	home := t.TempDir()
	override := writeTestFile(t, filepath.Join(home, "custom", "Unity"))
	fromEnv := writeTestFile(t, filepath.Join(home, "env", "Unity"))
	writeTestFile(t, filepath.Join(home, "Unity", "Hub", "Editor", testVersion, "Editor", "Unity"))

	env := fakeResolveEnv("linux", home, map[string]string{"UNITY_EDITOR": fromEnv})
	got, err := findEditorExecutable(testVersion, override, env)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != override {
		t.Fatalf("path: got %q, want override %q", got, override)
	}
}

func TestFindEditorExecutable_EnvBeforeHub(t *testing.T) {
	home := t.TempDir()
	fromEnv := writeTestFile(t, filepath.Join(home, "env", "Unity"))
	writeTestFile(t, filepath.Join(home, "Unity", "Hub", "Editor", testVersion, "Editor", "Unity"))

	env := fakeResolveEnv("linux", home, map[string]string{"UNITY_EDITOR": fromEnv})
	got, err := findEditorExecutable(testVersion, "", env)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != fromEnv {
		t.Fatalf("path: got %q, want $UNITY_EDITOR %q", got, fromEnv)
	}
}

func TestFindEditorExecutable_LinuxHubDefault(t *testing.T) {
	home := t.TempDir()
	want := writeTestFile(t, filepath.Join(home, "Unity", "Hub", "Editor", testVersion, "Editor", "Unity"))

	got, err := findEditorExecutable(testVersion, "", fakeResolveEnv("linux", home, nil))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != want {
		t.Fatalf("path: got %q, want %q", got, want)
	}
}

func TestFindEditorExecutable_WindowsProgramFiles(t *testing.T) {
	home := t.TempDir()
	programFiles := filepath.Join(home, "Program Files")
	want := writeTestFile(t, filepath.Join(programFiles, "Unity", "Hub", "Editor", testVersion, "Editor", "Unity.exe"))

	env := fakeResolveEnv("windows", home, map[string]string{"ProgramFiles": programFiles})
	got, err := findEditorExecutable(testVersion, "", env)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != want {
		t.Fatalf("path: got %q, want %q", got, want)
	}
}

func TestFindEditorExecutable_DarwinSecondaryInstallPath(t *testing.T) {
	home := t.TempDir()
	customRoot := filepath.Join(home, "External", "UnityHub")
	want := writeTestFile(t, filepath.Join(customRoot, testVersion,
		"Unity.app", "Contents", "MacOS", "Unity"))

	configPath := filepath.Join(home, "Library", "Application Support", "UnityHub", "secondaryInstallPath.json")
	if err := os.MkdirAll(filepath.Dir(configPath), 0o755); err != nil {
		t.Fatalf("failed to create Hub config dir: %v", err)
	}
	data, err := json.Marshal(customRoot)
	if err != nil {
		t.Fatalf("failed to marshal config: %v", err)
	}
	if err := os.WriteFile(configPath, data, 0o644); err != nil {
		t.Fatalf("failed to write Hub config: %v", err)
	}

	got, err := findEditorExecutable(testVersion, "", fakeResolveEnv("darwin", home, nil))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != want {
		t.Fatalf("path: got %q, want %q", got, want)
	}
}

func TestSecondaryInstallPath_ObjectForm(t *testing.T) {
	home := t.TempDir()
	configPath := filepath.Join(home, "config.json")
	if err := os.WriteFile(configPath, []byte(`{"path":"/opt/unity"}`), 0o644); err != nil {
		t.Fatalf("failed to write config: %v", err)
	}

	got := secondaryInstallPath(configPath)
	if got != "/opt/unity" {
		t.Fatalf("path: got %q, want %q", got, "/opt/unity")
	}
}

func TestFindEditorExecutable_NotFoundListsCandidates(t *testing.T) {
	home := t.TempDir()
	override := filepath.Join(home, "missing", "Unity")
	fromEnv := filepath.Join(home, "env", "Unity")

	env := fakeResolveEnv("linux", home, map[string]string{"UNITY_EDITOR": fromEnv})
	_, err := findEditorExecutable(testVersion, override, env)
	if err == nil {
		t.Fatal("expected error when no candidate exists")
	}

	msg := err.Error()
	for _, want := range []string{
		override,
		fromEnv,
		"$UNITY_EDITOR",
		"--editor override",
		filepath.Join(home, "Unity", "Hub", "Editor", testVersion, "Editor", "Unity"),
		"UNITY_EDITOR",
	} {
		if !strings.Contains(msg, want) {
			t.Errorf("error message missing %q:\n%s", want, msg)
		}
	}
}

func TestEditorExecutablePath(t *testing.T) {
	tests := []struct {
		goos string
		root string
		want string
	}{
		{
			goos: "darwin",
			root: "/Applications/Unity/Hub/Editor",
			want: "/Applications/Unity/Hub/Editor/6000.4.4f1/Unity.app/Contents/MacOS/Unity",
		},
		{
			goos: "windows",
			root: `C:\Program Files\Unity\Hub\Editor`,
			want: "C:/Program Files/Unity/Hub/Editor/6000.4.4f1/Editor/Unity.exe",
		},
		{
			goos: "linux",
			root: "/home/user/Unity/Hub/Editor/",
			want: "/home/user/Unity/Hub/Editor/6000.4.4f1/Editor/Unity",
		},
	}

	for _, tt := range tests {
		t.Run(tt.goos, func(t *testing.T) {
			if got := editorExecutablePath(tt.root, testVersion, tt.goos); got != tt.want {
				t.Fatalf("path: got %q, want %q", got, tt.want)
			}
		})
	}
}
