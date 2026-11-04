package cmd

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"
)

// TestReadStdinIfPiped_BoundsANeverClosingProducer: a producer that never closes stdin must not
// outlast --timeout, and it must not run before discovery either (a project with no editor answers
// first).
func TestReadStdinIfPiped_BoundsANeverClosingProducer(t *testing.T) {
	readEnd, writeEnd, err := os.Pipe()
	if err != nil {
		t.Fatalf("pipe: %v", err)
	}
	t.Cleanup(func() {
		_ = writeEnd.Close()
		_ = readEnd.Close()
	})
	orig := os.Stdin
	os.Stdin = readEnd
	t.Cleanup(func() { os.Stdin = orig })

	start := time.Now()
	_, err = readStdinIfPiped(nil, 50)
	if err == nil {
		t.Fatal("expected the read to be bounded by the invocation budget")
	}
	if !strings.Contains(err.Error(), "reading piped stdin") {
		t.Errorf("error = %v, want it to name the stdin read", err)
	}
	if elapsed := time.Since(start); elapsed > time.Second {
		t.Errorf("the read was not bounded: %v", elapsed)
	}
}

func TestReadStdinIfPiped_PrependsPipedCode(t *testing.T) {
	readEnd, writeEnd, err := os.Pipe()
	if err != nil {
		t.Fatalf("pipe: %v", err)
	}
	orig := os.Stdin
	os.Stdin = readEnd
	t.Cleanup(func() {
		os.Stdin = orig
		_ = readEnd.Close()
	})

	if _, err := writeEnd.WriteString("return 1;\n"); err != nil {
		t.Fatalf("write: %v", err)
	}
	_ = writeEnd.Close()

	args, err := readStdinIfPiped([]string{"--usings", "X"}, 5000)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(args) != 3 || args[0] != "return 1;" || args[1] != "--usings" {
		t.Errorf("args = %v, want the piped code first", args)
	}
}

// TestWaitForStdoutWrites_DoesNotCutOffAnInFlightWrite: the signal fallback waits for a response
// write that is still going out instead of exiting on a fixed 250 ms timer.
func TestWaitForStdoutWrites_DoesNotCutOffAnInFlightWrite(t *testing.T) {
	beginStdoutWrite()
	go func() {
		time.Sleep(50 * time.Millisecond)
		endStdoutWrite()
	}()

	start := time.Now()
	waitForStdoutWrites(time.Second)
	if elapsed := time.Since(start); elapsed < 40*time.Millisecond {
		t.Errorf("the wait returned after %v, before the write finished", elapsed)
	}
	if stdoutWrites.Load() != 0 {
		t.Error("the write must have finished")
	}

	start = time.Now()
	waitForStdoutWrites(time.Second)
	if elapsed := time.Since(start); elapsed > 100*time.Millisecond {
		t.Errorf("no write in flight must return at once, took %v", elapsed)
	}
}

// TestPollTestResults_NoSecondDeadline: with a --timeout budget the poll reports that deadline, not
// the old private 10-minute one.
func TestPollTestResults_NoSecondDeadline(t *testing.T) {
	isolatedHome(t)
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()

	start := time.Now()
	_, err := pollTestResults(ctx, 8090)
	if err == nil {
		t.Fatal("expected the invocation deadline to end the poll")
	}
	if !strings.Contains(err.Error(), "--timeout") {
		t.Errorf("error = %v, want the invocation deadline named", err)
	}
	if strings.Contains(err.Error(), "10m") {
		t.Errorf("error = %v, want no second 10-minute limit", err)
	}
	if elapsed := time.Since(start); elapsed > time.Second {
		t.Errorf("the poll ignored its context: %v", elapsed)
	}
}
