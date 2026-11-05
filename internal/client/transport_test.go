package client

import (
	"bufio"
	"bytes"
	"context"
	"fmt"
	"log"
	"net"
	"sync/atomic"
	"testing"
	"time"
)

// doubleAnsweringServer answers each request and then writes a second response on the same
// connection, which is what a Unity listener being torn down by a domain reload leaves on the wire.
// It keeps the connection open afterwards, the way a listener that has not noticed the reload does.
func doubleAnsweringServer(t *testing.T) (port int, connections *int32) {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	t.Cleanup(func() { _ = ln.Close() })
	port = ln.Addr().(*net.TCPAddr).Port

	var conns int32
	connections = &conns
	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			atomic.AddInt32(&conns, 1)
			go func(c net.Conn) {
				defer c.Close()
				reader := bufio.NewReader(c)
				for {
					line, err := reader.ReadString('\n')
					if err != nil {
						return
					}
					if line == "\r\n" {
						break
					}
				}
				body := `{"success":true,"message":"ok","data":"x"}`
				fmt.Fprintf(c, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: %d\r\n\r\n%s", len(body), body)
				time.Sleep(20 * time.Millisecond)
				fmt.Fprintf(c, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}")
				time.Sleep(300 * time.Millisecond)
			}(conn)
		}
	}()
	return port, connections
}

// TestSendContext_NeverLogsAnUnsolicitedResponse is the regression for the stderr line
// `Unsolicited response received on idle HTTP channel ...` that a reload used to print in the middle
// of a caller's pipeline. The connector client keeps no connection alive, so the reload's leftover
// answer is written to a connection nobody is reading, and net/http has nothing to log.
func TestSendContext_NeverLogsAnUnsolicitedResponse(t *testing.T) {
	port, connections := doubleAnsweringServer(t)
	inst := &Instance{State: "ready", ProjectPath: t.TempDir(), Port: port, PID: 1}

	var logs bytes.Buffer
	original := log.Writer()
	log.SetOutput(&logs)
	t.Cleanup(func() { log.SetOutput(original) })

	for i := 0; i < 2; i++ {
		resp, err := SendContext(context.Background(), inst, "status", nil, 5000)
		if err != nil {
			t.Fatalf("request %d: %v", i+1, err)
		}
		if resp == nil || !resp.Success {
			t.Fatalf("request %d: response = %+v, want success", i+1, resp)
		}
	}

	// Give the transport's reader a chance to see the extra response if one were still attached.
	time.Sleep(150 * time.Millisecond)

	if logs.Len() != 0 {
		t.Errorf("the standard logger got %q, want nothing: a reload's leftover response must not reach stderr", logs.String())
	}
	if got := atomic.LoadInt32(connections); got != 2 {
		t.Errorf("server accepted %d connections for 2 requests, want one connection each", got)
	}
}

// TestSendContext_SurvivesADroppedKeepAliveConnection: with pooling off, the second request must open
// its own connection instead of failing on the one the fake listener has since closed.
func TestSendContext_SurvivesASequentialReload(t *testing.T) {
	port, connections := doubleAnsweringServer(t)
	inst := &Instance{State: "ready", ProjectPath: t.TempDir(), Port: port, PID: 1}

	if _, err := SendContext(context.Background(), inst, "status", nil, 5000); err != nil {
		t.Fatalf("first request: %v", err)
	}
	if _, err := SendContext(context.Background(), inst, "status", nil, 5000); err != nil {
		t.Fatalf("second request: %v", err)
	}
	if got := atomic.LoadInt32(connections); got != 2 {
		t.Errorf("server accepted %d connections for 2 requests, want 2", got)
	}
}

// TestFormatBudgetReportsTheBudgetGiven: `--timeout 1` must not be reported as 10 ms (F17).
func TestFormatBudgetReportsTheBudgetGiven(t *testing.T) {
	tests := []struct {
		budget time.Duration
		want   string
	}{
		{time.Millisecond, "1ms"},
		{5 * time.Millisecond, "5ms"},
		{999 * time.Millisecond, "999ms"},
		{1400 * time.Microsecond, "2ms"},
		{time.Second, "1s"},
		{1947998375 * time.Nanosecond, "2s"},
		{59997 * time.Millisecond, "60s"},
		{0, "0s"},
	}
	for _, tt := range tests {
		if got := FormatBudget(tt.budget); got != tt.want {
			t.Errorf("FormatBudget(%v) = %q, want %q", tt.budget, got, tt.want)
		}
	}
}
