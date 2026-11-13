// Package cmd implements the milvaion commands.
package cmd

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/signal"
	"regexp"
	"strings"

	"github.com/spf13/cobra"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/internal/config"
	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

// DefaultManagedTag marks jobs created by apply, so --prune only deletes those.
const DefaultManagedTag = "managed-by:milvaion-cli"

// exitError carries a specific exit code.
type exitError struct{ code int }

func (e exitError) Error() string { return fmt.Sprintf("exit %d", e.code) }

type app struct {
	url, apiKey string
	out, errOut io.Writer
	// newClient is replaced in tests.
	newClient func(url, key string) *client.Client
}

func (a *app) client() (*client.Client, error) {
	cfg, err := config.Resolve(a.url, a.apiKey)
	if err != nil {
		return nil, err
	}
	if cfg.URL == "" {
		return nil, errors.New("no Milvaion URL: run `milvaion login`, set MILVAION_URL, or pass --url")
	}
	if cfg.APIKey == "" {
		return nil, errors.New("no api key: run `milvaion login`, set MILVAION_API_KEY, or pass --api-key")
	}
	return a.newClient(cfg.URL, cfg.APIKey), nil
}

// NewRootCommand builds the command tree. It is exported for tests.
func NewRootCommand(version, commit, date string, out, errOut io.Writer) *cobra.Command {
	a := &app{out: out, errOut: errOut, newClient: func(u, k string) *client.Client {
		c := client.New(u, k)
		c.UserAgent = "milvaion-cli/" + version
		return c
	}}
	root := &cobra.Command{
		Use:   "milvaion",
		Short: "Manage Milvaion jobs from the command line and from YAML files",
		Long: `milvaion manages jobs on a Milvaion server.

Define jobs in YAML (apiVersion: milvaion/v1) and keep them in version control:

  milvaion login --url https://milvaion.example.com --api-key mv_...
  milvaion export > jobs.yaml
  milvaion diff -f jobs.yaml
  milvaion apply -f jobs.yaml --prune

The URL and api key can also come from MILVAION_URL and MILVAION_API_KEY.`,
		SilenceUsage:  true,
		SilenceErrors: true,
		Version:       fmt.Sprintf("%s (commit %s, built %s)", version, commit, date),
	}
	root.SetOut(out)
	root.SetErr(errOut)
	root.PersistentFlags().StringVar(&a.url, "url", "", "Milvaion URL (overrides MILVAION_URL and the saved login)")
	root.PersistentFlags().StringVar(&a.apiKey, "api-key", "", "api key (overrides MILVAION_API_KEY and the saved login)")

	root.AddCommand(
		newLoginCommand(a),
		newApplyCommand(a),
		newDiffCommand(a),
		newExportCommand(a),
		newGetCommand(a),
		newTriggerCommand(a),
		newLogsCommand(a),
		newSchemaCommand(a),
	)
	return root
}

// Execute runs the CLI and returns the process exit code.
func Execute(version, commit, date string) int {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	root := NewRootCommand(version, commit, date, os.Stdout, os.Stderr)
	if err := root.ExecuteContext(ctx); err != nil {
		var ee exitError
		if errors.As(err, &ee) {
			return ee.code
		}
		fmt.Fprintln(os.Stderr, "Error:", err)
		return 1
	}
	return 0
}

var guidPattern = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)

// resolveJobID accepts a job id or an exact display name.
func resolveJobID(ctx context.Context, c *client.Client, ref string) (string, error) {
	if guidPattern.MatchString(ref) {
		return ref, nil
	}
	rows, err := c.FindJobsByName(ctx, ref)
	if err != nil {
		return "", err
	}
	switch len(rows) {
	case 0:
		return "", fmt.Errorf("no job named %q", ref)
	case 1:
		return rows[0].ID, nil
	}
	ids := make([]string, len(rows))
	for i, r := range rows {
		ids[i] = r.ID
	}
	return "", fmt.Errorf("%d jobs are named %q; pass the id instead: %s", len(rows), ref, strings.Join(ids, ", "))
}
