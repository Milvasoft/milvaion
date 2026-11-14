package milvaion

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	amqp "github.com/rabbitmq/amqp091-go"
)

// Worker runs Milvaion jobs. Register handlers with Handle, then call Run.
type Worker struct {
	opts    resolvedOptions
	jobs    map[string]*resolvedJob
	order   []*resolvedJob
	cancels *cancelRegistry
	logs    *logBuffer
	proc    *processor
	pub     publisher
	amqp    *amqpPublisher // RabbitMQ transport only
	broker  transport
	sampler resourceSampler

	started  atomic.Bool
	stopping atomic.Bool

	consumersMu sync.Mutex
	consumers   []consumer

	heartbeatMu      sync.Mutex
	samplerMu        sync.Mutex
	lastImmediate    time.Time
	pendingHeartbeat bool
	lastConnectErrMu sync.Mutex
	lastConnectErr   error
}

type consumer struct {
	channel *amqp.Channel
	tag     string
}

// NewWorker validates the options and creates a worker.
func NewWorker(options Options) (*Worker, error) {
	opts, err := resolveOptions(options)

	if err != nil {
		return nil, err
	}

	w := &Worker{opts: opts, jobs: map[string]*resolvedJob{}, cancels: newCancelRegistry()}

	if opts.transport == transportKafka {
		k := newKafkaTransport(w)
		w.broker, w.pub = k, k
	} else {
		w.amqp = &amqpPublisher{}
		w.broker, w.pub = &rabbitTransport{w: w}, w.amqp
	}

	w.logs = newLogBuffer(func(ctx context.Context, batch workerLogBatchMessage) error {
		body, err := json.Marshal(batch)

		if err != nil {
			return err
		}

		key := ""

		if len(batch.Logs) > 0 {
			key = batch.Logs[0].OccurrenceID
		}

		return w.pub.publish(ctx, "", queueWorkerLogs, body, publishOptions{persistent: true, key: key})
	}, opts.logger)

	w.proc = newProcessor(opts, w.jobs, w.pub, w.logs, w.cancels)
	w.proc.onCountChanged = w.sendImmediateHeartbeat

	return w, nil
}

// WorkerID of this worker application.
func (w *Worker) WorkerID() string { return w.opts.workerID }

// InstanceID of this process.
func (w *Worker) InstanceID() string { return w.opts.instanceID }

// RunningJobs is the number of jobs this instance is running now.
func (w *Worker) RunningJobs() int { return w.proc.runningJobs() }

// Handle registers a handler under the name the dashboard dispatches the job by (JobNameInWorker).
// Names are unique within a worker and matched case-insensitively. Call it before Run.
func (w *Worker) Handle(name string, handler Handler, options ...JobOptions) error {
	if w.started.Load() {
		return errors.New("milvaion: handlers must be registered before Run")
	}

	name = strings.TrimSpace(name)

	if name == "" {
		return errors.New("milvaion: job name is required")
	}

	key := strings.ToLower(name)

	if _, exists := w.jobs[key]; exists {
		return fmt.Errorf("milvaion: job %q is already registered; job names must be unique within a worker (case-insensitive)", name)
	}

	var o JobOptions

	if len(options) > 0 {
		o = options[0]
	}

	job, err := resolveJob(name, handler, o, w.opts)

	if err != nil {
		return err
	}

	w.jobs[key] = &job
	w.order = append(w.order, &job)

	return nil
}

// Run connects, registers this instance and processes jobs until ctx is cancelled. It then stops
// taking jobs, lets running ones finish for up to ShutdownTimeout, returns the rest to the queue,
// tells the API the instance has left and returns nil. It returns an error if the broker (RabbitMQ or
// Kafka) or Redis cannot be reached within StartupTimeout.
func (w *Worker) Run(ctx context.Context) error {
	if !w.started.CompareAndSwap(false, true) {
		return errors.New("milvaion: Run can only be called once")
	}

	if len(w.order) == 0 {
		return errors.New("milvaion: register at least one job with Handle before Run")
	}

	w.opts.logger.Info("starting worker", "worker", w.opts.workerID, "jobs", len(w.order))

	startCtx, cancelStart := context.WithTimeout(ctx, w.opts.startupTimeout)
	defer cancelStart()

	var listener *cancellationListener

	if w.opts.redisURL != "" {
		var err error

		if listener, err = startCancellationListener(startCtx, w.opts.redisURL, w.opts.cancellationChannel, w.cancels, w.opts.logger); err != nil {
			return err
		}

		defer listener.close()
	} else {
		w.opts.logger.Warn("Redis is disabled: jobs cannot be cancelled from the dashboard")
	}

	if err := w.broker.start(ctx, startCtx); err != nil {
		return err
	}

	defer w.broker.close()

	heartbeatCtx, stopHeartbeats := context.WithCancel(context.Background())
	heartbeatsDone := make(chan struct{})

	go func() {
		defer close(heartbeatsDone)
		ticker := time.NewTicker(w.opts.heartbeatInterval)
		defer ticker.Stop()

		for {
			select {
			case <-ticker.C:
				w.sendHeartbeat(false)
			case <-heartbeatCtx.Done():
				return
			}
		}
	}()

	<-ctx.Done()

	w.shutdown()
	stopHeartbeats()
	<-heartbeatsDone

	w.sendHeartbeat(true)

	flushCtx, cancelFlush := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancelFlush()

	if err := w.logs.flush(flushCtx); err != nil {
		w.opts.logger.Warn("some job logs could not be sent before shutdown", "error", err)
	}

	w.logs.close()
	w.opts.logger.Info("worker stopped")

	return nil
}

func (w *Worker) shutdown() {
	w.stopping.Store(true)
	w.opts.logger.Info("stopping worker")

	w.broker.stopConsuming()
	w.proc.beginShutdown()

	if !w.proc.waitForIdle(w.opts.shutdownTimeout) {
		w.opts.logger.Warn("jobs still running after the shutdown timeout; returning them to the queue", "running", w.proc.runningJobs(), "timeout", w.opts.shutdownTimeout)
		w.cancels.abortAll(ErrWorkerStopping)
		w.proc.waitForIdle(10 * time.Second)
	}

	w.broker.drained()
}

func (w *Worker) cancelConsumers() {
	w.consumersMu.Lock()

	for _, c := range w.consumers {
		_ = c.channel.Cancel(c.tag, false)
	}

	w.consumersMu.Unlock()
}

// connectionLoop keeps a RabbitMQ connection up, rebuilding the topology, consumers and
// registration after every reconnect. It closes ready after the first successful setup.
func (w *Worker) connectionLoop(ctx context.Context, ready chan struct{}) {
	var once sync.Once
	attempt := 0

	for ctx.Err() == nil {
		conn, err := amqp.DialConfig(w.opts.rabbitURL, amqp.Config{
			Heartbeat:  60 * time.Second,
			Properties: amqp.Table{"connection_name": w.opts.instanceID},
		})

		if err == nil {
			err = w.setup(conn)
		}

		if err != nil {
			w.lastConnectErrMu.Lock()
			w.lastConnectErr = err
			w.lastConnectErrMu.Unlock()

			w.opts.logger.Warn("could not connect to RabbitMQ", "error", err)

			if conn != nil {
				_ = conn.Close()
			}

			delay := min(time.Second*time.Duration(1<<min(attempt, 4)), 10*time.Second)
			attempt++

			if sleepContext(ctx, delay) != nil {
				return
			}

			continue
		}

		attempt = 0
		w.opts.logger.Info("connected to RabbitMQ")
		once.Do(func() { close(ready) })

		closed := conn.NotifyClose(make(chan *amqp.Error, 1))

		select {
		case reason := <-closed:
			w.amqp.detach()
			w.clearConsumers()
			w.opts.logger.Warn("lost the RabbitMQ connection; reconnecting", "reason", reason)
		case <-ctx.Done():
			w.amqp.detach()
			_ = conn.Close()
			return
		}
	}
}

func (w *Worker) setup(conn *amqp.Connection) error {
	var queueArgs amqp.Table

	if w.opts.quorum {
		queueArgs = amqp.Table{"x-queue-type": "quorum"}
	}

	pubChannel, err := conn.Channel()

	if err != nil {
		return err
	}

	if err := pubChannel.Confirm(false); err != nil {
		return err
	}

	for _, queue := range []string{queueStatusUpdates, queueWorkerLogs, queueWorkerHeartbeat, queueWorkerRegistration} {
		if _, err := pubChannel.QueueDeclare(queue, true, false, false, false, queueArgs); err != nil {
			return fmt.Errorf("declaring %s: %w", queue, err)
		}
	}

	if err := pubChannel.ExchangeDeclare(exchangeJobs, "topic", true, false, false, false, nil); err != nil {
		return err
	}

	if err := pubChannel.ExchangeDeclare(exchangeDeadLetter, "direct", true, false, false, false, nil); err != nil {
		return err
	}

	if _, err := pubChannel.QueueDeclare(queueFailedOccurrences, true, false, false, false, queueArgs); err != nil {
		return err
	}

	if err := pubChannel.QueueBind(queueFailedOccurrences, deadLetterRoutingKey, exchangeDeadLetter, false, nil); err != nil {
		return err
	}

	w.watchChannel(conn, pubChannel, "publish")

	// Before consuming, so the first delivery can already report its status.
	w.amqp.attach(pubChannel)

	var consumers []consumer

	for _, job := range w.order {
		channel, err := conn.Channel()

		if err != nil {
			return err
		}

		if err := channel.Qos(job.maxParallelJobs, 0, false); err != nil {
			return err
		}

		args := amqp.Table{"x-dead-letter-exchange": exchangeDeadLetter, "x-dead-letter-routing-key": deadLetterRoutingKey}

		for k, v := range queueArgs {
			args[k] = v
		}

		if _, err := channel.QueueDeclare(job.queue, true, false, false, false, args); err != nil {
			return fmt.Errorf("declaring %s: %w", job.queue, err)
		}

		if err := channel.QueueBind(job.queue, job.routingPattern, exchangeJobs, false, nil); err != nil {
			return err
		}

		w.watchChannel(conn, channel, job.queue)

		// A reconnect while stopping restores publishing for running jobs, but takes no new ones.
		if w.stopping.Load() {
			continue
		}

		tag := w.opts.instanceID + "." + job.consumerID
		deliveries, err := channel.Consume(job.queue, tag, false, false, false, false, nil)

		if err != nil {
			return err
		}

		consumers = append(consumers, consumer{channel: channel, tag: tag})

		go w.consume(deliveries, job)

		w.opts.logger.Info("consuming", "job", job.name, "queue", job.queue, "pattern", job.routingPattern, "prefetch", job.maxParallelJobs)
	}

	w.consumersMu.Lock()
	w.consumers = consumers
	w.consumersMu.Unlock()

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	if err := w.register(ctx); err != nil {
		return err
	}

	w.sendHeartbeat(false)
	w.opts.logger.Info("registered", "worker", w.opts.workerID)

	return nil
}

func (w *Worker) consume(deliveries <-chan amqp.Delivery, job *resolvedJob) {
	for message := range deliveries {
		go w.proc.handle(delivery{
			body:          message.Body,
			headers:       message.Headers,
			correlationID: message.CorrelationId,
			redelivered:   message.Redelivered,
			ack:           func() error { return message.Ack(false) },
			nack:          func(requeue bool) error { return message.Nack(false, requeue) },
		}, job)
	}
}

// watchChannel reconnects when a channel closes while its connection stays up (for example after
// a protocol error); a dropped connection is handled by the connection loop.
func (w *Worker) watchChannel(conn *amqp.Connection, channel *amqp.Channel, name string) {
	closed := channel.NotifyClose(make(chan *amqp.Error, 1))

	go func() {
		reason, ok := <-closed

		if !ok || reason == nil {
			return
		}

		time.Sleep(250 * time.Millisecond)

		if conn.IsClosed() || w.stopping.Load() {
			return
		}

		w.opts.logger.Warn("RabbitMQ channel closed while the connection stayed up; reconnecting", "channel", name, "reason", reason)
		_ = conn.Close()
	}()
}

func (w *Worker) clearConsumers() {
	w.consumersMu.Lock()
	w.consumers = nil
	w.consumersMu.Unlock()
}

// register publishes this instance's registration.
func (w *Worker) register(ctx context.Context) error {
	registration, err := json.Marshal(w.buildRegistration())

	if err != nil {
		return err
	}

	if err := w.pub.publish(ctx, "", queueWorkerRegistration, registration, publishOptions{persistent: true, key: w.opts.instanceID}); err != nil {
		return fmt.Errorf("registering: %w", err)
	}

	return nil
}

func (w *Worker) buildRegistration() registrationMessage {
	hostName, _ := os.Hostname()
	routing := map[string]string{}
	dataSchemas := map[string]string{}
	resultSchemas := map[string]string{}
	jobTypes := make([]string, 0, len(w.order))
	configs := make([]jobConfigMetadata, 0, len(w.order))

	for _, job := range w.order {
		routing[job.name] = job.routingPattern
		jobTypes = append(jobTypes, job.name)
		configs = append(configs, jobConfigMetadata{
			JobType:                 job.name,
			ConsumerID:              job.consumerID,
			MaxParallelJobs:         job.maxParallelJobs,
			ExecutionTimeoutSeconds: int(job.executionTimeout.Seconds()),
		})

		if job.dataSchema != "" {
			dataSchemas[job.name] = job.dataSchema
		}

		if job.resultSchema != "" {
			resultSchemas[job.name] = job.resultSchema
		}
	}

	metadata, _ := json.Marshal(workerMetadata{
		ProcessorCount:    runtime.NumCPU(),
		OSVersion:         runtime.GOOS + "/" + runtime.GOARCH,
		RuntimeVersion:    "Go " + strings.TrimPrefix(runtime.Version(), "go"),
		HeartbeatInterval: int(w.opts.heartbeatInterval.Seconds()),
		Sdk:               "go",
		SdkVersion:        SDKVersion,
		JobConfigs:        configs,
	})

	return registrationMessage{
		WorkerID:             w.opts.workerID,
		InstanceID:           w.opts.instanceID,
		DisplayName:          fmt.Sprintf("%s (%s)", w.opts.workerID, w.opts.instanceID),
		HostName:             hostName,
		IPAddress:            localIPAddress(),
		RoutingPatterns:      routing,
		JobDataDefinitions:   dataSchemas,
		JobResultDefinitions: resultSchemas,
		JobTypes:             jobTypes,
		MaxParallelJobs:      w.opts.maxParallelJobs,
		Version:              w.opts.version,
		Metadata:             string(metadata),
	}
}

func (w *Worker) sendHeartbeat(stopping bool) {
	w.samplerMu.Lock()
	memory, cpu := w.sampler.memoryBytes(), w.sampler.cpuUsagePercent()
	w.samplerMu.Unlock()

	current := w.proc.runningJobs()

	if stopping {
		current = 0
	}

	body, _ := json.Marshal(heartbeatMessage{
		WorkerID:        w.opts.workerID,
		InstanceID:      w.opts.instanceID,
		CurrentJobs:     current,
		MemoryBytes:     memory,
		CPUUsagePercent: cpu,
		Timestamp:       now(),
		IsStopping:      stopping,
	})

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	if err := w.pub.publish(ctx, "", queueWorkerHeartbeat, body, publishOptions{key: w.opts.instanceID}); err != nil {
		w.opts.logger.Debug("heartbeat failed; will retry on the next interval", "error", err)
	}
}

// sendImmediateHeartbeat reports a changed job count at most once a second; the last change is always sent.
func (w *Worker) sendImmediateHeartbeat() {
	w.heartbeatMu.Lock()
	defer w.heartbeatMu.Unlock()

	if w.pendingHeartbeat || !w.started.Load() {
		return
	}

	w.pendingHeartbeat = true
	wait := max(0, time.Until(w.lastImmediate.Add(time.Second)))

	time.AfterFunc(wait, func() {
		w.heartbeatMu.Lock()
		w.pendingHeartbeat = false
		w.lastImmediate = time.Now()
		w.heartbeatMu.Unlock()

		w.sendHeartbeat(false)
	})
}

var errNotConnected = errors.New("milvaion: not connected to RabbitMQ")

// amqpPublisher publishes on the current confirm channel and waits for the broker to accept each message.
type amqpPublisher struct {
	mu      sync.RWMutex
	channel *amqp.Channel
}

func (p *amqpPublisher) attach(channel *amqp.Channel) {
	p.mu.Lock()
	p.channel = channel
	p.mu.Unlock()
}

func (p *amqpPublisher) detach() {
	p.mu.Lock()
	p.channel = nil
	p.mu.Unlock()
}

func (p *amqpPublisher) publish(ctx context.Context, exchange, routingKey string, body []byte, o publishOptions) error {
	p.mu.RLock()
	channel := p.channel
	p.mu.RUnlock()

	if channel == nil {
		return errNotConnected
	}

	mode := amqp.Transient

	if o.persistent {
		mode = amqp.Persistent
	}

	confirmation, err := channel.PublishWithDeferredConfirmWithContext(ctx, exchange, routingKey, false, false, amqp.Publishing{
		ContentType:   "application/json",
		DeliveryMode:  mode,
		CorrelationId: o.correlationID,
		Headers:       amqp.Table(o.headers),
		Body:          body,
	})

	if err != nil {
		return err
	}

	if confirmation == nil {
		return nil
	}

	acked, err := confirmation.WaitContext(ctx)

	if err != nil {
		return err
	}

	if !acked {
		return errors.New("milvaion: RabbitMQ rejected the message")
	}

	return nil
}
