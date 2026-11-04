package editor

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"runtime"
	"strings"
)

// resolveEnv holds the platform inputs used to locate a Unity installation.
// Production callers go through FindEditorExecutable; tests construct it with a
// fake GOOS/home so the Hub layouts can be exercised without touching the host.
type resolveEnv struct {
	goos    string
	homeDir string
	getenv  func(string) string
	isFile  func(string) bool
}

// FindEditorExecutable locates the Unity editor binary for version.
//
// Resolution order: explicit override, $UNITY_EDITOR, then Unity Hub's default
// install location for the current OS (plus a Hub secondary install path when
// it can be read cheaply). version is the ProjectVersion.txt value, e.g.
// "6000.4.4f1".
func FindEditorExecutable(version, override string) (string, error) {
	homeDir, _ := os.UserHomeDir()
	return findEditorExecutable(version, override, resolveEnv{
		goos:    runtime.GOOS,
		homeDir: homeDir,
		getenv:  os.Getenv,
		isFile:  isRegularFile,
	})
}

func findEditorExecutable(version, override string, env resolveEnv) (string, error) {
	type candidate struct {
		source string
		path   string
	}

	var candidates []candidate
	add := func(source, path string) {
		if path == "" {
			return
		}
		for _, existing := range candidates {
			if existing.path == path {
				return
			}
		}
		candidates = append(candidates, candidate{source: source, path: path})
	}

	add("--editor override", override)
	if fromEnv := env.getenv("UNITY_EDITOR"); fromEnv != "" {
		add("$UNITY_EDITOR", fromEnv)
	}
	if version != "" {
		for _, root := range hubInstallRoots(env) {
			add("Unity Hub", editorExecutablePath(root, version, env.goos))
		}
	}

	for _, c := range candidates {
		if env.isFile(c.path) {
			return c.path, nil
		}
	}

	var b strings.Builder
	fmt.Fprintf(&b, "unity editor executable not found for version %q; tried:", version)
	if len(candidates) == 0 {
		b.WriteString("\n  (no candidates: version, --editor override and $UNITY_EDITOR were all empty)")
	}
	for _, c := range candidates {
		fmt.Fprintf(&b, "\n  %s: %s", c.source, c.path)
	}
	b.WriteString("\npass --editor <path> or set $UNITY_EDITOR to the Unity executable")
	return "", errors.New(b.String())
}

// hubInstallRoots lists directories whose <root>/<version>/... layout mirrors
// the default Unity Hub Editor directory.
func hubInstallRoots(env resolveEnv) []string {
	var roots []string
	switch env.goos {
	case "darwin":
		roots = append(roots, "/Applications/Unity/Hub/Editor")
		roots = append(roots, secondaryInstallPath(
			env.homeDir+"/Library/Application Support/UnityHub/secondaryInstallPath.json"))
	case "windows":
		programFiles := env.getenv("ProgramFiles")
		if programFiles == "" {
			programFiles = `C:\Program Files`
		}
		roots = append(roots, programFiles+`\Unity\Hub\Editor`)

		appData := env.getenv("APPDATA")
		if appData == "" {
			appData = env.homeDir + `\AppData\Roaming`
		}
		roots = append(roots, secondaryInstallPath(appData+`\UnityHub\secondaryInstallPath.json`))
	case "linux":
		roots = append(roots, env.homeDir+"/Unity/Hub/Editor")
		roots = append(roots, secondaryInstallPath(
			env.homeDir+"/.config/UnityHub/secondaryInstallPath.json"))
	}

	deduped := roots[:0]
	for _, root := range roots {
		root = strings.TrimRight(strings.ReplaceAll(root, `\`, "/"), "/")
		if root == "" {
			continue
		}
		duplicate := false
		for _, existing := range deduped {
			if existing == root {
				duplicate = true
				break
			}
		}
		if !duplicate {
			deduped = append(deduped, root)
		}
	}
	return deduped
}

// secondaryInstallPath reads Unity Hub's alternate install root. Hub stores the
// path either as a bare JSON string or, in some versions, as an object. Any
// unreadable or unexpected file is treated as "no custom location".
func secondaryInstallPath(configPath string) string {
	data, err := os.ReadFile(configPath)
	if err != nil {
		return ""
	}

	var asString string
	if err := json.Unmarshal(data, &asString); err == nil {
		return strings.TrimSpace(asString)
	}

	var asObject struct {
		Path string `json:"path"`
	}
	if err := json.Unmarshal(data, &asObject); err == nil {
		return strings.TrimSpace(asObject.Path)
	}
	return ""
}

// editorExecutablePath builds the binary path for a Hub install root. Paths use
// forward slashes on every OS, which Windows accepts; keeping the helper free of
// host filepath semantics lets tests simulate darwin/windows from Linux.
func editorExecutablePath(root, version, goos string) string {
	base := strings.TrimRight(strings.ReplaceAll(root, `\`, "/"), "/") + "/" + version
	switch goos {
	case "darwin":
		return base + "/Unity.app/Contents/MacOS/Unity"
	case "windows":
		return base + "/Editor/Unity.exe"
	default:
		return base + "/Editor/Unity"
	}
}

func isRegularFile(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.Mode().IsRegular()
}
