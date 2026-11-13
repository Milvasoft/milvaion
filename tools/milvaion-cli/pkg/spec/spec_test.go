package spec

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

const sample = `
apiVersion: milvaion/v1
kind: Job
metadata:
  name: nightly-report
spec:
  description: Builds the report
  tags: [reports, nightly, reports]
  worker: reporter
  jobName: ReportJob
  cron: "0 0 2 * * *"
  concurrencyPolicy: queue
  jobData:
    format: pdf
    pages: 3
  executionTimeoutSeconds: 600
---
apiVersion: milvaion/v1
kind: JobList
items:
  - metadata: {name: one-off}
    spec:
      worker: reporter
      jobName: ReportJob
      executeAt: 2030-01-31T09:00:00+03:00
      active: false
`

func mustParse(t *testing.T, src string) []Job {
	t.Helper()
	jobs, err := Parse(strings.NewReader(src), "test.yaml")
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	return jobs
}

func TestParseNormalisesValues(t *testing.T) {
	jobs := mustParse(t, sample)
	if len(jobs) != 2 {
		t.Fatalf("got %d jobs, want 2", len(jobs))
	}
	j := jobs[0]
	if got := j.Values["tags"].([]string); strings.Join(got, ",") != "nightly,reports" {
		t.Errorf("tags = %v, want sorted and unique", got)
	}
	if got := j.Values["concurrencyPolicy"]; got != "Queue" {
		t.Errorf("concurrencyPolicy = %v, want canonical Queue", got)
	}
	if got := j.Values["jobData"]; got != `{"format":"pdf","pages":3}` {
		t.Errorf("jobData = %v", got)
	}
	if got := j.Values["executionTimeoutSeconds"]; got != int64(600) {
		t.Errorf("executionTimeoutSeconds = %#v", got)
	}
	if got := jobs[1].Values["executeAt"]; got != "2030-01-31T06:00:00Z" {
		t.Errorf("executeAt = %v, want converted to UTC", got)
	}
	if got := jobs[1].Values["active"]; got != false {
		t.Errorf("active = %v", got)
	}
}

func TestParseErrors(t *testing.T) {
	cases := map[string]struct{ src, want string }{
		"wrong version":  {"apiVersion: milvaion/v2\nkind: Job\nmetadata: {name: a}\nspec: {cron: '0 * * * * *'}", "unsupported apiVersion"},
		"missing kind":   {"apiVersion: milvaion/v1\nmetadata: {name: a}", "kind is missing"},
		"missing name":   {"apiVersion: milvaion/v1\nkind: Job\nspec: {cron: '0 * * * * *'}", "metadata.name is required"},
		"typo":           {"apiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nspec: {cron: '0 * * * * *', wroker: x}", "did you mean worker"},
		"five field":     {"apiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nspec: {cron: '* * * * *'}", "has 5 fields"},
		"no schedule":    {"apiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nspec: {}", "set spec.cron"},
		"bad enum":       {"apiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nspec: {cron: '0 * * * * *', concurrencyPolicy: Never}", "expected one of Skip, Queue"},
		"bad json":       {"apiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nspec: {cron: '0 * * * * *', jobData: '{oops'}", "not valid JSON"},
		"duplicate":      {"apiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nspec: {cron: '0 * * * * *'}\n---\napiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nspec: {cron: '0 * * * * *'}", "defined twice"},
		"external no id": {"apiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nspec: {external: true}", "externalJobId is required"},
		"top-level typo": {"apiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nsepc: {}", "unknown top-level key"},
	}
	for name, tc := range cases {
		t.Run(name, func(t *testing.T) {
			_, err := Parse(strings.NewReader(tc.src), "x.yaml")
			if err == nil || !strings.Contains(err.Error(), tc.want) {
				t.Fatalf("error = %v, want it to contain %q", err, tc.want)
			}
		})
	}
}

func TestJobDataAcceptsJSONString(t *testing.T) {
	jobs := mustParse(t, "apiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nspec:\n  cron: '0 * * * * *'\n  jobData: '{\"b\": 1, \"a\": 2}'\n")
	if got := jobs[0].Values["jobData"]; got != `{"a":2,"b":1}` {
		t.Fatalf("jobData = %v", got)
	}
}

// apiJob builds a client.Job the way GetJob does, from a detail response.
func apiJob(t *testing.T, detail map[string]any) *client.Job {
	t.Helper()
	b, _ := json.Marshal(detail)
	var j client.Job
	if err := json.Unmarshal(b, &j); err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(b, &j.Raw); err != nil {
		t.Fatal(err)
	}
	return &j
}

func serverJob(t *testing.T) *client.Job {
	return apiJob(t, map[string]any{
		"id": "id-1", "displayName": "nightly-report", "description": "Builds the report",
		"tags": "reports,managed-by:cli,nightly", "workerId": "reporter", "jobType": "ReportJob",
		"jobData": `{"pages": 3, "format": "pdf"}`, "cronExpression": "0 0 2 * * *",
		"executeAt": "2026-11-15T02:00:00Z", "isActive": true, "concurrentExecutionPolicy": 1,
		"zombieTimeoutMinutes": nil, "executionTimeoutSeconds": 600,
		"autoDisableSettings": map[string]any{"enabled": nil, "threshold": nil, "failureWindowMinutes": nil, "consecutiveFailureCount": 4},
	})
}

func TestUnchangedJobHasNoChanges(t *testing.T) {
	desired := mustParse(t, sample)[0]
	current, err := FromAPIJob(serverJob(t), "managed-by:cli")
	if err != nil {
		t.Fatal(err)
	}
	if changes := Compare(&desired, &current); len(changes) != 0 {
		for _, c := range changes {
			t.Errorf("unexpected change %s: %s -> %s", c.Field.Name, Format(c.Field, c.Old), Format(c.Field, c.New))
		}
	}
}

func TestPlanActions(t *testing.T) {
	jobs := mustParse(t, sample)
	cur, _ := FromAPIJob(serverJob(t), "managed-by:cli")

	t.Run("create when missing", func(t *testing.T) {
		p := ComputePlan(jobs, map[string]Current{}, nil, false)
		if p.Count(Create) != 2 {
			t.Fatalf("plan = %+v", p.Steps)
		}
	})

	t.Run("update in place", func(t *testing.T) {
		changed := cur
		changed.Values = copyValues(cur.Values)
		changed.Values["description"] = "old text"
		p := ComputePlan(jobs[:1], map[string]Current{"nightly-report": {ID: "id-1", Spec: changed, Managed: true}}, nil, false)
		s := p.Steps[0]
		if s.Action != Update || len(s.Changes) != 1 || s.Changes[0].Field.Name != "description" {
			t.Fatalf("step = %+v", s)
		}
		body := UpdateBody(&s, "managed-by:cli")
		if body["description"] != "Builds the report" || len(body) != 1 {
			t.Fatalf("update body = %v", body)
		}
	})

	t.Run("worker change replaces", func(t *testing.T) {
		changed := cur
		changed.Values = copyValues(cur.Values)
		changed.Values["worker"] = "elsewhere"
		p := ComputePlan(jobs[:1], map[string]Current{"nightly-report": {ID: "id-1", Spec: changed, Managed: true}}, nil, false)
		if p.Steps[0].Action != Replace {
			t.Fatalf("action = %v", p.Steps[0].Action)
		}
	})

	t.Run("adopting an unmanaged job adds the tag", func(t *testing.T) {
		p := ComputePlan(jobs[:1], map[string]Current{"nightly-report": {ID: "id-1", Spec: cur, Managed: false}}, nil, false)
		s := p.Steps[0]
		if s.Action != Update || !s.AddsManagedTag {
			t.Fatalf("step = %+v", s)
		}
		if got := UpdateBody(&s, "managed-by:cli")["tags"]; got != "managed-by:cli,nightly,reports" {
			t.Fatalf("tags = %v", got)
		}
	})

	t.Run("prune deletes only managed jobs missing from the file", func(t *testing.T) {
		managed := []Current{{ID: "id-1", Spec: Job{Name: "nightly-report"}}, {ID: "id-9", Spec: Job{Name: "gone"}}}
		p := ComputePlan(jobs[:1], map[string]Current{"nightly-report": {ID: "id-1", Spec: cur, Managed: true}}, managed, true)
		if p.Count(Delete) != 1 || p.Steps[len(p.Steps)-1].ID != "id-9" {
			t.Fatalf("plan = %+v", p.Steps)
		}
		p = ComputePlan(jobs[:1], map[string]Current{"nightly-report": {ID: "id-1", Spec: cur, Managed: true}}, managed, false)
		if p.Count(Delete) != 0 {
			t.Fatal("deleted without --prune")
		}
	})

	t.Run("omitted autoDisable is left alone", func(t *testing.T) {
		changed := cur
		changed.Values = copyValues(cur.Values)
		changed.Values["autoDisable"] = map[string]any{"enabled": false, "threshold": int64(3), "failureWindowMinutes": nil}
		if c := Compare(&jobs[0], &changed); len(c) != 0 {
			t.Fatalf("changes = %+v", c)
		}
	})

	t.Run("executeAt is ignored for cron jobs", func(t *testing.T) {
		if _, ok := cur.Values["executeAt"]; !ok {
			t.Fatal("server job should carry executeAt")
		}
		if c := Compare(&jobs[0], &cur); len(c) != 0 {
			t.Fatalf("changes = %+v", c)
		}
	})
}

func TestCreateBody(t *testing.T) {
	j := mustParse(t, sample)[0]
	b := CreateBody(&j, "managed-by:cli")
	want := map[string]any{
		"displayName": "nightly-report", "description": "Builds the report", "tags": "managed-by:cli,nightly,reports",
		"workerId": "reporter", "selectedJobName": "ReportJob", "cronExpression": "0 0 2 * * *", "isActive": true,
		"concurrentExecutionPolicy": 1, "jobData": `{"format":"pdf","pages":3}`, "executionTimeoutSeconds": int64(600),
		"isExternal": false,
	}
	for k, v := range want {
		if b[k] != v {
			t.Errorf("%s = %#v, want %#v", k, b[k], v)
		}
	}
	if _, ok := b["autoDisableSettings"]; ok {
		t.Error("autoDisableSettings should not be sent when omitted")
	}
}

func TestExportRoundTrip(t *testing.T) {
	cur, err := FromAPIJob(serverJob(t), "managed-by:cli")
	if err != nil {
		t.Fatal(err)
	}
	var buf bytes.Buffer
	if err := Encode(&buf, []Job{cur}); err != nil {
		t.Fatal(err)
	}
	out := buf.String()
	for _, unwanted := range []string{"managed-by", "executeAt", "autoDisable", "active:"} {
		if strings.Contains(out, unwanted) {
			t.Errorf("export contains %q:\n%s", unwanted, out)
		}
	}
	back := mustParse(t, out)
	if c := Compare(&back[0], &cur); len(c) != 0 {
		t.Fatalf("re-imported export differs: %+v\n%s", c, out)
	}
}

func TestEnumAcceptsStringsFromAPI(t *testing.T) {
	j := serverJob(t)
	j.Raw["concurrentExecutionPolicy"] = json.RawMessage(`"Queue"`)
	cur, err := FromAPIJob(j, "")
	if err != nil {
		t.Fatal(err)
	}
	if cur.Values["concurrencyPolicy"] != "Queue" {
		t.Fatalf("got %v", cur.Values["concurrencyPolicy"])
	}
}

// TestNewFieldIsOneEntry guards the promise in Field's doc comment: a field the
// API gains (for example a time zone) needs only a table entry.
func TestNewFieldIsOneEntry(t *testing.T) {
	saved := Fields
	defer func() { Fields = saved }()
	Fields = append(append([]Field(nil), saved...),
		Field{Name: "timeZone", Kind: KString, Create: "timeZone", Update: "timeZone", Detail: "timeZone"},
		Field{Name: "priority", Kind: KInt, Create: "priority", Update: "priority", Detail: "priority"},
	)
	j := mustParse(t, "apiVersion: milvaion/v1\nkind: Job\nmetadata: {name: a}\nspec: {cron: '0 * * * * *', timeZone: Europe/Istanbul, priority: 5}\n")[0]
	b := CreateBody(&j, "")
	if b["timeZone"] != "Europe/Istanbul" || b["priority"] != int64(5) {
		t.Fatalf("create body = %v", b)
	}
	server := apiJob(t, map[string]any{"id": "1", "displayName": "a", "cronExpression": "0 * * * * *", "isActive": true,
		"concurrentExecutionPolicy": 0, "timeZone": "UTC", "priority": 5})
	cur, _ := FromAPIJob(server, "")
	changes := Compare(&j, &cur)
	if len(changes) != 1 || changes[0].Field.Name != "timeZone" {
		t.Fatalf("changes = %+v", changes)
	}
}

func copyValues(m map[string]any) map[string]any {
	out := map[string]any{}
	for k, v := range m {
		out[k] = v
	}
	return out
}
