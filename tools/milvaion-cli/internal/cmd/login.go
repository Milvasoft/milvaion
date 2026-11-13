package cmd

import (
	"bufio"
	"errors"
	"fmt"
	"os"
	"strings"

	"github.com/spf13/cobra"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/internal/config"
	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

func newLoginCommand(a *app) *cobra.Command {
	var keyFromStdin, skipCheck bool
	c := &cobra.Command{
		Use:   "login",
		Short: "Save the Milvaion URL and api key",
		Long: `Save the Milvaion URL and api key to the config file.

Create the key in the dashboard under User Management > Api Keys and grant it the
ScheduledJobManagement and WorkerManagement permissions the commands you use need.

To keep the key out of shell history, pipe it in:

  echo "$KEY" | milvaion login --url https://milvaion.example.com --api-key-stdin

MILVAION_URL and MILVAION_API_KEY override the saved values, which is usually the
better choice in CI.`,
		Args: cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			key := a.apiKey
			if keyFromStdin {
				line, err := bufio.NewReader(cmd.InOrStdin()).ReadString('\n')
				if err != nil && line == "" {
					return fmt.Errorf("reading api key from stdin: %w", err)
				}
				key = strings.TrimSpace(line)
			}
			if a.url == "" || key == "" {
				return errors.New("both --url and an api key (--api-key or --api-key-stdin) are required")
			}
			url := strings.TrimRight(a.url, "/")
			if !skipCheck {
				c := a.newClient(url, key)
				if _, err := c.ListWorkers(cmd.Context()); err != nil {
					var apiErr *client.APIError
					if errors.As(err, &apiErr) && apiErr.HTTPStatus == 403 {
						fmt.Fprintln(a.errOut, "Warning: the key works but cannot list workers (WorkerManagement.List).")
					} else {
						return fmt.Errorf("checking the key against %s: %w (use --skip-check to save anyway)", url, err)
					}
				}
			}
			path, err := config.Save(config.Config{URL: url, APIKey: key})
			if err != nil {
				return err
			}
			fmt.Fprintf(a.out, "Logged in to %s. Saved to %s\n", url, path)
			if os.Getenv(config.EnvURL) != "" || os.Getenv(config.EnvAPIKey) != "" {
				fmt.Fprintln(a.errOut, "Note: MILVAION_URL or MILVAION_API_KEY is set and takes precedence over the saved login.")
			}
			return nil
		},
	}
	c.Flags().BoolVar(&keyFromStdin, "api-key-stdin", false, "read the api key from standard input")
	c.Flags().BoolVar(&skipCheck, "skip-check", false, "save without checking the key against the server")
	return c
}
