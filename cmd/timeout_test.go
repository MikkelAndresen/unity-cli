package cmd

import (
	"context"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestEffectiveInvocationTimeout(t *testing.T) {
	orig := flagTimeout
	flagTimeout = defaultRequestTimeoutMs
	t.Cleanup(func() { flagTimeout = orig })

	tests := []struct {
		name     string
		category string
		subArgs  []string
		explicit bool
		want     int
	}{
		{"normal command uses the global default", "exec", nil, false, defaultRequestTimeoutMs},
		{"test is bounded at 30 minutes by default", "test", nil, false, longCommandTimeoutMs},
		{"verify is bounded at 30 minutes by default", "verify", nil, false, longCommandTimeoutMs},
		{"editor refresh --compile waits out a compile", "editor", []string{"refresh", "--compile"}, false, longCommandTimeoutMs},
		{"plain editor refresh keeps the normal budget", "editor", []string{"refresh"}, false, defaultRequestTimeoutMs},
		{"burst_status widens the budget past its wait", "burst_status", []string{"--wait", "180"}, false, 210000},
		{"burst_status without a wait keeps the normal budget", "burst_status", nil, false, defaultRequestTimeoutMs},
		{"explicit --timeout wins", "test", nil, true, defaultRequestTimeoutMs},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := effectiveInvocationTimeout(tt.category, tt.subArgs, tt.explicit); got != tt.want {
				t.Errorf("effectiveInvocationTimeout() = %d, want %d", got, tt.want)
			}
		})
	}
}

func TestHasInvocationDeadline_LeavesWaitAndBuildAlone(t *testing.T) {
	if hasInvocationDeadline(waitCategory) {
		t.Error("wait's own --timeout is its budget; the global deadline would cut it short")
	}
	if hasInvocationDeadline("build") {
		t.Error("a build must be allowed to outlast the global request timeout")
	}
	if !hasInvocationDeadline("exec") {
		t.Error("exec should share one invocation deadline")
	}
}

func TestRemainingTimeoutMs(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 200*time.Millisecond)
	defer cancel()
	if got := remainingTimeoutMs(ctx, 5000); got <= 0 || got > 250 {
		t.Errorf("remainingTimeoutMs() = %d, want the remaining ~200ms", got)
	}
	if got := remainingTimeoutMs(context.Background(), 5000); got != 5000 {
		t.Errorf("without a deadline it must use the fallback, got %d", got)
	}
}

func TestWaitForAliveContext_UsesTheSharedDeadline(t *testing.T) {
	fastPolling(t)
	ctx, cancel := context.WithTimeout(context.Background(), 80*time.Millisecond)
	defer cancel()

	old := &client.Instance{State: "ready", ProjectPath: t.TempDir(), Port: 8090, Timestamp: time.Now().Add(-time.Second).UnixMilli()}
	resolve := func() (*client.Instance, error) { return old, nil }

	start := time.Now()
	_, err := waitForAliveContext(ctx, resolve, remainingTimeoutMs(ctx, 5000))
	elapsed := time.Since(start)
	if err == nil || !strings.Contains(err.Error(), "timed out after") {
		t.Fatalf("expected a shared-deadline timeout, got %v", err)
	}
	if elapsed > 500*time.Millisecond {
		t.Errorf("the wait ignored its context deadline: %v", elapsed)
	}
}

// TestWaitAndSendShareOneBudget is the double-budget regression: waiting for a fresh heartbeat
// must leave less than the full budget for the request, so the pair cannot take 2x. It asserts on
// the budget the connector is told (deadline_ms), not on wall-clock margins: a loaded host makes
// the wait longer, which only makes the reported budget smaller.
func TestWaitAndSendShareOneBudget(t *testing.T) {
	fastPolling(t)
	_, port, deadlines := slowHTTP(t, 100*time.Millisecond)
	const budget = 10 * time.Second
	freshAt := time.Now().Add(time.Second)
	old := time.Now().Add(-time.Second)
	resolve := func() (*client.Instance, error) {
		stamp := old
		if time.Now().After(freshAt) {
			stamp = time.Now()
		}
		return &client.Instance{State: "ready", ProjectPath: t.TempDir(), Port: port, Timestamp: stamp.UnixMilli()}, nil
	}

	ctx, cancel := context.WithTimeout(context.Background(), budget)
	defer cancel()
	inst, err := waitForAliveContext(ctx, resolve, remainingTimeoutMs(ctx, 10000))
	if err != nil {
		t.Fatalf("waitForAliveContext: %v", err)
	}
	if _, err = client.SendContext(ctx, inst, "exec", nil, remainingTimeoutMs(ctx, 0)); err != nil {
		t.Fatalf("send: %v", err)
	}

	received := <-deadlines
	// The wait spent about a second of the ten, so the request must be told a budget that is
	// visibly smaller than the whole invocation. The old double-budget path sent the full 10000.
	if received <= 0 || received > 9500 {
		t.Errorf("deadline_ms = %d, want the ~9000ms left after the wait (budget 10000ms)", received)
	}
}

// TestCompileWaitHonoursTheInvocationDeadline is the regression for a short --timeout on verify or
// editor refresh --compile: the compile wait has its own 5-minute deadline, so it must also stop
// when the invocation context expires. Both commands share this wait.
func TestCompileWaitHonoursTheInvocationDeadline(t *testing.T) {
	compileTestBudgets(t)
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "compiling", CompileFinishedAt: 1_000}, nil
	}

	ctx, cancel := context.WithTimeout(context.Background(), 40*time.Millisecond)
	defer cancel()
	start := time.Now()
	_, err := waitForCompileTo(ctx, resolve, io.Discard, "editor refresh --compile", compileBaseline{FinishedAt: 1_000})
	elapsed := time.Since(start)
	if err == nil || !strings.Contains(err.Error(), "--timeout") {
		t.Fatalf("expected the invocation deadline to end the wait, got %v", err)
	}
	if elapsed > 250*time.Millisecond {
		t.Errorf("the compile wait ignored the invocation deadline: %v", elapsed)
	}
}

func TestTimeoutMessagesUseTheBudgetFormatter(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want string
	}{
		{
			name: "heartbeat wait",
			err:  waitForAliveTimeout(59997),
			want: "timed out after 60s waiting for Unity to answer",
		},
		{
			name: "invocation deadline",
			err:  invocationDeadlineError("verify", 59997, context.DeadlineExceeded),
			want: "timed out after 60s waiting for Unity to answer verify",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if !strings.Contains(tt.err.Error(), tt.want) {
				t.Errorf("message = %q, want it to contain %q", tt.err, tt.want)
			}
		})
	}
}

// slowHTTP answers after delay and reports each request's deadline_ms.
func slowHTTP(t *testing.T, delay time.Duration) (*httptest.Server, int, <-chan int) {
	t.Helper()
	deadlines := make(chan int, 8)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var req client.CommandRequest
		if body, err := io.ReadAll(r.Body); err == nil {
			_ = json.Unmarshal(body, &req)
		}
		deadlines <- req.DeadlineMs
		select {
		case <-r.Context().Done():
		case <-time.After(delay):
			_, _ = w.Write([]byte(`{"success":true}`))
		}
	}))
	t.Cleanup(server.Close)
	return server, server.Listener.Addr().(*net.TCPAddr).Port, deadlines
}
