//go:build !windows

package cmd

import (
	"os"
	"syscall"
)

func terminationSignals() []os.Signal {
	return []os.Signal{os.Interrupt, syscall.SIGTERM, syscall.SIGALRM, syscall.SIGHUP}
}
