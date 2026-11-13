// Command terraform-provider-milvaion is the Terraform provider for Milvaion.
package main

import (
	"context"
	"flag"
	"log"

	"github.com/hashicorp/terraform-plugin-framework/providerserver"

	"github.com/Milvasoft/milvaion/tools/terraform-provider-milvaion/internal/provider"
)

// Set by GoReleaser.
var version = "dev"

func main() {
	var debug bool
	flag.BoolVar(&debug, "debug", false, "run the provider with support for debuggers like delve")
	flag.Parse()

	err := providerserver.Serve(context.Background(), provider.New(version), providerserver.ServeOpts{
		Address: "registry.terraform.io/milvasoft/milvaion",
		Debug:   debug,
	})
	if err != nil {
		log.Fatal(err)
	}
}
