package cmd

import (
	"github.com/youngwoocho02/unity-cli/internal/client"
)

// Connector error codes carried in a failed response's `data.code`. They are declared in
// unity-connector/Editor/Core/ErrorCodes.cs; the connector decides the code, the CLI decides what
// the caller should do next.
const (
	codeMainThreadBusy      = "main_thread_busy"
	codeDomainReload        = "domain_reload"
	codeReloadDuringCommand = "reload_during_command"
	codeSerializationFail   = "serialization_failed"
)

// connectorErrorHint turns the connector's reason for answering without a result into the next step.
// It returns "" for a failure with no known code, where the connector's own message is the whole
// report and adding a guess would only mislead.
func connectorErrorHint(resp *client.CommandResponse) string {
	switch client.ConnectorErrorCode(resp) {
	case codeMainThreadBusy:
		return "the command did not run and nothing changed; run 'unity-cli wait' until the editor is idle, then repeat it"
	case codeDomainReload:
		return "the command did not reach a tool handler; run 'unity-cli wait' for the reloaded editor, then repeat it"
	case codeReloadDuringCommand:
		return "the command had already started when Unity reloaded, so its effects may be applied and only its return value was lost; check 'unity-cli status' before repeating it"
	case codeSerializationFail:
		return "the command ran and its effects stand; only the returned value was lost, so return something smaller (a string, or an object of scalars)"
	default:
		return ""
	}
}
