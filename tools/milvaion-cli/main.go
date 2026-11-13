// Command milvaion manages Milvaion jobs from the command line and from YAML files.
package main

import (
	"os"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/internal/cmd"
)

// Set by GoReleaser.
var (
	version = "dev"
	commit  = "none"
	date    = "unknown"
)

func main() {
	os.Exit(cmd.Execute(version, commit, date))
}
