// Package fakeapi is an in-memory stand-in for the Milvaion job and worker
// endpoints, for unit tests of the CLI and the Terraform provider. It mirrors
// the request and response shapes of the real API closely enough to exercise
// the client, not the scheduler's behaviour.
package fakeapi

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"sort"
	"strings"
	"sync"
)

// APIKey is the only key the fake accepts.
const APIKey = "test-key"

// Server is a fake Milvaion API.
type Server struct {
	*httptest.Server

	mu      sync.Mutex
	nextID  int
	Jobs    map[string]map[string]any
	Workers []map[string]any
	// Requests counts calls per "METHOD path".
	Requests map[string]int
}

// New starts a fake API with one active worker, "test-worker", that runs TestJob and OtherJob.
func New() *Server {
	s := &Server{
		Jobs:     map[string]map[string]any{},
		Requests: map[string]int{},
		Workers: []map[string]any{{
			"workerId": "test-worker", "displayName": "Test worker", "status": "Active", "version": "1.2.3",
			"jobNames": []string{"TestJob", "OtherJob"}, "currentJobs": 0,
			"routingPatterns": map[string]string{"TestJob": "test-worker.test.*", "OtherJob": "test-worker.other.*"},
			"instances":       []map[string]any{{"instanceId": "test-worker-1", "hostName": "host", "ipAddress": "10.0.0.1"}},
		}},
	}
	s.Server = httptest.NewServer(http.HandlerFunc(s.handle))
	return s
}

func ok(w http.ResponseWriter, data any) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{"isSuccess": true, "statusCode": 200, "messages": []any{}, "data": data})
}

func fail(w http.ResponseWriter, msg string) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{"isSuccess": false, "statusCode": 200,
		"messages": []any{map[string]any{"key": "", "message": msg, "type": 1}}, "data": nil})
}

func (s *Server) handle(w http.ResponseWriter, r *http.Request) {
	s.mu.Lock()
	defer s.mu.Unlock()
	path := strings.TrimPrefix(r.URL.Path, "/api/v1")
	s.Requests[r.Method+" "+path]++
	if r.Header.Get("X-ApiKey") != APIKey {
		w.WriteHeader(http.StatusUnauthorized)
		return
	}
	var body map[string]any
	if r.Body != nil {
		_ = json.NewDecoder(r.Body).Decode(&body)
	}
	switch r.Method + " " + path {
	case "PATCH /jobs":
		s.listJobs(w, body)
	case "GET /jobs/job":
		ok(w, s.Jobs[r.URL.Query().Get("JobId")])
	case "POST /jobs/job":
		s.createJob(w, body)
	case "PUT /jobs/job":
		s.updateJob(w, body)
	case "DELETE /jobs/job":
		id := r.URL.Query().Get("JobId")
		if _, exists := s.Jobs[id]; !exists {
			fail(w, "Job not found")
			return
		}
		delete(s.Jobs, id)
		ok(w, id)
	case "POST /jobs/job/trigger":
		if _, exists := s.Jobs[fmt.Sprint(body["jobId"])]; !exists {
			fail(w, "Job not found")
			return
		}
		ok(w, "00000000-0000-0000-0000-00000000aaaa")
	case "PATCH /workers":
		ok(w, s.Workers)
	case "GET /workers/worker":
		for _, wk := range s.Workers {
			if wk["workerId"] == r.URL.Query().Get("WorkerId") {
				ok(w, wk)
				return
			}
		}
		ok(w, nil)
	case "PATCH /jobs/occurrences":
		ok(w, []any{})
	default:
		http.NotFound(w, r)
	}
}

func (s *Server) listJobs(w http.ResponseWriter, body map[string]any) {
	var rows []map[string]any
	var criteria []any
	if f, ok := body["filtering"].(map[string]any); ok {
		criteria, _ = f["criterias"].([]any)
	}
	for _, j := range s.Jobs {
		match := true
		for _, c := range criteria {
			cm := c.(map[string]any)
			if cm["filterBy"] == "DisplayName" && j["displayName"] != cm["value"] {
				match = false
			}
		}
		if match {
			rows = append(rows, map[string]any{
				"id": j["id"], "displayName": j["displayName"], "tags": j["tags"], "jobType": j["jobType"],
				"cronExpression": j["cronExpression"], "isActive": j["isActive"], "isExternal": j["externalJobInfo"] != nil,
			})
		}
	}
	sort.Slice(rows, func(a, b int) bool { return fmt.Sprint(rows[a]["id"]) < fmt.Sprint(rows[b]["id"]) })
	ok(w, rows)
}

func (s *Server) worker(id string) map[string]any {
	for _, wk := range s.Workers {
		if wk["workerId"] == id {
			return wk
		}
	}
	return nil
}

func (s *Server) createJob(w http.ResponseWriter, b map[string]any) {
	if wid, _ := b["workerId"].(string); wid != "" && s.worker(wid) == nil {
		fail(w, "Worker "+wid+" not found")
		return
	}
	s.nextID++
	id := fmt.Sprintf("00000000-0000-0000-0000-%012d", s.nextID)
	jobData := b["jobData"]
	if jobData == nil {
		jobData = "{}"
	}
	active := true
	if v, ok := b["isActive"].(bool); ok {
		active = v
	}
	job := map[string]any{
		"id": id, "displayName": b["displayName"], "description": b["description"], "tags": b["tags"],
		"workerId": b["workerId"], "jobType": b["selectedJobName"], "jobData": jobData,
		"executeAt": b["executeAt"], "cronExpression": b["cronExpression"], "isActive": active,
		"concurrentExecutionPolicy": num(b["concurrentExecutionPolicy"]),
		"zombieTimeoutMinutes":      b["zombieTimeoutMinutes"], "executionTimeoutSeconds": b["executionTimeoutSeconds"],
		"version": 1, "autoDisableSettings": autoDisable(b["autoDisableSettings"]),
	}
	if ext, _ := b["isExternal"].(bool); ext {
		job["externalJobInfo"] = map[string]any{"isExternal": true, "externalJobId": b["externalJobId"]}
	}
	s.Jobs[id] = job
	ok(w, id)
}

func num(v any) any {
	if v == nil {
		return 0
	}
	return v
}

func autoDisable(v any) map[string]any {
	out := map[string]any{"enabled": true, "threshold": nil, "failureWindowMinutes": nil, "consecutiveFailureCount": 0}
	if m, ok := v.(map[string]any); ok {
		for k, val := range m {
			out[k] = val
		}
	}
	return out
}

// updateFields maps UpdateScheduledJobCommand properties to detail properties.
var updateFields = map[string]string{
	"displayName": "displayName", "description": "description", "tags": "tags", "jobType": "jobType",
	"jobData": "jobData", "cronExpression": "cronExpression", "isActive": "isActive",
	"concurrentExecutionPolicy": "concurrentExecutionPolicy", "zombieTimeoutMinutes": "zombieTimeoutMinutes",
	"executionTimeoutSeconds": "executionTimeoutSeconds",
}

func (s *Server) updateJob(w http.ResponseWriter, b map[string]any) {
	job, exists := s.Jobs[fmt.Sprint(b["id"])]
	if !exists {
		fail(w, "Job not found")
		return
	}
	for k, v := range b {
		if k == "id" {
			continue
		}
		prop, _ := v.(map[string]any)
		if prop == nil || prop["isUpdated"] != true {
			continue
		}
		if k == "autoDisableSettings" {
			job["autoDisableSettings"] = autoDisable(prop["value"])
			continue
		}
		target, known := updateFields[k]
		if !known {
			fail(w, "unknown update property "+k)
			return
		}
		job[target] = prop["value"]
	}
	ok(w, job["id"])
}
