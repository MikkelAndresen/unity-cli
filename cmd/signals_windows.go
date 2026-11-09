//go:build windows

package cmd

import "os"

func terminationSignals() []os.Signal {
	return []os.Signal{os.Interrupt}
}
