package cmd

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"strings"
	"time"
)

type verifyOptions struct {
	flags       map[string]string
	noTests     bool
	noBurst     bool
	keepConsole bool
	allowStale  bool
	asJSON      bool
	burstWait   int
	max         int
}

// editorVerifyReply is the connector's verify tool answer: the replies of the steps it ran in the
// editor, which this file prints exactly as the step-by-step verify does.
type editorVerifyReply struct {
	Job     string          `json:"job"`
	Refresh json.RawMessage `json:"refresh"`
	Compile struct {
		HasErrors        bool `json:"has_errors"`
		NothingToCompile bool `json:"nothing_to_compile"`
		SkipProven       bool `json:"skip_proven"`
	} `json:"compile"`
	Diagnostics json.RawMessage `json:"diagnostics"`
	Burst       *struct {
		Found   bool            `json:"found"`
		Success bool            `json:"success"`
		Message string          `json:"message"`
		Data    json.RawMessage `json:"data"`
	} `json:"burst"`
	Tests *struct {
		Success bool            `json:"success"`
		Message string          `json:"message"`
		Data    json.RawMessage `json:"data"`
	} `json:"tests"`
	TestsDelegated bool   `json:"tests_delegated"`
	Stopped        string `json:"stopped"`
	Error          string `json:"error"`
	ErrorExit      int    `json:"error_exit"`
}

// verifyReloadRetries bounds how often verify re-attaches to its job: each successful compile
// reloads the domain once, and an editor that keeps reloading is answered with the last error.
const verifyReloadRetries = 5

// verifyInEditor runs verify as one job in the editor (the connector's verify tool) instead of one
// request per step. handled is false when the connector has no verify tool, and the caller then
// runs the steps itself.
//
// A successful compile reloads the domain, which drops the request; the job survives in the
// editor, so the same job id is sent again once the editor answers, and that returns its result.
func verifyInEditor(ctx context.Context, o verifyOptions, send sendFn, resolve instanceResolver, port int, out io.Writer) (int, bool) {
	params := map[string]interface{}{
		"job":          newVerifyJobID(),
		"no_tests":     o.noTests,
		"no_burst":     o.noBurst,
		"keep_console": o.keepConsole,
		"allow_stale":  o.allowStale,
		"burst_wait":   o.burstWait,
		"max":          o.max,
	}
	for _, k := range []string{"mode", "filter", "assembly"} {
		if v, ok := o.flags[k]; ok {
			params[k] = v
		}
	}

	var r verifyResult
	finish := func(code int) int {
		r.Exit = code
		if o.asJSON {
			b, _ := json.Marshal(r)
			_, _ = fmt.Fprintln(out, string(b))
		} else if r.Error != "" {
			_, _ = fmt.Fprintln(out, r.Error)
		}
		return code
	}
	noReply := func(format string, a ...interface{}) int {
		r.Error = fmt.Sprintf(format, a...)
		for _, arg := range a {
			if err, ok := arg.(error); ok && isConnectorRefusal(err) {
				return finish(verifyFailed)
			}
		}
		return finish(verifyNoReply)
	}

	resp, err := send("verify", params)
	for attempt := 1; attempt < verifyReloadRetries && answerLostToReload(resp, err); attempt++ {
		budget := time.Duration(remainingTimeoutMs(ctx, int(compileWaitTimeout.Milliseconds()))) * time.Millisecond
		if _, ready := waitForEditorAnswering(ctx, resolve, budget); !ready {
			break
		}
		resp, err = send("verify", params)
	}
	if err != nil {
		return noReply("editor: %v", err), true
	}
	if !resp.Success {
		if strings.Contains(resp.Message, "Unknown command") {
			return 0, false
		}
		return noReply("editor: %s", resp.Message), true
	}
	var reply editorVerifyReply
	if err := json.Unmarshal(resp.Data, &reply); err != nil {
		return noReply("editor: unreadable verify reply: %v", err), true
	}
	if reply.Job != params["job"] {
		return noReply("editor: verify answered job %q, not this one (%v)", reply.Job, params["job"]), true
	}
	if reply.Error != "" {
		r.Error = reply.Error
		code := reply.ErrorExit
		if code == 0 {
			code = verifyNoReply
		}
		return finish(code), true
	}

	var d struct {
		Total       int          `json:"total"`
		Diagnostics []verifyDiag `json:"diagnostics"`
	}
	_ = json.Unmarshal(reply.Diagnostics, &d)
	prov, provJSON := provenanceIn(reply.Diagnostics)
	hasErrors := reply.Compile.HasErrors
	r.Compile.Total, r.Compile.Errors, r.Compile.Provenance = d.Total, d.Diagnostics, provJSON
	r.Compile.OK = d.Total == 0 && !hasErrors
	if !o.asJSON {
		printCompile(out, r.Compile.OK, d.Total, false, d.Diagnostics, "", hasErrors)
		printProvenance(out, prov, false)
	}
	if reply.Compile.NothingToCompile {
		note := "no script compilation was observed after the refresh; the compile verdict is the editor's current state"
		if reply.Compile.SkipProven {
			note = "nothing changed since the last compile, so it was not repeated; the verdict is that compile's"
		}
		r.Notes = append(r.Notes, note)
		if !o.asJSON {
			_, _ = fmt.Fprintf(out, "  (%s)\n", note)
		}
	}
	if !r.Compile.OK {
		return finish(verifyFailed), true
	}

	staleCount := staleAssembliesUnderTest(prov, o.flags["assembly"])
	if prov != nil && prov.StaleAssemblyCount > staleCount && staleCount == 0 {
		r.Notes = append(r.Notes, fmt.Sprintf("%d stale assembly(ies) are outside --assembly; the tests will not load them",
			prov.StaleAssemblyCount))
		if !o.asJSON {
			_, _ = fmt.Fprintf(out, "  (%d stale assembly(ies) are outside --assembly; the tests will not load them)\n",
				prov.StaleAssemblyCount)
		}
	}
	if staleCount > 0 {
		line := prov.StaleAssemblyLine
		if line == "" || staleCount != prov.StaleAssemblyCount {
			line = fmt.Sprintf("%d project assembly(ies) under test are older than their sources", staleCount)
		}
		if !o.allowStale {
			if o.asJSON {
				r.Error = line + " — tests were not run; pass --allow-stale to run them anyway"
			} else {
				_, _ = fmt.Fprintln(out, line)
				_, _ = fmt.Fprintf(out, "tests: refused (%d stale assembly(s)) — run unity-cli editor refresh --compile, or pass --allow-stale to run them anyway\n",
					staleCount)
			}
			return finish(verifyFailed), true
		}
		if !o.asJSON {
			_, _ = fmt.Fprintf(out, "warning: %s\n", line)
		}
	}

	burstFailed := false
	if b := reply.Burst; b != nil && b.Found {
		if !b.Success {
			r.BurstError = b.Message
			if !o.asJSON {
				_, _ = fmt.Fprintf(out, "burst: could not be checked (%s)\n", firstLine(b.Message))
			}
		} else {
			var burst verifyBurst
			if err := json.Unmarshal(b.Data, &burst); err != nil {
				r.BurstError = fmt.Sprintf("unreadable burst_status reply: %v", err)
				if !o.asJSON {
					_, _ = fmt.Fprintf(out, "burst: could not be checked (%s)\n", r.BurstError)
				}
			} else {
				r.Burst = &burst
				if !o.asJSON {
					printBurst(out, &burst)
				}
				burstFailed = burst.ErrorCount > 0
			}
		}
	}

	switch {
	case o.noTests:
		if burstFailed {
			return finish(verifyFailed), true
		}
		return finish(verifyOK), true
	case reply.TestsDelegated:
		tresp, err := runVerifyTests(ctx, o.flags, o.allowStale, send, port)
		if err != nil {
			return noReply("tests: %v", err), true
		}
		return reportVerifyTests(&r, tresp.Success, tresp.Message, tresp.Data, o.flags, resolve, out, o.asJSON, o.max,
			burstFailed, finish, noReply), true
	case reply.Tests != nil:
		return reportVerifyTests(&r, reply.Tests.Success, reply.Tests.Message, reply.Tests.Data, o.flags, resolve, out,
			o.asJSON, o.max, burstFailed, finish, noReply), true
	default:
		return noReply("tests: the editor's verify reply has no test result"), true
	}
}

func newVerifyJobID() string {
	b := make([]byte, 8)
	if _, err := rand.Read(b); err != nil {
		return fmt.Sprintf("%x", time.Now().UnixNano())
	}
	return hex.EncodeToString(b)
}
