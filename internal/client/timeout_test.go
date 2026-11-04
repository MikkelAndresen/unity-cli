package client

import (
	"context"
	"errors"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func slowServer(t *testing.T, delay time.Duration) (*httptest.Server, int) {
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

func TestSendContext_DeadlineIsNotAConnectionFailure(t *testing.T) {
	_, port := slowServer(t, 500*time.Millisecond)
	inst := &Instance{State: "ready", ProjectPath: t.TempDir(), Port: port, PID: 1}

	start := time.Now()
	_, err := SendContext(context.Background(), inst, "exec", nil, 100)
	elapsed := time.Since(start)
	if err == nil {
		t.Fatal("expected a timeout")
	}
	var timeout *RequestTimeoutError
	if !errors.As(err, &timeout) {
		t.Fatalf("expected RequestTimeoutError, got %T: %v", err, err)
	}
	for _, want := range []string{
		"timed out after",
		"waiting for Unity to answer exec",
		"the editor may still be running it",
	} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("message missing %q: %s", want, err)
		}
	}
	if strings.Contains(err.Error(), "cannot connect") || strings.Contains(err.Error(), "crash") {
		t.Errorf("a deadline must not read as a connection failure: %s", err)
	}
	if elapsed > time.Second {
		t.Errorf("the request ran past its budget: %v", elapsed)
	}
}

func TestSendContext_ParentDeadlineSetsTheBudget(t *testing.T) {
	_, port := slowServer(t, 500*time.Millisecond)
	inst := &Instance{State: "ready", ProjectPath: t.TempDir(), Port: port, PID: 1}
	ctx, cancel := context.WithTimeout(context.Background(), 120*time.Millisecond)
	defer cancel()

	_, err := SendContext(ctx, inst, "test", nil, 0)
	var timeout *RequestTimeoutError
	if !errors.As(err, &timeout) {
		t.Fatalf("expected RequestTimeoutError, got %T: %v", err, err)
	}
	if timeout.Budget <= 0 || timeout.Budget > 500*time.Millisecond {
		t.Errorf("expected the parent's remaining budget, got %v", timeout.Budget)
	}
	if !strings.Contains(err.Error(), "waiting for Unity to answer test") {
		t.Errorf("message must name the command: %s", err)
	}
}

func TestSendContext_CancelIsNotTimeoutOrCrash(t *testing.T) {
	started := make(chan struct{})
	release := make(chan struct{})
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		close(started)
		<-release
	}))
	defer func() {
		close(release)
		server.Close()
	}()
	inst := &Instance{State: "ready", ProjectPath: t.TempDir(), Port: server.Listener.Addr().(*net.TCPAddr).Port, PID: 1}

	ctx, cancel := context.WithCancel(context.Background())
	result := make(chan error, 1)
	go func() {
		_, err := SendContext(ctx, inst, "exec", nil, 5000)
		result <- err
	}()

	<-started
	cancel()
	err := <-result
	if err == nil {
		t.Fatal("expected the canceled request to fail")
	}
	for _, unwanted := range []string{"timed out", "cannot connect", "crash"} {
		if strings.Contains(err.Error(), unwanted) {
			t.Errorf("a canceled request must not read as %q: %s", unwanted, err)
		}
	}
	if !strings.Contains(err.Error(), "cancelled") {
		t.Errorf("expected cancellation text, got: %s", err)
	}
}
