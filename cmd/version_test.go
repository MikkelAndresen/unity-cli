package cmd

import (
	"io"
	"os"
	"strings"
	"testing"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func captureStdout(t *testing.T, fn func()) string {
	t.Helper()
	return captureFile(t, &os.Stdout, fn)
}

func captureStderr(t *testing.T, fn func()) string {
	t.Helper()
	return captureFile(t, &os.Stderr, fn)
}

func captureFile(t *testing.T, target **os.File, fn func()) string {
	t.Helper()
	original := *target
	r, w, err := os.Pipe()
	if err != nil {
		t.Fatalf("pipe: %v", err)
	}
	*target = w
	defer func() { *target = original }()

	fn()

	if err := w.Close(); err != nil {
		t.Fatalf("close pipe: %v", err)
	}
	out, err := io.ReadAll(r)
	if err != nil {
		t.Fatalf("read pipe: %v", err)
	}
	if err := r.Close(); err != nil {
		t.Fatalf("close read pipe: %v", err)
	}
	return string(out)
}

func TestNormalizeVersion(t *testing.T) {
	tests := []struct {
		in   string
		want string
	}{
		{"", ""},
		{"dev", "dev"},
		{"0.7.1", "0.7.1"},
		{"  v0.7.1 ", "0.7.1"},
		{"V0.7.0", "0.7.0"},
		{"vv0.7.0", "v0.7.0"},
	}
	for _, tt := range tests {
		if got := normalizeVersion(tt.in); got != tt.want {
			t.Errorf("normalizeVersion(%q) = %q, want %q", tt.in, got, tt.want)
		}
	}
}

func TestParseVersion(t *testing.T) {
	tests := []struct {
		in   string
		want semanticVersion
		ok   bool
	}{
		{"0.7.1", semanticVersion{0, 7, 1}, true},
		{"v1.2.3", semanticVersion{1, 2, 3}, true},
		{"0.7", semanticVersion{}, false},
		{"0.7.1.2", semanticVersion{}, false},
		{"banana", semanticVersion{}, false},
		{"0.7.x", semanticVersion{}, false},
	}
	for _, tt := range tests {
		got, ok := parseVersion(tt.in)
		if ok != tt.ok || got != tt.want {
			t.Errorf("parseVersion(%q) = %+v, %v; want %+v, %v", tt.in, got, ok, tt.want, tt.ok)
		}
	}
}

func TestAssessConnectorVersion(t *testing.T) {
	tests := []struct {
		name            string
		cli             string
		connector       string
		compatible      bool
		refusal         bool
		warning         bool
		verdictContains string
	}{
		{"development CLI is not checked", "dev", "0.1.0", true, false, false, "development CLI"},
		{"exact match", "0.7.1", "0.7.1", true, false, false, "compatible"},
		{"leading v on both sides", "v0.7.1", "v0.7.1", true, false, false, "compatible"},
		{"patch drift is silent", "0.7.1", "0.7.0", true, false, false, "same major.minor"},
		{"connector patch ahead is silent", "0.7.0", "0.7.9", true, false, false, "same major.minor"},
		{"minor drift with a newer CLI", "0.8.0", "0.7.0", true, false, true, "minor"},
		{"minor drift with a newer connector", "0.7.1", "0.8.0", true, false, true, "minor"},
		{"minor drift after 1.0", "1.2.0", "1.3.0", true, false, true, "minor"},
		{"major drift", "0.9.0", "1.0.0", true, false, true, "major"},
		{"connector below minimum", "0.8.0", "0.6.9", false, true, false, "older than the minimum"},
		{"minimum connector is accepted", "0.7.1", "0.7.0", true, false, false, "compatible"},
		{"unknown connector version", "0.8.0", "", true, false, true, "unknown"},
		{"unparseable connector version", "0.8.0", "banana", true, false, true, "unrecognised"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := assessConnectorVersion(&client.Instance{ConnectorVersion: tt.connector}, tt.cli)
			// A compatible verdict is exactly "no refusal"; there is no separate flag to drift from it.
			if compatible := got.Refusal == nil; compatible != tt.compatible {
				t.Errorf("compatible = %v, want %v (verdict %q)", compatible, tt.compatible, got.Verdict)
			}
			if (got.Refusal != nil) != tt.refusal {
				t.Errorf("Refusal = %v, want %v", got.Refusal, tt.refusal)
			}
			if (got.Warning != "") != tt.warning {
				t.Errorf("Warning = %q, want present=%v", got.Warning, tt.warning)
			}
			if !strings.Contains(got.Verdict, tt.verdictContains) {
				t.Errorf("Verdict = %q, want it to contain %q", got.Verdict, tt.verdictContains)
			}
			if got.Warning != "" {
				if strings.Contains(got.Warning, "\n") {
					t.Errorf("warning must be one line, got %q", got.Warning)
				}
				for _, want := range []string{"unity-cli " + got.CLI, "connector " + got.Connector, "unity-cli update", "--ignore-version-mismatch"} {
					if got.Connector == "" && want == "connector " {
						continue
					}
					if !strings.Contains(got.Warning, want) {
						t.Errorf("warning %q does not contain %q", got.Warning, want)
					}
				}
			}
		})
	}
}

// TestVersionGate_ExemptCommandsStillAnswer exercises the behaviour of every exempt entry rather
// than restating the map: each one must keep answering a connector that the minimum floor refuses.
func TestVersionGate_ExemptCommandsStillAnswer(t *testing.T) {
	origVersion := Version
	Version = "0.8.0"
	t.Cleanup(func() { Version = origVersion })

	for command := range versionExemptCommands {
		t.Run(command, func(t *testing.T) {
			if err := newVersionGate(command).check(&client.Instance{ConnectorVersion: "0.1.0"}); err != nil {
				t.Fatalf("%q must not be refused, got %v", command, err)
			}
		})
	}
}

// TestVersionGate_StateChangingCommandsAreRefused is the other half of the rule: the commands that
// drive the editor are gated, so a connector below the minimum cannot be driven blind.
func TestVersionGate_StateChangingCommandsAreRefused(t *testing.T) {
	origVersion := Version
	Version = "0.8.0"
	t.Cleanup(func() { Version = origVersion })

	for _, command := range []string{"exec", "editor", "test", "build", "verify"} {
		t.Run(command, func(t *testing.T) {
			if err := newVersionGate(command).check(&client.Instance{ConnectorVersion: "0.1.0"}); err == nil {
				t.Fatalf("%q must be refused for a connector below the minimum", command)
			}
		})
	}
}

func TestVersionGate_SameMinorIsSilent(t *testing.T) {
	origVersion := Version
	Version = "0.7.1"
	t.Cleanup(func() { Version = origVersion })

	g := newVersionGate("exec")
	errOut := captureStderr(t, func() {
		if err := g.check(&client.Instance{ConnectorVersion: "0.7.0"}); err != nil {
			t.Fatalf("patch drift must not fail: %v", err)
		}
	})
	if errOut != "" {
		t.Fatalf("patch drift must be silent, got %q", errOut)
	}
}

func TestVersionGate_MinorDriftWarnsOnce(t *testing.T) {
	origVersion := Version
	Version = "0.8.0"
	t.Cleanup(func() { Version = origVersion })

	g := newVersionGate("exec")
	inst := &client.Instance{ConnectorVersion: "0.7.0"}
	errOut := captureStderr(t, func() {
		for i := 0; i < 3; i++ {
			if err := g.check(inst); err != nil {
				t.Fatalf("minor drift must warn, not fail: %v", err)
			}
		}
	})
	if lines := strings.Count(errOut, "\n"); lines != 1 {
		t.Fatalf("warning must be printed once, got %d lines: %q", lines, errOut)
	}
	if !strings.Contains(errOut, "unity-cli 0.8.0") || !strings.Contains(errOut, "connector 0.7.0") {
		t.Fatalf("warning must name both versions, got %q", errOut)
	}
	if !strings.Contains(errOut, "unity-cli update") || !strings.Contains(errOut, "--ignore-version-mismatch") {
		t.Fatalf("warning must say how to fix or suppress it, got %q", errOut)
	}
}

func TestVersionGate_IgnoreSuppressesWarningAndRefusal(t *testing.T) {
	origVersion := Version
	origIgnore := flagIgnoreVersionMismatch
	Version = "0.8.0"
	flagIgnoreVersionMismatch = true
	t.Cleanup(func() {
		Version = origVersion
		flagIgnoreVersionMismatch = origIgnore
	})

	for _, connector := range []string{"0.7.0", "0.6.0"} {
		g := newVersionGate("exec")
		errOut := captureStderr(t, func() {
			if err := g.check(&client.Instance{ConnectorVersion: connector}); err != nil {
				t.Fatalf("--ignore-version-mismatch must suppress the refusal for %s: %v", connector, err)
			}
		})
		if errOut != "" {
			t.Fatalf("--ignore-version-mismatch must suppress the warning for %s, got %q", connector, errOut)
		}
	}
}
