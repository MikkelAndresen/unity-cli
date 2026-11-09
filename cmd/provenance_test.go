package cmd

import (
	"bytes"
	"strings"
	"testing"
)

// A DLL the last compile chose not to rewrite is neither clean nor a refusal: it gets its own line,
// and the stale assembly line (which verify prints itself, as part of its refusal) is not repeated.
func TestPrintProvenance_PrintsTheCoveredDllLineOnce(t *testing.T) {
	var out bytes.Buffer
	printProvenance(&out, &compileProvenance{
		ProvenanceLine:            provLine,
		StaleAssemblyLine:         "stale assembly: Game.dll is older than Assets/Game.cs — run unity-cli editor refresh --compile",
		StaleAssemblyCoveredLine:  "not stale: Game.Tests.dll is older than Assets/Tests.cs — unity-cli editor refresh --compile clean",
		StaleAssemblyCoveredCount: 1,
	}, true)

	lines := strings.Split(strings.TrimSpace(out.String()), "\n")
	if len(lines) != 2 {
		t.Fatalf("output = %q, want the provenance line plus the covered line", out.String())
	}
	if lines[0] != provLine {
		t.Errorf("first line = %q, want the provenance line", lines[0])
	}
	if !strings.HasPrefix(lines[1], "not stale:") {
		t.Errorf("second line = %q, want the coveredDll line", lines[1])
	}
	if strings.Contains(out.String(), "stale assembly:") {
		t.Errorf("output = %q, want the stale assembly line left to verify's refusal path", out.String())
	}
}

// verify's text output leaves the covered-DLL line out: it changes nothing verify does.
func TestPrintProvenance_CoveredLineCanBeLeftOut(t *testing.T) {
	var out bytes.Buffer
	printProvenance(&out, &compileProvenance{
		ProvenanceLine:            provLine,
		StaleAssemblyCoveredLine:  "not stale: Game.Tests.dll is older than Assets/Tests.cs",
		StaleAssemblyCoveredCount: 1,
	}, false)
	if got := strings.TrimSpace(out.String()); got != provLine {
		t.Errorf("output = %q, want only the provenance line", got)
	}
}
