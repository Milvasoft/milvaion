package com.milvasoft.milvaion.worker;

import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

import java.net.InetAddress;
import java.time.Duration;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/**
 * A Milvaion worker. Register jobs with {@link #job}, then call {@link #start()}, and
 * {@link #close()} on shutdown, or {@link #run()} to do both and block until the JVM stops.
 */
public final class MilvaionWorker implements AutoCloseable {
    private static final Logger LOG = LoggerFactory.getLogger(MilvaionWorker.class);

    private final WorkerOptions options;
    private final Map<String, ResolvedJob> jobs = new LinkedHashMap<>();
    private final CancellationRegistry cancellations = new CancellationRegistry();
    private final ResourceSampler sampler = new ResourceSampler();
    private final WorkerTransport transport;
    private final LogBuffer logs;
    private final JobProcessor processor;
    private final ScheduledExecutorService heartbeats = Executors.newSingleThreadScheduledExecutor(r -> LogBuffer.daemon(r, "milvaion-heartbeat"));
    private final CountDownLatch stopped = new CountDownLatch(1);
    private volatile String state = "idle";
    private CancellationListener listener;
    private long lastImmediateHeartbeat;
    private boolean heartbeatPending;

    public MilvaionWorker(WorkerOptions options) {
        this.options = options.resolve();
        this.transport = this.options.transport() == TransportType.KAFKA ? new KafkaTransport(this.options) : new RabbitMqTransport(this.options);
        this.logs = new LogBuffer(batch -> transport.publishLogs(batch.path("logs").path(0).path("occurrenceId").asText(""), Json.bytes(batch)));
        this.processor = new JobProcessor(this.options, jobs, transport.jobs(), logs, cancellations);
        this.processor.onCountChanged = this::sendImmediateHeartbeat;
    }

    public String workerId() { return options.workerId(); }

    public String instanceId() { return options.instanceId(); }

    /** Number of jobs this instance is running now. */
    public int runningJobs() { return processor.running(); }

    /** Registers a job with default options. */
    public MilvaionWorker job(String name, JobHandler handler) {
        return job(name, handler, new JobOptions());
    }

    /**
     * Registers a handler under the name the dashboard dispatches the job by (JobNameInWorker).
     * Names are unique within a worker and matched case-insensitively.
     */
    public synchronized MilvaionWorker job(String name, JobHandler handler, JobOptions jobOptions) {
        if (!state.equals("idle")) throw new IllegalStateException("Jobs must be added before start().");
        if (name == null || name.isBlank()) throw new IllegalArgumentException("Job name is required.");
        if (handler == null) throw new IllegalArgumentException("Handler of job '" + name + "' is null.");

        String key = name.trim().toLowerCase(Locale.ROOT);

        if (jobs.containsKey(key))
            throw new IllegalArgumentException("Job '" + name + "' is already registered. Job names must be unique within a worker (case-insensitive).");

        jobs.put(key, jobOptions.resolve(name.trim(), handler, options));

        return this;
    }

    /** Starts the worker, stops it when the JVM shuts down (SIGTERM, Ctrl+C) and blocks until then. */
    public void run() throws Exception {
        start();
        Runtime.getRuntime().addShutdownHook(new Thread(this::close, "milvaion-shutdown"));
        stopped.await();
    }

    /** Connects, registers this instance and starts consuming. Throws if the broker or Redis cannot be reached in time. */
    public synchronized void start() throws Exception {
        if (!state.equals("idle")) throw new IllegalStateException("Worker cannot be started from state '" + state + "'.");
        if (jobs.isEmpty()) throw new IllegalStateException("Add at least one job before start().");

        state = "starting";
        LOG.info("Starting worker {} (instance {}) with {} job type(s).", options.workerId(), options.instanceId(), jobs.size());

        try {
            if (options.redisUri() != null) {
                listener = new CancellationListener(options.redisUri(), options.cancellationChannel(), cancellations);
                listener.start(options.startupTimeout());
            } else {
                LOG.warn("Redis is disabled: jobs cannot be cancelled from the dashboard.");
            }

            transport.start(jobs, processor, new WorkerTransport.Hooks() {
                @Override
                public void register() throws Exception {
                    MilvaionWorker.this.register();
                }

                @Override
                public void reregister() {
                    heartbeats.execute(MilvaionWorker.this::registerQuietly);
                }
            });
        } catch (Exception e) {
            state = "stopped";
            closeConnections();
            throw e;
        }

        long interval = options.heartbeatInterval().toMillis();
        heartbeats.scheduleAtFixedRate(() -> sendHeartbeat(false), interval, interval, TimeUnit.MILLISECONDS);
        state = "running";
    }

    private void registerQuietly() {
        try {
            register();
        } catch (Exception e) {
            LOG.warn("Re-registering after a reconnect failed: {}", e.getMessage());
        }
    }

    private void register() throws Exception {
        transport.publishRegistration(Json.bytes(buildRegistration()));
        sendHeartbeat(false);
        LOG.info("Registered {} (instance {}).", options.workerId(), options.instanceId());
    }

    ObjectNode buildRegistration() {
        ObjectNode metadata = Json.object();
        metadata.put("IsExternal", false);
        metadata.putNull("ExternalScheduler");
        metadata.put("ProcessorCount", Runtime.getRuntime().availableProcessors());
        metadata.put("OSVersion", System.getProperty("os.name") + " " + System.getProperty("os.version"));
        metadata.put("RuntimeVersion", "Java " + System.getProperty("java.version"));
        metadata.put("HeartbeatInterval", options.heartbeatInterval().toSeconds());
        metadata.put("Sdk", "java");
        metadata.put("SdkVersion", Protocol.SDK_VERSION);
        ArrayNode configs = metadata.putArray("JobConfigs");

        ObjectNode registration = Json.object();
        registration.put("workerId", options.workerId());
        registration.put("instanceId", options.instanceId());
        registration.put("displayName", options.workerId() + " (" + options.instanceId() + ")");
        registration.put("hostName", hostName());
        registration.put("ipAddress", ResourceSampler.localIpAddress());
        ObjectNode routing = registration.putObject("routingPatterns");
        ObjectNode dataSchemas = registration.putObject("jobDataDefinitions");
        ObjectNode resultSchemas = registration.putObject("jobResultDefinitions");
        ArrayNode jobTypes = registration.putArray("jobTypes");

        for (ResolvedJob job : jobs.values()) {
            routing.put(job.name(), job.routingPattern());
            jobTypes.add(job.name());

            if (job.dataSchema() != null) dataSchemas.put(job.name(), job.dataSchema());
            if (job.resultSchema() != null) resultSchemas.put(job.name(), job.resultSchema());

            ObjectNode config = configs.addObject();
            config.put("JobType", job.name());
            config.put("ConsumerId", job.consumerId());
            config.put("MaxParallelJobs", job.maxParallelJobs());
            config.put("ExecutionTimeoutSeconds", job.executionTimeout().toSeconds());
        }

        registration.put("maxParallelJobs", options.maxParallelJobs());
        registration.put("version", options.version());
        // The API reads this case-sensitively, so its properties are PascalCase.
        registration.put("metadata", Json.string(metadata));

        return registration;
    }

    private static String hostName() {
        try {
            return InetAddress.getLocalHost().getHostName();
        } catch (Exception e) {
            return "Unknown";
        }
    }

    private void sendHeartbeat(boolean stopping) {
        ObjectNode heartbeat = Json.object();
        heartbeat.put("workerId", options.workerId());
        heartbeat.put("instanceId", options.instanceId());
        heartbeat.put("currentJobs", stopping ? 0 : processor.running());
        heartbeat.put("memoryBytes", sampler.memoryBytes());
        heartbeat.put("cpuUsagePercent", sampler.cpuUsagePercent());
        heartbeat.put("timestamp", Json.now());
        heartbeat.put("isStopping", stopping);

        try {
            transport.publishHeartbeat(Json.bytes(heartbeat));
        } catch (Exception e) {
            LOG.debug("Heartbeat failed; will retry on the next interval: {}", e.getMessage());
        }
    }

    /** Reports a changed job count at most once a second; the last change is always sent. */
    private synchronized void sendImmediateHeartbeat() {
        if (!state.equals("running") && !state.equals("stopping")) return;
        if (heartbeatPending) return;

        heartbeatPending = true;
        long wait = Math.max(0, lastImmediateHeartbeat + 1000 - System.currentTimeMillis());

        try {
            heartbeats.schedule(() -> {
                synchronized (MilvaionWorker.this) {
                    heartbeatPending = false;
                    lastImmediateHeartbeat = System.currentTimeMillis();
                }

                sendHeartbeat(false);
            }, wait, TimeUnit.MILLISECONDS);
        } catch (Exception e) {
            heartbeatPending = false; // shutting down
        }
    }

    /**
     * Stops taking new jobs, lets running ones finish for up to {@code shutdownTimeout}, returns the
     * rest to the queue, tells the API the instance has left and disconnects. Safe to call more than once.
     */
    @Override
    public void close() {
        synchronized (this) {
            if (!state.equals("running")) return;

            state = "stopping";
        }

        LOG.info("Stopping worker instance {}...", options.instanceId());

        transport.stopConsuming();
        processor.beginShutdown();

        try {
            if (!processor.waitForIdle(options.shutdownTimeout())) {
                LOG.warn("{} job(s) still running after {}s. Returning them to the queue.", processor.running(), options.shutdownTimeout().toSeconds());
                cancellations.abortAll(JobAbortedException.Reason.SHUTDOWN, "Worker is shutting down.");
                processor.waitForIdle(Duration.ofSeconds(10));
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }

        heartbeats.shutdownNow();
        sendHeartbeat(true);

        try {
            logs.flush();
        } catch (Exception e) {
            LOG.warn("Some job logs could not be sent before shutdown: {}", e.getMessage());
        }

        logs.close();
        closeConnections();
        processor.close();

        state = "stopped";
        stopped.countDown();
        LOG.info("Worker instance {} stopped.", options.instanceId());
    }

    private void closeConnections() {
        if (listener != null) listener.close();

        transport.close();
    }
}
