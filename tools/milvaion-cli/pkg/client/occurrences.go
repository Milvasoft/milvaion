package client

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"time"
)

// OccurrenceStatuses maps the JobOccurrenceStatus enum values to their names.
var OccurrenceStatuses = []string{"Queued", "Running", "Completed", "Failed", "Cancelled", "TimedOut", "Unknown", "Skipped"}

// IsFinalStatus reports whether an occurrence in this status will not change again.
func IsFinalStatus(status string) bool {
	switch status {
	case "Completed", "Failed", "Cancelled", "TimedOut", "Skipped":
		return true
	}
	return false
}

// OccurrenceSummary is one row of the occurrence list.
type OccurrenceSummary struct {
	ID             string          `json:"id"`
	JobID          string          `json:"jobId"`
	JobDisplayName string          `json:"jobDisplayName"`
	JobName        string          `json:"jobName"`
	WorkerID       string          `json:"workerId"`
	StatusRaw      json.RawMessage `json:"status"`
	StartTime      *time.Time      `json:"startTime"`
	EndTime        *time.Time      `json:"endTime"`
	DurationMs     *int64          `json:"durationMs"`
	CreatedAt      time.Time       `json:"createdAt"`
}

// Status returns the status name.
func (o *OccurrenceSummary) Status() string { return EnumName(OccurrenceStatuses, o.StatusRaw) }

// LogEntry is one line a worker logged for an occurrence.
type LogEntry struct {
	Timestamp time.Time      `json:"timestamp"`
	Level     string         `json:"level"`
	Message   string         `json:"message"`
	Data      map[string]any `json:"data"`
}

// Occurrence is one execution of a job, with its logs.
type Occurrence struct {
	OccurrenceSummary
	CorrelationID string     `json:"correlationId"`
	Result        string     `json:"result"`
	Exception     string     `json:"exception"`
	Logs          []LogEntry `json:"logs"`
	JobVersion    int        `json:"jobVersion"`
}

// OccurrenceFilter narrows ListOccurrences.
type OccurrenceFilter struct {
	JobID  string
	Status string
	Search string
	Limit  int
}

// ListOccurrences returns the newest occurrences, at most Limit (default 20).
func (c *Client) ListOccurrences(ctx context.Context, f OccurrenceFilter) ([]OccurrenceSummary, error) {
	limit := f.Limit
	if limit <= 0 {
		limit = 20
	}
	var crit []Criteria
	if f.JobID != "" {
		crit = append(crit, Criteria{FilterBy: "JobId", Value: f.JobID, Type: FilterEquals})
	}
	if f.Status != "" {
		v, err := EnumValue(OccurrenceStatuses, f.Status)
		if err != nil {
			return nil, fmt.Errorf("status: %w", err)
		}
		crit = append(crit, Criteria{FilterBy: "Status", Value: v, Type: FilterEquals})
	}

	var all []OccurrenceSummary
	cursor := ""
	for len(all) < limit {
		req := listRequest{RowCount: min(limit-len(all), pageSize), Cursor: cursor, SearchTerm: f.Search}
		if len(crit) > 0 {
			req.Filtering = &filtering{Criterias: crit}
		}
		env, err := c.do(ctx, http.MethodPatch, "/jobs/occurrences", nil, req)
		if err != nil {
			return nil, err
		}
		var rows []OccurrenceSummary
		if err := decodeData(env, &rows); err != nil {
			return nil, err
		}
		all = append(all, rows...)
		if len(rows) == 0 || env.HasNextPage == nil || !*env.HasNextPage || env.NextCursor == nil || *env.NextCursor == "" {
			break
		}
		cursor = *env.NextCursor
	}
	return all, nil
}

// GetOccurrence returns one occurrence with its logs, or ErrNotFound.
func (c *Client) GetOccurrence(ctx context.Context, id string) (*Occurrence, error) {
	env, err := c.do(ctx, http.MethodGet, "/jobs/occurrences/occurrence", url.Values{"OccurrenceId": {id}}, nil)
	if err != nil {
		return nil, err
	}
	if isNull(env.Data) {
		return nil, fmt.Errorf("occurrence %s: %w", id, ErrNotFound)
	}
	var occ Occurrence
	if err := json.Unmarshal(env.Data, &occ); err != nil {
		return nil, err
	}
	return &occ, nil
}
