//go:build !windows

package client

import "testing"

// TestParseProcStat pins the field split that makes the zombie check work: the name is in
// parentheses and may contain both spaces and parentheses, and the state is the byte after the
// last ')'.
func TestParseProcStat(t *testing.T) {
	tests := []struct {
		name   string
		line   string
		want   ProcessEntry
		wantOK bool
	}{
		{"running editor", "4242 (Unity) S 1 4242 4242 0 -1", ProcessEntry{Name: "Unity"}, true},
		{"zombie editor", "4242 (Unity) Z 1 4242 4242 0 -1", ProcessEntry{Name: "Unity", Zombie: true}, true},
		{"name with spaces and parens", "7 (a (b) c) R 1 7 7 0 -1", ProcessEntry{Name: "a (b) c"}, true},
		{"truncated line", "4242 (Unity", ProcessEntry{}, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, ok := parseProcStat(tt.line)
			if ok != tt.wantOK || got != tt.want {
				t.Errorf("parseProcStat(%q) = %+v, %v; want %+v, %v", tt.line, got, ok, tt.want, tt.wantOK)
			}
		})
	}
}

// TestParsePSLine pins the macOS fallback parser: ps indents its rows, comm is a full path that may
// contain spaces, and the stat column's Z is the zombie marker.
func TestParsePSLine(t *testing.T) {
	tests := []struct {
		name   string
		line   string
		pid    int
		want   ProcessEntry
		wantOK bool
	}{
		{"indented macOS row", "  4242 S    /Applications/Unity/Hub/Editor/6000.4.4f1/Unity.app/Contents/MacOS/Unity",
			4242, ProcessEntry{Name: "/Applications/Unity/Hub/Editor/6000.4.4f1/Unity.app/Contents/MacOS/Unity"}, true},
		{"zombie editor", "  4242 Z    /Applications/Unity/Hub/Editor/6000.4.4f1/Unity.app/Contents/MacOS/Unity",
			4242, ProcessEntry{Name: "/Applications/Unity/Hub/Editor/6000.4.4f1/Unity.app/Contents/MacOS/Unity", Zombie: true}, true},
		{"path with spaces", "  7 S      /Applications/My Tools/Unity", 7, ProcessEntry{Name: "/Applications/My Tools/Unity"}, true},
		{"empty line", "", 0, ProcessEntry{}, false},
		{"missing comm", "  7 S", 0, ProcessEntry{}, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			pid, entry, ok := parsePSLine(tt.line)
			if ok != tt.wantOK || pid != tt.pid || entry != tt.want {
				t.Errorf("parsePSLine(%q) = %d, %+v, %v; want %d, %+v, %v", tt.line, pid, entry, ok, tt.pid, tt.want, tt.wantOK)
			}
		})
	}
}
