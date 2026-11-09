package cmd

import (
	"bytes"
	"encoding/json"
	"io"
	"strings"
	"testing"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestConnectorErrorHint(t *testing.T) {
	tests := []struct {
		name string
		resp *client.CommandResponse
		want string
	}{
		{
			name: "main thread never picked it up",
			resp: &client.CommandResponse{Success: false,
				Message: "exec was queued but the main thread did not pick it up within 108s",
				Data:    json.RawMessage(`{"code":"main_thread_busy","command":"exec","started":false}`)},
			want: "the command did not run and nothing changed",
		},
		{
			name: "domain reload dropped it",
			resp: &client.CommandResponse{Success: false,
				Message: "exec never ran: Unity stopped answering before the main thread picked it up",
				Data:    json.RawMessage(`{"code":"domain_reload","command":"exec","started":false}`)},
			want: "the command did not reach a tool handler",
		},
		{
			name: "the compile gate refused before dispatch",
			resp: &client.CommandResponse{Success: false,
				Message: "Editor is currently compiling — retry shortly.",
				Data:    json.RawMessage(`{"code":"compiling","started":false,"compiling":true}`)},
			want: "the command did not run; the editor is still compiling",
		},
		{
			name: "the command was running when the reload came",
			resp: &client.CommandResponse{Success: false,
				Message: "exec had already started when Unity stopped answering (the domain is reloading)",
				Data:    json.RawMessage(`{"code":"reload_during_command","command":"exec","started":true}`)},
			want: "the command had already started when Unity reloaded",
		},
		{
			name: "the result could not be serialized",
			resp: &client.CommandResponse{Success: false,
				Message: "exec returned a value that could not be serialized",
				Data:    json.RawMessage(`{"code":"serialization_failed","command":"exec"}`)},
			want: "the command ran and its effects stand",
		},
		{
			name: "a parameter the tool does not know",
			resp: &client.CommandResponse{Success: false,
				Message: "spawn_enemy does not take the parameter 'colour'.",
				Data:    json.RawMessage(`{"code":"unknown_parameter","command":"spawn_enemy","parameter":"colour","started":false}`)},
			want: "the request was refused before the tool ran",
		},
		{
			name: "an ordinary tool error gets no guess",
			resp: &client.CommandResponse{Success: false, Message: "exec failed: CS0103",
				Data: json.RawMessage(`{"compiling":true}`)},
			want: "",
		},
		{
			name: "a code the CLI does not know gets no guess",
			resp: &client.CommandResponse{Success: false, Data: json.RawMessage(`{"code":"from_the_future"}`)},
			want: "",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := connectorErrorHint(tt.resp)
			if tt.want == "" {
				if got != "" {
					t.Errorf("connectorErrorHint() = %q, want no hint", got)
				}
				return
			}
			if !strings.HasPrefix(got, tt.want) {
				t.Errorf("connectorErrorHint() = %q, want it to start with %q", got, tt.want)
			}
		})
	}
}

// The connector's own words are the report; the CLI adds only the next step, so an agent that reads
// stderr gets both without re-deriving the cause from the code.
func TestPrintResponseTo_FailedConnectorResponsePrintsTheNextStep(t *testing.T) {
	var errOut bytes.Buffer
	printResponseTo(io.Discard, &errOut, &client.CommandResponse{
		Success: false,
		Message: "exec was queued but the main thread did not pick it up within 27s: editor busy (blocked by the running command test (42s))",
		Data:    json.RawMessage(`{"code":"main_thread_busy","command":"exec","queue_depth":2}`),
	})
	out := errOut.String()

	for _, want := range []string{
		"Error: exec was queued but the main thread did not pick it up",
		`Details: {"code":"main_thread_busy"`,
		"next:      the command did not run and nothing changed",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("stderr missing %q:\n%s", want, out)
		}
	}
}

func TestPrintResponseTo_OrdinaryErrorKeepsItsShape(t *testing.T) {
	var errOut bytes.Buffer
	printResponseTo(io.Discard, &errOut, &client.CommandResponse{Success: false, Message: "Unknown command: nope"})
	out := errOut.String()
	if strings.Contains(out, "next:") {
		t.Errorf("an unknown failure must not gain a guessed next step:\n%s", out)
	}
	if !strings.Contains(out, "Error: Unknown command: nope") {
		t.Errorf("stderr = %q", out)
	}
}

func TestConnectorErrorHint_UnknownCommand(t *testing.T) {
	burst := connectorErrorHint(&client.CommandResponse{Message: "Unknown command: burst_status"})
	if !strings.Contains(burst, "com.unity.burst") {
		t.Errorf("burst hint = %q, want it to name the missing package", burst)
	}
	if other := connectorErrorHint(&client.CommandResponse{Message: "Unknown command: scene_lst"}); other != "" {
		t.Errorf("hint = %q, want none for a command that is not a Burst tool", other)
	}
	if got := connectorErrorHint(&client.CommandResponse{Message: "something else"}); got != "" {
		t.Errorf("hint = %q, want none for an unrecognised failure", got)
	}
}
