package cmd

import (
	"bytes"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// TestE2E runs the CLI against a real Milvaion. It is skipped unless
// MILVAION_E2E=1 and these are set:
//
//	MILVAION_E2E_URL        e.g. http://localhost:5000
//	MILVAION_E2E_API_KEY    a key with ScheduledJobManagement.* and WorkerManagement.List
//	MILVAION_E2E_WORKER_ID  an active worker
//	MILVAION_E2E_JOB_NAME   a job type that worker offers
//
// It creates jobs named cli-e2e-* under a tag unique to the run and prunes them at the end.
func TestE2E(t *testing.T) {
	if os.Getenv("MILVAION_E2E") == "" {
		t.Skip("set MILVAION_E2E=1 to run against a real Milvaion")
	}
	url, key := os.Getenv("MILVAION_E2E_URL"), os.Getenv("MILVAION_E2E_API_KEY")
	worker, jobName := os.Getenv("MILVAION_E2E_WORKER_ID"), os.Getenv("MILVAION_E2E_JOB_NAME")
	if url == "" || key == "" || worker == "" || jobName == "" {
		t.Fatal("set MILVAION_E2E_URL, MILVAION_E2E_API_KEY, MILVAION_E2E_WORKER_ID and MILVAION_E2E_JOB_NAME")
	}
	t.Setenv("MILVAION_CONFIG", filepath.Join(t.TempDir(), "config.yaml"))
	t.Setenv("MILVAION_URL", url)
	t.Setenv("MILVAION_API_KEY", key)
	suffix := fmt.Sprint(time.Now().UnixNano())
	tag := "managed-by:cli-e2e-" + suffix

	cli := func(args ...string) (string, error) {
		var out, errOut bytes.Buffer
		root := NewRootCommand("e2e", "", "", &out, &errOut)
		root.SetArgs(append(args, "--managed-tag", tag))
		err := root.Execute()
		return out.String() + errOut.String(), err
	}
	plain := func(args ...string) (string, error) {
		var out, errOut bytes.Buffer
		root := NewRootCommand("e2e", "", "", &out, &errOut)
		root.SetArgs(args)
		err := root.Execute()
		return out.String() + errOut.String(), err
	}
	write := func(content string) string {
		p := filepath.Join(t.TempDir(), "jobs.yaml")
		if err := os.WriteFile(p, []byte(content), 0o600); err != nil {
			t.Fatal(err)
		}
		return p
	}
	doc := func(data string) string {
		return fmt.Sprintf(`apiVersion: milvaion/v1
kind: JobList
items:
  - metadata: {name: cli-e2e-recurring-%[1]s}
    spec:
      tags: [e2e]
      worker: %[2]s
      jobName: %[3]s
      cron: "0 0 4 * * *"
      active: false
      jobData: {step: %[4]s}
      autoDisable: {threshold: 3}
  - metadata: {name: cli-e2e-once-%[1]s}
    spec:
      worker: %[2]s
      jobName: %[3]s
      executeAt: %[5]s
      active: false
`, suffix, worker, jobName, data, time.Now().Add(72*time.Hour).UTC().Truncate(time.Second).Format(time.RFC3339))
	}

	first := write(doc("1"))
	t.Cleanup(func() {
		// Prune everything this run created.
		_, _ = cli("apply", "-f", write("apiVersion: milvaion/v1\nkind: JobList\nitems: []\n"), "--prune")
	})

	if out, err := cli("apply", "-f", first); err != nil || !strings.Contains(out, "Applied 2 change(s)") {
		t.Fatalf("apply: %v\n%s", err, out)
	}
	if out, err := cli("diff", "-f", first, "--exit-code"); err != nil {
		t.Fatalf("diff after apply should be clean: %v\n%s", err, out)
	}
	second := write(doc("2"))
	if out, err := cli("apply", "-f", second); err != nil || !strings.Contains(out, "(jobData)") {
		t.Fatalf("update: %v\n%s", err, out)
	}
	out, err := cli("export", "--managed")
	if err != nil || !strings.Contains(out, "step: 2") || strings.Contains(out, tag) {
		t.Fatalf("export: %v\n%s", err, out)
	}
	if out, err := cli("apply", "-f", write(out)); err != nil || !strings.Contains(out, "No changes") {
		t.Fatalf("re-applying the export should change nothing: %v\n%s", err, out)
	}
	if out, err := plain("get", "occurrences", "--job", "cli-e2e-recurring-"+suffix); err != nil || !strings.Contains(out, "STATUS") {
		t.Fatalf("get occurrences: %v\n%s", err, out)
	}
	onlyFirst := doc("2")[:strings.Index(doc("2"), "  - metadata: {name: cli-e2e-once")]
	if out, err := cli("apply", "-f", write(onlyFirst), "--prune"); err != nil || !strings.Contains(out, "deleted  cli-e2e-once-"+suffix) {
		t.Fatalf("prune: %v\n%s", err, out)
	}
}
