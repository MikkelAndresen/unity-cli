package cmd

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func writeHeartbeatSet(t *testing.T, files map[string]client.Instance) string {
	t.Helper()
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	dir := filepath.Join(home, ".unity-cli", "instances")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatalf("create instances dir: %v", err)
	}
	for name, inst := range files {
		data, err := json.Marshal(inst)
		if err != nil {
			t.Fatalf("marshal %s: %v", name, err)
		}
		if err := os.WriteFile(filepath.Join(dir, name), data, 0o644); err != nil {
			t.Fatalf("write %s: %v", name, err)
		}
	}
	return home
}

func freeLocalPort(t *testing.T) int {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("reserve port: %v", err)
	}
	port := ln.Addr().(*net.TCPAddr).Port
	_ = ln.Close()
	return port
}

func serverPort(t *testing.T, srv *httptest.Server) int {
	t.Helper()
	port, err := strconv.Atoi(strings.TrimPrefix(srv.URL, "http://127.0.0.1:"))
	if err != nil {
		t.Fatalf("parse test server port from %q: %v", srv.URL, err)
	}
	return port
}

func answeringServer(t *testing.T) (*httptest.Server, *int32) {
	t.Helper()
	var hits int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		atomic.AddInt32(&hits, 1)
		_, _ = io.Copy(io.Discard, r.Body)
		fmt.Fprint(w, `{"success":true,"message":"ok","data":"retried"}`)
	}))
	t.Cleanup(srv.Close)
	return srv, &hits
}

// droppingServer accepts the request and closes before any response bytes, the shape of a connector
// that went away with its domain after the request may already have been read.
func droppingServer(t *testing.T) (*httptest.Server, *int32) {
	t.Helper()
	var hits int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		atomic.AddInt32(&hits, 1)
		hijacker, ok := w.(http.Hijacker)
		if !ok {
			t.Errorf("test server cannot hijack the connection")
			return
		}
		conn, _, err := hijacker.Hijack()
		if err != nil {
			t.Errorf("hijack: %v", err)
			return
		}
		_ = conn.Close()
	}))
	t.Cleanup(srv.Close)
	return srv, &hits
}

func heartbeat(project string, port, pid int, state string, at time.Time) client.Instance {
	return client.Instance{
		State:       state,
		ProjectPath: project,
		Port:        port,
		PID:         pid,
		Timestamp:   at.UnixMilli(),
	}
}

// domainReloadServer answers with the connector's promise that the request never reached a tool
// handler. data is the whole failure payload so a test can omit `started` or set it true.
func domainReloadServer(t *testing.T, data string) (*httptest.Server, *int32) {
	t.Helper()
	var hits int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		atomic.AddInt32(&hits, 1)
		_, _ = io.Copy(io.Discard, r.Body)
		fmt.Fprintf(w, `{"success":false,"message":"exec never ran: Unity stopped answering before the main thread picked it up","data":%s}`, data)
	}))
	t.Cleanup(srv.Close)
	return srv, &hits
}

func TestSendAfterReload_RetriesCommandTheConnectorSaysNeverRan(t *testing.T) {
	project := t.TempDir()
	oldSrv, oldHits := domainReloadServer(t, `{"code":"domain_reload","command":"exec","started":false}`)
	oldPort := serverPort(t, oldSrv)
	newSrv, newHits := answeringServer(t)
	newPort := serverPort(t, newSrv)

	writeHeartbeatSet(t, map[string]client.Instance{
		"old.json": heartbeat(project, oldPort, 0, "ready", time.Now().Add(-5*time.Second)),
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	old := &client.Instance{State: "ready", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().Add(-5 * time.Second).UnixMilli()}
	fresh := &client.Instance{State: "ready", ProjectPath: project, Port: newPort, PID: 0, Timestamp: time.Now().UnixMilli()}
	var calls int32
	resolve := func() (*client.Instance, error) {
		if atomic.AddInt32(&calls, 1) == 1 {
			return old, nil
		}
		return fresh, nil
	}

	resp, err := sendAfterReload(context.Background(), resolve, newVersionGate("exec"), "exec", map[string]interface{}{"code": "return 1;"}, 2000)
	if err != nil {
		t.Fatalf("sendAfterReload: %v", err)
	}
	if !resp.Success || string(resp.Data) != `"retried"` {
		t.Fatalf("unexpected response: %+v", resp)
	}
	if got := atomic.LoadInt32(oldHits); got != 1 {
		t.Errorf("old-port server got %d hits, want 1", got)
	}
	if got := atomic.LoadInt32(newHits); got != 1 {
		t.Errorf("new-port server got %d hits, want 1 from the retry", got)
	}
}

func TestSendAfterReload_DoesNotRetryWhenConnectorCannotSayItNeverRan(t *testing.T) {
	tests := []struct {
		name string
		data string
	}{
		{"handler had started", `{"code":"domain_reload","command":"exec","started":true}`},
		{"started flag missing", `{"code":"domain_reload","command":"exec"}`},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			project := t.TempDir()
			oldSrv, oldHits := domainReloadServer(t, tt.data)
			oldPort := serverPort(t, oldSrv)
			newSrv, newHits := answeringServer(t)
			newPort := serverPort(t, newSrv)
			writeHeartbeatSet(t, map[string]client.Instance{
				"old.json": heartbeat(project, oldPort, 0, "ready", time.Now().Add(-5*time.Second)),
				"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
			})
			old := &client.Instance{State: "ready", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().Add(-5 * time.Second).UnixMilli()}
			resolve := func() (*client.Instance, error) { return old, nil }

			resp, err := sendAfterReload(context.Background(), resolve, newVersionGate("exec"), "exec", map[string]interface{}{"code": "return 1;"}, 2000)
			if err != nil {
				t.Fatalf("sendAfterReload: %v", err)
			}
			if resp.Success || client.ConnectorErrorCode(resp) != codeDomainReload {
				t.Fatalf("unexpected response: %+v", resp)
			}
			if got := atomic.LoadInt32(oldHits); got != 1 {
				t.Errorf("old-port server got %d hits, want 1", got)
			}
			if got := atomic.LoadInt32(newHits); got != 0 {
				t.Errorf("new-port server got %d hits, want no retry", got)
			}
		})
	}
}

func TestSendAfterReload_DeadlineExhaustedReturnsOriginalAnswer(t *testing.T) {
	project := t.TempDir()
	oldSrv, oldHits := domainReloadServer(t, `{"code":"domain_reload","command":"exec","started":false}`)
	oldPort := serverPort(t, oldSrv)
	newSrv, newHits := answeringServer(t)
	newPort := serverPort(t, newSrv)
	writeHeartbeatSet(t, map[string]client.Instance{
		"old.json": heartbeat(project, oldPort, 0, "ready", time.Now().Add(-5*time.Second)),
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	// The resolver never advances to the fresh heartbeat, so the retry can only wait until the
	// invocation deadline: the original answer must then be reported, not a timeout.
	old := &client.Instance{State: "reloading", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().Add(-5 * time.Second).UnixMilli()}
	resolve := func() (*client.Instance, error) { return old, nil }

	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	resp, err := sendAfterReload(ctx, resolve, newVersionGate("exec"), "exec", map[string]interface{}{"code": "return 1;"}, 5000)
	if err != nil {
		t.Fatalf("sendAfterReload: %v", err)
	}
	if resp == nil || resp.Success || client.ConnectorErrorCode(resp) != codeDomainReload {
		t.Fatalf("unexpected response: %+v", resp)
	}
	if got := atomic.LoadInt32(oldHits); got != 1 {
		t.Errorf("old-port server got %d hits, want 1", got)
	}
	if got := atomic.LoadInt32(newHits); got != 0 {
		t.Errorf("new-port server got %d hits, want no retry once the deadline is spent", got)
	}
}

func TestSendAfterReload_RetriesReadOnlyCommandOnPortChange(t *testing.T) {
	project := t.TempDir()
	oldSrv, oldHits := droppingServer(t)
	oldPort := serverPort(t, oldSrv)
	newSrv, newHits := answeringServer(t)
	newPort := serverPort(t, newSrv)

	writeHeartbeatSet(t, map[string]client.Instance{
		"old.json": heartbeat(project, oldPort, 0, "ready", time.Now().Add(-5*time.Second)),
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	old := &client.Instance{State: "ready", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().Add(-5 * time.Second).UnixMilli()}
	fresh := &client.Instance{State: "ready", ProjectPath: project, Port: newPort, PID: 0, Timestamp: time.Now().UnixMilli()}
	var calls int32
	resolve := func() (*client.Instance, error) {
		if atomic.AddInt32(&calls, 1) == 1 {
			return old, nil
		}
		return fresh, nil
	}

	resp, err := sendAfterReload(context.Background(), resolve, newVersionGate("list"), "list", nil, 2000)
	if err != nil {
		t.Fatalf("sendAfterReload: %v", err)
	}
	if !resp.Success || string(resp.Data) != `"retried"` {
		t.Fatalf("unexpected response: %+v", resp)
	}
	if got := atomic.LoadInt32(oldHits); got != 1 {
		t.Errorf("old-port server got %d hits, want 1", got)
	}
	if got := atomic.LoadInt32(newHits); got != 1 {
		t.Fatalf("new-port server got %d hits, want 1", got)
	}
}

func TestSendAfterReload_RetriesStateChangingCommandOnlyWhenRefused(t *testing.T) {
	project := t.TempDir()
	newSrv, newHits := answeringServer(t)
	newPort := serverPort(t, newSrv)
	oldPort := freeLocalPort(t)

	writeHeartbeatSet(t, map[string]client.Instance{
		"old.json": heartbeat(project, oldPort, 0, "ready", time.Now().Add(-5*time.Second)),
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	old := &client.Instance{State: "ready", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().Add(-5 * time.Second).UnixMilli()}
	resolve := func() (*client.Instance, error) { return old, nil }

	resp, err := sendAfterReload(context.Background(), resolve, newVersionGate("exec"), "exec", map[string]interface{}{"code": "return 1;"}, 2000)
	if err != nil {
		t.Fatalf("sendAfterReload: %v", err)
	}
	if !resp.Success {
		t.Fatalf("unexpected response: %+v", resp)
	}
	if got := atomic.LoadInt32(newHits); got != 1 {
		t.Fatalf("new-port server got %d hits, want 1 after a refused connection", got)
	}
}

func TestSendAfterReload_DoesNotRepeatStateChangingCommandAfterEOF(t *testing.T) {
	project := t.TempDir()
	oldSrv, oldHits := droppingServer(t)
	oldPort := serverPort(t, oldSrv)
	newSrv, newHits := answeringServer(t)
	newPort := serverPort(t, newSrv)

	writeHeartbeatSet(t, map[string]client.Instance{
		"old.json": heartbeat(project, oldPort, 0, "ready", time.Now().Add(-5*time.Second)),
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	old := &client.Instance{State: "ready", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().Add(-5 * time.Second).UnixMilli()}
	resolve := func() (*client.Instance, error) { return old, nil }

	_, err := sendAfterReload(context.Background(), resolve, newVersionGate("exec"), "exec", map[string]interface{}{"code": "return 1;"}, 2000)
	if err == nil {
		t.Fatal("expected the request to fail")
	}
	for _, want := range []string{"may have run", fmt.Sprintf("port %d", newPort)} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("error must contain %q, got:\n%s", want, err)
		}
	}
	if got := atomic.LoadInt32(oldHits); got != 1 {
		t.Errorf("old-port server got %d hits, want 1", got)
	}
	if got := atomic.LoadInt32(newHits); got != 0 {
		t.Errorf("new-port server got %d hits, want 0 for a command that may already have run", got)
	}
}

func TestSendAfterReload_ReadOnlyRetriesOnceOnly(t *testing.T) {
	project := t.TempDir()
	oldSrv, oldHits := droppingServer(t)
	oldPort := serverPort(t, oldSrv)
	newSrv, newHits := droppingServer(t)
	newPort := serverPort(t, newSrv)

	writeHeartbeatSet(t, map[string]client.Instance{
		"old.json": heartbeat(project, oldPort, 0, "ready", time.Now().Add(-5*time.Second)),
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	old := &client.Instance{State: "ready", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().Add(-5 * time.Second).UnixMilli()}
	fresh := &client.Instance{State: "ready", ProjectPath: project, Port: newPort, PID: 0, Timestamp: time.Now().UnixMilli()}
	var calls int32
	resolve := func() (*client.Instance, error) {
		if atomic.AddInt32(&calls, 1) == 1 {
			return old, nil
		}
		return fresh, nil
	}

	if _, err := sendAfterReload(context.Background(), resolve, newVersionGate("list"), "list", nil, 2000); err == nil {
		t.Fatal("expected the request to fail")
	}
	if oldGot, newGot := atomic.LoadInt32(oldHits), atomic.LoadInt32(newHits); oldGot != 1 || newGot != 1 {
		t.Fatalf("hits old=%d new=%d, want exactly one attempt each", oldGot, newGot)
	}
}

// console and modal carry mutating actions (--clear, --action answer). The retry list is keyed by
// command, so both must be treated as state-changing even when a particular call was a read.
func TestSendAfterReload_DoesNotRepeatMutatingToolAfterEOF(t *testing.T) {
	for _, command := range []string{"console", "modal"} {
		t.Run(command, func(t *testing.T) {
			project := t.TempDir()
			oldSrv, oldHits := droppingServer(t)
			oldPort := serverPort(t, oldSrv)
			newSrv, newHits := answeringServer(t)
			newPort := serverPort(t, newSrv)

			writeHeartbeatSet(t, map[string]client.Instance{
				"old.json": heartbeat(project, oldPort, 0, "ready", time.Now().Add(-5*time.Second)),
				"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
			})
			old := &client.Instance{State: "ready", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().Add(-5 * time.Second).UnixMilli()}
			resolve := func() (*client.Instance, error) { return old, nil }

			_, err := sendAfterReload(context.Background(), resolve, newVersionGate(command), command, nil, 2000)
			if err == nil || !strings.Contains(err.Error(), "may have run") {
				t.Fatalf("error = %v, want a 'may have run' report instead of a retry", err)
			}
			if got := atomic.LoadInt32(oldHits); got != 1 {
				t.Errorf("old-port server got %d hits, want 1", got)
			}
			if got := atomic.LoadInt32(newHits); got != 0 {
				t.Errorf("new-port server got %d hits, want 0 for a mutating tool", got)
			}
		})
	}
}

// A safe command that lost its answer while the heartbeat still said "reloading" must wait for the
// editor to answer instead of spending the retry against a domain that cannot take it.
func TestSendAfterReload_ReadOnlyWaitsOutReloadingHeartbeat(t *testing.T) {
	origPoll := statusPollInterval
	statusPollInterval = 5 * time.Millisecond
	t.Cleanup(func() { statusPollInterval = origPoll })

	project := t.TempDir()
	oldSrv, oldHits := droppingServer(t)
	oldPort := serverPort(t, oldSrv)
	newSrv, newHits := answeringServer(t)
	newPort := serverPort(t, newSrv)

	writeHeartbeatSet(t, map[string]client.Instance{
		"old.json": heartbeat(project, oldPort, 0, "reloading", time.Now()),
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	old := &client.Instance{State: "reloading", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().UnixMilli()}
	fresh := &client.Instance{State: "ready", ProjectPath: project, Port: newPort, PID: 0, Timestamp: time.Now().UnixMilli()}
	var calls int32
	resolve := func() (*client.Instance, error) {
		// The old record keeps saying reloading for the first few polls, then the domain is back.
		if atomic.AddInt32(&calls, 1) <= 3 {
			return old, nil
		}
		return fresh, nil
	}

	resp, err := sendAfterReload(context.Background(), resolve, newVersionGate("compile_status"), "compile_status", nil, 2000)
	if err != nil {
		t.Fatalf("sendAfterReload: %v", err)
	}
	if !resp.Success || string(resp.Data) != `"retried"` {
		t.Fatalf("unexpected response: %+v", resp)
	}
	if got := atomic.LoadInt32(oldHits); got != 1 {
		t.Errorf("old-port server got %d hits, want 1", got)
	}
	if got := atomic.LoadInt32(newHits); got != 1 {
		t.Errorf("new-port server got %d hits, want 1 after the reload", got)
	}
}

// The compile gate refuses before dispatch, so its answer is also a guarantee that nothing ran.
func TestSendAfterReload_RetriesCommandRefusedWhileCompiling(t *testing.T) {
	origPoll := statusPollInterval
	statusPollInterval = 5 * time.Millisecond
	t.Cleanup(func() { statusPollInterval = origPoll })

	project := t.TempDir()
	oldSrv, oldHits := domainReloadServer(t, `{"code":"compiling","started":false,"compiling":true}`)
	oldPort := serverPort(t, oldSrv)
	newSrv, newHits := answeringServer(t)
	newPort := serverPort(t, newSrv)

	writeHeartbeatSet(t, map[string]client.Instance{
		"old.json": heartbeat(project, oldPort, 0, "compiling", time.Now()),
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	old := &client.Instance{State: "compiling", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().UnixMilli()}
	fresh := &client.Instance{State: "ready", ProjectPath: project, Port: newPort, PID: 0, Timestamp: time.Now().UnixMilli()}
	var calls int32
	resolve := func() (*client.Instance, error) {
		if atomic.AddInt32(&calls, 1) <= 3 {
			return old, nil
		}
		return fresh, nil
	}

	resp, err := sendAfterReload(context.Background(), resolve, newVersionGate("exec"), "exec", map[string]interface{}{"code": "return 1;"}, 2000)
	if err != nil {
		t.Fatalf("sendAfterReload: %v", err)
	}
	if !resp.Success || string(resp.Data) != `"retried"` {
		t.Fatalf("unexpected response: %+v", resp)
	}
	if got := atomic.LoadInt32(oldHits); got != 1 {
		t.Errorf("compiling connector got %d hits, want 1", got)
	}
	if got := atomic.LoadInt32(newHits); got != 1 {
		t.Errorf("new-port server got %d hits, want 1 after the compile", got)
	}
}

func TestSendAfterReload_DoesNotRetryCompileRefusalWithoutStartedFlag(t *testing.T) {
	project := t.TempDir()
	oldSrv, oldHits := domainReloadServer(t, `{"code":"compiling","compiling":true}`)
	oldPort := serverPort(t, oldSrv)
	newSrv, newHits := answeringServer(t)
	newPort := serverPort(t, newSrv)

	writeHeartbeatSet(t, map[string]client.Instance{
		"old.json": heartbeat(project, oldPort, 0, "compiling", time.Now()),
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	old := &client.Instance{State: "compiling", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().UnixMilli()}
	resolve := func() (*client.Instance, error) { return old, nil }

	resp, err := sendAfterReload(context.Background(), resolve, newVersionGate("exec"), "exec", map[string]interface{}{"code": "return 1;"}, 2000)
	if err != nil {
		t.Fatalf("sendAfterReload: %v", err)
	}
	if resp.Success || client.ConnectorErrorCode(resp) != codeCompiling {
		t.Fatalf("unexpected response: %+v", resp)
	}
	if got := atomic.LoadInt32(oldHits); got != 1 {
		t.Errorf("compiling connector got %d hits, want 1", got)
	}
	if got := atomic.LoadInt32(newHits); got != 0 {
		t.Errorf("new-port server got %d hits, want no retry", got)
	}
}

func TestDiscoverStatusInstance_PicksFreshestHeartbeatForProject(t *testing.T) {
	project := t.TempDir()
	writeHeartbeatSet(t, map[string]client.Instance{
		"aaa_old.json": heartbeat(project, 8090, 0, "ready", time.Now().Add(-5*time.Second)),
		"zzz_new.json": heartbeat(project, 8091, 0, "ready", time.Now()),
	})

	got, err := discoverStatusInstance(project, 0)
	if err != nil {
		t.Fatalf("discoverStatusInstance: %v", err)
	}
	if got.Port != 8091 {
		t.Fatalf("status resolved port %d, want the freshest heartbeat on 8091", got.Port)
	}
}

// TestSendAfterReload_DoesNotRetryWhenTheBeatDidNotAdvance: the retry target must be a newer beat.
// The same record (or an unchanged timestamp) is not evidence that the editor moved, so the
// original error stands instead of sending the command again.
func TestSendAfterReload_DoesNotRetryWhenTheBeatDidNotAdvance(t *testing.T) {
	project := t.TempDir()
	oldSrv, oldHits := droppingServer(t)
	oldPort := serverPort(t, oldSrv)
	beatAt := time.Now()
	writeHeartbeatSet(t, map[string]client.Instance{
		"only.json": heartbeat(project, oldPort, 0, "reloading", beatAt),
	})
	old := &client.Instance{State: "reloading", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: beatAt.UnixMilli()}
	resolve := func() (*client.Instance, error) { return old, nil }

	_, err := sendAfterReload(context.Background(), resolve, newVersionGate("exec"), "exec", map[string]interface{}{"code": "return 1;"}, 2000)
	if err == nil {
		t.Fatal("expected the original failure")
	}
	if strings.Contains(err.Error(), "retry:") {
		t.Errorf("an unchanged beat must not trigger a retry:\n%s", err)
	}
	if got := atomic.LoadInt32(oldHits); got != 1 {
		t.Errorf("old-port server got %d hits, want 1", got)
	}
}

// TestSendAfterReload_RespectsAnExplicitPortPin: --port selects the instance, so a reload that
// moved the editor to another port must not silently redirect the retry there.
func TestSendAfterReload_RespectsAnExplicitPortPin(t *testing.T) {
	origPort := flagPort
	t.Cleanup(func() { flagPort = origPort })

	project := t.TempDir()
	oldPort := freeLocalPort(t)
	newSrv, newHits := answeringServer(t)
	newPort := serverPort(t, newSrv)
	writeHeartbeatSet(t, map[string]client.Instance{
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	inst := &client.Instance{State: "ready", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().Add(-5 * time.Second).UnixMilli()}
	resolve := func() (*client.Instance, error) { return inst, nil }
	flagPort = oldPort

	err := func() error {
		_, err := sendAfterReload(context.Background(), resolve, newVersionGate("exec"), "console", nil, 2000)
		return err
	}()
	if err == nil {
		t.Fatal("expected the original connection failure")
	}
	if got := atomic.LoadInt32(newHits); got != 0 {
		t.Errorf("new-port server got %d hits, want none: --port pinned the old instance", got)
	}
}

// TestSendAfterReload_RetryFailureKeepsTheOriginalDiagnosis: when the retry fails too, the operator
// must still see why the first attempt died.
func TestSendAfterReload_RetryFailureKeepsTheOriginalDiagnosis(t *testing.T) {
	project := t.TempDir()
	oldPort := freeLocalPort(t)
	newSrv, newHits := droppingServer(t)
	newPort := serverPort(t, newSrv)
	writeHeartbeatSet(t, map[string]client.Instance{
		"new.json": heartbeat(project, newPort, 0, "ready", time.Now()),
	})
	inst := &client.Instance{State: "ready", ProjectPath: project, Port: oldPort, PID: 0, Timestamp: time.Now().Add(-5 * time.Second).UnixMilli()}
	resolve := func() (*client.Instance, error) { return inst, nil }

	_, err := sendAfterReload(context.Background(), resolve, newVersionGate("exec"), "exec", map[string]interface{}{"code": "return 1;"}, 2000)
	if err == nil {
		t.Fatal("expected the retry to fail")
	}
	for _, want := range []string{"cannot connect to Unity at port " + fmt.Sprint(oldPort), "did not answer the retry"} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("error must contain %q, got:\n%s", want, err)
		}
	}
	if got := atomic.LoadInt32(newHits); got != 1 {
		t.Errorf("new-port server got %d hits, want 1", got)
	}
}
