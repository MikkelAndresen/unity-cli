package client

import (
	"context"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

// echoServer answers /command immediately and hands the decoded request body back to the test.
func echoServer(t *testing.T) (int, chan CommandRequest) {
	t.Helper()
	bodies := make(chan CommandRequest, 8)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		var req CommandRequest
		_ = json.Unmarshal(body, &req)
		bodies <- req
		_, _ = w.Write([]byte(`{"success":true,"message":"ok"}`))
	}))
	t.Cleanup(server.Close)
	return server.Listener.Addr().(*net.TCPAddr).Port, bodies
}

func TestSendContext_TellsTheConnectorTheRemainingBudget(t *testing.T) {
	port, bodies := echoServer(t)
	inst := &Instance{State: "ready", ProjectPath: t.TempDir(), Port: port, PID: 1}

	if _, err := SendContext(context.Background(), inst, "exec", nil, 20000); err != nil {
		t.Fatalf("send: %v", err)
	}

	got := <-bodies
	if got.Command != "exec" {
		t.Fatalf("the connector must be told the command, got %q", got.Command)
	}
	// The connector answers a queued command inside this number, so it must be the real budget and
	// never larger: a bigger number would let the client hang up first.
	if got.DeadlineMs <= 0 || got.DeadlineMs > 20000 {
		t.Errorf("deadline_ms = %d, want the 20000ms budget or the part of it left", got.DeadlineMs)
	}
}

func TestSendContext_ShrinksTheBudgetForEveryLaterRequest(t *testing.T) {
	port, bodies := echoServer(t)
	inst := &Instance{State: "ready", ProjectPath: t.TempDir(), Port: port, PID: 1}
	// A budget far larger than the sleep below, so a loaded machine cannot expire the context and
	// turn the assertion into a scheduling test. What is under test is that the second send reports
	// less than the first, not how many milliseconds elapsed.
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()

	if _, err := SendContext(ctx, inst, "verify", nil, 0); err != nil {
		t.Fatalf("first send: %v", err)
	}
	first := (<-bodies).DeadlineMs
	time.Sleep(20 * time.Millisecond)
	if _, err := SendContext(ctx, inst, "verify", nil, 0); err != nil {
		t.Fatalf("second send: %v", err)
	}
	second := (<-bodies).DeadlineMs

	if first <= 0 || first > 60000 {
		t.Errorf("first deadline_ms = %d, want the parent's ~60s", first)
	}
	if second >= first {
		t.Errorf("deadline_ms must shrink as the invocation deadline runs down: first=%d second=%d", first, second)
	}
}

func TestSendContext_NoDeadlineMeansNoNumber(t *testing.T) {
	port, bodies := echoServer(t)
	inst := &Instance{State: "ready", ProjectPath: t.TempDir(), Port: port, PID: 1}

	if _, err := SendContext(context.Background(), inst, "exec", nil, 0); err != nil {
		t.Fatalf("send: %v", err)
	}

	got := <-bodies
	if got.DeadlineMs != 0 {
		t.Errorf("an unbounded caller must not claim a deadline, got %d", got.DeadlineMs)
	}
	var raw map[string]interface{}
	body, _ := json.Marshal(got)
	if err := json.Unmarshal(body, &raw); err != nil {
		t.Fatalf("marshal: %v", err)
	}
	if _, ok := raw["deadline_ms"]; ok {
		t.Error("deadline_ms must be omitted, not 0: 0 would look like an expired deadline")
	}
}

func TestConnectorErrorCode(t *testing.T) {
	tests := []struct {
		name string
		resp *CommandResponse
		want string
	}{
		{"busy", &CommandResponse{Success: false, Data: json.RawMessage(`{"code":"main_thread_busy"}`)}, "main_thread_busy"},
		{"reload", &CommandResponse{Success: false, Data: json.RawMessage(`{"code":"domain_reload","command":"exec"}`)}, "domain_reload"},
		{"plain tool error", &CommandResponse{Success: false, Message: "exec failed: boom"}, ""},
		{"data is a string", &CommandResponse{Success: false, Data: json.RawMessage(`"boom"`)}, ""},
		{"null data", &CommandResponse{Success: false, Data: json.RawMessage(`null`)}, ""},
		{"no data", &CommandResponse{Success: false}, ""},
		{"nil response", nil, ""},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := ConnectorErrorCode(tt.resp); got != tt.want {
				t.Errorf("ConnectorErrorCode() = %q, want %q", got, tt.want)
			}
		})
	}
}
