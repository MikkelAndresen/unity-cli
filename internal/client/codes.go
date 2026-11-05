package client

import "encoding/json"

// CodeUnknownParameter is the connector's `data.code` for a request that named a parameter the
// tool's schema does not declare (a misspelled flag). The connector refused it before running, so
// nothing changed and the response lists the parameters the tool accepts. Declared in
// unity-connector/Editor/Core/ErrorCodes.cs; cmd/connector_error.go maps the codes to next steps.
const CodeUnknownParameter = "unknown_parameter"

// ConnectorErrorCode returns the machine-readable code the connector attached to a failed response
// (`data.code`), or "" when the response carries no JSON object or no known field. The codes are
// declared in unity-connector/Editor/Core/ErrorCodes.cs; cmd/connector_error.go maps them to the
// next step, so both sides move together.
func ConnectorErrorCode(resp *CommandResponse) string {
	if resp == nil || len(resp.Data) == 0 || string(resp.Data) == "null" {
		return ""
	}
	var probe struct {
		Code string `json:"code"`
	}
	if err := json.Unmarshal(resp.Data, &probe); err != nil {
		return ""
	}
	return probe.Code
}

// ConnectorResponseStarted reports the connector's `started` flag from a failed response's data.
// ok is false when the field is absent or not a boolean; only an explicit false proves that the
// tool handler was never entered, so only that value lets the CLI repeat a state-changing command.
func ConnectorResponseStarted(resp *CommandResponse) (started bool, ok bool) {
	if resp == nil || len(resp.Data) == 0 || string(resp.Data) == "null" {
		return false, false
	}
	var probe struct {
		Started *bool `json:"started"`
	}
	if err := json.Unmarshal(resp.Data, &probe); err != nil || probe.Started == nil {
		return false, false
	}
	return *probe.Started, true
}
