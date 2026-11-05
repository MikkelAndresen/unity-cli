package cmd

import (
	"bytes"
	"os"
	"strings"
	"testing"
)

// The tests here guard wording the CLI promises about itself. They read the printed help and the
// README, because the findings they cover were help text that contradicted the behaviour.

func topLevelHelp(t *testing.T) string {
	t.Helper()
	var buf bytes.Buffer
	printHelpTo(&buf)
	return buf.String()
}

// TestExecHelpStatesTheAutoReturnRule: exec "Time.time" (the top-level example) only works because a
// bare expression is returned, and a statement body still needs 'return' (F15).
func TestExecHelpStatesTheAutoReturnRule(t *testing.T) {
	for _, want := range []string{`exec "Time.time"`, "single expression"} {
		if !strings.Contains(topLevelHelp(t), want) {
			t.Errorf("top-level help no longer contains %q", want)
		}
	}
	for _, want := range []string{"single expression", "'return'"} {
		if !strings.Contains(topicHelpText(t, "exec"), want) {
			t.Errorf("exec --help no longer mentions %q", want)
		}
	}
}

// TestMetaHelpExplainsTheTimingFields: `--meta`'s queue_wait_ms stops when the handler starts and
// total_ms is the whole editor-side span, so the help has to name both and say neither is the wall
// clock (F20).
func TestMetaHelpExplainsTheTimingFields(t *testing.T) {
	help := topLevelHelp(t)
	for _, want := range []string{"compile_ms", "queue_wait_ms", "handler start", "total_ms", "wall-clock total"} {
		if !strings.Contains(help, want) {
			t.Errorf("--meta help does not explain %q", want)
		}
	}
}

// TestWaitHelpSaysAnUnprovableCompileFails: `wait --compile` exits 1 when it cannot prove the edit was
// compiled, and the exit-code list has to say so (F1).
func TestWaitHelpSaysAnUnprovableCompileFails(t *testing.T) {
	help := topicHelpText(t, "wait")
	for _, want := range []string{"--compile could not be proven", "unity-cli verify"} {
		if !strings.Contains(help, want) {
			t.Errorf("wait --help does not mention %q", want)
		}
	}
}

// TestDocsAgreeOnTheTestFilter is the anti-drift check for F14: the connector matches --filter against
// the full test name (RunTests.BuildFilter hands it to NUnit as a test name and a group name), so the
// help must not call it a namespace or substring match.
func TestDocsAgreeOnTheTestFilter(t *testing.T) {
	help := topicHelpText(t, "test")
	for _, stale := range []string{
		"Filter by namespace, class, or full test name",
		"Must be the full path",
		"substring",
	} {
		if strings.Contains(help, stale) {
			t.Errorf("test --help still contains the contradictory wording %q", stale)
		}
	}
	if !strings.Contains(help, "full name matches exactly") {
		t.Error("test --help does not say the filter is matched against the full name")
	}

	for _, path := range []string{"../README.md", "../README.ko.md"} {
		data, err := os.ReadFile(path)
		if err != nil {
			t.Fatalf("read %s: %v", path, err)
		}
		readme := string(data)
		for _, stale := range []string{"Filter by test name (substring match)", "테스트 이름으로 필터링 (부분 문자열 매치)"} {
			if strings.Contains(readme, stale) {
				t.Errorf("%s still describes --filter as %q", path, stale)
			}
		}
		if !strings.Contains(readme, "no tests matched (filter") {
			t.Errorf("%s does not say a run that selects no test fails", path)
		}
	}
}
