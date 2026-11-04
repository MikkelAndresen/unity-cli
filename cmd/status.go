package cmd

import (
	"fmt"
	"io"
	"os"
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
	if age > 3*time.Second {
		fmt.Fprint(os.Stderr, staleHeartbeatReport(status, age, client.FindEditorEvidence(status.ProjectPath, status.Port)))
		return checkConnectorVersion(status, Version, flagIgnoreVersionMismatch)
	}

	fmt.Printf("Unity (port %d): %s\n", status.Port, status.State)
	fmt.Printf("  Project: %s\n", status.ProjectPath)
	fmt.Printf("  Version: %s\n", status.UnityVersion)
	fmt.Printf("  Connector: %s\n", connectorVersionLabel(status.ConnectorVersion))
	fmt.Printf("  PID:     %d\n", status.PID)
	if isModal(status) {
		fmt.Print(describeModal(status.Modal))
	}
	if status.Build != nil {
		fmt.Print(describeBuild(status.Build))
	}
	return checkConnectorVersion(status, Version, flagIgnoreVersionMismatch)
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

func discoverStatusInstance(project string, port int) (*client.Instance, error) {
	if port > 0 {
		return client.FindByPort(port)
	}
	return client.DiscoverInstance(project, 0)
}

// readStatus finds the instance file matching the given port (any state).
func readStatus(port int) (*client.Instance, error) {
	return client.FindByPort(port)
}

func connectorVersionLabel(version string) string {
	if strings.TrimSpace(version) == "" {
		return "unknown"
	}
	return version
}

func checkConnectorVersion(inst *client.Instance, cliVersion string, ignoreMismatch bool) error {
	if normalizeVersion(cliVersion) == "dev" {
		return nil
	}
	if ignoreMismatch {
		return nil
	}
	if inst == nil {
		return nil
	}

	connectorVersion := strings.TrimSpace(inst.ConnectorVersion)
	if connectorVersion == "" {
		return fmt.Errorf("connector version is unknown; update the Unity Connector package to match unity-cli %s, or rerun with --ignore-version-mismatch", cliVersion)
	}
	if normalizeVersion(connectorVersion) != normalizeVersion(cliVersion) {
		return fmt.Errorf("connector version mismatch: unity-cli %s, connector %s. Update both to the same release, or rerun with --ignore-version-mismatch", cliVersion, connectorVersion)
	}
	return nil
}

func normalizeVersion(version string) string {
	version = strings.TrimSpace(version)
	version = strings.TrimPrefix(version, "v")
	version = strings.TrimPrefix(version, "V")
	return version
}

// waitForAlive resolves the current target instance, then polls until a newer heartbeat appears.
// This keeps following the same project even if Unity rebinds to a new port during reload.
func waitForAlive(resolve instanceResolver, timeoutMs int) (*client.Instance, error) {
	baseline := time.Now().UnixMilli()
	inst, err := resolve()
	if err == nil {
		baseline = inst.Timestamp
		// Already fresh — check if timestamp was updated within the last second
		if time.Now().UnixMilli()-baseline < 1000 {
			return inst, nil
		}
	}

	fmt.Fprintf(os.Stderr, "Waiting for Unity...\n")

	deadline := time.Now().Add(time.Duration(timeoutMs) * time.Millisecond)
	for time.Now().Before(deadline) {
		time.Sleep(statusPollInterval)
		inst, err = resolve()
		if err != nil {
			continue
		}
		if inst.Timestamp > baseline {
			fmt.Fprintf(os.Stderr, "Unity is ready.\n")
			return inst, nil
		}
	}

	return nil, fmt.Errorf("timed out waiting for Unity")
}

// waitForReadyTo polls until the heartbeat state becomes "ready" again after a compile request,
// writing progress to logw. command names the waiting command if a modal dialog blocks the editor.
// It cannot tell a compile that finished from an editor that was already ready; callers that need
// that proof use waitForCompileTo.
func waitForReadyTo(resolve instanceResolver, logw io.Writer, command string) (hasErrors, timedOut bool, err error) {
	_, _ = fmt.Fprintf(logw, "Waiting for compilation...\n")

	deadline := time.Now().Add(compileWaitTimeout)
	for time.Now().Before(deadline) {
		time.Sleep(statusPollInterval)
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
	// before the CLI concludes the asset refresh left nothing to recompile. The connector keeps
	// its heartbeat "compiling" for 3 s after a request, so a real compile is visible long before
	// this elapses; without it, a no-op compile request would wait out the full timeout.
	compileIdleGrace = 20 * time.Second
)

// compileBaseline is the connector's view of script compilation when the compile was requested.
// FinishedAt comes from the refresh_unity reply, so a compile that finishes between the request
// and the first poll still counts as evidence.
type compileBaseline struct {
	FinishedAt int64
	PID        int
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
func waitForCompileTo(resolve instanceResolver, logw io.Writer, command string, baseline compileBaseline) (compileOutcome, error) {
	_, _ = fmt.Fprintf(logw, "Waiting for compilation...\n")

	start := time.Now()
	deadline := start.Add(compileWaitTimeout)
	for time.Now().Before(deadline) {
		time.Sleep(statusPollInterval)
		status, err := resolve()
		if err != nil {
			continue
		}
		if isModal(status) {
			return compileOutcome{}, modalBlockedError(status, command, true)
		}

		outcome := compileOutcome{Elapsed: time.Since(start), HasErrors: status.CompileErrors}
		tracked := baseline.FinishedAt != 0 || status.CompileFinishedAt != 0
		restarted := baseline.PID != 0 && status.PID != 0 && status.PID != baseline.PID
		// The compile clock advances when the compilation ends, which is *before* the domain reload
		// it triggers; the editor only answers again once that reload is over, hence "ready" too.
		finished := status.State == "ready" && status.CompileFinishedAt > baseline.FinishedAt

		switch {
		case finished:
			outcome.Evidence = true
			_, _ = fmt.Fprintf(logw, "%s\n", compileResultLine(status.CompileErrors))
			return outcome, nil
		case restarted:
			// The editor restarted under the request; its startup compile state is the freshest
			// answer, but the CLI cannot claim this request caused it.
			if status.State != "ready" {
				continue
			}
			outcome.Restarted = true
			outcome.Untracked = !tracked
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
	return compileOutcome{TimedOut: true, Elapsed: time.Since(start)}, nil
}

func compileResultLine(hasErrors bool) string {
	if hasErrors {
		return "Compilation finished with errors."
	}
	return "Compilation complete."
}
