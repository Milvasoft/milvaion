package cmd

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/spf13/cobra"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/spec"
)

type planOptions struct {
	files      []string
	prune      bool
	managedTag string
}

func (o *planOptions) bind(c *cobra.Command) {
	c.Flags().StringArrayVarP(&o.files, "filename", "f", nil, "YAML file or directory of .yaml/.yml files to read, or - for stdin (repeatable)")
	c.Flags().BoolVar(&o.prune, "prune", false, "delete managed jobs that are not in the files")
	c.Flags().StringVar(&o.managedTag, "managed-tag", DefaultManagedTag,
		"tag that marks jobs as managed by apply; --prune only deletes jobs carrying it. Use a different tag per repository")
	_ = c.MarkFlagRequired("filename")
}

func newApplyCommand(a *app) *cobra.Command {
	var o planOptions
	var allowReplace bool
	c := &cobra.Command{
		Use:   "apply -f FILE",
		Short: "Create or update jobs so they match the YAML files",
		Long: `Create or update jobs so they match the YAML files.

Jobs are matched by metadata.name, which is the job's display name. A job that
does not exist is created; one that differs is updated in place. Running apply
again with the same files changes nothing.

Every job apply touches gets the managed tag (default managed-by:milvaion-cli).
With --prune, managed jobs that are not in the files are deleted. Jobs created in
the dashboard do not carry the tag and are never pruned.

Some fields (worker, executeAt, external, externalJobId) cannot be changed in
place. Changing them means deleting the job and creating it again, which drops
its run history, so apply refuses unless you pass --allow-replace.`,
		Args: cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			c, err := a.client()
			if err != nil {
				return err
			}
			plan, err := buildPlan(cmd.Context(), c, &o, cmd.InOrStdin())
			if err != nil {
				return err
			}
			if !plan.HasChanges() {
				fmt.Fprintf(a.out, "No changes. %d job(s) up to date.\n", len(plan.Steps))
				return nil
			}
			if n := plan.Count(spec.Replace); n > 0 && !allowReplace {
				plan.Print(a.out, false)
				return fmt.Errorf("%d job(s) need to be replaced, which deletes their run history; re-run with --allow-replace", n)
			}
			return executePlan(cmd.Context(), c, &plan, o.managedTag, a.out)
		},
	}
	o.bind(c)
	c.Flags().BoolVar(&allowReplace, "allow-replace", false, "allow deleting and re-creating jobs whose immutable fields changed")
	return c
}

func newDiffCommand(a *app) *cobra.Command {
	var o planOptions
	var exitCode, verbose bool
	c := &cobra.Command{
		Use:   "diff -f FILE",
		Short: "Show what apply would change",
		Long: `Show what apply would change, without changing anything.

With --exit-code, diff exits 0 when there is nothing to change and 2 when there
is, so a CI job can detect drift.`,
		Args: cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			c, err := a.client()
			if err != nil {
				return err
			}
			plan, err := buildPlan(cmd.Context(), c, &o, cmd.InOrStdin())
			if err != nil {
				return err
			}
			plan.Print(a.out, verbose)
			if exitCode && plan.HasChanges() {
				return exitError{code: 2}
			}
			return nil
		},
	}
	o.bind(c)
	c.Flags().BoolVar(&exitCode, "exit-code", false, "exit 2 when there are changes")
	c.Flags().BoolVarP(&verbose, "verbose", "v", false, "also list unchanged jobs")
	return c
}

// readFiles parses every file named by -f.
func readFiles(paths []string, stdin io.Reader) ([]spec.Job, error) {
	var all []spec.Job
	for _, p := range paths {
		if p == "-" {
			jobs, err := spec.Parse(stdin, "stdin")
			if err != nil {
				return nil, err
			}
			all = append(all, jobs...)
			continue
		}
		info, err := os.Stat(p)
		if err != nil {
			return nil, err
		}
		files := []string{p}
		if info.IsDir() {
			files = nil
			entries, err := os.ReadDir(p)
			if err != nil {
				return nil, err
			}
			for _, e := range entries {
				ext := strings.ToLower(filepath.Ext(e.Name()))
				if !e.IsDir() && (ext == ".yaml" || ext == ".yml") {
					files = append(files, filepath.Join(p, e.Name()))
				}
			}
			sort.Strings(files)
			if len(files) == 0 {
				return nil, fmt.Errorf("%s contains no .yaml or .yml files", p)
			}
		}
		for _, f := range files {
			fh, err := os.Open(f)
			if err != nil {
				return nil, err
			}
			jobs, err := spec.Parse(fh, f)
			fh.Close()
			if err != nil {
				return nil, err
			}
			all = append(all, jobs...)
		}
	}
	// Names must be unique across files too.
	seen := map[string]string{}
	for _, j := range all {
		if prev, dup := seen[j.Name]; dup {
			return nil, fmt.Errorf("job %q is defined twice: %s and %s", j.Name, prev, j.Source)
		}
		seen[j.Name] = j.Source
	}
	return all, nil
}

func buildPlan(ctx context.Context, c *client.Client, o *planOptions, stdin io.Reader) (spec.Plan, error) {
	desired, err := readFiles(o.files, stdin)
	if err != nil {
		return spec.Plan{}, err
	}
	if o.prune && o.managedTag == "" {
		return spec.Plan{}, errors.New("--prune needs a --managed-tag to tell managed jobs apart")
	}
	rows, err := c.ListJobs(ctx, "")
	if err != nil {
		return spec.Plan{}, fmt.Errorf("listing jobs: %w", err)
	}
	byName := map[string][]client.JobSummary{}
	var managed []spec.Current
	for _, r := range rows {
		byName[r.DisplayName] = append(byName[r.DisplayName], r)
		if spec.ManagedTagOf(r.Tags, o.managedTag) {
			managed = append(managed, spec.Current{ID: r.ID, Spec: spec.Job{Name: r.DisplayName}, Managed: true})
		}
	}
	current := map[string]spec.Current{}
	for _, d := range desired {
		matches := byName[d.Name]
		if len(matches) == 0 {
			continue
		}
		if len(matches) > 1 {
			ids := make([]string, len(matches))
			for i, m := range matches {
				ids[i] = m.ID
			}
			return spec.Plan{}, fmt.Errorf("%d jobs on the server are named %q (%s); rename or delete the extras so apply knows which one to manage",
				len(matches), d.Name, strings.Join(ids, ", "))
		}
		job, err := c.GetJob(ctx, matches[0].ID)
		if err != nil {
			return spec.Plan{}, fmt.Errorf("reading job %q: %w", d.Name, err)
		}
		cur, err := spec.FromAPIJob(job, o.managedTag)
		if err != nil {
			return spec.Plan{}, err
		}
		current[d.Name] = spec.Current{ID: job.ID, Spec: cur, Managed: spec.ManagedTagOf(job.Tags, o.managedTag)}
	}
	return spec.ComputePlan(desired, current, managed, o.prune), nil
}

func executePlan(ctx context.Context, c *client.Client, plan *spec.Plan, managedTag string, out io.Writer) error {
	done := 0
	for i := range plan.Steps {
		s := &plan.Steps[i]
		var err error
		switch s.Action {
		case spec.NoChange:
			continue
		case spec.Create:
			var id string
			id, err = c.CreateJob(ctx, spec.CreateBody(s.Desired, managedTag))
			if err == nil {
				fmt.Fprintf(out, "+ created  %s (%s)\n", s.Name, id)
			}
		case spec.Update:
			err = c.UpdateJob(ctx, s.ID, spec.UpdateBody(s, managedTag))
			if err == nil {
				fields := make([]string, 0, len(s.Changes))
				for _, ch := range s.Changes {
					fields = append(fields, ch.Field.Name)
				}
				if s.AddsManagedTag {
					fields = append(fields, "managed tag")
				}
				fmt.Fprintf(out, "~ updated  %s (%s)\n", s.Name, strings.Join(fields, ", "))
			}
		case spec.Replace:
			// Create first: if the new definition is rejected, the old job is still there.
			var id string
			if id, err = c.CreateJob(ctx, spec.CreateBody(s.Desired, managedTag)); err == nil {
				if err = c.DeleteJob(ctx, s.ID); err != nil {
					err = fmt.Errorf("created %s but could not delete the old job %s, delete it by hand: %w", id, s.ID, err)
				} else {
					fmt.Fprintf(out, "-/+ replaced %s (%s -> %s)\n", s.Name, s.ID, id)
				}
			}
		case spec.Delete:
			err = c.DeleteJob(ctx, s.ID)
			if err == nil {
				fmt.Fprintf(out, "- deleted  %s (%s)\n", s.Name, s.ID)
			}
		}
		if err != nil {
			return fmt.Errorf("%s %q failed after %d change(s): %w", s.Action, s.Name, done, err)
		}
		done++
	}
	fmt.Fprintf(out, "Applied %d change(s).\n", done)
	return nil
}
