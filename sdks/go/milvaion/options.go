package milvaion

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"log/slog"
	"net/url"
	"os"
	"runtime"
	"strconv"
	"strings"
	"time"
)

// Options configures a Worker. Only WorkerID is required.
type Options struct {
	// WorkerID identifies the worker application. Use the same value on every replica.
	WorkerID string

	// RabbitMQURL, e.g. "amqp://guest:guest@localhost:5672/". Default: guest on localhost.
	RabbitMQURL string
	// QueueType must match the API's MilvaionConfig:RabbitMQ:QueueType: "classic" (default) or "quorum".
	QueueType string

	// RedisURL, e.g. "redis://:password@localhost:6379/0". Redis only carries cancellation requests.
	// Default: redis://localhost:6379. Set DisableRedis to run without it.
	RedisURL     string
	DisableRedis bool
	// RedisKeyPrefix must match the API's MilvaionConfig:Redis:KeyPrefix. Default: "Milvaion:JobScheduler:".
	RedisKeyPrefix string

	// MaxParallelJobs is the capacity reported for this instance. Default: runtime.NumCPU() * 2.
	MaxParallelJobs int
	// ExecutionTimeout is the default job timeout. Negative disables it. Default: 1 hour.
	ExecutionTimeout time.Duration
	// HeartbeatInterval of the instance. Default: 30s.
	HeartbeatInterval time.Duration
	// JobHeartbeatInterval keeps long jobs from being flagged as zombies. Negative disables it. Default: 60s.
	JobHeartbeatInterval time.Duration
	// ShutdownTimeout is how long Run lets running jobs finish after its context is cancelled. Default: 30s.
	ShutdownTimeout time.Duration
	// StartupTimeout is how long Run waits for the first RabbitMQ and Redis connection. Default: 30s.
	StartupTimeout time.Duration

	// Version is shown in the dashboard. Default: "1.0.0".
	Version string
	// InstanceID overrides the generated "{WorkerID}-{8 hex}" id. It must be unique per process.
	InstanceID string
	// Logger for the SDK's own messages. Default: slog.Default().
	Logger *slog.Logger
}

// JobOptions configures one job type. The zero value uses the defaults.
type JobOptions struct {
	// RoutingPattern. Default: "{workerId}.{name without Job}.*".
	RoutingPattern string
	// ConsumerID shown in the dashboard. Default: "{name without Job}-consumer".
	ConsumerID string
	// MaxParallelJobs of this type on this instance (prefetch). Default: min(10, worker MaxParallelJobs).
	MaxParallelJobs int
	// ExecutionTimeout. A timeout set on the job in the dashboard wins. Negative disables it. Default: the worker default.
	ExecutionTimeout time.Duration
	// MaxRetries after the first failure before the job goes to the dead letter queue. Default: 3. Use NoRetries for 0.
	MaxRetries int
	// BaseRetryDelay: retry n waits BaseRetryDelay * 2^(n-1). Default: 5s.
	BaseRetryDelay time.Duration
	// DataSchema is the JSON Schema of the job data; the dashboard builds the job data form from it.
	// Any value that marshals to a JSON Schema object, or a JSON string/[]byte.
	DataSchema any
	// ResultSchema is the JSON Schema of the job result.
	ResultSchema any
}

// NoRetries as JobOptions.MaxRetries disables retries.
const NoRetries = -1

type resolvedOptions struct {
	workerID             string
	instanceID           string
	rabbitURL            string
	quorum               bool
	redisURL             string
	cancellationChannel  string
	maxParallelJobs      int
	executionTimeout     time.Duration
	heartbeatInterval    time.Duration
	jobHeartbeatInterval time.Duration
	shutdownTimeout      time.Duration
	startupTimeout       time.Duration
	version              string
	logger               *slog.Logger
}

type resolvedJob struct {
	name             string
	handler          Handler
	routingPattern   string
	queue            string
	consumerID       string
	maxParallelJobs  int
	executionTimeout time.Duration
	maxRetries       int
	baseRetryDelay   time.Duration
	dataSchema       string
	resultSchema     string
}

func resolveOptions(o Options) (resolvedOptions, error) {
	workerID := strings.TrimSpace(o.WorkerID)

	if workerID == "" {
		return resolvedOptions{}, errors.New("milvaion: WorkerID is required")
	}

	queueType := strings.ToLower(strings.TrimSpace(o.QueueType))

	if queueType != "" && queueType != "classic" && queueType != "quorum" {
		return resolvedOptions{}, fmt.Errorf("milvaion: QueueType must be \"classic\" or \"quorum\", got %q", o.QueueType)
	}

	maxParallel := o.MaxParallelJobs

	if maxParallel == 0 {
		maxParallel = runtime.NumCPU() * 2
	}

	if maxParallel < 0 {
		return resolvedOptions{}, fmt.Errorf("milvaion: MaxParallelJobs must be positive, got %d", maxParallel)
	}

	rabbitURL := o.RabbitMQURL

	if rabbitURL == "" {
		rabbitURL = "amqp://guest:guest@localhost:5672/"
	}

	if _, err := url.Parse(rabbitURL); err != nil {
		return resolvedOptions{}, fmt.Errorf("milvaion: invalid RabbitMQURL: %w", err)
	}

	redisURL := ""

	if !o.DisableRedis {
		redisURL = o.RedisURL

		if redisURL == "" {
			redisURL = "redis://localhost:6379"
		}
	}

	keyPrefix := o.RedisKeyPrefix

	if keyPrefix == "" {
		keyPrefix = "Milvaion:JobScheduler:"
	}

	instanceID := o.InstanceID

	if instanceID == "" {
		instanceID = generateInstanceID(workerID)
	}

	logger := o.Logger

	if logger == nil {
		logger = slog.Default()
	}

	return resolvedOptions{
		workerID:             workerID,
		instanceID:           instanceID,
		rabbitURL:            rabbitURL,
		quorum:               queueType == "quorum",
		redisURL:             redisURL,
		cancellationChannel:  keyPrefix + "cancellation_channel",
		maxParallelJobs:      maxParallel,
		executionTimeout:     orDefault(o.ExecutionTimeout, time.Hour),
		heartbeatInterval:    positiveOr(o.HeartbeatInterval, 30*time.Second),
		jobHeartbeatInterval: orDefault(o.JobHeartbeatInterval, 60*time.Second),
		shutdownTimeout:      orDefault(o.ShutdownTimeout, 30*time.Second),
		startupTimeout:       positiveOr(o.StartupTimeout, 30*time.Second),
		version:              stringOr(o.Version, "1.0.0"),
		logger:               logger.With("component", "milvaion", "instance", instanceID),
	}, nil
}

func resolveJob(name string, handler Handler, o JobOptions, w resolvedOptions) (resolvedJob, error) {
	if handler == nil {
		return resolvedJob{}, fmt.Errorf("milvaion: handler of job %q is nil", name)
	}

	maxParallel := o.MaxParallelJobs

	if maxParallel == 0 {
		maxParallel = min(10, w.maxParallelJobs)
	}

	if maxParallel < 1 || maxParallel > 65535 {
		return resolvedJob{}, fmt.Errorf("milvaion: MaxParallelJobs of job %q must be between 1 and 65535, got %d", name, maxParallel)
	}

	maxRetries := o.MaxRetries

	switch {
	case maxRetries == 0:
		maxRetries = 3
	case maxRetries < 0:
		maxRetries = 0
	}

	dataSchema, err := schemaString(o.DataSchema)

	if err != nil {
		return resolvedJob{}, fmt.Errorf("milvaion: DataSchema of job %q: %w", name, err)
	}

	resultSchema, err := schemaString(o.ResultSchema)

	if err != nil {
		return resolvedJob{}, fmt.Errorf("milvaion: ResultSchema of job %q: %w", name, err)
	}

	pattern := strings.TrimSpace(o.RoutingPattern)

	if pattern == "" {
		pattern = GenerateRoutingPattern(name, w.workerID)
	}

	return resolvedJob{
		name:             name,
		handler:          handler,
		routingPattern:   pattern,
		queue:            QueueNameForPattern(pattern),
		consumerID:       stringOr(o.ConsumerID, defaultConsumerID(name)),
		maxParallelJobs:  maxParallel,
		executionTimeout: orDefault(o.ExecutionTimeout, w.executionTimeout),
		maxRetries:       maxRetries,
		baseRetryDelay:   orDefault(o.BaseRetryDelay, 5*time.Second),
		dataSchema:       dataSchema,
		resultSchema:     resultSchema,
	}, nil
}

// OptionsFromEnv reads options from MILVAION_WORKER_ID, MILVAION_RABBITMQ_URL,
// MILVAION_RABBITMQ_QUEUE_TYPE, MILVAION_REDIS_URL ("false" disables Redis), MILVAION_REDIS_KEY_PREFIX,
// MILVAION_MAX_PARALLEL_JOBS, MILVAION_EXECUTION_TIMEOUT_SECONDS and MILVAION_HEARTBEAT_INTERVAL_SECONDS.
// Non-zero fields of base win over the environment.
func OptionsFromEnv(base Options) (Options, error) {
	env := func(key string) string { return strings.TrimSpace(os.Getenv(key)) }
	o := base

	if o.WorkerID == "" {
		o.WorkerID = env("MILVAION_WORKER_ID")
	}

	if o.RabbitMQURL == "" {
		o.RabbitMQURL = env("MILVAION_RABBITMQ_URL")
	}

	if o.QueueType == "" {
		o.QueueType = env("MILVAION_RABBITMQ_QUEUE_TYPE")
	}

	if redis := env("MILVAION_REDIS_URL"); strings.EqualFold(redis, "false") {
		o.DisableRedis = true
	} else if o.RedisURL == "" {
		o.RedisURL = redis
	}

	if o.RedisKeyPrefix == "" {
		o.RedisKeyPrefix = env("MILVAION_REDIS_KEY_PREFIX")
	}

	ints := []struct {
		key string
		set func(int)
		has bool
	}{
		{"MILVAION_MAX_PARALLEL_JOBS", func(v int) { o.MaxParallelJobs = v }, o.MaxParallelJobs != 0},
		{"MILVAION_EXECUTION_TIMEOUT_SECONDS", func(v int) { o.ExecutionTimeout = secondsOrDisabled(v) }, o.ExecutionTimeout != 0},
		{"MILVAION_HEARTBEAT_INTERVAL_SECONDS", func(v int) { o.HeartbeatInterval = time.Duration(v) * time.Second }, o.HeartbeatInterval != 0},
	}

	for _, entry := range ints {
		value := env(entry.key)

		if value == "" || entry.has {
			continue
		}

		parsed, err := strconv.Atoi(value)

		if err != nil {
			return o, fmt.Errorf("milvaion: %s must be an integer, got %q", entry.key, value)
		}

		entry.set(parsed)
	}

	return o, nil
}

// GenerateRoutingPattern is the routing pattern a job gets when it does not set one, identical to
// the .NET SDK: "SendEmailJob" in worker "Mail-Worker" becomes "mail-worker.sendemail.*".
func GenerateRoutingPattern(jobName, workerID string) string {
	return strings.ToLower(workerID) + "." + strings.ToLower(trimJobSuffix(jobName)) + ".*"
}

// QueueNameForPattern is the queue a job type is consumed from.
func QueueNameForPattern(pattern string) string {
	return queueJobs + "." + strings.NewReplacer("*", "wildcard", "#", "all").Replace(pattern)
}

func defaultConsumerID(jobName string) string {
	return strings.ToLower(trimJobSuffix(jobName)) + "-consumer"
}

func trimJobSuffix(name string) string {
	if len(name) >= 3 && strings.EqualFold(name[len(name)-3:], "job") {
		return name[:len(name)-3]
	}

	return name
}

func generateInstanceID(workerID string) string {
	buf := make([]byte, 4)
	_, _ = rand.Read(buf)

	return workerID + "-" + hex.EncodeToString(buf)
}

// orDefault: zero means default, negative means disabled (0).
func orDefault(value, fallback time.Duration) time.Duration {
	switch {
	case value == 0:
		return fallback
	case value < 0:
		return 0
	default:
		return value
	}
}

func positiveOr(value, fallback time.Duration) time.Duration {
	if value > 0 {
		return value
	}

	return fallback
}

func stringOr(value, fallback string) string {
	if strings.TrimSpace(value) == "" {
		return fallback
	}

	return value
}

func secondsOrDisabled(seconds int) time.Duration {
	if seconds <= 0 {
		return -1
	}

	return time.Duration(seconds) * time.Second
}
