package main

import (
	"fmt"
	"os"

	"github.com/youngwoocho02/unity-cli/cmd"
)

var Version = "dev"

func init() {
	cmd.Version = Version
}

func main() {
	err := cmd.Execute()
	// An interrupt outranks the command's own status: the request was canceled by a signal, so the
	// shell must see 128+signo even if the command had already decided on a different exit code.
	if code := cmd.SignalExitCode(); code != 0 {
		os.Exit(code)
	}
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error: %v\n", err)
		os.Exit(1)
	}
}
