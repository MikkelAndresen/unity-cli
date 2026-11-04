package cmd

import (
	"context"
	"fmt"
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

// installSignalCancellation makes SIGINT/SIGTERM/SIGALRM/SIGHUP cancel in-flight requests instead
// of being swallowed by the Go runtime, and guarantees the process leaves with 128+signo even when
// the command is between requests (a test or build poll sleep). The fallback timer only matters
// for those sleeps: a command that observes the canceled context returns long before it.
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
		time.Sleep(250 * time.Millisecond)
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
