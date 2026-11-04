package cmd

import (
	"context"
	"errors"
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
// must leave less than the full budget for the request, so the pair cannot take 2x.
func TestWaitAndSendShareOneBudget(t *testing.T) {
	fastPolling(t)
	_, port := slowHTTP(t, 200*time.Millisecond)
	freshAt := time.Now().Add(250 * time.Millisecond)
	old := time.Now().Add(-time.Second)
	resolve := func() (*client.Instance, error) {
		stamp := old
		if time.Now().After(freshAt) {
			stamp = time.Now()
		}
		return &client.Instance{State: "ready", ProjectPath: t.TempDir(), Port: port, Timestamp: stamp.UnixMilli()}, nil
	}

	ctx, cancel := context.WithTimeout(context.Background(), 300*time.Millisecond)
	defer cancel()
	start := time.Now()
	inst, err := waitForAliveContext(ctx, resolve, remainingTimeoutMs(ctx, 300))
	if err != nil {
		t.Fatalf("waitForAliveContext: %v", err)
	}
	_, err = client.SendContext(ctx, inst, "exec", nil, remainingTimeoutMs(ctx, 0))
	elapsed := time.Since(start)

	var timeout *client.RequestTimeoutError
	if !errors.As(err, &timeout) {
		t.Fatalf("expected the one budget to expire during the request, got %T: %v", err, err)
	}
	if elapsed > 450*time.Millisecond {
		t.Errorf("wait + send spent two budgets: %v (budget 300ms)", elapsed)
	}
}

func slowHTTP(t *testing.T, delay time.Duration) (*httptest.Server, int) {
	t.Helper()
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		select {
		case <-r.Context().Done():
		case <-time.After(delay):
			_, _ = w.Write([]byte(`{"success":true}`))
		}
	}))
	t.Cleanup(server.Close)
	return server, server.Listener.Addr().(*net.TCPAddr).Port
}
