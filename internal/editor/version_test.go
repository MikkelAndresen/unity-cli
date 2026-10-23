package editor

import (
	"os"
	"path/filepath"
	"testing"
)

func writeProjectVersion(t *testing.T, root, content string) {
	t.Helper()
	dir := filepath.Join(root, "ProjectSettings")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatalf("failed to create ProjectSettings: %v", err)
	}
	if err := os.WriteFile(filepath.Join(dir, "ProjectVersion.txt"), []byte(content), 0o644); err != nil {
		t.Fatalf("failed to write ProjectVersion.txt: %v", err)
	}
}

func TestReadProjectVersion(t *testing.T) {
	tests := []struct {
		name    string
		content string
		want    string
	}{
		{
			name: "editor version line",
			content: "m_EditorVersion: 6000.4.4f1\n" +
				"m_EditorVersionWithRevision: 6000.4.4f1 (0c5f9b2b1e0e)\n",
			want: "6000.4.4f1",
		},
		{
			name:    "crlf line endings",
			content: "m_EditorVersion: 2022.3.10f1\r\nm_EditorVersionWithRevision: 2022.3.10f1 (ff3792e53c62)\r\n",
			want:    "2022.3.10f1",
		},
		{
			name:    "revision fallback",
			content: "m_EditorVersionWithRevision: 6000.4.4f1 (0c5f9b2b1e0e)\n",
			want:    "6000.4.4f1",
		},
		{
			name:    "utf8 bom",
			content: "\ufeffm_EditorVersion: 6000.4.4f1\n",
			want:    "6000.4.4f1",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			root := t.TempDir()
			writeProjectVersion(t, root, tt.content)

			got, err := ReadProjectVersion(root)
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if got != tt.want {
				t.Fatalf("version: got %q, want %q", got, tt.want)
			}
		})
	}
}

func TestReadProjectVersion_Errors(t *testing.T) {
	t.Run("missing file", func(t *testing.T) {
		if _, err := ReadProjectVersion(t.TempDir()); err == nil {
			t.Fatal("expected error for project without ProjectVersion.txt")
		}
	})

	t.Run("empty project path", func(t *testing.T) {
		if _, err := ReadProjectVersion(""); err == nil {
			t.Fatal("expected error for empty project path")
		}
	})

	t.Run("no version entry", func(t *testing.T) {
		root := t.TempDir()
		writeProjectVersion(t, root, "unrelated: value\n")
		if _, err := ReadProjectVersion(root); err == nil {
			t.Fatal("expected error for ProjectVersion.txt without a version")
		}
	})
}
