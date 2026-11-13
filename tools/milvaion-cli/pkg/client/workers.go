package client

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"time"
)

// WorkerInstance is one running process of a worker.
type WorkerInstance struct {
	InstanceID  string `json:"instanceId"`
	HostName    string `json:"hostName"`
	IPAddress   string `json:"ipAddress"`
	CurrentJobs int    `json:"currentJobs"`
}

// Worker is a registered worker as returned by the workers endpoints.
type Worker struct {
	WorkerID        string            `json:"workerId"`
	DisplayName     string            `json:"displayName"`
	JobNames        []string          `json:"jobNames"`
	RoutingPatterns map[string]string `json:"routingPatterns"`
	CurrentJobs     int               `json:"currentJobs"`
	Status          string            `json:"status"`
	LastHeartbeat   *time.Time        `json:"lastHeartbeat"`
	RegisteredAt    time.Time         `json:"registeredAt"`
	Version         string            `json:"version"`
	Instances       []WorkerInstance  `json:"instances"`
	Metadata        json.RawMessage   `json:"metadata"`
}

// ListWorkers returns every registered worker.
func (c *Client) ListWorkers(ctx context.Context) ([]Worker, error) {
	env, err := c.do(ctx, http.MethodPatch, "/workers", nil, map[string]any{})
	if err != nil {
		return nil, err
	}
	var workers []Worker
	if err := decodeData(env, &workers); err != nil {
		return nil, err
	}
	return workers, nil
}

// GetWorker returns a worker by id, or ErrNotFound.
func (c *Client) GetWorker(ctx context.Context, id string) (*Worker, error) {
	env, err := c.do(ctx, http.MethodGet, "/workers/worker", url.Values{"WorkerId": {id}}, nil)
	if err != nil {
		return nil, err
	}
	if isNull(env.Data) {
		return nil, fmt.Errorf("worker %s: %w", id, ErrNotFound)
	}
	var w Worker
	if err := json.Unmarshal(env.Data, &w); err != nil {
		return nil, err
	}
	return &w, nil
}
