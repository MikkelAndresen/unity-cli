package client

import "encoding/json"

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
