package milvaion

// Wire-level names and message shapes shared with the Milvaion API.
// See docs/githubdocs/WORKER-PROTOCOL.md in the repository for the full protocol.

// SDKVersion is the version of this SDK, reported to the dashboard.
const SDKVersion = "1.1.0"

const (
	exchangeJobs         = "jobs.topic"
	exchangeDeadLetter   = "dlx_scheduled_jobs"
	deadLetterRoutingKey = "failed_jobs"

	queueJobs               = "scheduled_jobs_queue"
	queueWorkerLogs         = "worker_logs_queue"
	queueWorkerHeartbeat    = "worker_heartbeat_queue"
	queueWorkerRegistration = "worker_registration_queue"
	queueStatusUpdates      = "job_status_updates_queue"
	queueFailedOccurrences  = "failed_jobs_queue"
)

// Status is the API's JobOccurrenceStatus, sent as a number.
type Status int

const (
	StatusQueued Status = iota
	StatusRunning
	StatusCompleted
	StatusFailed
	StatusCancelled
	StatusTimedOut
	StatusUnknown
	StatusSkipped
)

// LogLevel is the level of an occurrence log entry as the dashboard shows it.
type LogLevel string

const (
	LevelDebug       LogLevel = "Debug"
	LevelInformation LogLevel = "Information"
	LevelWarning     LogLevel = "Warning"
	LevelError       LogLevel = "Error"
)

// ScheduledJob is the body of a job delivery. The API sends more fields than these.
type ScheduledJob struct {
	ID                      string   `json:"id"`
	DisplayName             string   `json:"displayName,omitempty"`
	Description             string   `json:"description,omitempty"`
	Tags                    string   `json:"tags,omitempty"`
	JobNameInWorker         string   `json:"jobNameInWorker"`
	JobData                 *string  `json:"jobData,omitempty"`
	ExecuteAt               *string  `json:"executeAt,omitempty"`
	CronExpression          *string  `json:"cronExpression,omitempty"`
	WorkerID                string   `json:"workerId,omitempty"`
	RoutingPattern          string   `json:"routingPattern,omitempty"`
	ExecutionTimeoutSeconds *float64 `json:"executionTimeoutSeconds,omitempty"`
	ZombieTimeoutMinutes    *int     `json:"zombieTimeoutMinutes,omitempty"`
	Version                 int      `json:"version,omitempty"`
}

type occurrenceLog struct {
	Timestamp     string         `json:"timestamp"`
	Level         LogLevel       `json:"level"`
	Message       string         `json:"message"`
	Data          map[string]any `json:"data"`
	Category      string         `json:"category"`
	ExceptionType *string        `json:"exceptionType"`
}

type workerLogMessage struct {
	OccurrenceID     string        `json:"occurrenceId"`
	WorkerID         string        `json:"workerId"`
	Log              occurrenceLog `json:"log"`
	MessageTimestamp string        `json:"messageTimestamp"`
}

type workerLogBatchMessage struct {
	Logs           []workerLogMessage `json:"logs"`
	BatchTimestamp string             `json:"batchTimestamp"`
	Count          int                `json:"count"`
}

type statusUpdateMessage struct {
	OccurrenceID     string  `json:"occurrenceId"`
	JobID            string  `json:"jobId"`
	WorkerID         string  `json:"workerId"`
	InstanceID       string  `json:"instanceId"`
	Status           Status  `json:"status"`
	StartTime        *string `json:"startTime"`
	EndTime          *string `json:"endTime"`
	DurationMs       *int64  `json:"durationMs"`
	Result           *string `json:"result"`
	Exception        *string `json:"exception"`
	MessageTimestamp string  `json:"messageTimestamp"`
}

type heartbeatMessage struct {
	WorkerID        string  `json:"workerId"`
	InstanceID      string  `json:"instanceId"`
	CurrentJobs     int     `json:"currentJobs"`
	MemoryBytes     int64   `json:"memoryBytes"`
	CPUUsagePercent float64 `json:"cpuUsagePercent"`
	Timestamp       string  `json:"timestamp"`
	IsStopping      bool    `json:"isStopping"`
}

type registrationMessage struct {
	WorkerID             string            `json:"workerId"`
	InstanceID           string            `json:"instanceId"`
	DisplayName          string            `json:"displayName"`
	HostName             string            `json:"hostName"`
	IPAddress            string            `json:"ipAddress"`
	RoutingPatterns      map[string]string `json:"routingPatterns"`
	JobDataDefinitions   map[string]string `json:"jobDataDefinitions"`
	JobResultDefinitions map[string]string `json:"jobResultDefinitions"`
	JobTypes             []string          `json:"jobTypes"`
	MaxParallelJobs      int               `json:"maxParallelJobs"`
	Version              string            `json:"version"`
	// JSON string. The API reads it case-sensitively, so its fields are PascalCase.
	Metadata string `json:"metadata"`
}

type workerMetadata struct {
	IsExternal        bool                `json:"IsExternal"`
	ExternalScheduler *string             `json:"ExternalScheduler"`
	ProcessorCount    int                 `json:"ProcessorCount"`
	OSVersion         string              `json:"OSVersion"`
	RuntimeVersion    string              `json:"RuntimeVersion"`
	HeartbeatInterval int                 `json:"HeartbeatInterval"`
	Sdk               string              `json:"Sdk"`
	SdkVersion        string              `json:"SdkVersion"`
	JobConfigs        []jobConfigMetadata `json:"JobConfigs"`
}

type jobConfigMetadata struct {
	JobType                 string `json:"JobType"`
	ConsumerID              string `json:"ConsumerId"`
	MaxParallelJobs         int    `json:"MaxParallelJobs"`
	ExecutionTimeoutSeconds int    `json:"ExecutionTimeoutSeconds"`
}

type dlqMessage struct {
	ID              string  `json:"id"`
	DisplayName     string  `json:"displayName"`
	JobNameInWorker string  `json:"jobNameInWorker"`
	JobData         *string `json:"jobData"`
	ExecuteAt       *string `json:"executeAt"`
	Status          Status  `json:"status"`
	Exception       string  `json:"exception"`
}

type cancellationRequest struct {
	CorrelationID string `json:"CorrelationId"`
	JobID         string `json:"JobId"`
	OccurrenceID  string `json:"OccurrenceId"`
	Reason        string `json:"Reason"`
}
