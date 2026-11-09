package cmd

import (
	"context"
	"fmt"
	"io"
	"os"
	"os/signal"
	"sync/atomic"
	"syscall"
	"time"
)

// signalExitCode is set by the signal handler and read by main and by the commands that exit
// themselves (verify, wait). It is the only way an interrupt outranks a command's own exit code.
var signalExitCode atomic.Int32

// SignalExitCode is the shell exit status for a signal that interrupted this process (128+signo),
// or 0 when no signal was caught.
func SignalExitCode() int { return int(signalExitCode.Load()) }

// stdoutWrites counts response writes in flight. The signal fallback waits for it to reach zero
// before its hard exit, so a signal cannot truncate a report that was already on its way to a slow
// pipe. A command that holds the process open some other way is still cut off by the grace.
var stdoutWrites atomic.Int32

func beginStdoutWrite() { stdoutWrites.Add(1) }
func endStdoutWrite()   { stdoutWrites.Add(-1) }

// signalWriteGrace bounds how long the fallback waits for an in-flight response write.
var signalWriteGrace = 5 * time.Second

// signalSettle is the head start a command gets to observe the canceled context and print its own
// answer before the fallback exits; a write that begins in that window is then waited for.
var signalSettle = 250 * time.Millisecond

// countedWriter marks every write in flight, for reports written straight to stdout instead of
// through printResponse (verify, wait).
type countedWriter struct{ w io.Writer }

func (c countedWriter) Write(p []byte) (int, error) {
	beginStdoutWrite()
	defer endStdoutWrite()
	return c.w.Write(p)
}

// waitForStdoutWrites blocks until no response write is in flight, or the grace expires.
func waitForStdoutWrites(grace time.Duration) {
	deadline := time.Now().Add(grace)
	for stdoutWrites.Load() != 0 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
}

// installSignalCancellation makes SIGINT/SIGTERM/SIGALRM/SIGHUP cancel in-flight requests instead
// of being swallowed by the Go runtime, and guarantees the process leaves with 128+signo even when
// the command is between requests (a test or build poll sleep). The fallback only matters for those
// sleeps and for a response write that has not reached a slow reader yet: a command that observes
// the canceled context returns long before it.
func installSignalCancellation() (context.Context, context.CancelFunc) {
	ctx, cancel := context.WithCancel(context.Background())
	signals := make(chan os.Signal, 1)
	signal.Notify(signals, terminationSignals()...)

	go func() {
		sig := <-signals
		code := 128 + signalNumber(sig)
		signalExitCode.Store(int32(code))
		fmt.Fprintf(os.Stderr, "unity-cli: interrupted by %s\n", sig)
		cancel()
		time.Sleep(signalSettle)
		waitForStdoutWrites(signalWriteGrace)
		os.Exit(code)
	}()

	return ctx, func() {
		signal.Stop(signals)
		cancel()
	}
}

// exitNow is os.Exit for commands that answer with their own status code: an interrupt must still
// win, otherwise 'verify' would exit 2 after its request was canceled by SIGINT.
func exitNow(code int) {
	if interrupted := SignalExitCode(); interrupted != 0 {
		code = interrupted
	}
	os.Exit(code)
}

func signalNumber(sig os.Signal) int {
	if s, ok := sig.(syscall.Signal); ok {
		return int(s)
	}
	return 1
}
