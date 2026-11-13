package cmd

import (
	"fmt"
	"os"
	"sort"

	"github.com/spf13/cobra"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/spec"
)

func newExportCommand(a *app) *cobra.Command {
	var names []string
	var tag, managedTag, outFile string
	var onlyManaged, includeExternal bool
	c := &cobra.Command{
		Use:   "export",
		Short: "Write the current jobs as milvaion/v1 YAML",
		Long: `Write the current jobs as milvaion/v1 YAML, ready for apply.

Use it once to bring existing jobs under version control:

  milvaion export > jobs.yaml
  milvaion apply -f jobs.yaml   # adopts them: adds the managed tag, nothing else

External jobs (reported by Quartz or Hangfire) are skipped unless you pass
--include-external, because their scheduler, not Milvaion, owns them.`,
		Args: cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			c, err := a.client()
			if err != nil {
				return err
			}
			ctx := cmd.Context()
			var crit []client.Criteria
			if tag != "" {
				crit = append(crit, client.Criteria{FilterBy: "Tags", Value: tag, Type: client.FilterContains})
			}
			rows, err := c.ListJobs(ctx, "", crit...)
			if err != nil {
				return err
			}
			wanted := map[string]bool{}
			for _, n := range names {
				wanted[n] = true
			}
			sort.Slice(rows, func(i, j int) bool { return rows[i].DisplayName < rows[j].DisplayName })
			var jobs []spec.Job
			for _, r := range rows {
				if len(wanted) > 0 && !wanted[r.DisplayName] {
					continue
				}
				if r.IsExternal && !includeExternal {
					continue
				}
				if tag != "" && !spec.ManagedTagOf(r.Tags, tag) {
					continue
				}
				if onlyManaged && !spec.ManagedTagOf(r.Tags, managedTag) {
					continue
				}
				job, err := c.GetJob(ctx, r.ID)
				if err != nil {
					return fmt.Errorf("reading job %q: %w", r.DisplayName, err)
				}
				s, err := spec.FromAPIJob(job, managedTag)
				if err != nil {
					return err
				}
				jobs = append(jobs, s)
			}
			if len(names) > 0 && len(jobs) == 0 {
				return fmt.Errorf("no job matched %v", names)
			}
			w := a.out
			if outFile != "" {
				f, err := os.Create(outFile)
				if err != nil {
					return err
				}
				defer f.Close()
				w = f
			}
			if len(jobs) == 0 {
				fmt.Fprintln(a.errOut, "No jobs to export.")
				return nil
			}
			if err := spec.Encode(w, jobs); err != nil {
				return err
			}
			if outFile != "" {
				fmt.Fprintf(a.errOut, "Exported %d job(s) to %s\n", len(jobs), outFile)
			}
			return nil
		},
	}
	c.Flags().StringArrayVar(&names, "name", nil, "export only the job with this display name (repeatable)")
	c.Flags().StringVar(&tag, "tag", "", "export only jobs with this tag")
	c.Flags().BoolVar(&onlyManaged, "managed", false, "export only jobs that carry the managed tag")
	c.Flags().StringVar(&managedTag, "managed-tag", DefaultManagedTag, "managed tag to strip from the output and to filter on with --managed")
	c.Flags().BoolVar(&includeExternal, "include-external", false, "include jobs reported by external schedulers")
	c.Flags().StringVarP(&outFile, "output-file", "o", "", "write to this file instead of standard output")
	return c
}
