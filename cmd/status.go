package cmd

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

type instanceResolver func() (*client.Instance, error)

var statusPollInterval = 500 * time.Millisecond

func statusCmd(inst *client.Instance) error {
	status, err := readStatus(inst.Port)
	if err != nil {
		return fmt.Errorf("no status for port %d — Unity may not be running", inst.Port)
	}

	age := time.Since(time.UnixMilli(status.Timestamp))
	if age > staleHeartbeatAge {
		verdict := assessCrash(status)
		fmt.Fprint(os.Stderr, oneTrailingNewline(stalledEditorReport(status, age, verdict)))
		printVersionSummary(os.Stderr, status)
		if verdict.Crashed {
			// A dead editor is not a state an agent can wait out, so it leaves as a failed exit.
			return fmt.Errorf("%s", crashLine(status, verdict))
		}
		return nil
	}

	fmt.Printf("Unity (port %d): %s\n", status.Port, status.State)
	fmt.Printf("  Project: %s\n", status.ProjectPath)
	fmt.Printf("  Version: %s\n", status.UnityVersion)
	printVersionSummary(os.Stdout, status)
	fmt.Printf("  PID:     %d\n", status.PID)
	if isStartingState(status.State) {
		fmt.Println("  Note:    the HTTP listener is up; the editor has not run its first update tick yet")
	}
	if isModal(status) {
		fmt.Print(describeModal(status.Modal))
	}
	if status.Build != nil {
		fmt.Print(describeBuild(status.Build))
	}
	return nil
}

func printVersionSummary(w io.Writer, status *client.Instance) {
	v := assessConnectorVersion(status, Version)
	fmt.Fprintf(w, "  CLI:     %s\n", v.CLI)
	fmt.Fprintf(w, "  Connector: %s\n", connectorVersionLabel(status.ConnectorVersion))
	fmt.Fprintf(w, "  Compatibility: %s\n", v.Verdict)
}

// staleHeartbeatReport is what status prints once the heartbeat stops arriving. The evidence line
// is the difference between "Unity is not running" and "Unity is open with no connector loaded".
func staleHeartbeatReport(status *client.Instance, age time.Duration, ev *client.EditorEvidence) string {
	var b strings.Builder
	fmt.Fprintf(&b, "Unity (port %d): not responding (last heartbeat %s ago)\n", status.Port, age.Truncate(time.Second))
	if ev.Found() {
		fmt.Fprintf(&b, "%s\n", evidenceHint(ev))
	}
	return b.String()
}

// discoverInstanceKeepingEvidence is a variable so tests can exercise the case where the instance
// scan removed the dead-pid heartbeat before the crash verdict ran.
var discoverInstanceKeepingEvidence = client.DiscoverInstanceKeepingEvidence

func discoverStatusInstance(project string, port int) (*client.Instance, error) {
	if port > 0 {
		return readStatus(port)
	}
	inst, beat, err := discoverInstanceKeepingEvidence(project, 0)
	if err == nil {
		return inst, nil
	}
	// The instance scan deletes heartbeats whose pid is gone, which is what a crash leaves behind. beat
	// was read before that scan; when it reads as a crash, that is the answer `status` owes the caller.
	// Anything else keeps the original "no Unity instances" failure.
	if beat != nil && assessCrash(beat).Crashed {
		return beat, nil
	}
	return nil, err
}

// readStatus finds the instance file matching the given port (any state). It reads the file directly
// instead of through the instance scan, which would delete a crashed editor's heartbeat before
// anything could judge it.
func readStatus(port int) (*client.Instance, error) {
	if inst := client.NewestHeartbeat("", port); inst != nil {
		return inst, nil
	}
	return nil, fmt.Errorf("no instance on port %d", port)
}

func connectorVersionLabel(version string) string {
	if strings.TrimSpace(version) == "" {
		return "unknown"
	}
	return version
}

// minConnectorVersion is the oldest connector protocol this CLI is built against. 0.7.0 is the
// first release with the command set and heartbeat fields (compileFinishedAt, modal, build) that
// current commands such as verify, wait and build send or read; anything older is refused unless
// --ignore-version-mismatch says the caller knows better.
const minConnectorVersion = "0.7.0"

// versionExemptCommands are read-only or recovery commands: they must keep answering even when the
// connector speaks a different protocol, because they are how an operator diagnoses and repairs the
// mismatch. Gating them only turns a version drift into an unusable session (#11).
var versionExemptCommands = map[string]struct{}{
	"status":              {},
	"console":             {},
	"compile_diagnostics": {},
	"compile_status":      {},
	"api":                 {},
	"burst_status":        {},
	"modal":               {},
	"tools":               {},
	"tool_describe":       {},
	"wait":                {},
	"dialogs":             {},
	"list":                {},
	"help":                {},
	"version":             {},
	"update":              {},
}

type connectorCompatibility struct {
	CLI        string
	Connector  string
	Compatible bool
	Verdict    string
	Warning    string
	Refusal    error
}

type connectorRefusal struct {
	message string
}

func (e *connectorRefusal) Error() string { return e.message }

func isConnectorRefusal(err error) bool {
	var refusal *connectorRefusal
	return errors.As(err, &refusal)
}

type semanticVersion struct {
	major int
	minor int
	patch int
}

func assessConnectorVersion(inst *client.Instance, cliVersion string) connectorCompatibility {
	v := connectorCompatibility{CLI: normalizeVersion(cliVersion)}
	if inst != nil {
		v.Connector = strings.TrimSpace(inst.ConnectorVersion)
	}

	cli, cliOK := parseVersion(v.CLI)
	if v.CLI == "dev" || !cliOK {
		v.Compatible = true
		v.Verdict = "development CLI; compatibility not checked"
		return v
	}
	if v.Connector == "" {
		v.Compatible = true
		v.Verdict = "unknown connector version; update the connector package"
		v.Warning = versionWarning(v.CLI, "", "the connector reports no version")
		return v
	}

	connector, connectorOK := parseVersion(v.Connector)
	if !connectorOK {
		v.Compatible = true
		v.Verdict = fmt.Sprintf("unrecognised connector version %q; update the connector package", v.Connector)
		v.Warning = versionWarning(v.CLI, v.Connector, "the connector version is not a version number")
		return v
	}
	min, _ := parseVersion(minConnectorVersion)
	if versionLess(connector, min) {
		v.Compatible = false
		v.Verdict = fmt.Sprintf("connector %s is older than the minimum %s; update the connector package", v.Connector, minConnectorVersion)
		v.Refusal = &connectorRefusal{message: fmt.Sprintf("connector %s is older than the minimum %s supported by unity-cli %s (%s). Update the com.youngwoocho02.unity-cli-connector package in Unity, or rerun with --ignore-version-mismatch", v.Connector, minConnectorVersion, v.CLI, executablePath())}
		return v
	}
	if connector.major == cli.major && connector.minor == cli.minor {
		v.Compatible = true
		v.Verdict = fmt.Sprintf("compatible (same major.minor %d.%d)", cli.major, cli.minor)
		return v
	}

	v.Compatible = true
	field := "minor"
	if connector.major != cli.major {
		field = "major"
	}
	v.Verdict = fmt.Sprintf("compatible with a warning (CLI and connector differ in %s version)", field)
	v.Warning = versionWarning(v.CLI, v.Connector, "they differ in "+field+" version")
	return v
}

func versionWarning(cliVersion, connectorVersion, detail string) string {
	if connectorVersion == "" {
		connectorVersion = "unknown"
	}
	return fmt.Sprintf("warning: unity-cli %s (%s) and connector %s: %s. Run `unity-cli update` for the CLI and update the com.youngwoocho02.unity-cli-connector package in Unity; rerun with --ignore-version-mismatch to hide this warning.", cliVersion, executablePath(), connectorVersion, detail)
}

func executablePath() string {
	exe, err := os.Executable()
	if err != nil || strings.TrimSpace(exe) == "" {
		return "unknown path"
	}
	return exe
}

func parseVersion(version string) (semanticVersion, bool) {
	parts := strings.SplitN(normalizeVersion(version), ".", 3)
	if len(parts) != 3 {
		return semanticVersion{}, false
	}
	values := [3]int{}
	for i, part := range parts {
		n, err := strconv.Atoi(part)
		if err != nil || n < 0 {
			return semanticVersion{}, false
		}
		values[i] = n
	}
	return semanticVersion{major: values[0], minor: values[1], patch: values[2]}, true
}

func versionLess(a, b semanticVersion) bool {
	if a.major != b.major {
		return a.major < b.major
	}
	if a.minor != b.minor {
		return a.minor < b.minor
	}
	return a.patch < b.patch
}

func normalizeVersion(version string) string {
	version = strings.TrimSpace(version)
	version = strings.TrimPrefix(version, "v")
	version = strings.TrimPrefix(version, "V")
	return version
}

// versionGate applies the compatibility rule once per command invocation. Diagnostic commands bypass
// both the refusal and the warning; everything else prints a drift warning at most once per version
// pair, even though the gate runs both before dispatch and inside every send.
type versionGate struct {
	cliVersion string
	ignore     bool
	exempt     bool
	warned     map[string]bool
}

func newVersionGate(command string) *versionGate {
	_, exempt := versionExemptCommands[command]
	return &versionGate{
		cliVersion: Version,
		ignore:     flagIgnoreVersionMismatch,
		exempt:     exempt,
		warned:     map[string]bool{},
	}
}

func (g *versionGate) check(inst *client.Instance) error {
	v := assessConnectorVersion(inst, g.cliVersion)
	if g.ignore || g.exempt {
		return nil
	}
	if v.Refusal != nil {
		return v.Refusal
	}
	if v.Warning == "" {
		return nil
	}
	key := v.CLI + "\x00" + v.Connector
	if g.warned[key] {
		return nil
	}
	g.warned[key] = true
	fmt.Fprintln(os.Stderr, v.Warning)
	return nil
}

// resolveKeepingEvidence is the wait-loop resolver: like DiscoverInstance, but it keeps the
// heartbeat the instance scan deletes, so a crash during the wait still reaches crashedWhileWaiting.
func resolveKeepingEvidence(project string, port int) instanceResolver {
	return func() (*client.Instance, error) {
		inst, beat, err := discoverInstanceKeepingEvidence(project, port)
		if err != nil && beat != nil {
			return beat, nil
		}
		return inst, err
	}
}

// waitForAlive resolves the current target instance, then polls until a newer heartbeat appears.
// This keeps following the same project even if Unity rebinds to a new port during reload.
func waitForAlive(resolve instanceResolver, timeoutMs int) (*client.Instance, error) {
	return waitForAliveContext(context.Background(), resolve, timeoutMs)
}

// waitForAliveContext is the same wait, bounded by ctx as well as timeoutMs: the caller's one
// invocation deadline has to cover discovery and waiting too, or a slow start spends the budget
// twice. A non-positive timeoutMs means "until ctx is canceled".
func waitForAliveContext(ctx context.Context, resolve instanceResolver, timeoutMs int) (*client.Instance, error) {
	baseline := time.Now().UnixMilli()
	inst, err := resolve()
	if err == nil {
		if err := crashedWhileWaiting(inst); err != nil {
			return nil, err
		}
		baseline = inst.Timestamp
		// Already fresh and past startup — check if timestamp was updated within the last second.
		// A fresh "starting" heartbeat means the listener answered but the editor has not ticked
		// yet, so returning it would send the command to an editor that cannot run it.
		if time.Now().UnixMilli()-baseline < 1000 && !isStartingState(inst.State) {
			return inst, nil
		}
	}

	fmt.Fprintf(os.Stderr, "Waiting for Unity...\n")

	var deadline time.Time
	if timeoutMs > 0 {
		deadline = time.Now().Add(time.Duration(timeoutMs) * time.Millisecond)
	}
	for {
		if !deadline.IsZero() && !time.Now().Before(deadline) {
			return nil, waitForAliveTimeout(timeoutMs)
		}
		select {
		case <-ctx.Done():
			if ctx.Err() == context.DeadlineExceeded {
				return nil, waitForAliveTimeout(timeoutMs)
			}
			return nil, fmt.Errorf("waiting for Unity cancelled: %w", ctx.Err())
		case <-time.After(statusPollInterval):
		}

		inst, err = resolve()
		if err != nil {
			continue
		}
		// The startup beater keeps advancing the timestamp while the state is still "starting";
		// only the first tick that replaces it proves the editor can run a command.
		if inst.Timestamp > baseline && !isStartingState(inst.State) {
			fmt.Fprintf(os.Stderr, "Unity is ready.\n")
			return inst, nil
		}
		if err := crashedWhileWaiting(inst); err != nil {
			return nil, err
		}
	}
}

func waitForAliveTimeout(timeoutMs int) error {
	if timeoutMs <= 0 {
		return fmt.Errorf("timed out waiting for Unity")
	}
	return fmt.Errorf("timed out after %s waiting for Unity to answer (the editor may still be starting or busy)",
		client.FormatBudget(time.Duration(timeoutMs)*time.Millisecond))
}

// pollOrCancel waits one poll interval, returning the context error when the invocation deadline
// or a signal ends the wait first.
func pollOrCancel(ctx context.Context) error {
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(statusPollInterval):
		return nil
	}
}

func compileWaitError(cause error) error {
	if cause == context.DeadlineExceeded {
		return fmt.Errorf("timed out waiting for compilation (--timeout): %w", cause)
	}
	return fmt.Errorf("waiting for compilation cancelled: %w", cause)
}

// crashedWhileWaiting answers with the crash report when the heartbeat on disk belongs to an editor
// the evidence says is dead, and nil otherwise — including for a heartbeat that is merely silent, which
// is the case the wait exists to ride out. The artifact scan is skipped until the beat has been silent
// long enough for `wait` to give up anyway, so the common live case never pays for it.
func crashedWhileWaiting(inst *client.Instance) error {
	if inst == nil || heartbeatAge(inst) <= waitSilentHeartbeat {
		return nil
	}
	if verdict := assessCrash(inst); verdict.Crashed {
		return crashedError(inst, verdict, nil)
	}
	return nil
}

// waitForReadyTo polls until the heartbeat state becomes "ready" again after a compile request,
// writing progress to logw. command names the waiting command if a modal dialog blocks the editor.
// It cannot tell a compile that finished from an editor that was already ready; callers that need
// that proof use waitForCompileTo.
func waitForReadyTo(ctx context.Context, resolve instanceResolver, logw io.Writer, command string) (hasErrors, timedOut bool, err error) {
	_, _ = fmt.Fprintf(logw, "Waiting for compilation...\n")

	deadline := time.Now().Add(compileWaitTimeout)
	for time.Now().Before(deadline) {
		if err := pollOrCancel(ctx); err != nil {
			return false, false, compileWaitError(err)
		}
		status, err := resolve()
		if err != nil {
			continue
		}
		if isModal(status) {
			return false, false, modalBlockedError(status, command, true)
		}
		if status.State == "ready" {
			if status.CompileErrors {
				_, _ = fmt.Fprintf(logw, "Compilation finished with errors.\n")
			} else {
				_, _ = fmt.Fprintf(logw, "Compilation complete.\n")
			}
			return status.CompileErrors, false, nil
		}
	}

	_, _ = fmt.Fprintf(logw, "Timed out waiting for compilation (%s).\n", compileWaitTimeout)
	return false, true, nil
}

// Compile wait budgets. Variables so tests can shrink them.
var (
	compileWaitTimeout = 5 * time.Minute
	// compileIdleGrace is how long a requested compile is given to show any sign of having run
	// before the CLI concludes the asset refresh left nothing to recompile. The connector only
	// guarantees a 3 s "compiling" heartbeat after a request; a compile queued behind other
	// editor work can start later, so this is a heuristic rather than proof.
	compileIdleGrace = 20 * time.Second
)

// compileBaseline is the connector's view of script compilation when the compile was requested.
// FinishedAt comes from the refresh_unity reply, so a compile that finishes between the request
// and the first poll still counts as evidence.
type compileBaseline struct {
	FinishedAt int64
	// Tracked says the reply carried the compile-clock key, so a clock that still reads 0 is a new
	// connector that has not stamped it yet, not one without the clock.
	Tracked bool
	PID     int
}

// compileOutcome says how waiting for a requested compile ended.
type compileOutcome struct {
	Evidence         bool // a compile was seen to finish after the request
	NothingToCompile bool // the grace window passed with the editor ready and no compile
	TimedOut         bool // neither happened before compileWaitTimeout
	Restarted        bool // the editor process changed while waiting
	Untracked        bool // the connector reports no compile clock: the wait cannot prove one ran
	HasErrors        bool // heartbeat compileErrors flag when the wait ended
	Elapsed          time.Duration
}

// waitForCompileTo polls the heartbeat until the connector reports a script compilation that
// finished after baseline. A heartbeat that is simply "ready" is not proof: it can already have
// been ready when polling started, and the compile can finish inside one poll interval.
func waitForCompileTo(ctx context.Context, resolve instanceResolver, logw io.Writer, command string, baseline compileBaseline) (compileOutcome, error) {
	_, _ = fmt.Fprintf(logw, "Waiting for compilation...\n")

	start := time.Now()
	deadline := start.Add(compileWaitTimeout)
	lastErrors := false
	for time.Now().Before(deadline) {
		if err := pollOrCancel(ctx); err != nil {
			return compileOutcome{}, compileWaitError(err)
		}
		status, err := resolve()
		if err != nil {
			continue
		}
		if isModal(status) {
			return compileOutcome{}, modalBlockedError(status, command, true)
		}

		lastErrors = status.CompileErrors
		outcome := compileOutcome{Elapsed: time.Since(start), HasErrors: lastErrors}
		tracked := baseline.Tracked || status.CompileFinishedAt != 0
		restarted := baseline.PID != 0 && status.PID != 0 && status.PID != baseline.PID
		// The compile clock advances when the compilation ends, which is *before* the domain reload
		// it triggers; the editor only answers again once that reload is over, hence "ready" too.
		finished := status.State == "ready" && status.CompileFinishedAt > baseline.FinishedAt

		switch {
		case restarted:
			// The editor restarted under the request; its startup compile state is the freshest
			// answer, but the CLI cannot claim this request caused it. Checked before finished:
			// the reopened process's startup compile also advances the wall clock past the baseline.
			if status.State != "ready" {
				continue
			}
			outcome.Restarted = true
			outcome.Untracked = !tracked
			_, _ = fmt.Fprintf(logw, "%s\n", compileResultLine(status.CompileErrors))
			return outcome, nil
		case finished:
			outcome.Evidence = true
			_, _ = fmt.Fprintf(logw, "%s\n", compileResultLine(status.CompileErrors))
			return outcome, nil
		case !tracked && status.State == "ready":
			// Connector without a compile clock: the old "ready means done" heuristic, flagged as
			// unproven in the report.
			outcome.Untracked = true
			_, _ = fmt.Fprintf(logw, "%s\n", compileResultLine(status.CompileErrors))
			return outcome, nil
		case status.State == "ready" && time.Since(start) > compileIdleGrace:
			outcome.NothingToCompile = true
			_, _ = fmt.Fprintf(logw, "No script compilation was observed; nothing needed recompiling.\n")
			return outcome, nil
		}
	}

	_, _ = fmt.Fprintf(logw, "Timed out waiting for compilation (%s).\n", compileWaitTimeout)
	return compileOutcome{TimedOut: true, HasErrors: lastErrors, Elapsed: time.Since(start)}, nil
}

func compileResultLine(hasErrors bool) string {
	if hasErrors {
		return "Compilation finished with errors."
	}
	return "Compilation complete."
}
