package cmd

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"strconv"
	"strings"
	"time"
)

// Exit codes of `unity-cli verify`.
const (
	verifyOK      = 0
	verifyFailed  = 1 // compile errors or failing tests
	verifyNoReply = 2 // the editor could not answer: unreachable, timed out, blocked by a dialog
)

type verifyDiag struct {
	File     string `json:"file"`
	Line     int    `json:"line"`
	Col      int    `json:"col"`
	Severity string `json:"severity"`
	Code     string `json:"code"`
	Message  string `json:"message"`
	// Hint names the closest real members/types (or the replacement for an obsolete API) when the
	// code is one an agent can act on: CS0117/CS1061/CS0246/CS0234/CS0619/CS0618.
	Hint string `json:"hint,omitempty"`
}

type verifyResult struct {
	Compile struct {
		OK         bool            `json:"ok"`
		Total      int             `json:"total"`
		Errors     []verifyDiag    `json:"errors,omitempty"`
		Source     string          `json:"source,omitempty"` // "console" when the connector predates compile_diagnostics
		Provenance json.RawMessage `json:"provenance,omitempty"`
	} `json:"compile"`
	Burst *verifyBurst `json:"burst,omitempty"`
	Tests *struct {
		Total    int      `json:"total"`
		Passed   int      `json:"passed"`
		Failed   int      `json:"failed"`
		Skipped  int      `json:"skipped"`
		Failures []string `json:"failures,omitempty"`
	} `json:"tests,omitempty"`
	Notes []string `json:"notes,omitempty"`
	// Burst could not be checked at all (old connector answers, editor busy, HTTP timeout). The tool
	// being absent is not an error: it means the project has no Burst to ask.
	BurstError string `json:"burst_error,omitempty"`
	Error      string `json:"error,omitempty"`
	Exit       int    `json:"exit"`
}

// verifyBurst is the burst_status reply. Compiled/failed/... count the [BurstCompile] targets in the
// project's own assemblies; ErrorCount counts every Burst (BCxxxx) error the editor is holding.
type verifyBurst struct {
	Idle           bool         `json:"idle"`
	IdleKnown      bool         `json:"idle_known"`
	Eager          string       `json:"eager"`
	Method         string       `json:"method"`
	Version        string       `json:"burst_version"`
	WaitedMs       int          `json:"waited_ms"`
	WaitS          float64      `json:"wait_s"`
	TimedOut       bool         `json:"timed_out"`
	ProjectTargets int          `json:"project_targets"`
	PackageTargets int          `json:"package_targets"`
	Matched        int          `json:"matched"`
	Compiled       int          `json:"compiled"`
	Failed         int          `json:"failed"`
	NotCompiled    int          `json:"not_compiled"`
	Unknown        int          `json:"unknown"`
	ErrorCount     int          `json:"error_count"`
	Errors         []verifyDiag `json:"errors,omitempty"`
	Notes          []string     `json:"notes,omitempty"`
}

// burstWaitSeconds is verify's bounded wait for Burst to finish; the connector caps its own wait at
// the same value, so a bounded wait never turns into an unbounded one.
const defaultBurstWaitSeconds = 60

// verifyCmd checks that the project compiles and its tests pass, in one call and with output short
// enough for an agent's context. ctx carries the invocation's long default budget (30 minutes); port
// is where PlayMode results are polled.
func verifyCmd(ctx context.Context, args []string, send, testSend sendFn, resolve instanceResolver, port int, out io.Writer) int {
	flags, err := parseSubFlags(args)
	if err != nil {
		_, _ = fmt.Fprintln(out, err)
		return verifyFailed
	}
	_, noTests := flags["no-tests"]
	_, noBurst := flags["no-burst"]
	_, keepConsole := flags["keep-console"]
	_, allowStale := flags["allow-stale"]
	if !allowStale {
		_, allowStale = flags["allow_stale"]
	}
	_, asJSON := flags["json"]
	burstWait := defaultBurstWaitSeconds
	if v, ok := flags["burst-wait"]; ok {
		if n, err := strconv.Atoi(v); err == nil && n >= 0 {
			burstWait = n
		}
	}
	max := 20
	if v, ok := flags["max"]; ok {
		if n, err := strconv.Atoi(v); err == nil && n > 0 {
			max = n
		}
	}

	// The connector closes its connection across a domain reload; Go's HTTP client logs that as an
	// "Unsolicited response" line, which is noise in the brief output.
	original := log.Writer()
	log.SetOutput(&suppressWriter{w: original, suppress: "Unsolicited response received on idle HTTP channel"})
	defer log.SetOutput(original)

	var r verifyResult
	finish := func(code int) int {
		r.Exit = code
		if asJSON {
			b, _ := json.Marshal(r)
			_, _ = fmt.Fprintln(out, string(b))
		} else if r.Error != "" {
			_, _ = fmt.Fprintln(out, r.Error)
		}
		return code
	}
	noReply := func(format string, a ...interface{}) int {
		r.Error = fmt.Sprintf(format, a...)
		return finish(verifyNoReply)
	}

	// Burst errors stay in the console after their code changes and the compile_diagnostics
	// console fallback would keep reporting them; Burst logs a live one again on the compile below.
	if !keepConsole {
		if _, err := send("console", map[string]interface{}{"clear": true}); err != nil {
			return noReply("editor: %v", err)
		}
	}

	resp, err := send("refresh_unity", map[string]interface{}{"compile": "request"})
	if err != nil {
		return noReply("editor: %v", err)
	}
	if !resp.Success {
		return noReply("editor: %s", resp.Message)
	}
	hasErrors, timedOut, err := waitForReadyTo(resolve, io.Discard, "verify")
	if err != nil {
		return noReply("compile: %v", err)
	}
	if timedOut {
		return noReply("compile: still compiling after 5 minutes")
	}

	findings, err := verifyDiagnostics(send, max, hasErrors)
	if err != nil {
		return noReply("compile: %v", err)
	}
	r.Compile.Total, r.Compile.Errors, r.Compile.Source, r.Compile.Provenance =
		findings.Total, findings.Diags, findings.Source, findings.ProvenanceJSON
	r.Compile.OK = findings.Total == 0 && !hasErrors
	if !asJSON {
		printCompile(out, r.Compile.OK, findings.Total, findings.Diags, findings.Source, hasErrors)
		printProvenance(out, findings.Provenance)
	}
	if !r.Compile.OK {
		return finish(verifyFailed)
	}

	if findings.Provenance != nil && findings.Provenance.StaleAssemblyCount > 0 {
		line := findings.Provenance.StaleAssemblyLine
		if line == "" {
			line = fmt.Sprintf("%d project assembly(ies) are older than their sources", findings.Provenance.StaleAssemblyCount)
		}
		if !allowStale {
			if asJSON {
				r.Error = line + " — tests were not run; pass --allow-stale to run them anyway"
			} else {
				_, _ = fmt.Fprintln(out, line)
				_, _ = fmt.Fprintf(out, "tests: refused (%d stale assembly(s)) — run unity-cli editor refresh --compile, or pass --allow-stale to run them anyway\n",
					findings.Provenance.StaleAssemblyCount)
			}
			return finish(verifyFailed)
		}
		if !asJSON {
			_, _ = fmt.Fprintf(out, "warning: %s\n", line)
		}
	}

	// Burst compiles outside Unity's script compilation, so a clean C# compile says nothing about the
	// [BurstCompile] jobs; ask Burst itself. A missing tool means no Burst (or an old connector): say
	// nothing rather than claiming the project has no Burst work.
	if !noBurst {
		burst, found, err := verifyBurstStatus(send, burstWait, max)
		if err != nil {
			r.BurstError = err.Error()
			if !asJSON {
				_, _ = fmt.Fprintf(out, "burst: could not be checked (%s)\n", firstLine(err.Error()))
			}
		}
		if found && burst != nil {
			r.Burst = burst
			if !asJSON {
				printBurst(out, burst)
			}
			if burst.ErrorCount > 0 {
				return finish(verifyFailed)
			}
		}
	}

	if noTests {
		return finish(verifyOK)
	}

	testArgs := []string{"--auto-save-scenes"}
	for _, k := range []string{"mode", "filter", "assembly"} {
		if v, ok := flags[k]; ok {
			testArgs = append(testArgs, "--"+k, v)
		}
	}
	if allowStale {
		testArgs = append(testArgs, "--allow-stale")
	}
	tresp, err := testCmd(ctx, testArgs, testSend, port)
	if err != nil {
		return noReply("tests: %v", err)
	}
	var summary struct {
		Total    int      `json:"total"`
		Passed   int      `json:"passed"`
		Failed   int      `json:"failed"`
		Skipped  int      `json:"skipped"`
		Failures []string `json:"failures"`
		Blocked  string   `json:"blocked"`
	}
	if len(tresp.Data) > 0 {
		_ = json.Unmarshal(tresp.Data, &summary)
	}
	if !tresp.Success && summary.Failed == 0 {
		// Not a test failure: dirty untitled scene, compile gate, missing Test Framework, ...
		return noReply("tests: %s", tresp.Message)
	}
	r.Tests = &struct {
		Total    int      `json:"total"`
		Passed   int      `json:"passed"`
		Failed   int      `json:"failed"`
		Skipped  int      `json:"skipped"`
		Failures []string `json:"failures,omitempty"`
	}{summary.Total, summary.Passed, summary.Failed, summary.Skipped, firstLines(summary.Failures, max)}
	if _, ok := flags["assembly"]; ok {
		if inst, err := resolve(); err == nil && inst != nil && versionBefore(inst.ConnectorVersion, 0, 7) {
			r.Notes = append(r.Notes, "connector < 0.7.0 ignores --assembly: every test ran")
		}
	}
	if !asJSON {
		printTests(out, r.Tests.Total, r.Tests.Passed, r.Tests.Failed, r.Tests.Skipped, r.Tests.Failures)
		for _, n := range r.Notes {
			_, _ = fmt.Fprintf(out, "  (%s)\n", n)
		}
	}
	if summary.Failed > 0 || summary.Total == 0 {
		return finish(verifyFailed)
	}
	return finish(verifyOK)
}

// verifyDiagnostics returns this compile's findings: the errors in the project's own code and the
// provenance object that came with them. When the heartbeat reports errors but none are in the
// source tree, it returns the third-party ones instead, so a failed compile is never reported as
// clean. Connectors before 0.7.0 have no compile_diagnostics; their console errors stand in, marked
// with source "console", and they predate provenance too.
func verifyDiagnostics(send sendFn, max int, hasErrors bool) (compileFindings, error) {
	query := func(sourceTreeOnly bool) (compileFindings, bool, error) {
		deadline := time.Now().Add(time.Minute)
		for {
			resp, err := send("compile_diagnostics", map[string]interface{}{"source_tree_only": sourceTreeOnly, "limit": max})
			if err != nil {
				return compileFindings{}, false, err
			}
			if !resp.Success {
				if strings.Contains(resp.Message, "Unknown command") {
					return compileFindings{}, false, nil
				}
				return compileFindings{}, true, fmt.Errorf("%s", resp.Message)
			}
			var d struct {
				Compiling   bool         `json:"compiling"`
				Total       int          `json:"total"`
				Diagnostics []verifyDiag `json:"diagnostics"`
			}
			if err := json.Unmarshal(resp.Data, &d); err != nil {
				return compileFindings{}, true, fmt.Errorf("unreadable compile_diagnostics reply: %w", err)
			}
			if d.Compiling {
				if time.Now().After(deadline) {
					return compileFindings{}, true, fmt.Errorf("editor still compiling")
				}
				time.Sleep(statusPollInterval)
				continue
			}
			p, pjson := provenanceIn(resp.Data)
			return compileFindings{Diags: d.Diagnostics, Total: d.Total, Provenance: p, ProvenanceJSON: pjson}, true, nil
		}
	}

	found, supported, err := query(true)
	if err != nil {
		return compileFindings{}, err
	}
	if !supported {
		consoleDiags, consoleTotal, source, consoleErr := consoleErrors(send, max)
		return compileFindings{Diags: consoleDiags, Total: consoleTotal, Source: source}, consoleErr
	}
	if found.Total == 0 && hasErrors {
		if second, _, err2 := query(false); err2 == nil {
			found = second
		} else {
			err = err2
		}
	}
	return found, err
}

func consoleErrors(send sendFn, max int) ([]verifyDiag, int, string, error) {
	resp, err := send("console", map[string]interface{}{"type": "error", "lines": max, "stacktrace": "none"})
	if err != nil {
		return nil, 0, "", err
	}
	messages := consoleEntryMessages(resp.Data)
	diags := make([]verifyDiag, 0, len(messages))
	for _, m := range messages {
		diags = append(diags, verifyDiag{Severity: "error", Message: firstLine(m)})
	}
	return diags, len(diags), "console", nil
}

// consoleEntryMessages reads the entry list out of a console reply. A bare array is what a
// connector without caps returns; the capped connector wraps the entries in an object that also
// carries the truncation counts.
func consoleEntryMessages(data []byte) []string {
	if len(data) == 0 {
		return nil
	}
	var list []string
	if err := json.Unmarshal(data, &list); err == nil {
		return list
	}
	var capped struct {
		Entries []string `json:"entries"`
	}
	if err := json.Unmarshal(data, &capped); err != nil {
		return nil
	}
	return capped.Entries
}

func printCompile(out io.Writer, ok bool, total int, diags []verifyDiag, source string, hasErrors bool) {
	if ok {
		_, _ = fmt.Fprintln(out, "compile: ok")
		return
	}
	note := ""
	if source == "console" {
		note = " (connector < 0.7.0: console errors shown)"
	}
	if total == 0 && hasErrors {
		_, _ = fmt.Fprintf(out, "compile: failed, but no error was reported%s; see unity-cli console\n", note)
		return
	}
	_, _ = fmt.Fprintf(out, "compile: %d error(s)%s\n", total, note)
	for _, d := range diags {
		where := ""
		if d.File != "" {
			where = fmt.Sprintf("%s:%d:%d ", d.File, d.Line, d.Col)
		}
		code := ""
		if d.Code != "" {
			code = d.Code + " "
		}
		_, _ = fmt.Fprintf(out, "  %s%s%s\n", where, code, d.Message)
		if d.Hint != "" {
			_, _ = fmt.Fprintf(out, "    hint: %s\n", d.Hint)
		}
	}
}

// verifyBurstStatus asks the connector's burst_status tool and waits up to waitSeconds for Burst to
// go idle. found=false when the connector has no such tool — no Burst installed, or a connector older
// than the tool — which is not a failure and leaves verify's verdict untouched. A transport error or a
// tool error comes back as err: verify reports it and keeps going, because Burst is a signal on top
// of the compile/test verdict, not part of it.
func verifyBurstStatus(send sendFn, waitSeconds, max int) (*verifyBurst, bool, error) {
	params := map[string]interface{}{"wait": waitSeconds, "limit": max}
	resp, err := send("burst_status", params)
	if err != nil {
		return nil, false, err
	}
	if !resp.Success {
		if strings.Contains(resp.Message, "Unknown command") {
			return nil, false, nil
		}
		return nil, true, fmt.Errorf("%s", resp.Message)
	}
	var b verifyBurst
	if err := json.Unmarshal(resp.Data, &b); err != nil {
		return nil, true, fmt.Errorf("unreadable burst_status reply: %w", err)
	}
	return &b, true, nil
}

func printBurst(out io.Writer, b *verifyBurst) {
	if b == nil {
		return
	}
	line := fmt.Sprintf("burst: %d targets compiled, %d error(s)", b.Compiled, b.ErrorCount)
	if b.Failed > 0 {
		line += fmt.Sprintf(", %d failed", b.Failed)
	}
	if b.NotCompiled > 0 {
		line += fmt.Sprintf(", %d not compiled", b.NotCompiled)
	}
	if b.Unknown > 0 {
		line += fmt.Sprintf(", %d unknown", b.Unknown)
	}
	if b.TimedOut {
		line += fmt.Sprintf(" (Burst still compiling after %ds)", int(b.WaitS))
	}
	_, _ = fmt.Fprintln(out, line)
	for _, e := range b.Errors {
		where := ""
		if e.File != "" && e.Line > 0 {
			where = fmt.Sprintf("%s:%d", e.File, e.Line)
			if e.Col > 0 {
				where += fmt.Sprintf(":%d", e.Col)
			}
			where += " "
		}
		code := ""
		if e.Code != "" {
			code = e.Code + " "
		}
		_, _ = fmt.Fprintf(out, "  %s%s%s\n", where, code, e.Message)
	}
	// One caveat line is enough for a two-line summary; the tool keeps the rest for burst_status.
	if len(b.Notes) > 0 {
		_, _ = fmt.Fprintf(out, "  (%s)\n", b.Notes[0])
	}
}

func printTests(out io.Writer, total, passed, failed, skipped int, failures []string) {
	if total == 0 {
		_, _ = fmt.Fprintln(out, "tests: none ran (check --assembly / --filter)")
		return
	}
	line := fmt.Sprintf("tests: %d/%d passed", passed, total)
	if failed > 0 {
		line += fmt.Sprintf(", %d failed", failed)
	}
	if skipped > 0 {
		line += fmt.Sprintf(", %d skipped", skipped)
	}
	_, _ = fmt.Fprintln(out, line)
	for _, f := range failures {
		_, _ = fmt.Fprintf(out, "  %s\n", f)
	}
}

func firstLines(items []string, max int) []string {
	out := make([]string, 0, len(items))
	for i, s := range items {
		if i == max {
			break
		}
		out = append(out, firstLine(s))
	}
	return out
}

// firstLine returns the first line of s with its runs of whitespace collapsed, so a test failure
// "Name:   message" reads "Name: message".
func firstLine(s string) string {
	s = strings.TrimSpace(s)
	if i := strings.IndexByte(s, '\n'); i >= 0 {
		s = s[:i]
	}
	return strings.Join(strings.Fields(s), " ")
}

// versionBefore reports whether a connector version X.Y.Z is older than major.minor. Unknown or
// development versions count as current.
func versionBefore(version string, major, minor int) bool {
	parts := strings.SplitN(normalizeVersion(version), ".", 3)
	if len(parts) < 2 {
		return false
	}
	ma, err1 := strconv.Atoi(parts[0])
	mi, err2 := strconv.Atoi(parts[1])
	if err1 != nil || err2 != nil {
		return false
	}
	return ma < major || (ma == major && mi < minor)
}

// verifyExit maps an error that stopped verify (or wait) before it could ask the editor anything
// (no instance, editor not alive, version mismatch, a dialog) to exit code 2.
func verifyExit(out io.Writer, err error) int {
	_, _ = fmt.Fprintln(out, err.Error())
	return verifyNoReply
}
