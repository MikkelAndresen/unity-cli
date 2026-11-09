package cmd

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"log"
	"os"
	"reflect"
	"strings"
	"testing"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func mockSend(wantCmd string, t *testing.T) (sendFn, *map[string]interface{}) {
	t.Helper()
	captured := map[string]interface{}{}
	fn := func(cmd string, params interface{}) (*client.CommandResponse, error) {
		if cmd != wantCmd {
			t.Errorf("send called with command %q, want %q", cmd, wantCmd)
		}
		if p, ok := params.(map[string]interface{}); ok {
			for k, v := range p {
				captured[k] = v
			}
		}
		return &client.CommandResponse{Success: true}, nil
	}
	return fn, &captured
}

func TestMetaLine(t *testing.T) {
	tests := []struct {
		name    string
		meta    json.RawMessage
		enabled bool
		want    string
	}{
		{"no meta", nil, true, ""},
		{"explicit null", json.RawMessage("null"), true, ""},
		{"empty object", json.RawMessage("{}"), true, "meta: {}\n"},
		// Without --meta the line is suppressed: 'exec ... 2>&1 | tail -1' would otherwise
		// surface the meta note instead of the result (#32).
		{
			"exec compile meta, flag off",
			json.RawMessage(`{"compile":"cached","compile_ms":0,"reference_assemblies":234}`),
			false,
			"",
		},
		{
			"exec compile meta",
			json.RawMessage(`{"compile":"cached","compile_ms":0,"reference_assemblies":234}`),
			true,
			"meta: {\"compile\":\"cached\",\"compile_ms\":0,\"reference_assemblies\":234}\n",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := metaLine(tt.meta, tt.enabled); got != tt.want {
				t.Errorf("metaLine(%s, %v) = %q, want %q", string(tt.meta), tt.enabled, got, tt.want)
			}
		})
	}
}

func TestPrintResponseToEndsWithExactlyOneNewline(t *testing.T) {
	tests := []struct {
		name string
		resp *client.CommandResponse
		want string
	}{
		{"plain string", &client.CommandResponse{Success: true, Data: json.RawMessage(`"hello"`)}, "hello\n"},
		{"multi-line string without trailing newline", &client.CommandResponse{Success: true, Data: json.RawMessage(`"line1\nline2"`)}, "line1\nline2\n"},
		// The final line must be the result, not a blank line left by Println (#32).
		{"multi-line string with trailing newline", &client.CommandResponse{Success: true, Data: json.RawMessage(`"line1\nline2\n"`)}, "line1\nline2\n"},
		{"string with several trailing newlines", &client.CommandResponse{Success: true, Data: json.RawMessage(`"line1\n\n\n"`)}, "line1\n"},
		{"number", &client.CommandResponse{Success: true, Data: json.RawMessage(`42`)}, "42\n"},
		{"object is indented", &client.CommandResponse{Success: true, Data: json.RawMessage(`{"a":1}`)}, "{\n  \"a\": 1\n}\n"},
		{"null with message", &client.CommandResponse{Success: true, Data: json.RawMessage(`null`), Message: "OK"}, "OK\n"},
		{"message with trailing newline", &client.CommandResponse{Success: true, Message: "done\n"}, "done\n"},
		{"null without message", &client.CommandResponse{Success: true, Data: json.RawMessage(`null`)}, ""},
		{"empty string result", &client.CommandResponse{Success: true, Data: json.RawMessage(`""`)}, "\n"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var out bytes.Buffer
			printResponseTo(&out, io.Discard, tt.resp)
			if got := out.String(); got != tt.want {
				t.Errorf("printResponseTo() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestParseSubFlags(t *testing.T) {
	tests := []struct {
		name    string
		args    []string
		want    map[string]string
		wantErr bool
	}{
		{"empty", nil, map[string]string{}, false},
		{"key value pair", []string{"--port", "8080"}, map[string]string{"port": "8080"}, false},
		{"boolean flag", []string{"--wait"}, map[string]string{"wait": "true"}, false},
		{"mixed", []string{"--port", "8080", "--wait", "--filter", "error"}, map[string]string{"port": "8080", "wait": "true", "filter": "error"}, false},
		{"consecutive boolean flags", []string{"--wait", "--clear"}, map[string]string{"wait": "true", "clear": "true"}, false},
		{"non-flag args ignored", []string{"play", "--wait"}, map[string]string{"wait": "true"}, false},
		{"repeated params keeps the last for command-level checks", []string{"--params", `{"a":1}`, "--params", `{"b":2}`}, map[string]string{"params": `{"b":2}`}, false},
		{"repeated plain flag is an error", []string{"--filter", "a", "--filter", "b"}, nil, true},
		{"repeated boolean flag is an error", []string{"--wait", "--wait"}, nil, true},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := parseSubFlags(tt.args)
			if tt.wantErr {
				if err == nil {
					t.Fatalf("parseSubFlags(%v) = %v, want error", tt.args, got)
				}
				return
			}
			if err != nil {
				t.Fatalf("parseSubFlags(%v) unexpected error: %v", tt.args, err)
			}
			if len(got) != len(tt.want) {
				t.Errorf("parseSubFlags(%v) = %v, want %v", tt.args, got, tt.want)
				return
			}
			for k, v := range tt.want {
				if got[k] != v {
					t.Errorf("parseSubFlags(%v)[%q] = %q, want %q", tt.args, k, got[k], v)
				}
			}
		})
	}
}

func TestSplitArgs(t *testing.T) {
	tests := []struct {
		name         string
		args         []string
		wantFlags    []string
		wantCommands []string
	}{
		{"empty", nil, nil, nil},
		{"commands only", []string{"editor", "play"}, nil, []string{"editor", "play"}},
		{"port flag", []string{"--port", "8080", "editor", "play"}, []string{"--port", "8080"}, []string{"editor", "play"}},
		{"project flag", []string{"--project", "myproj", "status"}, []string{"--project", "myproj"}, []string{"status"}},
		{"timeout flag", []string{"exec", "--timeout", "5000", "Time.time"}, []string{"--timeout", "5000"}, []string{"exec", "Time.time"}},
		{"ignore version mismatch flag", []string{"exec", "--ignore-version-mismatch", "Time.time"}, []string{"--ignore-version-mismatch"}, []string{"exec", "Time.time"}},
		{"meta flag", []string{"exec", "return 1;", "--meta"}, []string{"--meta"}, []string{"exec", "return 1;"}},
		{"multiple global flags", []string{"--port", "8080", "--timeout", "3000", "exec", "code"}, []string{"--port", "8080", "--timeout", "3000"}, []string{"exec", "code"}},
		// wait's own --timeout is seconds of waiting, not the global request timeout in ms.
		{"wait timeout belongs to the command", []string{"wait", "--compile", "--timeout", "120"}, nil, []string{"wait", "--compile", "--timeout", "120"}},
		{"timeout before the command stays global", []string{"--timeout", "5000", "wait"}, []string{"--timeout", "5000"}, []string{"wait"}},
		{"wait timeout with no value", []string{"wait", "--timeout"}, nil, []string{"wait", "--timeout"}},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			flags, commands := splitArgs(tt.args)
			if !sliceEqual(flags, tt.wantFlags) {
				t.Errorf("splitArgs(%v) flags = %v, want %v", tt.args, flags, tt.wantFlags)
			}
			if !sliceEqual(commands, tt.wantCommands) {
				t.Errorf("splitArgs(%v) commands = %v, want %v", tt.args, commands, tt.wantCommands)
			}
		})
	}
}

func TestBuildParams_IntParsing(t *testing.T) {
	p, err := buildParams([]string{"--lines", "50"}, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if p["lines"] != 50 {
		t.Errorf("expected lines=50, got %v", p["lines"])
	}
}

func TestBuildParams_BoolParsing(t *testing.T) {
	p, err := buildParams([]string{"--clear"}, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if p["clear"] != true {
		t.Errorf("expected clear=true, got %v", p["clear"])
	}
}

func TestBuildParams_StringParsing(t *testing.T) {
	p, err := buildParams([]string{"--filter", "error"}, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if p["filter"] != "error" {
		t.Errorf("expected filter=error, got %v", p["filter"])
	}
}

func TestBuildParams_BaseParams(t *testing.T) {
	p, err := buildParams([]string{"--depth", "5"}, map[string]interface{}{"action": "hierarchy"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if p["action"] != "hierarchy" {
		t.Errorf("expected action=hierarchy, got %v", p["action"])
	}
	if p["depth"] != 5 {
		t.Errorf("expected depth=5, got %v", p["depth"])
	}
}

func TestBuildParams_RepeatedParamsMerge(t *testing.T) {
	tests := []struct {
		name string
		args []string
		want map[string]interface{}
	}{
		{
			name: "independent keys are kept",
			args: []string{"--params", `{"code":"return 42;"}`, "--params", `{"await_timeout":1000}`},
			want: map[string]interface{}{"code": "return 42;", "await_timeout": float64(1000)},
		},
		{
			name: "filter from the first object survives",
			args: []string{"--params", `{"mode":"edit","assembly_names":["Game.Tests.Editor"]}`, "--params", `{"results_path":"Temp/out.xml"}`},
			want: map[string]interface{}{
				"mode":           "edit",
				"assembly_names": []interface{}{"Game.Tests.Editor"},
				"results_path":   "Temp/out.xml",
			},
		},
		{
			name: "later keys win",
			args: []string{"--params", `{"filter":"old","keep":1}`, "--params", `{"filter":"new"}`},
			want: map[string]interface{}{"filter": "new", "keep": float64(1)},
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			p, err := buildParams(tt.args, nil)
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if len(p) != len(tt.want) {
				t.Fatalf("buildParams(%v) = %v, want %v", tt.args, p, tt.want)
			}
			for k, want := range tt.want {
				if !reflect.DeepEqual(p[k], want) {
					t.Errorf("buildParams(%v)[%q] = %v, want %v", tt.args, k, p[k], want)
				}
			}
		})
	}
}

func TestBuildParams_RepeatedPlainFlagIsError(t *testing.T) {
	_, err := buildParams([]string{"--filter", "a", "--filter", "b"}, nil)
	if err == nil {
		t.Fatal("expected an error for a repeated non-params flag")
	}
	if !strings.Contains(err.Error(), "--filter") {
		t.Errorf("error should name the flag, got: %v", err)
	}
}

func TestBuildParams_InvalidJSONNamesTheOccurrence(t *testing.T) {
	tests := []struct {
		name string
		args []string
		want string
	}{
		{"first params", []string{"--params", `{`}, "invalid JSON in --params #1"},
		{"second params", []string{"--params", `{"ok":1}`, "--params", `{`}, "invalid JSON in --params #2"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, err := buildParams(tt.args, nil)
			if err == nil {
				t.Fatal("expected invalid JSON error")
			}
			if !strings.Contains(err.Error(), tt.want) {
				t.Errorf("error should contain %q, got: %v", tt.want, err)
			}
		})
	}
}

func TestExecRequestTimeout_WidensForAwaitTimeout(t *testing.T) {
	p, err := buildParams([]string{"--await_timeout", "100000"}, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	// A 100s await budget plus compile slack must outlast the 120s default, otherwise
	// the client reports a deadline instead of the connector's timed_out marker.
	if got := execRequestTimeout(120000, p); got != 130000 {
		t.Errorf("expected 130000, got %d", got)
	}
}

func TestExecRequestTimeout_ShortAwaitKeepsFlagTimeout(t *testing.T) {
	p, err := buildParams([]string{"--await_timeout", "500"}, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got := execRequestTimeout(120000, p); got != 120000 {
		t.Errorf("expected 120000, got %d", got)
	}
}

func TestExecRequestTimeout_NoAwaitTimeout(t *testing.T) {
	p, err := buildParams([]string{"return 1;"}, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got := execRequestTimeout(120000, p); got != 120000 {
		t.Errorf("expected 120000, got %d", got)
	}
}

func TestExecRequestTimeout_WaitForeverKeepsFlagTimeout(t *testing.T) {
	// await_timeout 0 means the connector never gives up; no client number helps.
	p := map[string]interface{}{"await_timeout": 0}
	if got := execRequestTimeout(120000, p); got != 120000 {
		t.Errorf("expected 120000, got %d", got)
	}
	if got := execRequestTimeout(0, p); got != 0 {
		t.Errorf("expected 0 (no client deadline) to stay 0, got %d", got)
	}
}

func TestExecRequestTimeout_CamelCaseAndStringForms(t *testing.T) {
	if got := execRequestTimeout(120000, map[string]interface{}{"awaitTimeout": "200000"}); got != 230000 {
		t.Errorf("expected 230000, got %d", got)
	}
	if got := execRequestTimeout(120000, map[string]interface{}{"await_timeout": 200000.0}); got != 230000 {
		t.Errorf("expected 230000, got %d", got)
	}
}

func sliceEqual(a, b []string) bool {
	if len(a) == 0 && len(b) == 0 {
		return true
	}
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func TestBuildParams_ExecUsingsFlags(t *testing.T) {
	p, err := buildParams([]string{"--usings", "System,UnityEngine", "--no_default_usings"}, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if p["usings"] != "System,UnityEngine" {
		t.Errorf("usings = %v, want System,UnityEngine", p["usings"])
	}
	if p["no_default_usings"] != true {
		t.Errorf("no_default_usings = %v, want true", p["no_default_usings"])
	}

	// The example in exec help puts the flag after the snippet; a bare boolean
	// followed by a positional would consume the snippet as its value.
	p, err = buildParams([]string{"return 1;", "--no_default_usings"}, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if p["no_default_usings"] != true {
		t.Errorf("no_default_usings = %v, want true", p["no_default_usings"])
	}
	if args, ok := p["args"].([]string); !ok || len(args) != 1 || args[0] != "return 1;" {
		t.Errorf("args = %v, want [return 1;]", p["args"])
	}
}

// TestPrintFailureReport_PrintsTheReportBeforeTheError: editor refresh --compile answers with a
// report and then exits non-zero, so the failing path must not swallow what the command produced.
func TestPrintFailureReport_PrintsTheReportBeforeTheError(t *testing.T) {
	resp := &client.CommandResponse{Success: false, Message: "Refresh done; compilation failed with 2 error(s).",
		Data: json.RawMessage(`{"compile_succeeded":false,"error_count":2}`)}
	err := errors.New("compilation failed: 2 error(s), listed in the report")

	noteCalls := 0
	errOut := captureStderr(t, func() {
		printFailureReport(resp, err, func() { noteCalls++ })
	})
	if !strings.Contains(errOut, "Refresh done") || !strings.Contains(errOut, `"error_count":2`) {
		t.Errorf("stderr = %q, want the report before the error", errOut)
	}
	if noteCalls != 1 {
		t.Errorf("state note calls = %d, want 1", noteCalls)
	}

	if out := captureStdout(t, func() { printFailureReport(nil, err, func() { noteCalls++ }) }); out != "" {
		t.Errorf("a nil response printed %q, want nothing", out)
	}
	if out := captureStdout(t, func() { printFailureReport(resp, nil, func() { noteCalls++ }) }); out != "" {
		t.Errorf("a nil error printed %q, want nothing", out)
	}
	if noteCalls != 1 {
		t.Errorf("state note calls = %d, want no note for a nil response or error", noteCalls)
	}
}

// TestPrintResponseTo_MetaGoesToTheErrorWriter: the result and the meta/error lines must be
// separable, so a test can assert the ordering a '2>&1 | tail -1' pipeline sees.
func TestPrintResponseTo_MetaGoesToTheErrorWriter(t *testing.T) {
	orig := flagMeta
	flagMeta = true
	t.Cleanup(func() { flagMeta = orig })

	resp := &client.CommandResponse{
		Success: true,
		Data:    json.RawMessage(`"result"`),
		Meta:    json.RawMessage(`{"compile":"cached"}`),
	}
	var out, errOut bytes.Buffer
	printResponseTo(&out, &errOut, resp)
	if out.String() != "result\n" {
		t.Errorf("stdout = %q, want only the result", out.String())
	}
	if errOut.String() != "meta: {\"compile\":\"cached\"}\n" {
		t.Errorf("stderr = %q, want only the meta line", errOut.String())
	}
}

// TestSilenceStdlibLogger: net/http's "Unsolicited response received on idle HTTP channel" note is a
// bare log.Printf, which no client or transport option can turn off — only the standard logger's
// destination can. This CLI logs nothing else through it.
func TestSilenceStdlibLogger(t *testing.T) {
	original := log.Writer()
	t.Cleanup(func() { log.SetOutput(original) })
	log.SetOutput(os.Stderr)

	silenceStdlibLogger()

	if log.Writer() != io.Discard {
		t.Errorf("log.Writer() = %v, want io.Discard", log.Writer())
	}
}
