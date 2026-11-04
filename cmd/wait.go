package cmd

import (
	"encoding/json"
	"fmt"
	"io"
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
// someone to stop play mode is not what this command is for.
func isIdleState(state string) bool {
	switch state {
	case "ready", "playing", "paused":
		return true
	}
	return false
}

// waitCmd blocks until the editor stops reporting work in progress, then reports the editor state
// and the compile provenance: when it last compiled, what it rebuilt, and whether any source file
// is newer than that. --compile additionally requires proof that the compile covers the caller's
// edit — either a compilation finished after wait started, or no source file is newer than the last
// one.
func waitCmd(args []string, send sendFn, resolve instanceResolver, out io.Writer) int {
	flags := parseSubFlags(args)
	_, wantCompile := flags["compile"]
	_, asJSON := flags["json"]

	timeout := time.Duration(waitDefaultTimeoutSeconds) * time.Second
	if raw, ok := flags["timeout"]; ok {
		seconds, err := strconv.Atoi(raw)
		if err != nil || seconds <= 0 {
			_, _ = fmt.Fprintf(out, "wait: --timeout wants a whole number of seconds, got %q\n", raw)
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
			if r.Reason != "" {
				_, _ = fmt.Fprintf(out, " — %s", r.Reason)
			}
			_, _ = fmt.Fprintln(out)
		}
		printProvenance(out, prov)
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
	if age := heartbeatAge(baseline); age > staleHeartbeatAge {
		report := stalledEditorReport(baseline, age, assessCrash(baseline))
		return noReply("%s", report)
	}
	r.State, r.Port, r.PID = baseline.State, baseline.Port, baseline.PID

	deadline := start.Add(timeout)
	var staleSince time.Time
	restartNoted := false
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
		r.CompileLanded = status.CompileFinishedAt > baseline.CompileFinishedAt

		// The editor coming back under a new pid restamps the compile clock too, and that is not
		// evidence the caller's edit was compiled into anything.
		if !restartNoted && baseline.PID != 0 && status.PID != 0 && status.PID != baseline.PID {
			restartNoted = true
			r.Notes = append(r.Notes, "the Unity editor restarted while waiting; the compile clock shows its startup compile")
		}

		// The connector rewrites its heartbeat twice a second even while compiling, and a player build
		// has its own writer, so long silence means the editor wedged or restarted: no amount of
		// --timeout will make it answer. The threshold is looser than statusCmd's because a domain
		// reload pauses the writes for a couple of seconds.
		if age := heartbeatAge(status); age > waitSilentHeartbeat {
			report := stalledEditorReport(status, age, assessCrash(status))
			return noReply("%s", report)
		}

		if isIdleState(status.State) {
			if !wantCompile || r.CompileLanded {
				askProvenance(false)
				return finish(waitIdle)
			}
			if status.CompileFinishedAt == 0 {
				r.Notes = append(r.Notes, "this connector reports no compile clock, so the wait cannot prove your edit was compiled; unity-cli verify does")
				askProvenance(false)
				return finish(waitIdle)
			}

			askProvenance(false)
			if prov == nil {
				r.Notes = append(r.Notes, "compile_status did not answer, so nothing can say whether the source tree is newer than the last compile; unity-cli verify compiles and proves it")
				return finish(waitIdle)
			}
			if prov.StaleCount == 0 {
				r.Notes = append(r.Notes, "no compilation ran while waiting and no source file is newer than the last compile: your edit is already in")
				if !prov.ScanComplete {
					r.Notes = append(r.Notes, "the source scan stopped early, so \"not stale\" is not certain")
				}
				return finish(waitIdle)
			}

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

		if !time.Now().Before(deadline) {
			r.TimedOut = true
			r.Reason = fmt.Sprintf("--timeout %s ran out", timeout)
			r.Notes = append(r.Notes, "run unity-cli verify (or unity-cli editor refresh --compile) to make the editor compile what it has not picked up")
			askProvenance(true)
			return finish(waitTimedOut)
		}
		time.Sleep(statusPollInterval)
	}
}

// heartbeatAge is how old the instance's last heartbeat is. A missing timestamp counts as ancient.
func heartbeatAge(inst *client.Instance) time.Duration {
	if inst == nil || inst.Timestamp == 0 {
		return time.Hour
	}
	return time.Since(time.UnixMilli(inst.Timestamp))
}

// staleHeartbeatAge is the cut-off statusCmd uses before calling an editor unresponsive. The client
// applies the same value when it judges whether a silent editor crashed instead of reloaded, so the
// two never disagree about what "stopped beating" means. The connector rewrites its heartbeat twice a
// second, including while it compiles.
const staleHeartbeatAge = client.BeatSilenceAge

// waitSilentHeartbeat is the same judgement made while waiting, with room for a domain reload to
// pause the writes.
const waitSilentHeartbeat = 10 * time.Second
