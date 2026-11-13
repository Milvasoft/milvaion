package cmd

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/fakeapi"
)

// run executes the CLI against the fake API and returns stdout, stderr and the error.
func run(t *testing.T, srv *fakeapi.Server, args ...string) (string, string, error) {
	t.Helper()
	t.Setenv("MILVAION_CONFIG", filepath.Join(t.TempDir(), "config.yaml"))
	t.Setenv("MILVAION_URL", srv.URL)
	t.Setenv("MILVAION_API_KEY", fakeapi.APIKey)
	var out, errOut bytes.Buffer
	root := NewRootCommand("test", "none", "now", &out, &errOut)
	root.SetArgs(args)
	err := root.Execute()
	return out.String(), errOut.String(), err
}

func writeFile(t *testing.T, content string) string {
	t.Helper()
	p := filepath.Join(t.TempDir(), "jobs.yaml")
	if err := os.WriteFile(p, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	return p
}

const jobsYAML = `apiVersion: milvaion/v1
kind: JobList
items:
  - metadata: {name: every-minute}
    spec:
      description: test
      tags: [team-a]
      worker: test-worker
      jobName: TestJob
      cron: "0 * * * * *"
      jobData: {greeting: hello}
      autoDisable: {enabled: true, threshold: 3}
  - metadata: {name: once}
    spec:
      worker: test-worker
      jobName: OtherJob
      executeAt: 2030-01-01T00:00:00Z
`

func TestApplyIsIdempotent(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	f := writeFile(t, jobsYAML)

	out, _, err := run(t, srv, "apply", "-f", f)
	if err != nil {
		t.Fatalf("first apply: %v", err)
	}
	if !strings.Contains(out, "Applied 2 change(s)") || len(srv.Jobs) != 2 {
		t.Fatalf("first apply output:\n%s", out)
	}

	out, _, err = run(t, srv, "apply", "-f", f)
	if err != nil || !strings.Contains(out, "No changes") {
		t.Fatalf("second apply should be a no-op, got %v:\n%s", err, out)
	}
	if srv.Requests["PUT /jobs/job"] != 0 || srv.Requests["POST /jobs/job"] != 2 {
		t.Fatalf("unexpected writes: %v", srv.Requests)
	}

	_, _, err = run(t, srv, "diff", "-f", f, "--exit-code")
	if err != nil {
		t.Fatalf("diff --exit-code on a clean state: %v", err)
	}
}

func TestApplyUpdatesChangedFields(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	if _, _, err := run(t, srv, "apply", "-f", writeFile(t, jobsYAML)); err != nil {
		t.Fatal(err)
	}
	changed := strings.Replace(jobsYAML, "greeting: hello", "greeting: hi", 1)
	f := writeFile(t, changed)

	out, _, err := run(t, srv, "diff", "-f", f, "--exit-code")
	var ee exitError
	if err == nil || !asExit(err, &ee) || ee.code != 2 {
		t.Fatalf("diff --exit-code should exit 2, got %v", err)
	}
	if !strings.Contains(out, `jobData: {"greeting":"hello"} -> {"greeting":"hi"}`) {
		t.Fatalf("diff output:\n%s", out)
	}

	if out, _, err = run(t, srv, "apply", "-f", f); err != nil || !strings.Contains(out, "updated  every-minute (jobData)") {
		t.Fatalf("apply: %v\n%s", err, out)
	}
	for _, j := range srv.Jobs {
		if j["displayName"] == "every-minute" && j["jobData"] != `{"greeting":"hi"}` {
			t.Fatalf("jobData not updated: %v", j["jobData"])
		}
	}
}

func asExit(err error, target *exitError) bool {
	e, ok := err.(exitError)
	if ok {
		*target = e
	}
	return ok
}

func TestPruneOnlyDeletesManagedJobs(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	if _, _, err := run(t, srv, "apply", "-f", writeFile(t, jobsYAML)); err != nil {
		t.Fatal(err)
	}
	// A job created in the dashboard: same shape, no managed tag.
	srv.Jobs["manual"] = map[string]any{"id": "manual", "displayName": "made-by-hand", "tags": "ops",
		"cronExpression": "0 0 * * * *", "isActive": true, "concurrentExecutionPolicy": 0}

	onlyFirst := jobsYAML[:strings.Index(jobsYAML, "  - metadata: {name: once}")]
	out, _, err := run(t, srv, "apply", "-f", writeFile(t, onlyFirst), "--prune")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out, "deleted  once") {
		t.Fatalf("output:\n%s", out)
	}
	if _, kept := srv.Jobs["manual"]; !kept || len(srv.Jobs) != 2 {
		t.Fatalf("jobs left: %v", srv.Jobs)
	}
}

func TestReplaceNeedsFlagAndKeepsOldJobOnFailure(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	if _, _, err := run(t, srv, "apply", "-f", writeFile(t, jobsYAML)); err != nil {
		t.Fatal(err)
	}
	moved := writeFile(t, strings.Replace(jobsYAML, "worker: test-worker", "worker: missing-worker", 1))

	if _, _, err := run(t, srv, "apply", "-f", moved); err == nil || !strings.Contains(err.Error(), "--allow-replace") {
		t.Fatalf("expected a refusal, got %v", err)
	}
	if _, _, err := run(t, srv, "apply", "-f", moved, "--allow-replace"); err == nil || !strings.Contains(err.Error(), "missing-worker not found") {
		t.Fatalf("expected the create to fail, got %v", err)
	}
	if len(srv.Jobs) != 2 {
		t.Fatalf("the old job must survive a failed replace, jobs: %d", len(srv.Jobs))
	}
}

func TestExportThenApplyIsNoOp(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	if _, _, err := run(t, srv, "apply", "-f", writeFile(t, jobsYAML)); err != nil {
		t.Fatal(err)
	}
	exported, _, err := run(t, srv, "export")
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(exported, "managed-by") {
		t.Fatalf("export leaks the managed tag:\n%s", exported)
	}
	out, _, err := run(t, srv, "apply", "-f", writeFile(t, exported))
	if err != nil || !strings.Contains(out, "No changes") {
		t.Fatalf("apply of an export should be a no-op: %v\n%s\n--- export:\n%s", err, out, exported)
	}
}

func TestGetAndTrigger(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	if _, _, err := run(t, srv, "apply", "-f", writeFile(t, jobsYAML)); err != nil {
		t.Fatal(err)
	}
	out, _, err := run(t, srv, "get", "workers")
	if err != nil || !strings.Contains(out, "test-worker") || !strings.Contains(out, "TestJob,OtherJob") {
		t.Fatalf("get workers: %v\n%s", err, out)
	}
	out, _, err = run(t, srv, "get", "jobs", "-o", "json")
	if err != nil || !strings.Contains(out, `"displayName": "every-minute"`) {
		t.Fatalf("get jobs: %v\n%s", err, out)
	}
	out, _, err = run(t, srv, "trigger", "every-minute")
	if err != nil || strings.TrimSpace(out) != "00000000-0000-0000-0000-00000000aaaa" {
		t.Fatalf("trigger: %v\n%s", err, out)
	}
	if _, _, err = run(t, srv, "trigger", "no-such-job"); err == nil || !strings.Contains(err.Error(), `no job named "no-such-job"`) {
		t.Fatalf("trigger unknown: %v", err)
	}
}

func TestBadKeyIsReportedClearly(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	t.Setenv("MILVAION_CONFIG", filepath.Join(t.TempDir(), "config.yaml"))
	var out, errOut bytes.Buffer
	root := NewRootCommand("test", "", "", &out, &errOut)
	root.SetArgs([]string{"get", "jobs", "--url", srv.URL, "--api-key", "wrong"})
	err := root.Execute()
	if err == nil || !strings.Contains(err.Error(), "unauthorized (401)") {
		t.Fatalf("error = %v", err)
	}
}

func TestLoginSavesConfig(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	cfg := filepath.Join(t.TempDir(), "config.yaml")
	t.Setenv("MILVAION_CONFIG", cfg)
	t.Setenv("MILVAION_URL", "")
	t.Setenv("MILVAION_API_KEY", "")
	var out, errOut bytes.Buffer
	root := NewRootCommand("test", "", "", &out, &errOut)
	root.SetIn(strings.NewReader(fakeapi.APIKey + "\n"))
	root.SetArgs([]string{"login", "--url", srv.URL + "/", "--api-key-stdin"})
	if err := root.Execute(); err != nil {
		t.Fatalf("login: %v", err)
	}
	b, err := os.ReadFile(cfg)
	if err != nil || !strings.Contains(string(b), "apiKey: "+fakeapi.APIKey) || strings.Contains(string(b), srv.URL+"/") {
		t.Fatalf("config = %q, %v", b, err)
	}

	// The saved login is used when no environment variables are set.
	root = NewRootCommand("test", "", "", &out, &errOut)
	root.SetArgs([]string{"get", "workers"})
	if err := root.Execute(); err != nil {
		t.Fatalf("get after login: %v", err)
	}
}
