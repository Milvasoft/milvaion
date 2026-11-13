package cmd

import (
	"fmt"
	"text/tabwriter"

	"github.com/spf13/cobra"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/spec"
)

func newSchemaCommand(a *app) *cobra.Command {
	return &cobra.Command{
		Use:   "schema",
		Short: "Describe the fields of the " + spec.APIVersion + " job schema",
		Args:  cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			fmt.Fprintf(a.out, "apiVersion: %s\nkind: %s (or %s with items:)\nmetadata.name: the job's display name; apply matches jobs on it.\n\nspec fields:\n\n",
				spec.APIVersion, spec.KindJob, spec.KindJobList)
			tw := tabwriter.NewWriter(a.out, 0, 0, 2, ' ', 0)
			fmt.Fprintln(tw, "FIELD\tIN PLACE\tDESCRIPTION")
			for _, f := range spec.Fields {
				inPlace := "yes"
				if f.Update == "" {
					inPlace = "replace"
				}
				fmt.Fprintf(tw, "%s\t%s\t%s\n", f.Name, inPlace, f.Doc)
			}
			return tw.Flush()
		},
	}
}
