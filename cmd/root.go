package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

var Version = "dev"

var (
	flagPort                  int
	flagProject               string
	flagTimeout               int
	flagIgnoreVersionMismatch bool
	flagMeta                  bool
)

func Execute() error {
	flag.IntVar(&flagPort, "port", 0, "Select Unity instance by active heartbeat port")
	flag.StringVar(&flagProject, "project", "", "Select Unity instance by project path")
	flag.IntVar(&flagTimeout, "timeout", defaultRequestTimeoutMs, "Request timeout in milliseconds")
	flag.BoolVar(&flagIgnoreVersionMismatch, "ignore-version-mismatch", false, "Run even when CLI and connector versions differ")
	flag.BoolVar(&flagMeta, "meta", false, "Print the connector's response meta (how the call was served) to stderr")

	flag.Usage = func() { printHelp() }

	args := os.Args[1:]
	flagArgs, cmdArgs := splitArgs(args)
	if err := flag.CommandLine.Parse(flagArgs); err != nil {
		fmt.Fprintf(os.Stderr, "flag parse error: %v\n", err)
		os.Exit(1)
	}

	if len(cmdArgs) == 0 {
		printHelp()
		return nil
	}

	category := cmdArgs[0]
	subArgs := cmdArgs[1:]

	// --help / -h on any command
	for _, a := range subArgs {
		if a == "--help" || a == "-h" {
			printTopicHelp(category)
			return nil
		}
	}

	signalCtx, stopSignals := installSignalCancellation()
	defer stopSignals()

	switch category {
	case "help", "--help", "-h":
		if len(subArgs) > 0 {
			printTopicHelp(subArgs[0])
		} else {
			printHelp()
		}
		return nil
	case "version", "--version", "-v":
		fmt.Println("unity-cli " + Version)
		return nil
	case "update":
		return updateCmd(subArgs)
	case "status":
		inst, err := discoverStatusInstance(flagProject, flagPort)
		if err != nil {
			return annotateNoEditor(err, flagProject, flagPort)
		}
		return statusCmd(inst)
	case "dialogs":
		// Runs with no connector and no heartbeat at all: startup dialogs are exactly the
		// window where neither exists yet.
		return dialogsCmd(subArgs)
	case "editor":
		// quit and restart run before the normal pipeline: restart has to work with
		// no live editor, both stop the editor that pipeline would wait on, and
		// quit --force must reach an editor parked on a modal dialog. The other
		// editor subcommands fall through to the normal send path below.
		if len(subArgs) > 0 && (subArgs[0] == "quit" || subArgs[0] == "restart") {
			lifecycle := restartCmd
			if subArgs[0] == "quit" {
				lifecycle = quitCmd
			}
			resp, err := lifecycle(subArgs[1:], newRestartDeps(flagWasSet("timeout")))
			if err != nil {
				return err
			}
			printResponse(resp)
			if resp == nil || resp.Success {
				return nil
			}
			// Not reached by today's lifecycle commands (they return an error instead), but a refused
			// quit must never exit 0. Normal return so the deferred signal handler teardown runs.
			return fmt.Errorf("%s", resp.Message)
		}
	}

	// verify and wait report an editor that can't answer as exit code 2, not as a CLI error.
	noEditor := func(err error) error {
		if category == "verify" || category == waitCategory {
			exitNow(verifyExit(os.Stdout, err))
		}
		return err
	}

	explicitTimeout := flagWasSet("timeout")
	effectiveTimeout := effectiveInvocationTimeout(category, subArgs, explicitTimeout)

	// Discovery comes before reading a piped snippet: a project with no editor must answer at once
	// instead of waiting for a producer that may never close.
	inst, beat, err := discoverInstanceKeepingEvidence(flagProject, flagPort)
	if err != nil {
		return noEditor(annotateNoEditorWith(beat, err, flagProject, flagPort))
	}

	// exec's --await_timeout deliberately outlives the normal request budget, so it must be known
	// before the one invocation deadline is set, and stdin is where it can come from.
	var execParams map[string]interface{}
	if category == "exec" {
		subArgs, err = readStdinIfPiped(subArgs, effectiveTimeout)
		if err != nil {
			return err
		}
		execParams, err = buildParams(subArgs, nil)
		if err != nil {
			return err
		}
		effectiveTimeout = execRequestTimeout(effectiveTimeout, execParams)
	}

	ctx := signalCtx
	if hasInvocationDeadline(category) && effectiveTimeout > 0 {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, time.Duration(effectiveTimeout)*time.Millisecond)
		defer cancel()
	}
	if err := ctx.Err(); err != nil {
		return noEditor(invocationDeadlineError(category, effectiveTimeout, err))
	}

	targetProject := flagProject
	if flagPort == 0 && targetProject == "" {
		targetProject = inst.ProjectPath
	}

	resolve := func() (*client.Instance, error) {
		if flagPort > 0 {
			return client.DiscoverInstance("", flagPort)
		}
		return client.DiscoverInstance(targetProject, 0)
	}

	// Wait loops must keep the heartbeat the scan deletes: that file is how a crash during the wait
	// reaches crashedWhileWaiting instead of looking like an editor that simply has not answered yet.
	guardProject := targetProject
	if flagPort > 0 {
		guardProject = ""
	}
	resolveForWait := resolveKeepingEvidence(guardProject, flagPort)

	var alive *client.Instance
	if waitOwnsStartup(category, inst.State) {
		alive = inst
	} else {
		alive, err = waitForAliveContext(ctx, resolveForWait, remainingTimeoutMs(ctx, effectiveTimeout))
		if err != nil {
			return noEditor(annotateNoEditor(err, targetProject, flagPort))
		}
	}
	gate := newVersionGate(category)
	if err := gate.check(alive); err != nil {
		return noEditor(err)
	}
	if category != modalCommand && isModal(alive) {
		return noEditor(modalBlockedError(alive, category, false))
	}

	send := sendWatchingModal(func(command string, params interface{}) (*client.CommandResponse, error) {
		return sendAfterReload(ctx, resolve, gate, command, params, flagTimeout)
	}, resolve)

	var resp *client.CommandResponse

	switch category {
	case "editor":
		resp, err = editorCmd(ctx, subArgs, send, resolve)
	case "test":
		currentInst, resolveErr := resolve()
		if resolveErr != nil {
			return resolveErr
		}
		testSend := sendWatchingModal(func(command string, params interface{}) (*client.CommandResponse, error) {
			return sendAfterReload(ctx, resolve, gate, command, params, 0)
		}, resolve)
		resp, err = testCmd(ctx, subArgs, testSend, currentInst.Port)
	case "build":
		// Polling a build must survive the build itself: the main thread is inside BuildPlayer for
		// the whole run, so the wait runs on build_status (served off the main thread) with its own
		// short request timeout rather than the global --timeout.
		buildPollSend := sendWatchingModal(func(command string, params interface{}) (*client.CommandResponse, error) {
			currentInst, err := resolve()
			if err != nil {
				return nil, err
			}
			return client.SendContext(ctx, currentInst, command, params, buildStatusRequestMs)
		}, resolve)
		resp, err = buildCmd(subArgs, send, buildPollSend, resolve, os.Stderr)
	case "verify":
		currentInst, resolveErr := resolve()
		if resolveErr != nil {
			return noEditor(resolveErr)
		}
		testSend := sendWatchingModal(func(command string, params interface{}) (*client.CommandResponse, error) {
			return sendAfterReload(ctx, resolve, gate, command, params, 0)
		}, resolve)
		exitNow(verifyCmd(ctx, subArgs, send, testSend, resolve, currentInst.Port, countedWriter{os.Stdout}))
	case waitCategory:
		exitNow(waitCmd(ctx, subArgs, send, resolveForWait, countedWriter{os.Stdout}))
	case "exec":
		// --await_timeout already widened the one invocation budget above, so the client never
		// gives up before the connector's own timed_out marker.
		resp, err = send("exec", execParams)
	default:
		var params map[string]interface{}
		params, err = buildParams(subArgs, nil)
		if err == nil {
			resp, err = send(category, params)
		}
	}

	// A call whose answer was lost to a reload ended with the editor mid-change and no verdict, so the
	// heartbeat is read back: whether the change landed is the caller's next question. Commands that
	// already report the state in their own words (editor play/stop, verify, test, build, wait) are out.
	lostToReload := answerLostToReload(resp, err) && stateNoteApplies(category, subArgs)

	// A call that never got a response object has its report carried by the returned error, so the
	// state line goes into that error rather than onto stderr before it.
	if lostToReload && resp == nil && err != nil {
		if note := stateAfterReloadNote(ctx, resolve); note != "" {
			err = fmt.Errorf("%w\n%s", err, strings.TrimRight(note, "\n"))
		}
		lostToReload = false
	}

	printStateAfterReload := func() {
		if !lostToReload {
			return
		}
		if note := stateAfterReloadNote(ctx, resolve); note != "" {
			fmt.Fprint(os.Stderr, note)
		}
	}

	if err != nil {
		printFailureReport(resp, err, printStateAfterReload)
		return err
	}

	printResponse(resp)
	printStateAfterReload()

	if !resp.Success {
		exitNow(1)
	}

	return nil
}

// printFailureReport prints a report a command built before failing (editor refresh --compile
// answers with its compile report and then exits non-zero), so the failing exit code does not hide
// the JSON the command already produced.
func printFailureReport(resp *client.CommandResponse, err error, note func()) {
	if err == nil || resp == nil {
		return
	}
	printResponse(resp)
	if note != nil {
		note()
	}
}

// stateNoteApplies excludes the commands that own their own reload wait: they would print the state
// twice, with different budgets, for the same reload. Only `editor play/stop` and
// `editor refresh --compile` wait for the state; the other editor subcommands do not, so they need
// the note like any other command.
func stateNoteApplies(category string, subArgs []string) bool {
	switch category {
	case "test", "verify", "build", waitCategory:
		return false
	case "editor":
		if len(subArgs) == 0 {
			return true
		}
		switch subArgs[0] {
		case "play", "stop":
			return false
		case "refresh":
			for _, arg := range subArgs[1:] {
				if arg == "--compile" {
					return false
				}
			}
		}
	}
	return true
}

// sendFn is the function signature for sending a command to Unity.
// Injected into each command function so they can be tested without a real Unity connection.
type sendFn func(command string, params interface{}) (*client.CommandResponse, error)

// retrySafeCommands may be sent again after an answer was lost: they are reads or idempotent
// diagnostics, so a second delivery cannot apply an effect twice. State-changing commands are not
// here; those are retried only when the connection never carried the request (RequestNotDelivered).
// The key is the command alone, so a tool with any mutating action stays out: `console --clear`
// and `modal --action answer` apply their effect again on a resend.
var retrySafeCommands = map[string]struct{}{
	"api":                 {},
	"burst_status":        {},
	"compile_diagnostics": {},
	"compile_status":      {},
	"list":                {},
}

// sendAfterReload makes one request against the freshest heartbeat and, when the connection died to a
// reload that moved the port or produced a newer beat, re-resolves and either retries once or tells the
// caller what to check. It never retries a state-changing command whose request may have been read, and
// it spends only the invocation's remaining budget (#9).
func sendAfterReload(ctx context.Context, resolve instanceResolver, gate *versionGate, command string, params interface{}, timeoutMs int) (*client.CommandResponse, error) {
	inst, err := resolve()
	if err != nil {
		return nil, err
	}
	if err := gate.check(inst); err != nil {
		return nil, err
	}

	resp, err := client.SendContext(ctx, inst, command, params, remainingTimeoutMs(ctx, timeoutMs))
	if err == nil {
		if !answerSaysNeverRan(resp) || ctx.Err() != nil {
			return resp, err
		}
		// The connector promises this request never reached a tool handler, so even a
		// state-changing command can be repeated once. Wait for the reloaded editor to answer
		// before spending the retry; the invocation's remaining budget bounds the wait.
		fresh, ready := waitForEditorAnswering(ctx, resolve, time.Duration(remainingTimeoutMs(ctx, timeoutMs))*time.Millisecond)
		if !ready {
			return resp, nil
		}
		if err := gate.check(fresh); err != nil {
			return resp, err
		}
		return client.SendContext(ctx, fresh, command, params, remainingTimeoutMs(ctx, timeoutMs))
	}
	if ctx.Err() != nil {
		return resp, err
	}

	var failure *client.ConnectionFailure
	if !errors.As(err, &failure) || failure.Class != client.FailureReload {
		return resp, err
	}

	if _, safe := retrySafeCommands[command]; safe {
		// The heartbeat can still say "reloading" when the old listener drops the connection, and a
		// retry sent then only earns another refusal. Wait for a fresh idle beat first; the
		// invocation's remaining budget bounds the wait, after which the original error stands.
		fresh, ready := waitForEditorAnswering(ctx, resolve, time.Duration(remainingTimeoutMs(ctx, timeoutMs))*time.Millisecond)
		if !ready {
			return resp, err
		}
		if err := gate.check(fresh); err != nil {
			return resp, err
		}
		return client.SendContext(ctx, fresh, command, params, remainingTimeoutMs(ctx, timeoutMs))
	}

	fresh := client.ReconnectInstance(inst)
	if fresh == nil || (fresh.Port == inst.Port && fresh.Timestamp <= inst.Timestamp) {
		return resp, err
	}
	// An explicit --port pins the target. Following the editor to another port would silently
	// retry against an instance the caller did not select.
	if flagPort > 0 && fresh.Port != flagPort {
		return resp, err
	}

	if !client.RequestNotDelivered(err) {
		return resp, fmt.Errorf("%w\n  retry:      the editor reloaded and now listens on port %d; the %s may have run — check before retrying", err, fresh.Port, command)
	}
	if err := gate.check(fresh); err != nil {
		return resp, err
	}
	return retryOnFresh(ctx, fresh, command, params, timeoutMs, err)
}

// retryOnFresh sends the retry but keeps the first failure reachable: when both attempts fail, the
// operator needs the original reload diagnosis, not only the second refusal.
func retryOnFresh(ctx context.Context, inst *client.Instance, command string, params interface{}, timeoutMs int, first error) (*client.CommandResponse, error) {
	resp, err := client.SendContext(ctx, inst, command, params, remainingTimeoutMs(ctx, timeoutMs))
	if err != nil {
		return resp, fmt.Errorf("%w\n  retry:      the reloaded editor did not answer the retry either: %v", first, err)
	}
	return resp, nil
}

// answerSaysNeverRan reports the connector's own guarantee that the tool handler was never entered:
// a domain reload interrupted a request while it was still queued, or the compile gate refused
// before dispatch. `started` must say so explicitly; a missing flag cannot distinguish "never ran"
// from "ran and lost its answer".
func answerSaysNeverRan(resp *client.CommandResponse) bool {
	if resp == nil || resp.Success {
		return false
	}
	switch client.ConnectorErrorCode(resp) {
	case codeDomainReload, codeCompiling:
	default:
		return false
	}
	started, ok := client.ConnectorResponseStarted(resp)
	return ok && !started
}

// waitForEditorAnswering polls until discovery finds a fresh heartbeat in an idle state. A
// never-ran answer (reload or compile gate) and a safe command's EOF can both arrive while the old
// record still says "reloading" or "compiling"; sending the retry then only earns another refusal.
// It is bounded by both budget and ctx, so the retry never outlives the invocation deadline.
func waitForEditorAnswering(ctx context.Context, resolve instanceResolver, budget time.Duration) (*client.Instance, bool) {
	deadline := time.Now().Add(budget)
	if d, ok := ctx.Deadline(); ok && d.Before(deadline) {
		deadline = d
	}

	for {
		inst, err := resolve()
		if err == nil && isIdleState(inst.State) && client.HeartbeatAge(inst) <= staleHeartbeatAge {
			return inst, true
		}

		remaining := time.Until(deadline)
		if remaining <= 0 {
			return nil, false
		}
		step := statusPollInterval
		if step > remaining {
			step = remaining
		}
		select {
		case <-ctx.Done():
			return nil, false
		case <-time.After(step):
		}
	}
}

func printResponse(resp *client.CommandResponse) {
	beginStdoutWrite()
	defer endStdoutWrite()
	printResponseTo(os.Stdout, os.Stderr, resp)
}

// printResponseTo writes the result to out and the error/meta lines to errw. Both are parameters so
// a test can assert the ordering a '2>&1 | tail -1' pipeline sees (#32).
func printResponseTo(out, errw io.Writer, resp *client.CommandResponse) {
	defer func() {
		if line := metaLine(resp.Meta, flagMeta); line != "" {
			fmt.Fprint(errw, line)
		}
	}()

	if !resp.Success {
		msg := resp.Message
		if msg == "" {
			msg = "unknown error"
		}
		if len(resp.Data) > 0 && string(resp.Data) != "null" {
			fmt.Fprintf(errw, "Error: %s\nDetails: %s\n", msg, string(resp.Data))
		} else {
			fmt.Fprintf(errw, "Error: %s\n", msg)
		}
		if hint := connectorErrorHint(resp); hint != "" {
			fmt.Fprintf(errw, "  next:      %s\n", hint)
		}
		return
	}

	if len(resp.Data) > 0 && string(resp.Data) != "null" {
		var pretty interface{}
		if json.Unmarshal(resp.Data, &pretty) == nil {
			// If data is a plain string, print it raw (preserves newlines for tree output etc.)
			if s, ok := pretty.(string); ok {
				// A result that already ends in a newline must not gain a second one: a
				// caller using '2>&1 | tail -1' would otherwise get a blank final line (#32).
				fmt.Fprint(out, oneTrailingNewline(s))
			} else {
				b, _ := json.MarshalIndent(pretty, "", "  ")
				fmt.Fprint(out, oneTrailingNewline(string(b)))
			}
		} else {
			fmt.Fprint(out, oneTrailingNewline(string(resp.Data)))
		}
	} else if resp.Message != "" {
		fmt.Fprint(out, oneTrailingNewline(resp.Message))
	}
}

// metaLine reports how the connector served a call (exec says whether it compiled the
// snippet or reused a cached one). Off unless --meta: agents commonly pipe exec through
// '2>&1 | tail -1', where any extra line lands in place of the result (#32).
func metaLine(meta json.RawMessage, enabled bool) string {
	if !enabled || len(meta) == 0 || string(meta) == "null" {
		return ""
	}
	return "meta: " + string(meta) + "\n"
}

// parseSubFlags parses --key value and --flag (boolean) pairs from subcommand args.
// Non-flag args (no "--" prefix) are silently ignored.
func parseSubFlags(args []string) (map[string]string, error) {
	flags, paramsJSON, _, err := parseFlagArgs(args)
	if err != nil {
		return nil, err
	}
	if len(paramsJSON) > 0 {
		flags["params"] = paramsJSON[len(paramsJSON)-1]
	}
	return flags, nil
}

// parseFlagArgs separates --flag values, repeated --params JSON strings, and positional args.
// Only --params may repeat (its objects are merged by buildParams); every other flag names one
// value, so a second occurrence is an error rather than a silent overwrite.
func parseFlagArgs(args []string) (flags map[string]string, paramsJSON, positional []string, err error) {
	flags = map[string]string{}
	for i := 0; i < len(args); i++ {
		a := args[i]
		if !strings.HasPrefix(a, "--") {
			positional = append(positional, a)
			continue
		}

		key := a[2:]
		value := "true"
		if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
			value = args[i+1]
			i++
		}
		if key == "params" {
			paramsJSON = append(paramsJSON, value)
			continue
		}
		if previous, exists := flags[key]; exists {
			return nil, nil, nil, fmt.Errorf("duplicate flag --%s (first %q, then %q); only --params may be repeated", key, previous, value)
		}
		flags[key] = value
	}
	return flags, paramsJSON, positional, nil
}

// defaultRequestTimeoutMs is the normal command budget: discovery, waiting for a fresh
// heartbeat, and the request itself all draw from this one number.
const defaultRequestTimeoutMs = 120000

// longCommandTimeoutMs is the default for commands that legitimately run for minutes: test and
// verify execute the project's tests, and editor refresh --compile waits out a compile. They are
// bounded rather than accidentally unbounded; --timeout (or an explicit --timeout 0) overrides it.
const longCommandTimeoutMs = 30 * 60 * 1000

// flagWasSet tells an explicit --timeout from the flag's default, which is what lets test/verify
// have a long default without changing what a bare --timeout 0 means.
func flagWasSet(name string) bool {
	set := false
	flag.CommandLine.Visit(func(f *flag.Flag) {
		if f.Name == name {
			set = true
		}
	})
	return set
}

func effectiveInvocationTimeout(category string, subArgs []string, explicit bool) int {
	if explicit {
		return flagTimeout
	}
	if category == "burst_status" {
		return burstWaitRequestMs(flagTimeout, subArgs)
	}
	if commandRunsLong(category, subArgs) {
		return longCommandTimeoutMs
	}
	return flagTimeout
}

// burstWaitRequestMs widens a burst_status invocation's budget so the client does not give up while
// the connector is still inside its own bounded wait (--wait is in seconds).
func burstWaitRequestMs(flagTimeoutMs int, subArgs []string) int {
	flags, err := parseSubFlags(subArgs)
	if err != nil {
		return flagTimeoutMs
	}
	raw, ok := flags["wait"]
	if !ok || flagTimeoutMs <= 0 {
		return flagTimeoutMs
	}
	seconds, convErr := strconv.ParseFloat(raw, 64)
	if convErr != nil || seconds <= 0 {
		return flagTimeoutMs
	}
	const waitSlackMs = 30000
	if want := int(seconds*1000) + waitSlackMs; want > flagTimeoutMs {
		return want
	}
	return flagTimeoutMs
}

func commandRunsLong(category string, subArgs []string) bool {
	if category == "test" || category == "verify" {
		return true
	}
	if category == "editor" && len(subArgs) > 0 && subArgs[0] == "refresh" {
		for _, a := range subArgs[1:] {
			if a == "--compile" {
				return true
			}
		}
	}
	return false
}

// hasInvocationDeadline names the two commands whose own flag is the budget: 'wait --timeout' is
// seconds of waiting, and a build's polling has to outlast BuildPlayer. Giving either the global
// request deadline would cut a legitimate run short.
func hasInvocationDeadline(category string) bool {
	return category != waitCategory && category != "build"
}

// remainingTimeoutMs is the budget left for the next request. Without a shared deadline (wait,
// build) it falls back to the command's own per-request value.
func remainingTimeoutMs(ctx context.Context, fallback int) int {
	deadline, ok := ctx.Deadline()
	if !ok {
		return fallback
	}
	remaining := time.Until(deadline)
	if remaining <= 0 {
		return 1
	}
	if ms := int(remaining.Milliseconds()); ms > 0 {
		return ms
	}
	return 1
}

func invocationDeadlineError(category string, timeoutMs int, cause error) error {
	if cause == context.DeadlineExceeded {
		return fmt.Errorf("timed out after %s waiting for Unity to answer %s (the editor may still be running it)",
			client.FormatBudget(time.Duration(timeoutMs)*time.Millisecond), category)
	}
	return fmt.Errorf("request cancelled: %w", cause)
}

// execRequestTimeout widens the HTTP request timeout so it outlasts the connector's
// own await budget (--await_timeout). A budget of 0 or less means the connector waits
// forever, which no client-side number can cover — leave the flag timeout alone.
func execRequestTimeout(flagTimeoutMs int, params map[string]interface{}) int {
	raw, ok := params["await_timeout"]
	if !ok {
		raw, ok = params["awaitTimeout"]
	}
	if !ok {
		return flagTimeoutMs
	}

	ms := 0
	switch v := raw.(type) {
	case int:
		ms = v
	case float64:
		ms = int(v)
	case string:
		if n, convErr := strconv.Atoi(v); convErr == nil {
			ms = n
		}
	}
	if ms <= 0 || flagTimeoutMs <= 0 {
		// 0 or less means "no client-side deadline" on either side.
		return flagTimeoutMs
	}

	const compileSlackMs = 30000
	if want := ms + compileSlackMs; want > flagTimeoutMs {
		return want
	}
	return flagTimeoutMs
}

// buildParams parses --flag value pairs and positional args from args and merges with base params.
// Repeated --params objects are merged in order, so a later key replaces an earlier one without
// dropping the rest of the parameters.
func buildParams(args []string, base map[string]interface{}) (map[string]interface{}, error) {
	flags, paramsJSON, positional, err := parseFlagArgs(args)
	if err != nil {
		return nil, err
	}

	params := map[string]interface{}{}
	for k, v := range base {
		params[k] = v
	}

	for i, raw := range paramsJSON {
		var object map[string]interface{}
		if jsonErr := json.Unmarshal([]byte(raw), &object); jsonErr != nil {
			return nil, fmt.Errorf("invalid JSON in --params #%d: %w", i+1, jsonErr)
		}
		for k, v := range object {
			params[k] = v
		}
	}
	for k, v := range flags {
		if _, exists := params[k]; exists {
			continue
		}
		if n, err := strconv.Atoi(v); err == nil {
			params[k] = n
		} else if v == "true" {
			params[k] = true
		} else if v == "false" {
			params[k] = false
		} else {
			params[k] = v
		}
	}

	if len(positional) > 0 {
		params["args"] = positional
	}

	return params, nil
}

// readStdinIfPiped reads stdin when piped and prepends it as the first positional arg. The read is
// bounded by timeoutMs (0 = no bound): a producer that never closes must not outlast --timeout.
func readStdinIfPiped(args []string, timeoutMs int) ([]string, error) {
	info, err := os.Stdin.Stat()
	if err != nil {
		return args, nil
	}
	if info.Mode()&os.ModeCharDevice != 0 {
		return args, nil // interactive terminal, not piped
	}

	type readResult struct {
		data []byte
		err  error
	}
	done := make(chan readResult, 1)
	go func() {
		data, err := io.ReadAll(os.Stdin)
		done <- readResult{data, err}
	}()

	var res readResult
	if timeoutMs > 0 {
		budget := time.Duration(timeoutMs) * time.Millisecond
		select {
		case res = <-done:
		case <-time.After(budget):
			return args, fmt.Errorf("timed out after %s reading piped stdin (--timeout); close the producer or pass the snippet as an argument",
				client.FormatBudget(budget))
		}
	} else {
		res = <-done
	}
	if res.err != nil || len(res.data) == 0 {
		return args, nil
	}
	code := strings.TrimRight(string(res.data), "\n\r")
	return append([]string{code}, args...), nil
}

// splitArgs separates global flags from subcommand args.
// Global flags must be parsed by flag.CommandLine before the subcommand runs.
//
// --timeout is the exception after the command name: for `wait` it is that command's own budget in
// seconds, while the global one is an HTTP request timeout in milliseconds. Stealing
// `wait --timeout 120` would hand wait a 120 ms request timeout and no budget at all. The global
// flag still works when it comes before the command name.
func splitArgs(args []string) (flags, commands []string) {
	command := ""
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--ignore-version-mismatch", "--meta":
			flags = append(flags, args[i])
		case "--port", "--project":
			flags = append(flags, args[i])
			if i+1 < len(args) {
				i++
				flags = append(flags, args[i])
			}
		case "--timeout":
			if command == waitCategory {
				commands = append(commands, args[i])
				if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
					i++
					commands = append(commands, args[i])
				}
				continue
			}
			flags = append(flags, args[i])
			if i+1 < len(args) {
				i++
				flags = append(flags, args[i])
			}
		default:
			if command == "" {
				command = args[i]
			}
			commands = append(commands, args[i])
		}
	}
	return
}

func printHelp() {
	fmt.Print(`unity-cli ` + Version + ` — Control Unity Editor from the command line

Usage: unity-cli <command> [subcommand] [options]

Editor Control:
  editor play [--wait|--no-wait]
                                Enter play mode; answers once the editor reports Play mode
  editor stop [--no-wait]       Exit play mode; answers once the editor reports Edit mode
  editor pause                  Toggle pause/resume (play mode only)
  editor refresh [--force]      Refresh asset database (blocked in play mode unless forced)
  editor refresh --compile [clean]
                                Recompile scripts and wait until done; "clean" also discards
                                Unity's compiled-assembly cache
  editor quit [--save|--discard] [--force]
                                Quit Unity and wait until the process has exited
  editor restart [--clean] [--save|--discard] [--force] [--editor-path <exe>]
                                Quit, optionally clean Library/Temp, relaunch, wait until ready

Console:
  console                       Newest 50 entries, capped stack traces (default)
  console --lines 20            Newest N entries
  console --type error,warning,log   Filter by log types (comma-separated)
  console --stacktrace full     Stack trace: none, user (default), full
  console --full                No size caps (the stacktrace mode still filters frames)
  console --clear               Clear console

Execute C#:
  exec "<code>"                 Run C# code in Unity (return required for output)
  echo '<code>' | exec          Pipe code via stdin (avoids shell escaping)
  exec "<code>" --usings x,y    Add extra using directives
  exec "<code>" --no_default_usings     Drop the default usings and aliases
  exec "<code>" --await_timeout 60000   Wait longer for a Task/coroutine to finish
  exec "<code>" --skip_compile_check    Run even while Unity reports compile errors

  Examples:
    exec "Time.time"
    exec "GameObject.FindObjectsOfType<Camera>().Length"
    exec "var go = new GameObject(\"Test\"); return go.name;"
    exec "await Task.Delay(100); return EditorApplication.timeSinceStartup;"

Menu:
  menu "<path>"                 Execute Unity menu item by path

  Examples:
    menu "File/Save Project"
    menu "Assets/Refresh"

Screenshot:
  screenshot                          Game view in Play mode, Scene view in Edit mode
  screenshot --mode gameview          The running game's frame, OnGUI/IMGUI included (Play mode)
  screenshot --mode camera            Camera.main rendered offscreen (no OnGUI, no overlays)
  screenshot --mode scene             Scene view camera rendered offscreen
  screenshot --output_path <path>     Custom output path

Reserialize:
  reserialize [path...]          Force reserialize (no args = entire project)

  Examples:
    reserialize                                                    Reserialize entire project
    reserialize Assets/Scenes/Main.unity
    reserialize Assets/Prefabs/A.prefab Assets/Prefabs/B.prefab

Burst:
  clear_burst_cache              Delete Library/BurstCache (Burst recompiles on next use)
  clear_burst_cache --scope jit  Only the JIT library cache, keep AOT settings hashes
  clear_burst_cache --dry_run    List files and size without deleting
  burst_status                   Is Burst done compiling, what compiled, what failed (BCxxxx)
  burst_status --wait 60         Wait up to 60s for Burst to finish, then report

  Examples:
    clear_burst_cache
    clear_burst_cache --dry_run
    clear_burst_cache --scope jit
    burst_status --wait 60
    burst_status --assembly MyGame.Runtime --list

Tests:
  test                            Run EditMode tests (default)
  test --mode PlayMode            Run PlayMode tests
  test --filter <name>            Filter by namespace, class, or full test name
  test --allow-dirty-scenes       Run even when open scenes have unsaved changes
  test --auto-save-scenes         Save dirty open scenes before running tests
  test --assembly <a,b>           Only tests in these assemblies
  test --allow-stale              Run even when an assembly is older than its sources

API lookup (reflection over the assemblies the editor loaded):
  api <Type>                  Kind, namespace, assembly, base, interfaces, member summary
  api <Type>.<Member>         Every overload with its C# signature (params, generics, constraints)
  api <Type> --assembly <a>   Only assemblies whose name contains <a>
  api <Type> --docs           Add the XML doc <summary> where Unity ships the doc file

  Examples:
    api Vector3.Lerp
    api EditorApplication.isCompiling
    api List.Add --limit 5
    api UnityEngine.Object.FindObjectOfType --docs

Verify (compile + tests, brief output for agents):
  verify                          Compile, list errors in your code, run EditMode tests
  verify --no-tests               Stop after the compile
  verify --assembly <a,b>         Only these test assemblies (also --filter, --mode)
  verify --no-burst               Skip the Burst step
  verify --allow-stale            Run tests even when an assembly is older than its sources

Wait / compile provenance (what a result covers):
  wait                            Block until the editor is idle, then print its state + compile line
  wait --compile                  Also require that your edit is in the last compile
  wait --timeout 60               Give up after 60 seconds (exit 1)
  compile_status                  Just the compile provenance, no waiting

  Every compile result carries one provenance line:
    compiled 14:02:11 (3 s ago, took 2.2 s) · 1 source file changed since previous compile · rebuilt: Game.Tests.Editor
    stale: Assets/Foo.cs modified after this compile — run verify again
    stale assembly: Game.Tests.dll (built 14:01:50) is older than Assets/Game/Tests.cs (14:04:02) — run unity-cli editor refresh --compile

  A stale assembly means the DLL a test run would load is older than its sources; verify and test
  refuse by default, because old code answers exactly like current code. --allow-stale overrides it.

  An error that names something the compiler could not find, or an API that is obsolete, prints a
  hint under it with the closest real answer from the loaded assemblies:
    Assets/Foo.cs:12:9 CS1061 'Transform' does not contain a definition for 'positon' ...
      hint: closest: Transform.position, Transform.localPosition, Rigidbody.position

Build (long-running; the editor stays observable while it runs):
  build --profile <name> --output <path>      Start a player build job, watch progress, exit 0/1
  build --target StandaloneOSX --output <p>   Build by BuildTarget instead of a BuildProfile
  build --no-wait ...                         Return the job id right away
  build status [id]                           Poll one job once (default: the newest)

  Examples:
    build --profile macOS_dev --output Builds/Mac/App.app
    build --profile macOS --output Builds/Mac/App.app --development
    build --target StandaloneLinux64 --output Builds/Linux/App --scenes Assets/Main.unity
    build status

Profiler:
  profiler hierarchy              Top-level profiler samples (last frame)
  profiler hierarchy --depth 5    Recursive drill-down (0=unlimited)
  profiler hierarchy --root Name  Set root by name (substring match)
  profiler hierarchy --frames 30  Average over last 30 frames
  profiler hierarchy --parent 5   Drill into item by ID
  profiler hierarchy --min 0.5    Filter items below 0.5ms
  profiler hierarchy --sort self  Sort by self time
  profiler enable                Start profiler recording
  profiler disable               Stop profiler recording
  profiler status                Show profiler state
  profiler clear                 Clear all captured frames

Input Injection (Play mode):
  input key <name> [press|release|tap]   Keyboard key by Key enum name (Space, W, LeftShift)
  input mouse [--x --y | --dx --dy] [--button left|right|middle|forward|back] [--mode ...]
                                Game view pixels (0,0 = top-left); --dx/--dy grow right/down
  input gamepad|axis --control <name> [--x --y | --value] [--mode set|press|release|tap]
  input touch [--x --y] [--mode began|moved|stationary|ended|tap] [--index N]
                                Same top-left Game view pixels as mouse
  input state                      Report pressed keys, mouse position, gamepads, focus, settings
  input reset                      Release everything, remove virtual devices, restore settings

  Injected state reaches the NEW Input System only; UnityEngine.Input and IMGUI Event code
  will not see it. Requires play mode except for state/reset. See 'unity-cli input --help'.

Modal Dialogs (macOS):
  modal                         Show the modal dialog blocking the editor
  modal answer --button <title> Click a button by title (or --index <n>)

Startup Dialogs (macOS host, no connector needed):
  dialogs                       List Unity's windows, their texts and button names (JSON)
  dialogs --click "<title>"    Press a button by title (--window "<title>" to pick the window)
  Use this when status says editor_starting_or_blocked or editor_unresponsive: the editor is
  open but its connector has not loaded or stopped answering, so 'modal' cannot see anything.
  Needs macOS + Accessibility permission.

Custom Tools:
  list                          List all registered tools with parameter schemas
  <name>                        Call a custom tool directly
  <name> --params '{"k":"v"}'   Call with JSON parameters (repeatable; objects merge, later keys win)

Status:
  status                        Show Unity Editor state (ready, compiling, etc.)
                                editor_starting_or_blocked means Unity is open but the connector
                                has not loaded; editor_unresponsive means it loaded this session and
                                stopped answering; see "unity-cli dialogs --help"
                                crashed means the editor process died instead: the report (on stderr,
                                with stdout left empty) names the crash log and its first exception
                                line, and the command exits non-zero

Update:
  update                        Update to the latest version
  update --check                Check for updates without installing

Global Options:
  --port <N>          Select Unity instance by active heartbeat port
  --project <path>    Select Unity instance by project path
  --timeout <ms>      One budget per invocation, in ms, shared by discovery, waiting for a fresh
                      heartbeat, and the request (default: 120000). A client deadline says
                      "timed out ... (the editor may still be running it)", not "cannot connect".
                      test, verify and 'editor refresh --compile' default to 1800000 (30 min);
                      an explicit 0 means no deadline. 'wait --timeout' and build polling keep
                      their own budgets.
                      The remaining budget is sent to the connector, which answers a queued command
                      before this deadline instead of leaving the connection silent: "... was queued
                      but the main thread did not pick it up within Ns ... The command did not run".
                      Per call, --params '{"queue_deadline_ms":300000}' overrides that wait.
  --ignore-version-mismatch
                      Run even when CLI and connector versions differ
  --meta              Print the response's meta (how the call was served) to stderr

Use "unity-cli <command> --help" for more information about a command.

Notes:
  - Unity must be open with the Connector package installed
  - Multiple Unity instances: use --port or --project to select
  - Custom tools: any [UnityCliTool] class is auto-discovered
  - A subcommand flag other than --params may be given once; repeating it is an error, not a silent overwrite
  - Only exec, build and custom tools read --params; test, verify, editor and wait take their own flags
  - Run 'list' to see all available tools
  - Every accepted command gets one answer: a queued command that the main thread never starts is
    answered as main_thread_busy, one still queued when Unity reloads as domain_reload (retry it),
    and one whose tool was already running as reload_during_command (it ran; only its return value
    was lost, so check the state before repeating it)
`)
}

func printTopicHelp(topic string) {
	switch topic {
	case "input":
		fmt.Print(`Usage: unity-cli input <key|mouse|gamepad|axis|touch|state|reset> [options]

Injects device state into the new Input System (com.unity.inputsystem) and pumps the player loop
so the player update consumes the events inside the same call — game Update() sees fresh press/release
edges. No OS keystrokes are involved.

Actions:
  key <name> [press|release|tap]   Key enum name: Space, W, A, LeftShift, Enter, Digit1 …
                                   Aliases: ctrl, alt, shift, cmd, esc, return, up, down.
                                   Default mode: press. --hold_ms / --hold_frames tune a tap.
  mouse                            --x/--y (Game view pixels, 0,0 = top-left; --dx/--dy grow
                                   right/down), --button left|right|middle|forward|back,
                                   --scroll_x/--scroll_y, --mode press|release|tap|click|move|set.
  gamepad / axis                   --control leftStick|rightStick|dpad|leftTrigger|buttonSouth|…
                                   --x/--y for sticks, --value for buttons/triggers (default 1),
                                   --mode set|press|release|tap. Adds a virtual Gamepad on demand.
  touch                            --x/--y in the same top-left Game view pixels as mouse,
                                   --mode began|moved|stationary|ended|tap, --index N.
                                   Adds a virtual Touchscreen on demand.
  state                            Read-only: pressed keys, mouse position/buttons/scroll,
                                   gamepad axes, touch phase, focus flags and InputSettings.
  reset                            Release injected state, remove the virtual devices the tool
                                   added, restore the InputSettings it changed.

Response fields:
  consumed                The device state now matches the request (isPressed / value read back).
  fresh_press_frames      Player-loop input updates that saw the control as freshly pressed — what
                          game Update() sees through wasPressedThisFrame. The tool's own readback
                          updates are not counted, and was_pressed_this_frame is read after them, so
                          it is usually false.
  held_state / state      The control read back while held and after the final pump.
  position / space        Mouse/touch position echoed in the caller's space, top_left_px (Game view
                          pixels, 0,0 at the top-left). 'state' reports mouse/touch positions in the
                          same space under position_space.
  focus.*                game_view_focused (a Game View window has editor focus),
                          application_is_focused (what the Input System's own focus gating uses),
                          editor_focused (Unity is the frontmost application).
  settings.*              InputSettings in effect, the values saved before the override, and
                          held_controls — what is still keeping the override in place.

Mouse/touch Y is converted to the Input System's bottom-left screen space with the Game view render
height (the value Screen.height reports once the player loop has rendered); replies keep the top-left
coordinates the caller used.

Injected events are visible to the new Input System only. Code reading UnityEngine.Input or
IMGUI Event sees nothing; every injection response repeats this in legacy_input_note.
key/mouse/gamepad (or its alias axis)/touch require play mode; state and reset work any time. While
the tool holds injected state it sets editorInputBehaviorInPlayMode=AllDeviceInputAlwaysGoesToGameView
and backgroundBehavior=IgnoreFocus, because with the defaults the Input System defers or drops
keyboard/pointer events while the Game View lacks focus. The values are restored when nothing is
held anymore, on 'input reset', and when play mode exits.

Examples:
  unity-cli editor play --wait
  unity-cli input key Space press
  unity-cli input state
  unity-cli input key Space release
  unity-cli input key W tap --hold_ms 200
  unity-cli input mouse --x 100 --y 200
  unity-cli input mouse --button left --mode click
  unity-cli input gamepad --control leftStick --x 1 --y 0
  unity-cli input reset
  unity-cli editor stop
`)
	case "editor":
		fmt.Print(`Usage: unity-cli editor <play|stop|pause|refresh|quit|restart> [options]

Subcommands:
  play [--wait|--no-wait]  Enter play mode.
                      Answers once the heartbeat reports the editor is in Play mode, which is
                      how a change that reloads the domain is confirmed: the connector that
                      asked for the reload cannot report its own result across it.
                      --wait also asks the connector to wait for the state change itself
                      (skipped when that change reloads the domain).
  stop [--no-wait]    Exit play mode. No effect if not playing.
                      Confirmed from the heartbeat like play.
    --no-wait         Return as soon as the change is requested, without reading the
                      resulting state back.
  pause               Toggle pause. Only works during play mode.
  refresh             Refresh AssetDatabase (reimport changed assets).
                      Blocked in play mode unless --force is set.
    --compile [clean] Recompile scripts and wait until compilation finishes.
                      "clean" also discards Unity's compiled-assembly cache (Bee) — the hammer for a
                      cache you believe is lying, though it can still leave Library/ScriptAssemblies
                      mtimes untouched because Bee restores its cached node output.
                      Answers with one report: compile_requested, compile_succeeded,
                      error_count, errors[], timed_out, elapsed_ms, plus nothing_to_compile
                      when the scripts were already up to date. The report also carries the
                      compile provenance (provenance, provenance_line, and stale_line when a
                      source file was written after the compile), so it says which edit it covers.
                      If an assembly is still older than its sources, one CleanBuildCache rebuild
                      is requested and stale_after_compile reports whether that fixed it.
                      error_count is the true total; errors[] is capped at 20 entries and the
                      report says when it was cut. Exit 1 when the compile failed or timed out; the
                      report is still printed. "clean" is refused on a connector that does not
                      advertise support, instead of silently doing a plain compile.
    --keep-console    With --compile, don't clear the console first (the default clear keeps
                      stale errors from a previous compile out of the report).
    --force           Allow refresh during play mode and force asset update.
  quit                Quit Unity and wait until its heartbeat reports stopped or
                      stops advancing. Unsaved scene changes are discarded unless --save.
    --save            Save open scenes and assets before quitting.
    --discard         Discard unsaved scene changes explicitly (the default).
    --force           If Unity ignores the quit or a modal dialog blocks it,
                      terminate the process (SIGTERM on unix).
  restart             Quit Unity, relaunch the same project, and wait until ready.
                      Also works when no editor is running (plain launch).
                      Must run on the host that can signal and launch Unity; from
                      a container it refuses before quitting the editor.
    --save            Save open scenes and assets before quitting.
    --discard         Discard unsaved scene changes explicitly (the default).
    --force           Terminate Unity if it does not quit gracefully.
    --clean           Delete Library/ and Temp/ before relaunching (full reimport).
    --editor-path <exe>  Unity executable to launch (default: $UNITY_EDITOR or Unity Hub).
                      --timeout <ms> overrides the quit and startup budgets
                      (default: 60s to quit; 5m to start, 15m with --clean).

Examples:
  unity-cli editor play --wait
  unity-cli editor stop
  unity-cli editor refresh --compile
  unity-cli editor refresh --force
  unity-cli editor quit --save
  unity-cli editor quit --save --force
  unity-cli editor quit --discard
  unity-cli editor restart
  unity-cli editor restart --clean
`)
	case "console":
		fmt.Print(`Usage: unity-cli console [options]

Read Unity console log entries.

Small by default so a chatty editor fits in one tool output: the newest 50 entries, at most 6
stack frames each, at most 1000 characters per entry and 15 KB of entry text in total. The reply
says what was cut (truncated, entries_total/entries_returned/entries_omitted, frames_omitted).

Options:
  --lines <N>          Newest N entries (default 50; 0 = no limit)
  --type <types>       Comma-separated log types: error, warning, log (default: error,warning,log)
  --stacktrace <mode>  none: first line only
                        user: user-code frames only, Unity/runtime frames collapsed (default)
                        full: every frame, still capped by --frames unless --full
  --frames <N>         Stack frames kept per entry (default 6; 0 = none, -1 = all)
  --entry_chars <N>    Max characters per entry (default 1000; 0 = no limit)
  --max_bytes <N>      Max total bytes of entry text (default 15360; 0 = no limit)
  --full               Remove every size cap (stacktrace mode still filters frames)
  --clear              Clear console

Examples:
  unity-cli console
  unity-cli console --lines 20 --type error,warning,log
  unity-cli console --stacktrace none --type error
  unity-cli console --type error --stacktrace full --frames -1
  unity-cli console --full
  unity-cli console --clear
`)
	case "exec":
		fmt.Print(`Usage: unity-cli exec "<code>" [options]

Execute C# code inside Unity Editor. Full access to UnityEngine,
UnityEditor, and all loaded assemblies.

Use 'return' to get output. Add --usings for types outside default namespaces.
Using directives at the top of the snippet are moved above the generated method,
and an alias there overrides the matching default alias.

Options:
  --usings <ns1,ns2>   Add extra using directives (namespaces or "A = B" aliases)
  --no_default_usings  Drop the default usings and aliases; combine with --usings
                       to supply the whole set
  --await_timeout <ms> Max ms to await a returned Task / pump a returned coroutine
                       (default 30000; 0 or negative waits forever)
  --skip_compile_check Run even when Unity reports outstanding compile errors. The
                       central gate normally blocks every tool (including exec) on
                       errors from the current compile; this is its escape hatch.
  --csc <path>         Path to csc compiler (csc.dll or csc.exe). Auto-detected if omitted.
  --dotnet <path>      Path to dotnet runtime. Auto-detected if omitted.
  --meta               Print the response's meta (compile cache state, timing) to stderr.

Default usings: System, System.Collections, System.Collections.Generic,
  System.IO, System.Linq, System.Reflection, System.Threading.Tasks, UnityEngine,
  UnityEngine.SceneManagement, UnityEditor, UnityEditor.SceneManagement,
  UnityEditorInternal
Default aliases: Object = UnityEngine.Object, Random = UnityEngine.Random,
  Debug = UnityEngine.Debug

Examples:
  unity-cli exec "return 1+1;"
  unity-cli exec "return Application.dataPath;"
  echo 'return EditorSceneManager.GetActiveScene().name;' | unity-cli exec
  echo 'Debug.Log("hello"); return null;' | unity-cli exec
  unity-cli exec "return World.All.Count;" --usings Unity.Entities
  unity-cli exec "using Object = System.Object; return typeof(Object).FullName;"
  unity-cli exec "return typeof(Object).FullName;" --no_default_usings --usings System
  unity-cli exec "await Task.Delay(100); return 1;"
  unity-cli exec "return Task.Run(() => AssetDatabase.Refresh());" --await_timeout 60000

Compile cache:
  The snippet is compiled by csc in a subprocess without freezing the editor, and an
  identical call (same body, same --usings, same reference set) reuses the assembly that
  is already loaded instead of compiling again. The response carries that as
  meta: {"compile":"compiled|cached","compile_ms":N,"reference_assemblies":N};
  'unity-cli exec --meta ...' prints it to stderr (off by default so '2>&1' pipelines
  keep seeing only the result).

Async:
  'await' works in the body, and a Task you return is awaited for you — the response
  carries its result, not the Task object. Nothing blocks the editor's main thread,
  so work that needs the editor to keep ticking still completes.

  A body using 'yield return' runs as a coroutine: 'yield return null' waits one
  editor update, a yielded Task is awaited, and any other yielded value is collected
  and returned as a list. Unity YieldInstructions (WaitForSeconds, WaitForEndOfFrame)
  are rejected rather than ignored — use 'await Task.Delay(ms)' to wait for time.

  If the wait outlives --await_timeout the response is an error carrying
  {"timed_out": true, "status": …}; the operation keeps running in the editor and its
  result is not observable from that response. A domain reload drops a pending result
  silently — park it in a static field or ScriptableObject and read it back.

Stdin:
  Pipe code via stdin to avoid shell escaping issues.
  echo '<code>' | unity-cli exec [--usings ns1,ns2]

Notes:
  - Use 'return' for output, 'return null;' for void operations
`)
	case "menu":
		fmt.Print(`Usage: unity-cli menu "<path>"

Execute a Unity menu item by its path.

Examples:
  unity-cli menu "File/Save Project"
  unity-cli menu "Assets/Refresh"
  unity-cli menu "Window/General/Console"

Note: File/Quit is blocked for safety.
`)
	case "screenshot":
		fmt.Print(`Usage: unity-cli screenshot [options]

Capture a PNG of what Unity is rendering. No mode reads screen pixels of an
editor window, so a window moved off every display cannot break a capture.

Options:
  --mode <mode>       gameview (default in Play mode), scene (default in Edit
                      mode), camera
                      gameview - the frame the running game produces, captured by
                                 ScreenCapture at the Game view's own resolution.
                                 Includes the game's OnGUI/IMGUI HUDs; excludes
                                 the Game view tab's own editor chrome. Needs Play
                                 mode: in Edit mode there is no game frame, and
                                 Unity never writes the file.
                      camera   - Camera.main rendered offscreen into a render
                                 texture: no OnGUI/IMGUI, no editor overlays.
                      scene    - the Scene view camera rendered offscreen: no
                                 gizmos, no Handles, no Scene-view UI.
  --width <N>        Image width for mode camera/scene (default: 1920)
  --height <N>       Image height for mode camera/scene (default: 1080)
                     (mode gameview keeps the Game view's resolution)
  --output_path <path>  Output path, absolute or relative to project root
                        (default: Screenshots/screenshot.png)

Examples:
  unity-cli screenshot
  unity-cli editor play --wait
  unity-cli screenshot --mode gameview --output_path Temp/shots/hud.png
  unity-cli screenshot --mode camera --width 3840 --height 2160
  unity-cli screenshot --output_path captures/my_scene.png

Note: --view is the older spelling of --mode: --view scene is --mode scene, but
--view game is --mode camera (the offscreen render), not the Game view.
`)
	case "reserialize":
		fmt.Print(`Usage: unity-cli reserialize [path...]

Force Unity to reserialize assets through its own YAML serializer.
Run after editing .prefab, .unity, .asset, or .mat files as text.
No arguments = reserialize the entire project.

Examples:
  unity-cli reserialize
  unity-cli reserialize Assets/Prefabs/Player.prefab
  unity-cli reserialize Assets/Scenes/Main.unity Assets/Scenes/Lobby.unity
`)
	case "clear_burst_cache":
		fmt.Print(`Usage: unity-cli clear_burst_cache [options]

Delete the project's Burst compilation cache (<project>/Library/BurstCache) and
report how many files and bytes were removed.

Burst rebuilds the cache on its next compile, so this is safe to run whenever
JIT-compiled jobs look stale. Only paths inside Library/BurstCache are touched —
no path argument is accepted.

Options:
  --scope <all|jit>   all (default): the whole Library/BurstCache
                      jit: only Library/BurstCache/JIT, keeping AotSettings*.hash
  --dry_run           List what would be deleted without deleting anything

Locked files are reported per path instead of aborting the whole clear; on
Windows the JIT cache can stay mapped until the editor quits.

Examples:
  unity-cli clear_burst_cache
  unity-cli clear_burst_cache --dry_run
  unity-cli clear_burst_cache --scope jit
`)
	case "burst_status":
		fmt.Print(`Usage: unity-cli burst_status [options]

Ask Burst what it has compiled. Burst compiles outside Unity's script compilation,
so a clean C# compile and a quiet console do not prove a [BurstCompile] job was
compiled — and a Burst (BCxxxx) error can appear after the compile finished.

Reports whether Burst's own queue is idle, and the [BurstCompile] targets in the
project's own assemblies (package assemblies are left out unless --include_packages)
with status compiled / failed / not-compiled-yet / unknown. Default output is short:
counts plus the Burst errors, each as file:line:col CODE message.

A filter (--type, --namespace, --assembly) turns the listing on: the matching targets
with their status.

How status is decided: Burst eager-compiles every target it knows when an assembly
finishes compiling, so "its queue is idle" plus "no error names this target" is what
compiled means here. --compile forces a Burst recompile now, so the answer comes from
this call (method: forced_recompile) instead of the last script compile (method: eager).

Needs com.unity.burst installed: without it the tool is not built and the command is
unknown. verify runs this step automatically; see unity-cli help verify.

Options:
  --wait <seconds>        Wait for Burst to go idle (default 0 = answer immediately;
                          with --compile the default is 60)
  --compile               Force Burst to recompile its targets now
  --type <name>           Only targets whose type or target name contains this
  --namespace <ns>        Only targets in this namespace or below
  --assembly <a,b>        Only targets in these assemblies (comma-separated)
  --list                  List matched targets even without a filter
  --include_packages      Include targets in package assemblies too
  --limit <n>             Max errors and targets to list (default 20)

Examples:
  unity-cli burst_status
  unity-cli burst_status --wait 60
  unity-cli burst_status --type ScaleValuesJob
  unity-cli burst_status --assembly MyGame.Runtime --list
  unity-cli burst_status --compile --wait 120 --timeout 130000
`)
	case "profiler":
		fmt.Print(`Usage: unity-cli profiler <subcommand> [options]

Subcommands:
  hierarchy             Top-level profiler samples (last frame)
    --depth <N>         Recursive depth (0=unlimited, default: 1)
    --root <name>       Set root by name (substring match, searches full tree)
    --frames <N>        Average over last N frames (flat output, sorted by time)
    --from <N>          Start frame index for range average
    --to <N>            End frame index for range average
    --parent <ID>       Drill into item by ID
    --min <ms>          Filter items below threshold
    --sort <col>        Sort by: total (default), self, calls
    --max <N>           Max children per level (default: 30)
    --frame <N>         Specific frame index
    --thread <N>        Thread index (0=main)
  enable                Start profiler recording
  disable               Stop profiler recording
  status                Show profiler state
  clear                 Clear all captured frames

Examples:
  unity-cli profiler hierarchy --depth 3
  unity-cli profiler hierarchy --root SimulationSystem --depth 3
  unity-cli profiler hierarchy --frames 30 --min 0.5 --sort self
  unity-cli profiler enable
`)
	case "test":
		fmt.Print(`Usage: unity-cli test [options]

Run Unity tests via the Test Runner API.

Options:
  --mode <EditMode|PlayMode>    Test mode (default: EditMode)
  --filter <name>               Filter by namespace, class, or full test name
                                Must be the full path (e.g. MyNamespace.MyClass)
  --assembly <a,b>              Only tests in these assemblies (comma-separated)
  --allow-dirty-scenes          Run even when open scenes have unsaved changes
  --auto-save-scenes            Save dirty open scenes before running tests
  --allow-stale                 Run even when an assembly under test is older than its sources

Tests legitimately run for minutes, so this command's invocation budget defaults to 30 minutes
instead of the global 120 s. --timeout <ms> overrides it; --timeout 0 means no deadline.

EditMode tests hold the connection open and return results directly.
PlayMode tests return immediately and poll a results file (domain reload safe).
By default, tests are blocked when any open scene has unsaved changes, and when an assembly under
test is older than its sources (a stale DLL runs old code). Fix that with
'unity-cli editor refresh --compile', or pass --allow-stale deliberately.

Requires the Unity Test Framework package (com.unity.test-framework).

Examples:
  unity-cli test
  unity-cli test --mode PlayMode
  unity-cli test --auto-save-scenes
  unity-cli test --filter MyNamespace.MyTests
  unity-cli test --assembly MyGame.Tests.Editor
  unity-cli test --mode EditMode --filter MyNamespace.MyTests.SpecificTest
`)
	case "verify":
		fmt.Print(`Usage: unity-cli verify [options]

Check that the project compiles and its tests pass, in one call, with output
brief enough for an agent's context:

  1. Clears the console (a Burst error stays there after its code changes;
     Burst logs a live one again on the next compile). --keep-console skips it.
  2. Refreshes assets, compiles, and waits for it to finish. If no compilation is observed
     (the editor was already ready), verify says so and reports the editor's current state.
  3. Lists this compile's errors in your code as file:line:col CODE message. An error that names a
     member or type the compiler could not find, or an API that is obsolete (CS0117, CS1061, CS0246,
     CS0234, CS0619, CS0618), prints an indented 'hint:' line under it with the closest real
     members/types, or the replacement the obsolete notice points at. Errors in Library/ or
     PackageCache/ are listed only when nothing else is.
  4. Asks Burst (burst_status) whether it is still compiling, what compiled, and
     what failed, waiting up to --burst-wait seconds. Burst errors print the same
     way as compile errors and fail verify. --no-burst skips the step; projects
     without Burst print nothing.
  5. Unless --no-tests: saves dirty open scenes and runs the tests, then
     prints passed/total and the first line of each failure.

Every compile result says which compile it reflects, so a clean answer is told apart from a stale
one. verify prints the connector's provenance line after the compile verdict:

  compile: ok
  compiled 14:02:11 (3 s ago, took 2.2 s) · 1 source file changed since previous compile · rebuilt: Game.Tests.Editor
  stale: Assets/Foo.cs modified after this compile — run verify again
  stale assembly: Game.Tests.dll (built 14:01:50) is older than Assets/Game/Tests.cs (14:04:02) — run unity-cli editor refresh --compile
  not stale: Game.Tests.dll (built 14:01:50) is older than Assets/Game/Tests.cs (14:04:02), but the compile that started 14:05:10 read that source and kept the DLL; a moved mtime alone does not mean the DLL is out of date

The stale line means a source file under Assets/ or a file:/embedded package was written after that
compile: the edit is in the project but not in the result. A stale assembly means the DLL the tests
would load is older than its sources; verify refuses to run tests until a clean refresh, unless
--allow-stale is passed. The "not stale" line is the opposite case: Unity does not rewrite a DLL whose
contents it considers current, so a source whose timestamp merely moved (touch, re-checkout) stays
older than its DLL even though a compile read it — it is reported, and the tests run. --json carries
the same fields under compile.provenance (unix ms and ISO timestamps). See 'unity-cli wait --help' to
block on that instead of reading it.

Options:
  --no-tests                    Stop after the compile
  --no-burst                    Skip the Burst step
  --burst-wait <seconds>        How long to wait for Burst to go idle (default 60)
  --assembly <a,b>              Only tests in these assemblies (comma-separated). A stale
                                assembly outside this list is reported but does not refuse.
  --filter <name>               Filter by namespace, class, or full test name
  --mode <EditMode|PlayMode>    Test mode (default: EditMode)
  --max <n>                     List at most n errors and n failures (default 20); the error
                                count is still the true total
  --keep-console                Don't clear the console first
  --allow-stale                 Run tests even when an assembly is older than its sources
  --json                        One JSON object instead of text

verify runs the tests, so its invocation budget defaults to 30 minutes instead of the global
120 s. --timeout <ms> overrides it; --timeout 0 means no deadline. Keep --timeout above --burst-wait
when you set it explicitly.

Exit codes:
  0  compiles, Burst is quiet, and every test passes
  1  compile errors, Burst errors, stale assemblies, failing tests, or no test ran
  2  the editor could not answer: not running, timed out, or blocked by a
     modal dialog (named, with the command that answers it)

A Burst check that cannot run (older connector, editor busy) is printed as a note
and does not change the exit code; keep --timeout above --burst-wait.

Needs connector 0.7.0 for live compile errors; older connectors fall back to
console errors. The burst step needs the connector's Burst tool, which is built
only when com.unity.burst is installed.

Examples:
  unity-cli verify
  unity-cli verify --no-tests
  unity-cli verify --no-burst
  unity-cli verify --assembly MyGame.Tests.Editor
  unity-cli verify --json
`)
	case "modal":
		fmt.Print(`Usage: unity-cli modal [list|answer] [options]

Inspect or answer a modal dialog that blocks the Unity Editor (macOS only).

A modal dialog (DisplayDialog, save prompts, file panels) freezes Unity's
main thread. While one is open, every other command fails immediately with
the dialog's text and buttons instead of hanging; a command that opened the
dialog itself resumes inside Unity once the dialog is answered.

Subcommands:
  list                List the open dialog: id, kind, texts, buttons (default)
  answer              Click one of the dialog's buttons
    --button <title>  Button title; falls back to case- and apostrophe-insensitive match
    --index <n>       Button index as reported by list
    --id <id>         Only click if the open dialog still has this id

Answering happens inside the Unity process; no Accessibility permission is
used and only Unity's own windows are reachable. IMGUI modal windows have no
native buttons and cannot be answered.

Examples:
  unity-cli modal
  unity-cli modal answer --button "Don't Save"
  unity-cli modal answer --index 0 --id 27186
`)
	case "dialogs":
		fmt.Print(`Usage: unity-cli dialogs [--click "<button title>"] [--window "<window title>"]

List — and optionally answer — the windows of the Unity processes on this machine,
read from OUTSIDE the editor with AppleScript + System Events (Accessibility).

This is the only way to see a dialog that appears before the connector assembly loads:
license / sign-in prompts, safe mode, "project already open", package resolution.
status reports that window as "editor_starting_or_blocked" (or, once a heartbeat has stopped
answering, "editor_unresponsive").

macOS only: on other systems the command explains that it must run on the macOS host.
The agent sandbox is granted no Accessibility permission, so run it in a Terminal on
the host.

Options:
  --click "<title>"  Press the button. Exact title first, then a case- and
                     apostrophe-insensitive match from the start of the title
                     ("Continue" also matches "Continue Anyway", but "Save" does
                     not match "Don't Save").
  --window "<title>" Only click in a window whose title contains this. Also used to
                     break a tie when the same button title is on several windows.

Windows are narrowed to the requested project (--project, or the running instance) by
matching the project folder name in the window title; when nothing matches, every
Unity window is listed instead, because startup dialogs rarely name the project.
A click in that unfiltered listing needs --window unless all listed windows belong to
one Unity process, so it cannot press a button in another project's editor.

Output is JSON: pid, window title, role, button titles, and the window's static
texts (capped at 12 per window; texts_total reports the real count).

If Accessibility permission is missing, the error names the exact grant to make in
System Settings -> Privacy & Security -> Accessibility (for /usr/bin/osascript and the
app running this command).

Windows Unity draws itself (IMGUI panels, some Safe Mode prompts) expose no native
buttons to Accessibility: they are listed with their title and texts, but there is
nothing to click. Then only a human can answer them.

Examples:
  unity-cli --project ~/Work/MyProject dialogs
  unity-cli dialogs
  unity-cli dialogs --click "Continue"
  unity-cli dialogs --click "Don't Save" --window "Save Changes"

See also: 'unity-cli modal' — the in-editor equivalent, available once the connector loads.
`)
	case "build":
		fmt.Print(`Usage: unity-cli build [--profile <name> | --target <BuildTarget>] --output <path> [options]
       unity-cli build status [id]

Starts a player build as a job. The connector validates the parameters, records the job, and returns
its id; the build itself starts on a later editor update tick. BuildPipeline.BuildPlayer blocks
Unity's main thread for the whole build no matter what, so the point of the job is that the editor
stays observable while it runs:

  - the heartbeat reports state "building" (with the job id and elapsed time) instead of "ready",
    so waiters do not mistake a building editor for a wedged one;
  - build_status answers on a thread that does not wait for the main thread, so 'unity-cli build
    status' works during the build;
  - the wait keeps polling through dropped connections and the domain reload a build can trigger,
    instead of aborting on the first failed request.

Job options:
  --profile <name|path>   BuildProfile asset: a name (looked up under Assets/Settings/Build
                          Profiles/<name>.asset, then by name anywhere) or an asset path.
                          The profile's platform must be the editor's active build target.
  --target <BuildTarget>  StandaloneOSX, StandaloneWindows64, StandaloneLinux64, iOS, Android, ...
                          Refused up front when the platform module is not installed.
  --output <path>         Required. Absolute, or relative to the project root. Not inside Assets/.
  --scenes <a,b>          Comma-separated scene paths. Defaults to the profile's scenes, else the
                          enabled scenes in Build Settings.
  --development           BuildOptions.Development.
  --no-wait               Return the job id and exit; poll it later with 'build status <id>'.

Progress on stderr:
  build job build-20240101-114213-9f0ac1 queued (macOS_dev -> /proj/Builds/Mac/App.app)
  building 12s  Compiling PlayerAssemblies 23%
  building 3m27s  Compiling PlayerAssemblies 61%
  build succeeded in 5m02s — 132.4 MB, 0 error(s), 4 warning(s) -> /proj/Builds/Mac/App.app

The percent comes from Unity's Progress window and is only shown when the build pumps the editor
update loop; when it does not, the lines carry elapsed time only. The final JSON on stdout is the job
record: phase, result, output path, size, duration, error/warning counts, and the first error
messages from the BuildReport.

Phases: queued, building, succeeded, failed, cancelled, interrupted. "interrupted" means the editor
stopped reporting the job while it was building (domain reload or editor exit), so the outcome of
that build is unknown.

While one build is running, a second 'build' is refused at once from the heartbeat ("Unity is already
building ..."); asking the connector instead would queue the request behind the running build and
look like a hang. Validation that needs the editor (unknown build profile, scenes that no longer exist
in the project, a target whose module is not installed) fails before the job starts.

Exit codes:
  0  the build succeeded, or a status poll of a job that has not failed
  1  failed, cancelled, interrupted, or a validation error before the job started

Job state lives in <project>/Temp/unity-cli/build-jobs.json, so it survives the domain reload a build
can trigger. Quitting the editor clears Temp, and with it the job history.

Examples:
  unity-cli build --profile macOS_dev --output Builds/Mac/App.app
  unity-cli build --profile macOS --output Builds/Mac/App.app --development
  unity-cli build --target StandaloneOSX --output Builds/Mac/App.app --scenes Assets/Main.unity
  unity-cli build --no-wait --profile macOS_dev --output Temp/App.app
  unity-cli build status
  unity-cli build status build-20240101-114213-9f0ac1
  unity-cli status                 # shows: Unity (port 8090): building
`)
	case "wait":
		fmt.Print(`Usage: unity-cli wait [--compile] [--timeout <seconds>] [--json]

Blocks until the editor stops reporting work in progress, then prints its state and the compile
provenance line. It replaces the 'sleep 45 && unity-cli compile_errors' loop: the answer says both
that the editor has settled and which compile it reflects.

Idle means the heartbeat state is ready, playing or paused — not compiling, not refreshing, not
reloading, not building, and no modal dialog is open. Play mode counts as idle: waiting for someone
to stop play mode is not what this command is for.

Options:
  --compile          Also require proof that the last compile covers your edit: a script compilation
                     finished after wait started (connector compile clock, compared to the value at
                     wait start — no local clock involved), OR the source scan finds nothing newer
                     than that compile. Without it, wait only reports the state.
  --timeout <second> Give up after this many seconds (default 120) and exit 1. For wait this flag is
                     seconds and must come after the command name; the global --timeout (before it)
                     stays an HTTP request timeout in milliseconds.
  --json             One JSON object instead of text.

An idle editor with stale sources that starts no compilation within 10 seconds ends the wait early
with exit 1: Unity only auto-refreshes while it has focus, so an editor driven from a terminal is not
going to compile what you just saved. Run unity-cli verify (or unity-cli editor refresh --compile).

Output:
  Unity (port 8090): ready
  compiled 14:02:11 (3 s ago, took 2.2 s) · 1 source file changed since previous compile · rebuilt: Game
  stale: Assets/Foo.cs modified after this compile — run verify again

The provenance comes from the connector's compile_status tool. A connector without it answers with
the state and says that provenance is unavailable.

The source scan walks Assets/ plus embedded and file:/local packages, and gives up after 200 ms on
the editor's main thread, saying so instead of stalling the editor.

Exit codes:
  0  the editor is idle (and with --compile, the edit is provably in)
  1  the editor was still working when --timeout ran out
  2  the editor could not answer: not running, heartbeat silent, or blocked by a modal dialog
     (named, with the command that answers it)

Examples:
  unity-cli wait
  unity-cli wait --compile --timeout 120
  unity-cli wait --json
  unity-cli editor refresh --compile && unity-cli wait --compile
`)
	case "api":
		fmt.Print(`Usage: unity-cli api <Type>[.<Member>] [options]

What the API actually is, read off the assemblies the editor has loaded — the same set
the project's code compiles against. Text search over Library/PackageCache disagrees with
what compiled (hashed folders, stubs, #if branches); reflection cannot.

  api Vector3                 kind, namespace, assembly, base type, interfaces, member counts
  api Vector3.Lerp            every overload, as C#: modifiers, generics + constraints,
                              ref/out/in/params, return type
  api EditorApplication       a bounded member summary (all members are counted, not listed)
  api List.Add                the member reading: last '.' splits type from member

Type queries accept the simple name, the full name, and either spelling of a generic
(BufferLookup, BufferLookup<T>, or the reflection spelling with its arity suffix). When
several types match, all of them are described, best match first, up to --max_types.

[Obsolete] members report the message, whether it is an error, and the replacement when the
notice names one ("Use X instead", "(UnityUpgradable) -> X").

Options:
  --assembly <a,b>   Only assemblies whose name contains one of these (comma-separated)
  --docs             Add the XML doc <summary> for the type and each member, when a doc .xml
                     sits next to the assembly (Unity ships one for its own modules; other
                     assemblies usually do not, and missing docs print nothing)
  --limit <n>        Max members listed per type (default 20)
  --max_types <n>    Max types to describe when several match (default 5)
  --budget <ms>      How long the assembly scan may take (default 1500)

The index is built lazily and cached per domain; 'index.complete: false' means the budget ran
out and some assemblies are not searchable yet — raise --budget or ask again.

Examples:
  unity-cli api Vector3.Lerp
  unity-cli api EditorApplication.isCompiling
  unity-cli api UnityEngine.Object.FindObjectOfType --docs
  unity-cli api GetSingletonBuffer --assembly Entities
`)
	case "compile_status":
		fmt.Print(`Usage: unity-cli compile_status [--scan false] [--scan_budget <ms>]

What the last script compilation covers, without waiting or compiling:
when it finished (unix ms, ISO and a printed line), how long it took, which assemblies it rebuilt,
how many source files changed since the compile before it, and which sources (.cs, .asmdef,
.asmref) were written after it — stale, meaning the project has an edit that compile does not.

  compiled_at_ms / compiled_at      when the last compile finished (connector clock)
  changed_since_previous_compile    what triggered it; null when no earlier compile is recorded
  rebuilt_assemblies                assemblies the compile produced
  stale_files / stale_count         sources newer than that compile
  scan_ms / scan_complete           cost of the scan; false means the counts are a lower bound

Options:
  --scan false        Skip the filesystem scan (timestamps of the last compile only)
  --scan_budget <ms>  How long the scan may take on the editor's main thread (default 200)

Scanned roots: Assets/, plus embedded and file:/local packages. Registry and git packages under
Library/PackageCache are not scanned: they are not your code, and walking them would spend the
whole budget.

The same provenance object rides along in compile_diagnostics (key "provenance"), and verify and
'editor refresh --compile' print its line. See 'unity-cli wait --help'.

Examples:
  unity-cli compile_status
  unity-cli compile_status --scan false
  unity-cli compile_status --scan_budget 1000
`)
	case "list":
		fmt.Print(`Usage: unity-cli list

List all registered tools (built-in + custom) with parameter schemas.

Example:
  unity-cli list
`)
	case "status":
		fmt.Print(`Usage: unity-cli status

Show the current Unity Editor state: port, project path, version, PID.
Reports "not responding" if heartbeat is older than 3 seconds.
State "modal" means a dialog blocks the editor; its text and buttons are
printed (see "unity-cli modal --help"). State "building" means a player build job holds the main
thread; the job id, phase and elapsed time are printed with it (see "unity-cli build --help").

When no heartbeat answers, status reports "editor_starting_or_blocked" if the project's
Temp/UnityLockfile is held but the connector has not loaded this session, and
"editor_unresponsive" if a heartbeat from this session stopped answering (with or without a
lockfile). In the first case a startup dialog may be waiting, or the lockfile is hours old and
the editor probably crashed; in the second the main thread is blocked, a dialog is open, or the
editor exited. When the editor is still open the connector cannot see it — read it with
"unity-cli dialogs" on the macOS host.

A silent heartbeat is told apart from a dead editor. The state becomes "crashed", and the command
exits non-zero, when either:

  - the heartbeat's pid is gone from a machine whose process table still lists Unity processes (so
    the pid is visible there and simply not running), or
  - the heartbeat stopped without writing its "stopped" marker AND a crash artifact newer than that
    last beat exists: <project>/mono_crash.*.json, ~/Library/Logs/DiagnosticReports/Unity-*.ips, or
    ~/Library/Logs/Unity/Editor-prev.log with crash wording in its tail.

The report names the artifact and its first exception line, because Logs/Editor.log rotates on the next
editor start and the dump is the only surviving record. Artifacts older than the last beat belong to an
earlier crash and are never blamed on this silence. Inside a container that cannot see the host's
processes the pid proves nothing, so only the artifact route can decide — that is why the verdict
always carries its evidence.

The same judgement runs on any command whose request dies mid-flight (EOF, connection reset or
refused): instead of a bare "cannot connect to Unity at port 8090: EOF" it says the editor process
exited during the request, or that the heartbeat shows a domain reload/port change. A request that
merely timed out is never called a crash.

Example:
  unity-cli status
`)
	case "update":
		fmt.Print(`Usage: unity-cli update [options]

Update the CLI binary to the latest release from GitHub.

Options:
  --check              Check for updates without installing

Examples:
  unity-cli update
  unity-cli update --check
`)
	case "custom-tools", "custom", "tools":
		fmt.Print(`How to write custom tools for unity-cli

Custom tools are C# classes that run inside Unity Editor. The CLI
discovers them automatically via reflection.

Create a static class with [UnityCliTool] in any Editor assembly:

    using UnityCliConnector;
    using Newtonsoft.Json.Linq;

    [UnityCliTool(Description = "Spawn an enemy at a position")]
    public static class SpawnEnemy
    {
        public class Parameters
        {
            [ToolParameter("X world position", Required = true)]
            public float X { get; set; }
        }

        public static object HandleCommand(JObject parameters)
        {
            float x = parameters["x"]?.Value<float>() ?? 0;
            var go = Object.Instantiate(prefab, new Vector3(x, 0, 0), Quaternion.identity);
            return new SuccessResponse("Spawned", new { name = go.name });
        }
    }

Rules:
  - Class must be static
  - Must have: public static object HandleCommand(JObject parameters)
  - Return SuccessResponse(message, data) or ErrorResponse(message)
  - Add Parameters class with [ToolParameter] for discoverability
  - Class name auto-converts to snake_case (SpawnEnemy → spawn_enemy)
  - Override name: [UnityCliTool(Name = "my_name")]
  - Runs on Unity main thread — all Unity APIs are safe
  - Discovered on Editor start and after every script recompilation
  - Duplicate tool names are detected and logged as errors (first wins)
`)
	case "setup", "install":
		fmt.Print(`Installation and Unity setup

CLI Installation:
  # Linux / macOS
  curl -fsSL https://raw.githubusercontent.com/MikkelAndresen/unity-cli/main/install.sh | sh

  # Windows (PowerShell)
  irm https://raw.githubusercontent.com/MikkelAndresen/unity-cli/main/install.ps1 | iex

  # Go install (any platform)
  go install github.com/youngwoocho02/unity-cli@latest

Unity Setup:
  1. Window → Package Manager → + → Add package from git URL
  2. Paste: https://github.com/MikkelAndresen/unity-cli.git?path=unity-connector
  The Connector starts automatically when Unity opens.

Verify:
  unity-cli list
`)
	default:
		fmt.Printf("Unknown help topic: %s\n\nUse \"unity-cli --help\" for available commands.\n", topic)
	}
}

// waitOwnsStartup: wait's own --timeout is the budget for startup, and waitCmd polls "starting"
// itself. Holding it in waitForAlive would spend the global request deadline instead, so
// `wait --timeout 2` could sit for two minutes before its own clock even started.
func waitOwnsStartup(category, state string) bool {
	return category == waitCategory && isStartingState(state)
}
