package cmd

import (
	"strings"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// Connector error codes carried in a failed response's `data.code`. They are declared in
// unity-connector/Editor/Core/ErrorCodes.cs; the connector decides the code, the CLI decides what
// the caller should do next.
const (
	codeMainThreadBusy      = "main_thread_busy"
	codeCompiling           = "compiling"
	codeDomainReload        = "domain_reload"
	codeReloadDuringCommand = "reload_during_command"
	codeSerializationFail   = "serialization_failed"
	codeUnknownParameter    = "unknown_parameter"
)

// connectorErrorHint turns the connector's reason for answering without a result into the next step.
// It returns "" for a failure with no known code, where the connector's own message is the whole
// report and adding a guess would only mislead.
func connectorErrorHint(resp *client.CommandResponse) string {
	switch client.ConnectorErrorCode(resp) {
	case codeMainThreadBusy:
		return "the command did not run and nothing changed; run 'unity-cli wait' until the editor is idle, then repeat it"
	case codeCompiling:
		return "the command did not run; the editor is still compiling, so run 'unity-cli wait' until it is idle, then repeat it"
	case codeDomainReload:
		return "the command did not reach a tool handler; run 'unity-cli wait' for the reloaded editor, then repeat it"
	case codeReloadDuringCommand:
		return "the command had already started when Unity reloaded, so its effects may be applied and only its return value was lost; check 'unity-cli status' before repeating it"
	case codeSerializationFail:
		return "the command ran and its effects stand; only the returned value was lost, so return something smaller (a string, or an object of scalars)"
	case codeUnknownParameter:
		// A passthrough tool's parameters are the connector's to judge (the CLI has no per-tool flag
		// list), so this is the answer a typo on a custom tool gets. Nothing ran, and repeating it
		// unchanged would only earn the same refusal.
		return "the request was refused before the tool ran; none of its parameters were applied, so check the parameter name ('unity-cli list' prints each tool's schema) and repeat it"
	default:
		return unknownCommandHint(resp)
	}
}

// burstTools live in unity-connector/Editor/Burst, an assembly whose asmdef only compiles when the
// project has com.unity.burst, so "Unknown command" for them means a missing package, not a typo.
var burstTools = map[string]bool{"burst_status": true, "clear_burst_cache": true}

func unknownCommandHint(resp *client.CommandResponse) string {
	if resp == nil {
		return ""
	}
	name, ok := strings.CutPrefix(resp.Message, "Unknown command: ")
	if !ok {
		return ""
	}
	if !burstTools[strings.TrimSpace(name)] {
		return ""
	}
	return "this tool is only compiled when the project has the com.unity.burst package; without Burst there is nothing for it to report"
}
