package editor

import (
	"os"
	"path/filepath"
	"testing"
)

// makeProject creates a minimal Unity project layout with Library/ and Temp/.
func makeProject(t *testing.T) string {
	t.Helper()
	root := t.TempDir()
	for _, dir := range []string{"Assets", "ProjectSettings", "Library", "Temp"} {
		if err := os.MkdirAll(filepath.Join(root, dir), 0o755); err != nil {
			t.Fatalf("failed to create %s: %v", dir, err)
		}
	}
	if err := os.WriteFile(filepath.Join(root, "Library", "artifact.bin"), []byte("generated"), 0o644); err != nil {
		t.Fatalf("failed to seed Library: %v", err)
	}
	if err := os.WriteFile(filepath.Join(root, "Temp", "UnityLockfile"), []byte("locked"), 0o644); err != nil {
		t.Fatalf("failed to seed Temp: %v", err)
	}
	return root
}

func TestCleanProject_RemovesLibraryAndTemp(t *testing.T) {
	root := makeProject(t)

	removed, err := CleanProject(root)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	want := map[string]bool{
		filepath.Join(root, "Library"): false,
		filepath.Join(root, "Temp"):    false,
	}
	for _, path := range removed {
		if _, ok := want[path]; !ok {
			t.Errorf("unexpected removed path %q", path)
			continue
		}
		want[path] = true
	}
	for path, seen := range want {
		if !seen {
			t.Errorf("expected %q to be reported as removed", path)
		}
		if _, err := os.Stat(path); !os.IsNotExist(err) {
			t.Errorf("%q should have been deleted, stat err: %v", path, err)
		}
	}

	for _, keep := range []string{"Assets", "ProjectSettings"} {
		if _, err := os.Stat(filepath.Join(root, keep)); err != nil {
			t.Errorf("%q should be preserved: %v", keep, err)
		}
	}
}

func TestCleanProject_MissingDirectoriesAreFine(t *testing.T) {
	root := t.TempDir()
	if err := os.MkdirAll(filepath.Join(root, "Assets"), 0o755); err != nil {
		t.Fatalf("failed to create Assets: %v", err)
	}
	if err := os.MkdirAll(filepath.Join(root, "ProjectSettings"), 0o755); err != nil {
		t.Fatalf("failed to create ProjectSettings: %v", err)
	}

	removed, err := CleanProject(root)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(removed) != 0 {
		t.Fatalf("expected nothing removed, got %v", removed)
	}
}

func TestCleanProject_RefusesUnsafePaths(t *testing.T) {
	t.Run("relative path", func(t *testing.T) {
		if _, err := CleanProject(filepath.Join("relative", "project")); err == nil {
			t.Fatal("expected error for relative project path")
		}
	})

	t.Run("empty path", func(t *testing.T) {
		if _, err := CleanProject(""); err == nil {
			t.Fatal("expected error for empty project path")
		}
	})

	t.Run("missing Assets", func(t *testing.T) {
		root := t.TempDir()
		if err := os.MkdirAll(filepath.Join(root, "ProjectSettings"), 0o755); err != nil {
			t.Fatalf("failed to create ProjectSettings: %v", err)
		}
		library := filepath.Join(root, "Library")
		if err := os.MkdirAll(library, 0o755); err != nil {
			t.Fatalf("failed to create Library: %v", err)
		}

		if _, err := CleanProject(root); err == nil {
			t.Fatal("expected error for project without Assets")
		}
		if _, err := os.Stat(library); err != nil {
			t.Errorf("Library must not be touched on refusal: %v", err)
		}
	})

	t.Run("missing ProjectSettings", func(t *testing.T) {
		root := t.TempDir()
		if err := os.MkdirAll(filepath.Join(root, "Assets"), 0o755); err != nil {
			t.Fatalf("failed to create Assets: %v", err)
		}

		if _, err := CleanProject(root); err == nil {
			t.Fatal("expected error for project without ProjectSettings")
		}
	})

	t.Run("Assets is a file", func(t *testing.T) {
		root := t.TempDir()
		if err := os.WriteFile(filepath.Join(root, "Assets"), []byte("not a dir"), 0o644); err != nil {
			t.Fatalf("failed to create Assets file: %v", err)
		}
		if err := os.MkdirAll(filepath.Join(root, "ProjectSettings"), 0o755); err != nil {
			t.Fatalf("failed to create ProjectSettings: %v", err)
		}

		if _, err := CleanProject(root); err == nil {
			t.Fatal("expected error when Assets is not a directory")
		}
	})
}

func TestCleanProject_RefusesTargetThatIsAFile(t *testing.T) {
	root := t.TempDir()
	if err := os.MkdirAll(filepath.Join(root, "Assets"), 0o755); err != nil {
		t.Fatalf("failed to create Assets: %v", err)
	}
	if err := os.MkdirAll(filepath.Join(root, "ProjectSettings"), 0o755); err != nil {
		t.Fatalf("failed to create ProjectSettings: %v", err)
	}
	library := filepath.Join(root, "Library")
	if err := os.WriteFile(library, []byte("unexpected"), 0o644); err != nil {
		t.Fatalf("failed to create Library file: %v", err)
	}

	if _, err := CleanProject(root); err == nil {
		t.Fatal("expected error when Library is a file")
	}
	if _, err := os.Stat(library); err != nil {
		t.Errorf("Library file must be left alone: %v", err)
	}
}

func TestLockfileHeld(t *testing.T) {
	root := makeProject(t)

	held, err := LockfileHeld(root)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !held {
		t.Fatal("expected lockfile to be reported as held")
	}

	if err := os.Remove(filepath.Join(root, "Temp", "UnityLockfile")); err != nil {
		t.Fatalf("failed to remove lockfile: %v", err)
	}
	held, err = LockfileHeld(root)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if held {
		t.Fatal("expected lockfile to be reported as absent")
	}

	if _, err := LockfileHeld(""); err == nil {
		t.Fatal("expected error for empty project path")
	}
}
