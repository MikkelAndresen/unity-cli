package cmd

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func testCmd(ctx context.Context, args []string, send sendFn, port int) (*client.CommandResponse, error) {
	flags, err := parseSubFlags(args)
	if err != nil {
		return nil, err
	}

	mode := "EditMode"
	if m, ok := flags["mode"]; ok {
		mode = m
	}

	if mode != "EditMode" && mode != "PlayMode" {
		return nil, fmt.Errorf("--mode must be EditMode or PlayMode, got: %s", mode)
	}

	params := map[string]interface{}{
		"mode": mode,
	}
	if filter, ok := flags["filter"]; ok {
		params["filter"] = filter
	}
	if assembly, ok := flags["assembly"]; ok {
		params["assembly"] = assembly
	}
	if _, ok := flags["allow-dirty-scenes"]; ok {
		params["allowDirtyScenes"] = true
	}
	if _, ok := flags["auto-save-scenes"]; ok {
		params["autoSaveScenes"] = true
	}
	if _, ok := flags["allow-stale"]; ok {
		params["allowStale"] = true
	}
	if _, ok := flags["allow_stale"]; ok {
		params["allowStale"] = true
	}

	resp, err := send("run_tests", params)
	if err != nil {
		return nil, err
	}

	if !resp.Success && strings.Contains(resp.Message, "Unknown command") {
		return nil, fmt.Errorf(
			"'run_tests' is not available.\n" +
				"Install the Unity Test Framework package:\n" +
				"  Window > Package Manager > search 'Test Framework' > Install")
	}

	// EditMode: results returned directly in response
	if mode == "EditMode" {
		return resp, nil
	}

	// PlayMode: Unity returns "running", poll results file
	if resp.Message != "running" {
		return resp, nil
	}

	fmt.Fprintln(os.Stderr, "PlayMode tests running, waiting for results...")

	return pollTestResults(ctx, testResultsPort(resp, port))
}

// testResultSummary is what a test run's reply says it did. Total stays a pointer so a connector
// that answers without a count is told apart from one that counted zero tests.
type testResultSummary struct {
	Total  *int `json:"total"`
	Passed int  `json:"passed"`
	Failed int  `json:"failed"`
}

// applyTestVerdict turns a run that selected no test into a failure, the way verify reports "no test
// ran". A filtered run that matches nothing is a typo in --filter or --assembly far more often than
// an empty suite, and exiting 0 with {"total":0} reports it as a passing run.
func applyTestVerdict(resp *client.CommandResponse, flags map[string]string) {
	summary, ok := testSummaryIn(resp)
	if !ok || summary.Total == nil || *summary.Total > 0 {
		return
	}
	resp.Success = false
	resp.Message = noTestsMatchedMessage(flags)
}

// testSummaryIn reads the run summary out of a reply that succeeded.
func testSummaryIn(resp *client.CommandResponse) (testResultSummary, bool) {
	if resp == nil || !resp.Success || len(resp.Data) == 0 {
		return testResultSummary{}, false
	}
	var summary testResultSummary
	if err := json.Unmarshal(resp.Data, &summary); err != nil {
		return testResultSummary{}, false
	}
	return summary, true
}

func noTestsMatchedMessage(flags map[string]string) string {
	var selection []string
	if filter, ok := flags["filter"]; ok {
		selection = append(selection, "filter "+filter)
	}
	if assembly, ok := flags["assembly"]; ok {
		selection = append(selection, "assembly "+assembly)
	}
	if len(selection) == 0 {
		return "no tests matched: the run selected no tests in this mode"
	}
	return "no tests matched (" + strings.Join(selection, ", ") + ")"
}

// staleOutsideSelectionNote is verify's note about project assemblies that are older than their
// sources but sit outside --assembly, where the run will not load them. test only hears from the
// connector about the assemblies it asked for, so this asks compile_status separately — and only for
// a scoped run, the case where the note can differ from the connector's own refusal.
func staleOutsideSelectionNote(send sendFn, assembly string) string {
	if strings.TrimSpace(assembly) == "" {
		return ""
	}
	prov, _ := fetchProvenance(send)
	if prov == nil || prov.StaleAssemblyCount == 0 {
		return ""
	}
	if staleAssembliesUnderTest(prov, assembly) > 0 {
		return ""
	}
	return fmt.Sprintf("%d stale assembly(ies) are outside --assembly; the tests will not load them", prov.StaleAssemblyCount)
}

// testResultsPort is the port the connector stamped the run with: a compile or reload between the
// resolve before the send and the send itself can move the connector, so polling the pre-send port
// would wait ten minutes for a result file the run never writes. Connectors that predate the port
// in the reply fall back to the caller's port.
func testResultsPort(resp *client.CommandResponse, fallback int) int {
	var data struct {
		Port int `json:"port"`
	}
	if len(resp.Data) > 0 && json.Unmarshal(resp.Data, &data) == nil && data.Port > 0 {
		return data.Port
	}
	return fallback
}

func pollTestResults(ctx context.Context, port int) (*client.CommandResponse, error) {
	home, err := os.UserHomeDir()
	if err != nil {
		return nil, fmt.Errorf("cannot determine home directory: %w", err)
	}

	resultsPath := filepath.Join(home, ".unity-cli", "status", fmt.Sprintf("test-results-%d.json", port))

	// The invocation context is the only bound: an explicit --timeout 0 means no deadline, which is
	// what the help text promises, so there is no second 10-minute limit here.
	for {
		select {
		case <-ctx.Done():
			if ctx.Err() == context.DeadlineExceeded {
				return nil, fmt.Errorf("timed out waiting for test results (--timeout): %w", ctx.Err())
			}
			return nil, fmt.Errorf("waiting for test results cancelled: %w", ctx.Err())
		case <-time.After(500 * time.Millisecond):
		}

		data, err := os.ReadFile(resultsPath)
		if err == nil {
			_ = os.Remove(resultsPath)
			var resp client.CommandResponse
			if err := json.Unmarshal(data, &resp); err != nil {
				return nil, fmt.Errorf("failed to parse test results: %w", err)
			}
			return &resp, nil
		}

		// Check Unity process is still alive (heartbeat may be stale during domain reload)
		inst, err := readStatus(port)
		if err == nil && inst.State == "stopped" {
			return nil, fmt.Errorf("unity editor has stopped (port %d)", port)
		}
		if err == nil && isModal(inst) {
			return nil, modalBlockedError(inst, "test --mode PlayMode", true)
		}
	}
}
