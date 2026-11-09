package cmd

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"strconv"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// waitCategory is the CLI command name, used by root.go's dispatch and by splitArgs, which must not
// steal wait's own --timeout as the global request timeout.
const waitCategory = "wait"

// Exit codes of `unity-cli wait`, the same shape as verify's.
const (
	waitIdle     = 0
	waitTimedOut = 1
	waitNoReply  = 2
)

// waitDefaultTimeoutSeconds bounds a wait with no --timeout. It is a budget of its own, separate
// from the global --timeout (milliseconds), because waiting for the editor is the command.
const waitDefaultTimeoutSeconds = 120

// waitStaleGrace is how long an idle-but-stale editor is given to start the compile itself before
// wait says it will not. Unity only auto-refreshes while it has focus, and an editor driven from a
// terminal does not have it, so without this the common case burns the whole --timeout. A variable
// so tests can shrink it.
var waitStaleGrace = 10 * time.Second

type waitResult struct {
	State string `json:"state"`
	Port  int    `json:"port,omitempty"`
	PID   int    `json:"pid,omitempty"`
	// WaitedMs is how long the editor was not already idle when the command started.
	WaitedMs int64 `json:"waited_ms"`
	Idle     bool  `json:"idle"`
	// CompileWanted and CompileLanded are the --compile answer: did a script compilation finish
	// after wait started, on the connector's own clock (comparing two connector stamps keeps a CLI
	// on another machine — an agent sandbox, for example — from needing a clock that agrees with
	// the editor's).
	CompileWanted bool `json:"compile"`
	CompileLanded bool `json:"compile_landed,omitempty"`
	TimedOut      bool `json:"timed_out,omitempty"`
	// StartingForMs is how long a "starting" editor has been waiting for its first update tick.
	StartingForMs int64 `json:"starting_for_ms,omitempty"`
	// Reason says why a wait gave up, which timed_out alone does not: the budget ran out, or the
	// editor sat idle with uncompiled sources instead of compiling them.
	Reason     string          `json:"reason,omitempty"`
	Notes      []string        `json:"notes,omitempty"`
	Provenance json.RawMessage `json:"provenance,omitempty"`
	Error      string          `json:"error,omitempty"`
	Exit       int             `json:"exit"`
}

// isIdleState is the heartbeat saying the editor is not mid-operation: it can answer a command and
// nothing is about to change what a compile result covers. Play mode counts as idle — waiting for
// someone to stop play mode is not what this command is for. "starting" is the listener's first
// heartbeat before the main thread has ticked; it is alive but not idle.
func isIdleState(state string) bool {
	switch state {
	case "ready", "playing", "paused":
		return true
	}
	return false
}

// isStartingState is the connector's first heartbeat of a session: HttpServer is listening, but the
// editor has not run an update tick yet, so it is not ready to answer a command.
func isStartingState(state string) bool {
	return state == "starting"
}

// waitCmd blocks until the editor stops reporting work in progress, then reports the editor state
// and the compile provenance: when it last compiled, what it rebuilt, and whether any source file
// is newer than that. --compile additionally requires proof that the compile covers the caller's
// edit — either a compilation finished after wait started, or no source file is newer than the last
// one.
func waitCmd(ctx context.Context, args []string, send sendFn, resolve instanceResolver, out io.Writer) int {
	flags, err := parseSubFlags(args)
	if err != nil {
		_, _ = fmt.Fprintf(os.Stderr, "wait: %v\n", err)
		return waitTimedOut
	}
	_, wantCompile := flags["compile"]
	_, asJSON := flags["json"]

	timeout := time.Duration(waitDefaultTimeoutSeconds) * time.Second
	if raw, ok := flags["timeout"]; ok {
		seconds, err := strconv.Atoi(raw)
		if err != nil || seconds <= 0 {
			msg := fmt.Sprintf("wait: --timeout wants a whole number of seconds, got %q", raw)
			if asJSON {
				b, _ := json.Marshal(waitResult{Error: msg, Exit: waitTimedOut})
				_, _ = fmt.Fprintln(out, string(b))
				return waitTimedOut
			}
			_, _ = fmt.Fprintln(out, msg)
			return waitTimedOut
		}
		timeout = time.Duration(seconds) * time.Second
	}

	var r waitResult
	r.CompileWanted = wantCompile
	start := time.Now()

	var prov *compileProvenance
	var provJSON json.RawMessage
	askProvenance := func(refresh bool) {
		if refresh || prov == nil {
			prov, provJSON = fetchProvenance(send)
		}
	}

	finish := func(code int) int {
		r.Exit = code
		r.Idle = isIdleState(r.State)
		r.WaitedMs = time.Since(start).Milliseconds()
		r.Provenance = provJSON
		if asJSON {
			b, _ := json.Marshal(r)
			_, _ = fmt.Fprintln(out, string(b))
			return code
		}
		if r.Error != "" {
			_, _ = fmt.Fprintln(out, r.Error)
			return code
		}
		_, _ = fmt.Fprintf(out, "Unity (port %d): %s\n", r.Port, r.State)
		if r.TimedOut {
			_, _ = fmt.Fprintf(out, "gave up after %s: still %s", time.Since(start).Truncate(time.Second), r.State)
			if r.StartingForMs > 0 {
				_, _ = fmt.Fprintf(out, " (for %s)", client.FormatAge(time.Duration(r.StartingForMs)*time.Millisecond))
			}
			if r.Reason != "" {
				_, _ = fmt.Fprintf(out, " — %s", r.Reason)
			}
			_, _ = fmt.Fprintln(out)
		} else if r.Reason != "" {
			// The editor is idle but --compile could not be proven; the reason is the verdict.
			_, _ = fmt.Fprintln(out, r.Reason)
		}
		printProvenance(out, prov, true)
		for _, n := range r.Notes {
			_, _ = fmt.Fprintf(out, "  (%s)\n", n)
		}
		return code
	}
	noReply := func(format string, a ...interface{}) int {
		r.Error = fmt.Sprintf(format, a...)
		return finish(waitNoReply)
	}

	baseline, err := resolve()
	if err != nil {
		return noReply("editor: %v", err)
	}
	if age := client.HeartbeatAge(baseline); age > staleHeartbeatAge {
		report := stalledEditorReport(baseline, age, assessCrash(baseline))
		return noReply("%s", report)
	}
	r.State, r.Port, r.PID = baseline.State, baseline.Port, baseline.PID

	deadline := start.Add(timeout)
	var staleSince time.Time
	restartNoted := false
	reloadNoted := false
	for {
		status, err := resolve()
		if err != nil {
			// The heartbeat file disappearing mid-wait means the editor quit; a domain reload keeps
			// the file (with state "reloading") and comes back on its own.
			return noReply("editor: %v", err)
		}
		if isModal(status) {
			return noReply("%s", modalBlockedError(status, "wait", true))
		}
		r.State, r.Port, r.PID = status.State, status.Port, status.PID
		r.StartingForMs = 0
		if age, ok := status.StartingFor(); ok {
			r.StartingForMs = age.Milliseconds()
		}

		// The editor coming back under a new pid restamps the compile clock too, and that is not
		// evidence the caller's edit was compiled into anything. Rebase the clock so a compile this
		// new process runs after the restart is still proof, while its startup compile is not.
		if baseline.PID != 0 && status.PID != 0 && status.PID != baseline.PID {
			if !restartNoted {
				restartNoted = true
				r.Notes = append(r.Notes, "the Unity editor restarted while waiting; the compile clock shows its startup compile")
			}
			baseline = status
			r.CompileLanded = false
		} else {
			r.CompileLanded = status.CompileFinishedAt > baseline.CompileFinishedAt
		}

		// The connector rewrites its heartbeat twice a second even while compiling, and a player build
		// has its own writer, so long silence means the editor wedged or restarted: no amount of
		// --timeout will make it answer. A domain reload also stops the update-driven writes for its
		// whole duration, so silence there is only a reason to stop if the editor died in it.
		if age := client.HeartbeatAge(status); age > waitSilentHeartbeat {
			verdict := assessCrash(status)
			if status.State != "reloading" || verdict.Crashed {
				return noReply("%s", stalledEditorReport(status, age, verdict))
			}
			if !reloadNoted {
				reloadNoted = true
				r.Notes = append(r.Notes, fmt.Sprintf("the heartbeat has been silent for %s, but the editor reports a domain reload; still waiting (--timeout bounds it)", age.Truncate(time.Second)))
			}
		}

		// The grace is for an idle editor with stale sources; time spent compiling or reloading is
		// not part of it, or a compile that finished between polls would leave the timer expired.
		if !isIdleState(status.State) {
			staleSince = time.Time{}
		}

		if isIdleState(status.State) {
			if !wantCompile || r.CompileLanded {
				askProvenance(false)
				return finish(waitIdle)
			}
			if status.CompileFinishedAt == 0 {
				// --compile is a promise that the edit is in the compile. A connector with no compile
				// clock cannot keep it, and answering 0 would let `wait --compile && test` run the tests
				// against code this wait could not prove was built.
				r.Reason = "this connector reports no compile clock, so the wait cannot prove your edit was compiled"
				r.Notes = append(r.Notes, "run unity-cli verify (or unity-cli editor refresh --compile) to compile it and get the provenance line")
				askProvenance(false)
				return finish(waitTimedOut)
			}

			askProvenance(false)
			if prov == nil {
				r.Reason = "compile_status did not answer, so nothing can say whether the source tree is newer than the last compile"
				r.Notes = append(r.Notes, "run unity-cli verify, which compiles and proves what the compile covers")
				return finish(waitTimedOut)
			}
			if prov.StaleCount == 0 {
				if prov.ScanNote != provenanceNotScanned {
					r.Notes = append(r.Notes, "no compilation ran while waiting and no source file is newer than the last compile: your edit is already in")
					if !prov.ScanComplete {
						r.Notes = append(r.Notes, "the source scan stopped early, so \"not stale\" is not certain")
					}
					return finish(waitIdle)
				}
				// compile_status answered a compiling editor from the previous record without a scan;
				// "not stale" there means "not looked", so wait for the compile to move the clock.
			} else {
				// Stale and not compiling: the editor has not picked the change up yet. Wait for it to
				// start; the compile clock ends the wait, so the tree is not scanned again.
				if staleSince.IsZero() {
					staleSince = time.Now()
				} else if time.Since(staleSince) > waitStaleGrace {
					r.TimedOut = true
					r.Reason = fmt.Sprintf("no compilation started within %s of finding uncompiled sources", waitStaleGrace)
					r.Notes = append(r.Notes, "Unity only auto-refreshes while it has focus; run unity-cli verify (or unity-cli editor refresh --compile) to compile them")
					// Re-ask: the lines the editor wrote say "(3 s ago)" as of when it wrote them, and
					// these were written one grace period ago.
					askProvenance(true)
					return finish(waitTimedOut)
				}
			}
		}

		if !time.Now().Before(deadline) {
			r.TimedOut = true
			r.Reason = fmt.Sprintf("--timeout %s ran out", timeout)
			r.Notes = append(r.Notes, "run unity-cli verify (or unity-cli editor refresh --compile) to make the editor compile what it has not picked up")
			askProvenance(true)
			return finish(waitTimedOut)
		}
		select {
		case <-ctx.Done():
			return noReply("wait cancelled: %v", ctx.Err())
		case <-time.After(statusPollInterval):
		}
	}
}

// staleHeartbeatAge is the cut-off statusCmd uses before calling an editor unresponsive. The client
// applies the same value when it judges whether a silent editor crashed instead of reloaded, so the
// two never disagree about what "stopped beating" means. The connector rewrites its heartbeat twice a
// second, including while it compiles.
const staleHeartbeatAge = client.BeatSilenceAge

// waitSilentHeartbeat is the same judgement made while waiting, with room for a domain reload to
// pause the writes.
const waitSilentHeartbeat = 10 * time.Second
