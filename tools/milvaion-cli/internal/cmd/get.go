package cmd

import (
	"encoding/json"
	"fmt"
	"io"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/spf13/cobra"
	"gopkg.in/yaml.v3"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

func newGetCommand(a *app) *cobra.Command {
	get := &cobra.Command{
		Use:   "get",
		Short: "List jobs, workers or occurrences",
	}
	get.AddCommand(newGetJobsCommand(a), newGetWorkersCommand(a), newGetOccurrencesCommand(a))
	return get
}

type outputFlag struct{ format string }

func (o *outputFlag) bind(c *cobra.Command) {
	c.Flags().StringVarP(&o.format, "output", "o", "table", "output format: table, json or yaml")
}

// print writes v as JSON or YAML, or calls table for the table format.
func (o *outputFlag) print(w io.Writer, v any, table func(tw *tabwriter.Writer)) error {
	switch o.format {
	case "json":
		enc := json.NewEncoder(w)
		enc.SetIndent("", "  ")
		return enc.Encode(v)
	case "yaml":
		// Round-trip through JSON so the YAML uses the API's field names.
		b, err := json.Marshal(v)
		if err != nil {
			return err
		}
		var generic any
		if err := json.Unmarshal(b, &generic); err != nil {
			return err
		}
		return yaml.NewEncoder(w).Encode(generic)
	case "table", "":
		tw := tabwriter.NewWriter(w, 0, 0, 2, ' ', 0)
		table(tw)
		return tw.Flush()
	}
	return fmt.Errorf("unknown output format %q (use table, json or yaml)", o.format)
}

func newGetJobsCommand(a *app) *cobra.Command {
	var out outputFlag
	var search, tag string
	c := &cobra.Command{
		Use:     "jobs",
		Aliases: []string{"job"},
		Short:   "List jobs",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			c, err := a.client()
			if err != nil {
				return err
			}
			var crit []client.Criteria
			if tag != "" {
				crit = append(crit, client.Criteria{FilterBy: "Tags", Value: tag, Type: client.FilterContains})
			}
			jobs, err := c.ListJobs(cmd.Context(), search, crit...)
			if err != nil {
				return err
			}
			return out.print(a.out, jobs, func(tw *tabwriter.Writer) {
				fmt.Fprintln(tw, "ID\tNAME\tJOB TYPE\tSCHEDULE\tACTIVE\tTAGS")
				for _, j := range jobs {
					schedule := j.CronExpression
					if j.IsExternal {
						schedule = "(external)"
					} else if schedule == "" {
						schedule = "(one-time)"
					}
					fmt.Fprintf(tw, "%s\t%s\t%s\t%s\t%t\t%s\n", j.ID, j.DisplayName, dash(j.JobType), schedule, j.IsActive, dash(j.Tags))
				}
			})
		},
	}
	out.bind(c)
	c.Flags().StringVar(&search, "search", "", "only jobs whose name or description contains this text")
	c.Flags().StringVar(&tag, "tag", "", "only jobs with this tag")
	return c
}

func newGetWorkersCommand(a *app) *cobra.Command {
	var out outputFlag
	c := &cobra.Command{
		Use:     "workers",
		Aliases: []string{"worker"},
		Short:   "List registered workers",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			c, err := a.client()
			if err != nil {
				return err
			}
			workers, err := c.ListWorkers(cmd.Context())
			if err != nil {
				return err
			}
			return out.print(a.out, workers, func(tw *tabwriter.Writer) {
				fmt.Fprintln(tw, "WORKER ID\tNAME\tSTATUS\tINSTANCES\tRUNNING\tVERSION\tJOB TYPES")
				for _, w := range workers {
					fmt.Fprintf(tw, "%s\t%s\t%s\t%d\t%d\t%s\t%s\n", w.WorkerID, dash(w.DisplayName), dash(w.Status),
						len(w.Instances), w.CurrentJobs, dash(w.Version), dash(strings.Join(w.JobNames, ",")))
				}
			})
		},
	}
	out.bind(c)
	return c
}

func newGetOccurrencesCommand(a *app) *cobra.Command {
	var out outputFlag
	var job, status string
	var limit int
	c := &cobra.Command{
		Use:     "occurrences",
		Aliases: []string{"occurrence", "runs"},
		Short:   "List the newest occurrences (runs)",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			c, err := a.client()
			if err != nil {
				return err
			}
			f := client.OccurrenceFilter{Status: status, Limit: limit}
			if job != "" {
				if f.JobID, err = resolveJobID(cmd.Context(), c, job); err != nil {
					return err
				}
			}
			occs, err := c.ListOccurrences(cmd.Context(), f)
			if err != nil {
				return err
			}
			return out.print(a.out, occs, func(tw *tabwriter.Writer) {
				fmt.Fprintln(tw, "ID\tJOB\tSTATUS\tWORKER\tSTARTED\tDURATION")
				for _, o := range occs {
					fmt.Fprintf(tw, "%s\t%s\t%s\t%s\t%s\t%s\n", o.ID, o.JobDisplayName, o.Status(), dash(o.WorkerID),
						formatTime(o.StartTime), formatDuration(o.DurationMs))
				}
			})
		},
	}
	out.bind(c)
	c.Flags().StringVar(&job, "job", "", "only occurrences of this job (name or id)")
	c.Flags().StringVar(&status, "status", "", "only occurrences in this status: "+strings.Join(client.OccurrenceStatuses, ", "))
	c.Flags().IntVar(&limit, "limit", 20, "maximum number of occurrences")
	return c
}

func dash(s string) string {
	if s == "" {
		return "-"
	}
	return s
}

func formatTime(t *time.Time) string {
	if t == nil {
		return "-"
	}
	return t.Local().Format("2006-01-02 15:04:05")
}

func formatDuration(ms *int64) string {
	if ms == nil {
		return "-"
	}
	return (time.Duration(*ms) * time.Millisecond).String()
}
