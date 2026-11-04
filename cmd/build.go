package cmd

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strconv"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// Build job phases the connector reports. Terminal phases end the wait; "interrupted" means the
// editor stopped reporting the job while it was still building, so its outcome is unknown.
const (
	// buildPhaseBuilding doubles as the heartbeat state the connector publishes while a build has
	// the main thread, which is what lets a second 'build' refuse instead of queueing behind it.
	buildPhaseBuilding    = "building"
	buildPhaseSucceeded   = "succeeded"
	buildPhaseFailed      = "failed"
	buildPhaseCancelled   = "cancelled"
	buildPhaseInterrupted = "interrupted"

	// build_status is served off the main thread, so a slow answer means the connector is
	// wedged rather than busy building; a short per-request timeout keeps the poll honest.
	buildStatusRequestMs = 15000

	buildFinalErrorsOnStderr = 3
)

// Poll pacing for the wait. Variables so tests can shrink them.
var (
	buildPollInterval   = 2 * time.Second
	buildContactTimeout = 3 * time.Minute
	buildProgressEvery  = 15 * time.Second
)

// buildJob mirrors the connector's BuildJobRecord, the JSON build_status answers with.
type buildJob struct {
	ID                  string   `json:"id"`
	Phase               string   `json:"phase"`
	Profile             string   `json:"profile,omitempty"`
	Target              string   `json:"target,omitempty"`
	Output              string   `json:"output,omitempty"`
	Message             string   `json:"message,omitempty"`
	ScenesSource        string   `json:"scenes_source,omitempty"`
	ElapsedSeconds      float64  `json:"elapsed_seconds"`
	ProgressPercent     *float64 `json:"progress_percent"`
	ProgressDescription string   `json:"progress_description,omitempty"`
	StartedAtMs         int64    `json:"started_at_ms"`
	FinishedAtMs        int64    `json:"finished_at_ms"`
	UpdatedAtMs         int64    `json:"updated_at_ms"`
	ErrorCount          int      `json:"error_count"`
	WarningCount        int      `json:"warning_count"`
	TotalBytes          int64    `json:"total_bytes"`
	TotalSeconds        float64  `json:"total_seconds"`
	Errors              []string `json:"errors"`
}

func (j *buildJob) terminal() bool {
	switch j.Phase {
	case buildPhaseSucceeded, buildPhaseFailed, buildPhaseCancelled, buildPhaseInterrupted:
		return true
	}
	return false
}

func (j *buildJob) succeeded() bool { return j.Phase == buildPhaseSucceeded }

// buildCmd runs "unity-cli build": either start a build job (and by default wait for it), or report
// one with "build status [id]".
//
// The start call answers as soon as the connector has validated the parameters and recorded the job;
// the build itself starts on a later editor update tick, so the editor's command lock is free and
// build_status keeps answering while the main thread is inside BuildPlayer.
func buildCmd(args []string, send, poll sendFn, resolve instanceResolver, logw io.Writer) (*client.CommandResponse, error) {
	if len(args) > 0 && args[0] == "status" {
		return buildStatusCmd(args[1:], poll)
	}

	if busy := buildingElsewhere(resolve); busy != nil {
		return nil, alreadyBuildingError(busy)
	}

	// The flags are parsed twice on purpose: parseSubFlags gives strings for the up-front
	// validation, buildParams also understands --params '{"profile":"macOS"}'.
	flags, err := parseSubFlags(args)
	if err != nil {
		return nil, err
	}
	if _, viaJSON := flags["params"]; !viaJSON {
		if err := checkBuildFlags(flags); err != nil {
			return nil, err
		}
	}

	params, err := buildParams(args, nil)
	if err != nil {
		return nil, err
	}
	if out, ok := params["output_path"]; ok {
		if _, has := params["output"]; !has {
			params["output"] = out
		}
		delete(params, "output_path")
	}
	noWait := takeBoolFlag(params, "no-wait") || takeBoolFlag(params, "no_wait") || takeBoolFlag(params, "nowait")

	resp, err := send("build", params)
	if err != nil {
		return nil, err
	}
	if !resp.Success {
		return resp, nil
	}

	job, err := jobFromResponse(resp)
	if err != nil {
		return resp, err
	}

	fmt.Fprintf(logw, "%s\n", buildQueuedLine(job))
	if noWait {
		return resp, nil
	}

	return waitForBuild(poll, logw, job.ID)
}

// buildingElsewhere reads the heartbeat for a build that already has Unity's main thread. Only the
// heartbeat knows about that job: a second build request would be queued for the main thread and sit
// unanswered for the rest of the running build, which is the wedge this whole command avoids.
func buildingElsewhere(resolve instanceResolver) *client.BuildJob {
	if resolve == nil {
		return nil
	}
	inst, err := resolve()
	if err != nil {
		// No heartbeat to read: let the request go through and let the connector answer.
		return nil
	}
	if inst.State == buildPhaseBuilding && inst.Build != nil {
		return inst.Build
	}
	return nil
}

func alreadyBuildingError(job *client.BuildJob) error {
	what := job.ID
	if job.Profile != "" {
		what = fmt.Sprintf("%s (%s)", job.ID, job.Profile)
	}
	return fmt.Errorf("Unity is already building %s, %s in — this build was not sent\n"+
		"  watch it: unity-cli build status %s", what, formatElapsed(job.ElapsedSeconds), job.ID)
}

// takeBoolFlag removes a CLI-only flag from the connector parameters and reports whether it was set.
func takeBoolFlag(params map[string]interface{}, key string) bool {
	value, ok := params[key]
	if !ok {
		return false
	}
	delete(params, key)
	switch v := value.(type) {
	case bool:
		return v
	case string:
		return v == "true"
	default:
		return true
	}
}

// checkBuildFlags fails on what the connector would only reject after the request round trip, so a
// typo never looks like a build that failed.
func checkBuildFlags(flags map[string]string) error {
	_, hasProfile := flags["profile"]
	_, hasTarget := flags["target"]

	if !hasProfile && !hasTarget {
		return fmt.Errorf("build needs --profile <build profile> or --target <BuildTarget>\n" +
			"  e.g. unity-cli build --profile macOS_dev --output Builds/Mac/App.app")
	}
	if hasProfile && hasTarget {
		return fmt.Errorf("build takes --profile or --target, not both")
	}
	if _, hasOutput := flags["output"]; !hasOutput {
		_, hasOutputPath := flags["output_path"]
		if !hasOutputPath {
			return fmt.Errorf("build needs --output <path> (absolute, or relative to the project root)")
		}
	}
	return nil
}

// buildStatusCmd answers with one job report. A failing build still exits non-zero, after printing
// the report, so a script can read the JSON and the exit code.
func buildStatusCmd(args []string, poll sendFn) (*client.CommandResponse, error) {
	params := map[string]interface{}{}
	if len(args) > 0 && !strings.HasPrefix(args[0], "--") {
		params["id"] = args[0]
	}

	resp, err := poll("build_status", params)
	if err != nil {
		return nil, err
	}
	if !resp.Success {
		return resp, nil
	}

	job, err := jobFromResponse(resp)
	if err != nil {
		return resp, err
	}
	if job.terminal() && !job.succeeded() {
		return resp, buildFailureError(job)
	}
	return resp, nil
}

// waitForBuild polls build_status until the job reaches a terminal phase. It never stops on a
// connection drop: the main thread is busy building, and the editor may be mid domain reload when
// the build ends. Contact is only given up on after buildContactTimeout without any answer.
func waitForBuild(poll sendFn, logw io.Writer, id string) (*client.CommandResponse, error) {
	started := time.Now()
	lastAnswer := time.Now()
	lastLine := ""
	lastLineAt := time.Time{}
	var lastResp *client.CommandResponse
	var lastJob *buildJob
	var lastErr error

	for {
		resp, err := poll("build_status", map[string]interface{}{"id": id})
		if err == nil && resp.Success {
			lastAnswer = time.Now()
			lastResp = resp

			job, parseErr := jobFromResponse(resp)
			if parseErr != nil {
				return resp, parseErr
			}
			lastJob = job

			if line := buildProgressLine(job); line != "" &&
				(line != lastLine || time.Since(lastLineAt) > buildProgressEvery) {
				fmt.Fprintln(logw, line)
				lastLine, lastLineAt = line, time.Now()
			}

			if job.terminal() {
				return finalizeBuild(resp, job, logw, started)
			}
		} else {
			lastErr = err
			if resp != nil && !resp.Success {
				lastErr = fmt.Errorf("%s", resp.Message)
			}
			// A proven crash ends the build with it: the job cannot outlive the editor's process, and
			// polling a dead one for the rest of buildContactTimeout only repeats the same answer.
			var failure *client.ConnectionFailure
			if errors.As(err, &failure) && failure.Crashed() {
				fmt.Fprintln(logw, "the Unity editor died during the build; the job stopped with it.")
				return lastResp, fmt.Errorf("%v\nThe build job %s died with the editor; restart it once Unity is back up.", failure, id)
			}
			if time.Since(lastAnswer) > buildContactTimeout {
				return lostBuildContact(lastResp, lastJob, id, lastErr)
			}
		}

		time.Sleep(buildPollInterval)
	}
}

// finalizeBuild turns a terminal report into the exit status, after printing the same summary an
// agent would otherwise grep out of Editor.log.
func finalizeBuild(resp *client.CommandResponse, job *buildJob, logw io.Writer, started time.Time) (*client.CommandResponse, error) {
	fmt.Fprintf(logw, "%s\n", buildSummaryLine(job, started))

	for i, msg := range job.Errors {
		if i >= buildFinalErrorsOnStderr {
			fmt.Fprintf(logw, "  … %d more error(s) in the report\n", len(job.Errors)-i)
			break
		}
		fmt.Fprintf(logw, "  error: %s\n", msg)
	}

	if job.succeeded() {
		return resp, nil
	}
	return resp, buildFailureError(job)
}

// lostBuildContact reports a wait that could no longer reach the connector. The last job report is
// returned as the payload so the caller still sees the state the build was last known in.
func lostBuildContact(resp *client.CommandResponse, job *buildJob, id string, lastErr error) (*client.CommandResponse, error) {
	message := fmt.Sprintf("lost contact with the editor %s after the build started: %v",
		buildContactTimeout, lastErr)
	if lastErr == nil {
		message = fmt.Sprintf("lost contact with the editor %s after the build started", buildContactTimeout)
	}

	if resp == nil {
		return nil, fmt.Errorf("%s; build job %s has no report yet — check 'unity-cli status' and then "+
			"'unity-cli build status %s'", message, id, id)
	}
	return resp, fmt.Errorf("%s; the job was last %s. It may still have finished: check the editor, then "+
		"run 'unity-cli build status %s'", message, job.Phase, id)
}

func buildFailureError(job *buildJob) error {
	switch job.Phase {
	case buildPhaseInterrupted:
		return fmt.Errorf("build job %s was interrupted: %s", job.ID, job.Message)
	case buildPhaseCancelled:
		return fmt.Errorf("build job %s was cancelled", job.ID)
	default:
		detail := job.Message
		if detail == "" && len(job.Errors) > 0 {
			detail = job.Errors[0]
		}
		if detail == "" {
			detail = "see the BuildReport in the editor (Window > Build Analysis)"
		}
		return fmt.Errorf("build job %s failed: %s", job.ID, detail)
	}
}

func jobFromResponse(resp *client.CommandResponse) (*buildJob, error) {
	if len(resp.Data) == 0 || string(resp.Data) == "null" {
		return nil, fmt.Errorf("the connector answered without a build job report (older connector?)")
	}
	var job buildJob
	if err := json.Unmarshal(resp.Data, &job); err != nil {
		return nil, fmt.Errorf("unreadable build report: %w", err)
	}
	if job.ID == "" || job.Phase == "" {
		return nil, fmt.Errorf("build report is missing its id or phase: %s", string(resp.Data))
	}
	return &job, nil
}

// describeBuild explains the job that has Unity's main thread, so "building" in the state line is
// not a dead end: the id it prints is what 'build status <id>' takes.
func describeBuild(job *client.BuildJob) string {
	return fmt.Sprintf("  Build:   %s  %s\n", job.ID,
		progressText(job.Phase, job.ProgressDescription, job.Output, job.ElapsedSeconds, job.ProgressPercent))
}

// progressText renders one progress reading — phase, elapsed time, what Unity says it is doing, and
// the percent when the build exposes one. Both the stderr wait lines and 'status' print it.
func progressText(phase, description, output string, elapsed float64, percent *float64) string {
	var b strings.Builder
	b.WriteString(phase)
	fmt.Fprintf(&b, " %s", formatElapsed(elapsed))
	if desc := strings.TrimSpace(description); desc != "" {
		fmt.Fprintf(&b, "  %s", desc)
	}
	if percent != nil && *percent >= 0 {
		fmt.Fprintf(&b, " %d%%", int(*percent*100))
	}
	if output != "" {
		fmt.Fprintf(&b, " -> %s", output)
	}
	return b.String()
}

func buildQueuedLine(job *buildJob) string {
	target := job.Profile
	if target == "" {
		target = job.Target
	}
	if target != "" {
		return fmt.Sprintf("build job %s queued (%s -> %s)", job.ID, target, job.Output)
	}
	return fmt.Sprintf("build job %s queued", job.ID)
}

// buildProgressLine is one stderr line per meaningful change: phase, elapsed time, and what Unity's
// progress window says when the build pumps the editor loop (percent is absent when it does not).
func buildProgressLine(job *buildJob) string {
	return progressText(job.Phase, job.ProgressDescription, "", job.ElapsedSeconds, job.ProgressPercent)
}

func buildSummaryLine(job *buildJob, started time.Time) string {
	elapsed := job.ElapsedSeconds
	if elapsed <= 0 {
		elapsed = time.Since(started).Seconds()
	}

	var b strings.Builder
	fmt.Fprintf(&b, "build %s in %s", job.Phase, formatElapsed(elapsed))
	if job.TotalBytes > 0 {
		fmt.Fprintf(&b, " — %s", formatBytes(job.TotalBytes))
	}
	fmt.Fprintf(&b, ", %d error(s), %d warning(s)", job.ErrorCount, job.WarningCount)
	if job.Output != "" {
		fmt.Fprintf(&b, " -> %s", job.Output)
	}
	return b.String()
}

// formatElapsed rounds first and then picks the unit, so 59.6s reads as 1m00s rather than 60s next
// to a 61s build's 1m01s.
func formatElapsed(seconds float64) string {
	total := int(seconds + 0.5)
	if total < 60 {
		return fmt.Sprintf("%ds", total)
	}
	return fmt.Sprintf("%dm%02ds", total/60, total%60)
}

func formatBytes(bytes int64) string {
	const unit = 1024
	if bytes < unit {
		return strconv.FormatInt(bytes, 10) + " B"
	}
	div, exp := int64(unit), 0
	for n := bytes / unit; n >= unit && exp < 4; n /= unit {
		div *= unit
		exp++
	}
	return fmt.Sprintf("%.1f %cB", float64(bytes)/float64(div), "KMGT"[exp])
}
