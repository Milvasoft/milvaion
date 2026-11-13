package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"sort"
	"time"

	"github.com/spf13/cobra"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

func newTriggerCommand(a *app) *cobra.Command {
	var reason, data string
	var force, follow bool
	c := &cobra.Command{
		Use:   "trigger JOB",
		Short: "Run a job now",
		Long: `Run a job now. JOB is the job's display name or id.

Prints the id of the new occurrence. With --follow, streams its logs until it
finishes and exits non-zero if it did not complete successfully.`,
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			c, err := a.client()
			if err != nil {
				return err
			}
			ctx := cmd.Context()
			id, err := resolveJobID(ctx, c, args[0])
			if err != nil {
				return err
			}
			if data != "" && !json.Valid([]byte(data)) {
				return errors.New("--data must be valid JSON")
			}
			if reason == "" {
				reason = "Triggered from milvaion CLI"
			}
			occ, err := c.TriggerJob(ctx, client.TriggerRequest{JobID: id, Reason: reason, JobData: data, Force: force})
			if err != nil {
				return err
			}
			fmt.Fprintln(a.out, occ)
			if follow {
				return followLogs(ctx, c, occ, a.out, 2*time.Second)
			}
			return nil
		},
	}
	c.Flags().StringVar(&reason, "reason", "", "reason recorded with the run")
	c.Flags().StringVar(&data, "data", "", "JSON payload for this run only, instead of the job's own")
	c.Flags().BoolVar(&force, "force", false, "run even if the job is already running and its policy is Skip")
	c.Flags().BoolVar(&follow, "follow", false, "stream the run's logs until it finishes")
	return c
}

func newLogsCommand(a *app) *cobra.Command {
	var follow bool
	var job string
	c := &cobra.Command{
		Use:   "logs [OCCURRENCE]",
		Short: "Show the logs of an occurrence",
		Long: `Show the logs of an occurrence (one run of a job).

Pass the occurrence id, or --job to show the newest run of a job.`,
		Args: cobra.MaximumNArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			c, err := a.client()
			if err != nil {
				return err
			}
			ctx := cmd.Context()
			var id string
			switch {
			case len(args) == 1 && job == "":
				id = args[0]
			case len(args) == 0 && job != "":
				jobID, err := resolveJobID(ctx, c, job)
				if err != nil {
					return err
				}
				occs, err := c.ListOccurrences(ctx, client.OccurrenceFilter{JobID: jobID, Limit: 1})
				if err != nil {
					return err
				}
				if len(occs) == 0 {
					return fmt.Errorf("job %q has not run yet", job)
				}
				id = occs[0].ID
			default:
				return errors.New("pass an occurrence id or --job, not both")
			}
			if follow {
				return followLogs(ctx, c, id, a.out, 2*time.Second)
			}
			occ, err := c.GetOccurrence(ctx, id)
			if err != nil {
				return err
			}
			printHeader(a.out, occ)
			for _, l := range sortedLogs(occ.Logs) {
				printLog(a.out, l)
			}
			printFooter(a.out, occ)
			return nil
		},
	}
	c.Flags().BoolVarP(&follow, "follow", "f", false, "keep printing new lines until the run finishes")
	c.Flags().StringVar(&job, "job", "", "show the newest run of this job (name or id)")
	return c
}

func printHeader(w io.Writer, o *client.Occurrence) {
	line := "Occurrence " + o.ID
	if o.JobDisplayName != "" {
		line += fmt.Sprintf("  job %q", o.JobDisplayName)
	}
	if o.WorkerID != "" {
		line += "  worker " + o.WorkerID
	}
	fmt.Fprintf(w, "%s  status %s\n", line, o.Status())
}

func sortedLogs(logs []client.LogEntry) []client.LogEntry {
	out := append([]client.LogEntry(nil), logs...)
	sort.SliceStable(out, func(i, j int) bool { return out[i].Timestamp.Before(out[j].Timestamp) })
	return out
}

func printLog(w io.Writer, l client.LogEntry) {
	fmt.Fprintf(w, "%s %-5s %s\n", l.Timestamp.Local().Format("15:04:05.000"), l.Level, l.Message)
}

func printFooter(w io.Writer, o *client.Occurrence) {
	if o.Exception != "" {
		fmt.Fprintf(w, "\nException:\n%s\n", o.Exception)
	}
	if o.Result != "" {
		fmt.Fprintf(w, "\nResult: %s\n", o.Result)
	}
	if client.IsFinalStatus(o.Status()) {
		fmt.Fprintf(w, "\nFinished: %s in %s\n", o.Status(), formatDuration(o.DurationMs))
	}
}

// followLogs polls the occurrence and prints new log lines until it reaches a
// final status. It returns an error when the run did not complete.
func followLogs(ctx context.Context, c *client.Client, id string, w io.Writer, interval time.Duration) error {
	// The API does not return logs in order, and new lines can arrive with earlier
	// timestamps, so remember what was printed instead of counting lines.
	printed := map[string]bool{}
	headerShown := false
	for {
		occ, err := c.GetOccurrence(ctx, id)
		if err != nil && !client.IsNotFound(err) {
			return err
		}
		if occ != nil {
			if !headerShown {
				printHeader(w, occ)
				headerShown = true
			}
			for _, l := range sortedLogs(occ.Logs) {
				key := l.Timestamp.String() + "\x00" + l.Level + "\x00" + l.Message
				if !printed[key] {
					printed[key] = true
					printLog(w, l)
				}
			}
			if client.IsFinalStatus(occ.Status()) {
				printFooter(w, occ)
				if occ.Status() != "Completed" {
					return exitError{code: 1}
				}
				return nil
			}
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(interval):
		}
	}
}
