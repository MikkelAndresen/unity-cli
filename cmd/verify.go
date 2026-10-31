package cmd

import (
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
}

type verifyResult struct {
	Compile struct {
		OK     bool         `json:"ok"`
		Total  int          `json:"total"`
		Errors []verifyDiag `json:"errors,omitempty"`
		Source string       `json:"source,omitempty"` // "console" when the connector predates compile_diagnostics
	} `json:"compile"`
	Tests *struct {
		Total    int      `json:"total"`
		Passed   int      `json:"passed"`
		Failed   int      `json:"failed"`
		Skipped  int      `json:"skipped"`
		Failures []string `json:"failures,omitempty"`
	} `json:"tests,omitempty"`
	Notes []string `json:"notes,omitempty"`
	Error string   `json:"error,omitempty"`
	Exit  int      `json:"exit"`
}

// verifyCmd checks that the project compiles and its tests pass, in one call and with output short
// enough for an agent's context. testSend carries no request timeout (test runs can be long); port
// is where PlayMode results are polled.
func verifyCmd(args []string, send, testSend sendFn, resolve instanceResolver, port int, out io.Writer) int {
	flags := parseSubFlags(args)
	_, noTests := flags["no-tests"]
	_, keepConsole := flags["keep-console"]
	_, asJSON := flags["json"]
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

	diags, total, source, err := verifyDiagnostics(send, max, hasErrors)
	if err != nil {
		return noReply("compile: %v", err)
	}
	r.Compile.Total, r.Compile.Errors, r.Compile.Source = total, diags, source
	r.Compile.OK = total == 0 && !hasErrors
	if !asJSON {
		printCompile(out, r.Compile.OK, total, diags, source, hasErrors)
	}
	if !r.Compile.OK {
		return finish(verifyFailed)
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
	tresp, err := testCmd(testArgs, testSend, port)
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

// verifyDiagnostics returns this compile's errors in the project's own code. When the heartbeat
// reports errors but none are in the source tree, it returns the third-party ones instead, so a
// failed compile is never reported as clean. Connectors before 0.7.0 have no compile_diagnostics;
// their console errors stand in, marked with source "console".
func verifyDiagnostics(send sendFn, max int, hasErrors bool) ([]verifyDiag, int, string, error) {
	query := func(sourceTreeOnly bool) ([]verifyDiag, int, bool, error) {
		deadline := time.Now().Add(time.Minute)
		for {
			resp, err := send("compile_diagnostics", map[string]interface{}{"source_tree_only": sourceTreeOnly, "limit": max})
			if err != nil {
				return nil, 0, false, err
			}
			if !resp.Success {
				if strings.Contains(resp.Message, "Unknown command") {
					return nil, 0, false, nil
				}
				return nil, 0, true, fmt.Errorf("%s", resp.Message)
			}
			var d struct {
				Compiling   bool         `json:"compiling"`
				Total       int          `json:"total"`
				Diagnostics []verifyDiag `json:"diagnostics"`
			}
			if err := json.Unmarshal(resp.Data, &d); err != nil {
				return nil, 0, true, fmt.Errorf("unreadable compile_diagnostics reply: %w", err)
			}
			if !d.Compiling {
				return d.Diagnostics, d.Total, true, nil
			}
			if time.Now().After(deadline) {
				return nil, 0, true, fmt.Errorf("editor still compiling")
			}
			time.Sleep(statusPollInterval)
		}
	}

	diags, total, supported, err := query(true)
	if err != nil {
		return nil, 0, "", err
	}
	if !supported {
		return consoleErrors(send, max)
	}
	if total == 0 && hasErrors {
		diags, total, _, err = query(false)
	}
	return diags, total, "", err
}

func consoleErrors(send sendFn, max int) ([]verifyDiag, int, string, error) {
	resp, err := send("console", map[string]interface{}{"type": "error", "lines": max, "stacktrace": "none"})
	if err != nil {
		return nil, 0, "", err
	}
	var messages []string
	if len(resp.Data) > 0 {
		_ = json.Unmarshal(resp.Data, &messages)
	}
	diags := make([]verifyDiag, 0, len(messages))
	for _, m := range messages {
		diags = append(diags, verifyDiag{Severity: "error", Message: firstLine(m)})
	}
	return diags, len(diags), "console", nil
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

// verifyExit maps an error that stopped verify before it could ask the editor anything
// (no instance, editor not alive, version mismatch, a dialog) to exit code 2.
func verifyExit(out io.Writer, err error) int {
	_, _ = fmt.Fprintln(out, err.Error())
	return verifyNoReply
}
