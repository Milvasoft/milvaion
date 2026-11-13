package com.milvasoft.milvaion.worker;

import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.rabbitmq.client.AMQP;
import com.rabbitmq.client.Channel;
import com.rabbitmq.client.Connection;
import com.rabbitmq.client.ConnectionFactory;
import com.rabbitmq.client.Recoverable;
import com.rabbitmq.client.RecoveryListener;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

import java.net.InetAddress;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
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
    private final AmqpPublisher publisher = new AmqpPublisher();
    private final LogBuffer logs;
    private final JobProcessor processor;
    private final ScheduledExecutorService heartbeats = Executors.newSingleThreadScheduledExecutor(r -> LogBuffer.daemon(r, "milvaion-heartbeat"));
    private final List<Consumer> consumers = new ArrayList<>();
    private final CountDownLatch stopped = new CountDownLatch(1);
    private volatile String state = "idle";
    private Connection connection;
    private CancellationListener listener;
    private long lastImmediateHeartbeat;
    private boolean heartbeatPending;

    private record Consumer(Channel channel, String tag, ExecutorService executor) {
    }

    public MilvaionWorker(WorkerOptions options) {
        this.options = options.resolve();
        this.logs = new LogBuffer(batch -> publisher.publish("", Protocol.QUEUE_WORKER_LOGS, Json.bytes(batch), true));
        this.processor = new JobProcessor(this.options, jobs, publisher, logs, cancellations);
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

    /** Connects, registers this instance and starts consuming. Throws if RabbitMQ or Redis cannot be reached in time. */
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

            connect();
        } catch (Exception e) {
            state = "stopped";
            closeConnections();
            throw e;
        }

        long interval = options.heartbeatInterval().toMillis();
        heartbeats.scheduleAtFixedRate(() -> sendHeartbeat(false), interval, interval, TimeUnit.MILLISECONDS);
        state = "running";
    }

    private void connect() throws Exception {
        ConnectionFactory factory = new ConnectionFactory();
        factory.setUri(options.rabbitMqUri());
        factory.setAutomaticRecoveryEnabled(true);
        factory.setTopologyRecoveryEnabled(true);
        factory.setNetworkRecoveryInterval(5000);
        factory.setRequestedHeartbeat(60);

        long deadline = System.nanoTime() + options.startupTimeout().toNanos();
        Exception last;

        while (true) {
            try {
                connection = factory.newConnection(options.instanceId());
                break;
            } catch (Exception e) {
                last = e;
                LOG.warn("Could not connect to RabbitMQ: {}", e.getMessage());

                if (System.nanoTime() + 2_000_000_000L > deadline)
                    throw new IllegalStateException("Could not connect to RabbitMQ within " + options.startupTimeout() + ": " + last.getMessage(), last);

                Thread.sleep(2000);
            }
        }

        ((Recoverable) connection).addRecoveryListener(new RecoveryListener() {
            @Override
            public void handleRecovery(Recoverable recoverable) {
                LOG.info("Reconnected to RabbitMQ.");
                heartbeats.execute(MilvaionWorker.this::registerQuietly);
            }

            @Override
            public void handleRecoveryStarted(Recoverable recoverable) {
                LOG.warn("Lost the RabbitMQ connection. Reconnecting...");
            }
        });

        setup();
        LOG.info("Connected to RabbitMQ.");
    }

    /** Declares the topology and starts the consumers. Automatic recovery restores both after a reconnect. */
    private void setup() throws Exception {
        Map<String, Object> queueArgs = options.quorum() ? Map.of("x-queue-type", "quorum") : null;
        Channel channel = connection.createChannel();
        channel.confirmSelect();

        for (String queue : List.of(Protocol.QUEUE_STATUS_UPDATES, Protocol.QUEUE_WORKER_LOGS, Protocol.QUEUE_WORKER_HEARTBEAT, Protocol.QUEUE_WORKER_REGISTRATION))
            channel.queueDeclare(queue, true, false, false, queueArgs);

        channel.exchangeDeclare(Protocol.EXCHANGE_JOBS, "topic", true);
        channel.exchangeDeclare(Protocol.EXCHANGE_DEAD_LETTER, "direct", true);
        channel.queueDeclare(Protocol.QUEUE_FAILED_OCCURRENCES, true, false, false, queueArgs);
        channel.queueBind(Protocol.QUEUE_FAILED_OCCURRENCES, Protocol.EXCHANGE_DEAD_LETTER, Protocol.DEAD_LETTER_ROUTING_KEY);

        // Before consuming, so the first delivery can already report its status.
        publisher.attach(channel);

        for (ResolvedJob job : jobs.values()) {
            Channel consumerChannel = connection.createChannel();
            consumerChannel.basicQos(job.maxParallelJobs());

            Map<String, Object> args = new HashMap<>();
            args.put("x-dead-letter-exchange", Protocol.EXCHANGE_DEAD_LETTER);
            args.put("x-dead-letter-routing-key", Protocol.DEAD_LETTER_ROUTING_KEY);

            if (queueArgs != null) args.putAll(queueArgs);

            consumerChannel.queueDeclare(job.queue(), true, false, false, args);
            consumerChannel.queueBind(job.queue(), Protocol.EXCHANGE_JOBS, job.routingPattern());

            ExecutorService executor = Executors.newFixedThreadPool(job.maxParallelJobs(), r -> LogBuffer.daemon(r, "milvaion-" + job.consumerId()));
            String tag = consumerChannel.basicConsume(job.queue(), false, options.instanceId() + "." + job.consumerId(),
                    (consumerTag, message) -> executor.execute(() -> processor.handle(toDelivery(consumerChannel, message), job)),
                    consumerTag -> LOG.warn("RabbitMQ cancelled the consumer of {}.", job.queue()));

            consumers.add(new Consumer(consumerChannel, tag, executor));
            LOG.info("Consuming {} from {} (pattern {}, prefetch {}).", job.name(), job.queue(), job.routingPattern(), job.maxParallelJobs());
        }

        register();
    }

    private static Delivery toDelivery(Channel channel, com.rabbitmq.client.Delivery message) {
        long tag = message.getEnvelope().getDeliveryTag();
        AMQP.BasicProperties properties = message.getProperties();

        return new Delivery(message.getBody(), properties.getHeaders() == null ? Map.of() : properties.getHeaders(), properties.getCorrelationId(),
                message.getEnvelope().isRedeliver(), new Delivery.Settler() {
                    @Override
                    public void ack() throws Exception {
                        synchronized (channel) {
                            channel.basicAck(tag, false);
                        }
                    }

                    @Override
                    public void nack(boolean requeue) throws Exception {
                        synchronized (channel) {
                            channel.basicNack(tag, false, requeue);
                        }
                    }
                });
    }

    private void registerQuietly() {
        try {
            register();
        } catch (Exception e) {
            LOG.warn("Re-registering after a reconnect failed: {}", e.getMessage());
        }
    }

    private void register() throws Exception {
        publisher.publish("", Protocol.QUEUE_WORKER_REGISTRATION, Json.bytes(buildRegistration()), true);
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
            publisher.publish("", Protocol.QUEUE_WORKER_HEARTBEAT, Json.bytes(heartbeat), false);
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

        for (Consumer consumer : consumers) {
            try {
                synchronized (consumer.channel()) {
                    consumer.channel().basicCancel(consumer.tag());
                }
            } catch (Exception e) {
                // Channel already closed
            }
        }

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
        consumers.forEach(consumer -> consumer.executor().shutdownNow());

        state = "stopped";
        stopped.countDown();
        LOG.info("Worker instance {} stopped.", options.instanceId());
    }

    private void closeConnections() {
        if (listener != null) listener.close();

        publisher.detach();

        if (connection != null) {
            try {
                connection.close(5000);
            } catch (Exception e) {
                // Already closed
            }
        }
    }

    /** Publishes on one confirm channel, waiting for the broker to accept each message. */
    private static final class AmqpPublisher implements Publisher {
        private volatile Channel channel;

        void attach(Channel channel) {
            this.channel = channel;
        }

        void detach() {
            this.channel = null;
        }

        @Override
        public void publish(String exchange, String routingKey, byte[] body, boolean persistent, String correlationId, Map<String, Object> headers) throws Exception {
            Channel current = channel;

            if (current == null || !current.isOpen()) throw new IllegalStateException("Not connected to RabbitMQ.");

            AMQP.BasicProperties properties = new AMQP.BasicProperties.Builder()
                    .contentType("application/json")
                    .deliveryMode(persistent ? 2 : 1)
                    .correlationId(correlationId)
                    .headers(headers)
                    .build();

            // A channel is not safe for concurrent publishing, and confirms are waited for per message.
            synchronized (current) {
                current.basicPublish(exchange, routingKey, properties, body);
                current.waitForConfirmsOrDie(30_000);
            }
        }
    }
}
