package client

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// ConcurrencyPolicies maps the API's ConcurrentExecutionPolicy enum values to their names.
var ConcurrencyPolicies = []string{"Skip", "Queue"}

// EnumName returns the name for an enum value that the API may send either as a
// number or as a string.
func EnumName(names []string, raw json.RawMessage) string {
	if isNull(raw) {
		return ""
	}
	var n int
	if err := json.Unmarshal(raw, &n); err == nil {
		if n >= 0 && n < len(names) {
			return names[n]
		}
		return strconv.Itoa(n)
	}
	var s string
	if err := json.Unmarshal(raw, &s); err == nil {
		for _, name := range names {
			if strings.EqualFold(name, s) {
				return name
			}
		}
		return s
	}
	return string(raw)
}

// EnumValue returns the numeric value the API expects for an enum name.
func EnumValue(names []string, name string) (int, error) {
	for i, n := range names {
		if strings.EqualFold(n, name) {
			return i, nil
		}
	}
	return 0, fmt.Errorf("invalid value %q, expected one of %s", name, strings.Join(names, ", "))
}

// AutoDisableSettings controls when a job switches itself off after repeated failures.
type AutoDisableSettings struct {
	Enabled              *bool `json:"enabled"`
	Threshold            *int  `json:"threshold"`
	FailureWindowMinutes *int  `json:"failureWindowMinutes"`
}

// ExternalJobInfo is set on jobs reported by an external scheduler (Quartz, Hangfire).
type ExternalJobInfo struct {
	ExternalJobID string `json:"externalJobId"`
	IsExternal    bool   `json:"isExternal"`
}

// Job is a scheduled job as returned by GET /jobs/job.
type Job struct {
	ID                      string               `json:"id"`
	DisplayName             string               `json:"displayName"`
	Description             string               `json:"description"`
	Tags                    string               `json:"tags"`
	WorkerID                string               `json:"workerId"`
	JobType                 string               `json:"jobType"`
	JobData                 string               `json:"jobData"`
	ExecuteAt               *time.Time           `json:"executeAt"`
	CronExpression          string               `json:"cronExpression"`
	IsActive                bool                 `json:"isActive"`
	ConcurrentPolicyRaw     json.RawMessage      `json:"concurrentExecutionPolicy"`
	ZombieTimeoutMinutes    *int                 `json:"zombieTimeoutMinutes"`
	ExecutionTimeoutSeconds *int                 `json:"executionTimeoutSeconds"`
	Version                 int                  `json:"version"`
	AutoDisableSettings     *AutoDisableSettings `json:"autoDisableSettings"`
	ExternalJobInfo         *ExternalJobInfo     `json:"externalJobInfo"`

	// Raw holds every field of the response, including ones this struct does not
	// know about yet, so newer API fields can be read without a client change.
	Raw map[string]json.RawMessage `json:"-"`
}

// ConcurrencyPolicy returns the policy name, e.g. "Skip".
func (j *Job) ConcurrencyPolicy() string {
	name := EnumName(ConcurrencyPolicies, j.ConcurrentPolicyRaw)
	if name == "" {
		return "Skip"
	}
	return name
}

// TagList splits the comma separated tags.
func (j *Job) TagList() []string { return SplitTags(j.Tags) }

// SplitTags splits a comma separated tag string, dropping blanks.
func SplitTags(s string) []string {
	var out []string
	for _, t := range strings.Split(s, ",") {
		if t = strings.TrimSpace(t); t != "" {
			out = append(out, t)
		}
	}
	return out
}

// JobSummary is one row of the job list.
type JobSummary struct {
	ID                  string          `json:"id"`
	DisplayName         string          `json:"displayName"`
	Description         string          `json:"description"`
	JobType             string          `json:"jobType"`
	CronExpression      string          `json:"cronExpression"`
	IsActive            bool            `json:"isActive"`
	Tags                string          `json:"tags"`
	IsExternal          bool            `json:"isExternal"`
	ConcurrentPolicyRaw json.RawMessage `json:"concurrentExecutionPolicy"`
}

// Filter criteria types understood by the list endpoints.
const (
	FilterContains = 1
	FilterEquals   = 5
)

// Criteria is one list filter.
type Criteria struct {
	FilterBy string `json:"filterBy"`
	Value    any    `json:"value"`
	Type     int    `json:"type"`
}

type listRequest struct {
	PageNumber int        `json:"pageNumber,omitempty"`
	RowCount   int        `json:"rowCount"`
	Cursor     string     `json:"cursor,omitempty"`
	SearchTerm string     `json:"searchTerm,omitempty"`
	Filtering  *filtering `json:"filtering,omitempty"`
	Sorting    *sorting   `json:"sorting,omitempty"`
}

type filtering struct {
	Criterias []Criteria `json:"criterias"`
}

type sorting struct {
	SortBy string `json:"sortBy"`
	Type   int    `json:"type"`
}

const pageSize = 200

// ListJobs returns every job matching the search term and criteria, following pagination.
func (c *Client) ListJobs(ctx context.Context, search string, criteria ...Criteria) ([]JobSummary, error) {
	var all []JobSummary
	for page := 1; ; page++ {
		req := listRequest{PageNumber: page, RowCount: pageSize, SearchTerm: search, Sorting: &sorting{SortBy: "Id", Type: 0}}
		if len(criteria) > 0 {
			req.Filtering = &filtering{Criterias: criteria}
		}
		env, err := c.do(ctx, http.MethodPatch, "/jobs", nil, req)
		if err != nil {
			return nil, err
		}
		var rows []JobSummary
		if err := decodeData(env, &rows); err != nil {
			return nil, err
		}
		all = append(all, rows...)
		if len(rows) < pageSize || (env.TotalDataCount != nil && len(all) >= *env.TotalDataCount) {
			return all, nil
		}
	}
}

// FindJobsByName returns the jobs whose display name equals name exactly.
func (c *Client) FindJobsByName(ctx context.Context, name string) ([]JobSummary, error) {
	rows, err := c.ListJobs(ctx, "", Criteria{FilterBy: "DisplayName", Value: name, Type: FilterEquals})
	if err != nil {
		return nil, err
	}
	// Filter again client side: equality filtering may be case-insensitive on some databases.
	var out []JobSummary
	for _, r := range rows {
		if r.DisplayName == name {
			out = append(out, r)
		}
	}
	return out, nil
}

// GetJob returns a job by id, or ErrNotFound.
func (c *Client) GetJob(ctx context.Context, id string) (*Job, error) {
	env, err := c.do(ctx, http.MethodGet, "/jobs/job", url.Values{"JobId": {id}}, nil)
	if err != nil {
		return nil, err
	}
	if isNull(env.Data) {
		return nil, fmt.Errorf("job %s: %w", id, ErrNotFound)
	}
	var job Job
	if err := json.Unmarshal(env.Data, &job); err != nil {
		return nil, err
	}
	if err := json.Unmarshal(env.Data, &job.Raw); err != nil {
		return nil, err
	}
	return &job, nil
}

// CreateJob creates a job from a CreateScheduledJobCommand body and returns its id.
func (c *Client) CreateJob(ctx context.Context, body map[string]any) (string, error) {
	env, err := c.do(ctx, http.MethodPost, "/jobs/job", nil, body)
	if err != nil {
		return "", err
	}
	var id string
	if err := decodeData(env, &id); err != nil {
		return "", err
	}
	return id, nil
}

// Updated wraps a value in the {value, isUpdated} shape the update endpoint expects.
func Updated(v any) map[string]any { return map[string]any{"value": v, "isUpdated": true} }

// UpdateJob applies a partial update. Each entry of changes is a field name of
// UpdateScheduledJobCommand and its new value; only those fields are changed.
func (c *Client) UpdateJob(ctx context.Context, id string, changes map[string]any) error {
	body := map[string]any{"id": id}
	for k, v := range changes {
		body[k] = Updated(v)
	}
	_, err := c.do(ctx, http.MethodPut, "/jobs/job", nil, body)
	return err
}

// DeleteJob deletes a job.
func (c *Client) DeleteJob(ctx context.Context, id string) error {
	_, err := c.do(ctx, http.MethodDelete, "/jobs/job", url.Values{"JobId": {id}}, nil)
	return err
}

// TriggerRequest describes a manual run.
type TriggerRequest struct {
	JobID   string `json:"jobId"`
	Reason  string `json:"reason,omitempty"`
	JobData string `json:"jobData,omitempty"`
	Force   bool   `json:"force"`
}

// TriggerJob starts a job now and returns the id of the new occurrence.
func (c *Client) TriggerJob(ctx context.Context, req TriggerRequest) (string, error) {
	env, err := c.do(ctx, http.MethodPost, "/jobs/job/trigger", nil, req)
	if err != nil {
		return "", err
	}
	var id string
	if err := decodeData(env, &id); err != nil {
		return "", err
	}
	return id, nil
}
