package cmd

import (
	"encoding/json"
	"fmt"
	"io"
	"strings"
)

// compileProvenance mirrors the connector's provenance payload (unity-connector
// Editor/Core/CompileProvenance.cs). Renaming a key there and not here drops the field silently,
// which is why the connector also sends the ready-made lines: they are what the CLI prints.
type compileProvenance struct {
	CompiledAtMs         int64    `json:"compiled_at_ms"`
	CompiledAt           string   `json:"compiled_at,omitempty"`
	CompileStartedAtMs   int64    `json:"compile_started_at_ms,omitempty"`
	DurationMs           int64    `json:"duration_ms,omitempty"`
	AgeSeconds           float64  `json:"age_seconds,omitempty"`
	RebuiltAssemblies    []string `json:"rebuilt_assemblies,omitempty"`
	RebuiltTruncated     bool     `json:"rebuilt_truncated,omitempty"`
	ChangedSincePrevious *int     `json:"changed_since_previous_compile"`
	StaleCount           int      `json:"stale_count,omitempty"`
	StaleFiles           []string `json:"stale_files,omitempty"`
	StaleTruncated       bool     `json:"stale_truncated,omitempty"`
	ScannedRoots         []string `json:"scanned_roots,omitempty"`
	ScannedFiles         int      `json:"scanned_files,omitempty"`
	ScanMs               int      `json:"scan_ms,omitempty"`
	ScanComplete         bool     `json:"scan_complete,omitempty"`
	ScanNote             string   `json:"scan_note,omitempty"`
	ProvenanceLine       string   `json:"provenance_line,omitempty"`
	StaleLine            string   `json:"stale_line,omitempty"`
}

// provenanceIn reads the provenance object a compile_diagnostics reply nests under "provenance".
// It returns it parsed (for the lines the CLI prints) and verbatim (for the JSON reports, which then
// pass through fields this CLI version does not model and keep the values the connector chose).
// The second return is nil when the key was absent, which tells an older connector apart from a
// connector with nothing to report.
func provenanceIn(data json.RawMessage) (*compileProvenance, json.RawMessage) {
	if len(data) == 0 || string(data) == "null" {
		return nil, nil
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(data, &fields); err != nil {
		return nil, nil
	}
	raw, ok := fields["provenance"]
	if !ok || len(raw) == 0 || string(raw) == "null" {
		return nil, nil
	}
	var p compileProvenance
	if err := json.Unmarshal(raw, &p); err != nil {
		return nil, raw
	}
	return &p, raw
}

// fetchProvenance calls the connector's compile_status, whose reply is the same object at the top
// level. nil means the connector predates the tool (or did not answer): the caller says so instead
// of claiming the compile was current.
func fetchProvenance(send sendFn) (*compileProvenance, json.RawMessage) {
	resp, err := send("compile_status", map[string]interface{}{})
	if err != nil || resp == nil || !resp.Success || len(resp.Data) == 0 {
		return nil, nil
	}
	if strings.Contains(resp.Message, "Unknown command") {
		return nil, nil
	}
	var p compileProvenance
	if err := json.Unmarshal(resp.Data, &p); err != nil {
		return nil, resp.Data
	}
	return &p, resp.Data
}

// compileFindings is what one compile_diagnostics round trip gives the CLI: this compile's errors,
// plus the provenance object the connector nests in the same reply, parsed for printing and verbatim
// for the JSON reports.
type compileFindings struct {
	Diags          []verifyDiag
	Total          int
	Source         string // "console" when the connector predates compile_diagnostics
	Provenance     *compileProvenance
	ProvenanceJSON json.RawMessage
}

// printProvenance writes the one line (plus the stale line when there is one) that tells the reader
// which edit this result reflects.
func printProvenance(out io.Writer, p *compileProvenance) {
	if p == nil {
		return
	}
	if p.ProvenanceLine != "" {
		_, _ = fmt.Fprintln(out, p.ProvenanceLine)
	}
	if p.StaleLine != "" {
		_, _ = fmt.Fprintln(out, p.StaleLine)
	}
	if p.ScanNote != "" {
		_, _ = fmt.Fprintf(out, "  (%s)\n", p.ScanNote)
	}
}
