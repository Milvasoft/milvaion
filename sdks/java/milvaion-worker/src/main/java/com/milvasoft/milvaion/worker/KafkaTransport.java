package com.milvasoft.milvaion.worker;

import org.apache.kafka.clients.CommonClientConfigs;
import org.apache.kafka.clients.admin.AdminClient;
import org.apache.kafka.clients.admin.AdminClientConfig;
import org.apache.kafka.clients.admin.NewTopic;
import org.apache.kafka.clients.consumer.ConsumerConfig;
import org.apache.kafka.clients.consumer.ConsumerRebalanceListener;
import org.apache.kafka.clients.consumer.ConsumerRecord;
import org.apache.kafka.clients.consumer.ConsumerRecords;
import org.apache.kafka.clients.consumer.CooperativeStickyAssignor;
import org.apache.kafka.clients.consumer.KafkaConsumer;
import org.apache.kafka.clients.consumer.OffsetAndMetadata;
import org.apache.kafka.clients.producer.KafkaProducer;
import org.apache.kafka.clients.producer.ProducerConfig;
import org.apache.kafka.clients.producer.ProducerRecord;
import org.apache.kafka.common.KafkaFuture;
import org.apache.kafka.common.TopicPartition;
import org.apache.kafka.common.config.SaslConfigs;
import org.apache.kafka.common.config.SslConfigs;
import org.apache.kafka.common.errors.TopicExistsException;
import org.apache.kafka.common.errors.WakeupException;
import org.apache.kafka.common.header.Header;
import org.apache.kafka.common.header.Headers;
import org.apache.kafka.common.serialization.ByteArrayDeserializer;
import org.apache.kafka.common.serialization.ByteArraySerializer;
import org.apache.kafka.common.serialization.StringDeserializer;
import org.apache.kafka.common.serialization.StringSerializer;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collection;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Properties;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * The Kafka transport, as described in docs/githubdocs/KAFKA-TRANSPORT.md.
 *
 * <p>The KafkaConsumer is not thread-safe, so one thread polls, tracks offsets and commits, and hands records to a
 * pool of job threads. Jobs run in parallel up to {@code maxParallelJobs}, also several from one partition (up to
 * {@code maxInFlightPerPartition}). A partition is paused while it has records that cannot be dispatched yet, and
 * the consumer keeps polling so the group does not evict it. Offsets are committed per partition up to the end of
 * the contiguous range of records whose outcome has been reported.
 */
final class KafkaTransport implements WorkerTransport {
    private static final Logger LOG = LoggerFactory.getLogger(KafkaTransport.class);
    private static final Duration POLL_TIMEOUT = Duration.ofMillis(100);
    private static final long COMMIT_INTERVAL_MS = 500;
    private static final long SEND_TIMEOUT_SECONDS = 35;

    private final WorkerOptions options;
    private final KafkaOptions kafka;
    private final KafkaTopics topics;
    private final int maxParallel;
    private final int maxPerPartition;
    private final AtomicInteger inFlight = new AtomicInteger();
    private final Map<TopicPartition, PartitionState> partitions = new HashMap<>();
    private final JobTransport jobTransport = new KafkaJobTransport();
    private volatile KafkaProducer<String, byte[]> producer;
    private KafkaConsumer<String, byte[]> consumer;
    private ExecutorService executor;
    private JobProcessor processor;
    private Thread pollThread;
    private volatile boolean stopping;
    private volatile boolean closing;
    private boolean drained;
    private long lastCommit;

    /** Records of one assigned partition that have been fetched and not dispatched yet, plus its commit tracker. */
    private static final class PartitionState {
        final TopicPartition partition;
        final boolean retryTopic;
        final OffsetTracker tracker = new OffsetTracker();
        final ArrayDeque<Pending> buffer = new ArrayDeque<>();
        final AtomicInteger inFlight = new AtomicInteger();
        boolean paused;
        long heldOffset = -1;
        long nextWaitingHeartbeat;

        PartitionState(TopicPartition partition, boolean retryTopic) {
            this.partition = partition;
            this.retryTopic = retryTopic;
        }
    }

    private record Pending(ConsumerRecord<String, byte[]> record, Map<String, Object> headers) {
    }

    KafkaTransport(WorkerOptions options) {
        this.options = options;
        this.kafka = options.kafka();
        this.topics = KafkaTopics.of(kafka.topicPrefix(), options.workerId());
        this.maxParallel = options.maxParallelJobs();
        this.maxPerPartition = kafka.maxInFlightPerPartition();
    }

    KafkaTopics topics() {
        return topics;
    }

    @Override
    public String name() {
        return "Kafka";
    }

    @Override
    public JobTransport jobs() {
        return jobTransport;
    }

    @Override
    public void start(Map<String, ResolvedJob> jobs, JobProcessor processor, Hooks hooks) throws Exception {
        this.processor = processor;

        if (kafka.autoCreateTopics()) createTopics();

        producer = new KafkaProducer<>(producerConfig());
        consumer = new KafkaConsumer<>(consumerConfig());
        executor = Executors.newFixedThreadPool(maxParallel, r -> LogBuffer.daemon(r, "milvaion-kafka-job"));
        pollThread = LogBuffer.daemon(this::pollLoop, "milvaion-kafka-poll");
        pollThread.start();

        LOG.info("Consuming {} and {} in group {} (max {} job(s) in parallel, {} per partition).",
                topics.jobs(), topics.retry(), topics.group(), maxParallel, maxPerPartition);

        hooks.register();
        LOG.info("Connected to Kafka ({}).", kafka.bootstrapServers());
    }

    // ---------------------------------------------------------------- configuration

    private Properties commonConfig() {
        Properties props = new Properties();
        props.put(CommonClientConfigs.BOOTSTRAP_SERVERS_CONFIG, kafka.bootstrapServers());
        props.put(CommonClientConfigs.CLIENT_ID_CONFIG, options.instanceId());
        props.put(CommonClientConfigs.SECURITY_PROTOCOL_CONFIG, kafka.securityProtocol());

        if (kafka.securityProtocol().startsWith("SASL_")) {
            String module = kafka.saslMechanism().equals("PLAIN")
                    ? "org.apache.kafka.common.security.plain.PlainLoginModule"
                    : "org.apache.kafka.common.security.scram.ScramLoginModule";

            props.put(SaslConfigs.SASL_MECHANISM, kafka.saslMechanism());
            props.put(SaslConfigs.SASL_JAAS_CONFIG, module + " required username=\"" + jaasEscape(kafka.saslUsername())
                    + "\" password=\"" + jaasEscape(kafka.saslPassword()) + "\";");
        }

        putIfSet(props, SslConfigs.SSL_TRUSTSTORE_LOCATION_CONFIG, kafka.sslTruststoreLocation());
        putIfSet(props, SslConfigs.SSL_TRUSTSTORE_PASSWORD_CONFIG, kafka.sslTruststorePassword());
        putIfSet(props, SslConfigs.SSL_TRUSTSTORE_TYPE_CONFIG, kafka.sslTruststoreType());
        putIfSet(props, SslConfigs.SSL_KEYSTORE_LOCATION_CONFIG, kafka.sslKeystoreLocation());
        putIfSet(props, SslConfigs.SSL_KEYSTORE_PASSWORD_CONFIG, kafka.sslKeystorePassword());
        putIfSet(props, SslConfigs.SSL_KEYSTORE_TYPE_CONFIG, kafka.sslKeystoreType());
        putIfSet(props, SslConfigs.SSL_KEY_PASSWORD_CONFIG, kafka.sslKeyPassword());

        return props;
    }

    private static void putIfSet(Properties props, String key, String value) {
        if (value != null && !value.isBlank()) props.put(key, value);
    }

    private static String jaasEscape(String value) {
        return value == null ? "" : value.replace("\\", "\\\\").replace("\"", "\\\"");
    }

    private Properties producerConfig() {
        Properties props = commonConfig();
        props.put(ProducerConfig.KEY_SERIALIZER_CLASS_CONFIG, StringSerializer.class.getName());
        props.put(ProducerConfig.VALUE_SERIALIZER_CLASS_CONFIG, ByteArraySerializer.class.getName());
        props.put(ProducerConfig.ACKS_CONFIG, "all");
        props.put(ProducerConfig.ENABLE_IDEMPOTENCE_CONFIG, "true");
        props.put(ProducerConfig.LINGER_MS_CONFIG, "5");
        props.put(ProducerConfig.REQUEST_TIMEOUT_MS_CONFIG, "15000");
        props.put(ProducerConfig.DELIVERY_TIMEOUT_MS_CONFIG, "30000");
        props.put(ProducerConfig.MAX_BLOCK_MS_CONFIG, "30000");
        return props;
    }

    private Properties consumerConfig() {
        Properties props = commonConfig();
        props.put(ConsumerConfig.GROUP_ID_CONFIG, topics.group());
        props.put(ConsumerConfig.KEY_DESERIALIZER_CLASS_CONFIG, StringDeserializer.class.getName());
        props.put(ConsumerConfig.VALUE_DESERIALIZER_CLASS_CONFIG, ByteArrayDeserializer.class.getName());
        props.put(ConsumerConfig.ENABLE_AUTO_COMMIT_CONFIG, "false");
        props.put(ConsumerConfig.AUTO_OFFSET_RESET_CONFIG, "earliest");
        props.put(ConsumerConfig.PARTITION_ASSIGNMENT_STRATEGY_CONFIG, CooperativeStickyAssignor.class.getName());
        props.put(ConsumerConfig.ALLOW_AUTO_CREATE_TOPICS_CONFIG, "false");
        return props;
    }

    /** Creates the worker's job and retry topics and the shared topics it produces to. Existing topics are fine. */
    private void createTopics() throws Exception {
        Properties props = commonConfig();
        long timeoutMs = Math.max(1000, options.startupTimeout().toMillis());
        props.put(AdminClientConfig.DEFAULT_API_TIMEOUT_MS_CONFIG, (int) Math.min(Integer.MAX_VALUE, timeoutMs));
        props.put(AdminClientConfig.REQUEST_TIMEOUT_MS_CONFIG, (int) Math.min(Integer.MAX_VALUE, Math.min(timeoutMs, 15000)));

        Optional<Short> replication = kafka.replicationFactor() < 0 ? Optional.empty() : Optional.of(kafka.replicationFactor());
        List<NewTopic> wanted = new ArrayList<>();

        for (String topic : topics.all()) wanted.add(new NewTopic(topic, Optional.of(kafka.numPartitions()), replication));

        try (AdminClient admin = AdminClient.create(props)) {
            Map<String, KafkaFuture<Void>> results = admin.createTopics(wanted).values();
            int created = 0;

            for (Map.Entry<String, KafkaFuture<Void>> entry : results.entrySet()) {
                try {
                    entry.getValue().get(timeoutMs + 5000, TimeUnit.MILLISECONDS);
                    created++;
                } catch (ExecutionException e) {
                    if (e.getCause() instanceof TopicExistsException) continue;

                    throw new IllegalStateException("Could not create Kafka topic " + entry.getKey() + " at " + kafka.bootstrapServers() + ": "
                            + (e.getCause() == null ? e.getMessage() : e.getCause().getMessage()), e.getCause() == null ? e : e.getCause());
                } catch (java.util.concurrent.TimeoutException e) {
                    throw new IllegalStateException("Could not reach Kafka at " + kafka.bootstrapServers() + " within " + options.startupTimeout(), e);
                }
            }

            if (created > 0) LOG.info("Created {} Kafka topic(s).", created);
        }
    }

    // ---------------------------------------------------------------- poll thread

    private void pollLoop() {
        try {
            consumer.subscribe(List.of(topics.jobs(), topics.retry()), new RebalanceListener());

            while (!closing) {
                try {
                    if (stopping && !drained) drain();

                    updatePauses();

                    ConsumerRecords<String, byte[]> records = consumer.poll(POLL_TIMEOUT);

                    for (ConsumerRecord<String, byte[]> record : records) {
                        PartitionState state = partitions.get(new TopicPartition(record.topic(), record.partition()));

                        if (state == null) continue; // revoked meanwhile; the new owner reads it

                        state.tracker.add(record.offset());
                        state.buffer.add(new Pending(record, headerMap(record.headers())));
                    }

                    if (!stopping) dispatch();

                    if (System.currentTimeMillis() - lastCommit >= COMMIT_INTERVAL_MS) commit(partitions.values());
                } catch (WakeupException e) {
                    // close() woke the consumer up
                } catch (Exception e) {
                    if (closing) break;

                    LOG.warn("Kafka consumer error; retrying: {}", e.getMessage());
                    sleepQuietly(1000);
                }
            }
        } catch (Throwable e) {
            LOG.error("The Kafka consumer stopped unexpectedly.", e);
        } finally {
            try {
                commit(partitions.values());
            } catch (Exception e) {
                LOG.warn("Final Kafka commit failed: {}", e.getMessage());
            }

            try {
                consumer.close(Duration.ofSeconds(10));
            } catch (Exception e) {
                LOG.debug("Closing the Kafka consumer failed: {}", e.getMessage());
            }
        }
    }

    /** After stopConsuming(): forget fetched records that have not started. They stay uncommitted, so they are read again. */
    private void drain() {
        for (PartitionState state : partitions.values()) {
            state.buffer.clear();
            state.heldOffset = -1;
        }

        drained = true;
    }

    private void updatePauses() {
        List<TopicPartition> pause = new ArrayList<>();
        List<TopicPartition> resume = new ArrayList<>();
        boolean full = inFlight.get() >= maxParallel;

        for (PartitionState state : partitions.values()) {
            boolean shouldPause = stopping || full || !state.buffer.isEmpty() || state.inFlight.get() >= maxPerPartition;

            if (shouldPause == state.paused) continue;

            state.paused = shouldPause;
            (shouldPause ? pause : resume).add(state.partition);
        }

        if (!pause.isEmpty()) consumer.pause(pause);
        if (!resume.isEmpty()) consumer.resume(resume);
    }

    /** Hands buffered records to free job slots, in partition order, holding retry records that are not due yet. */
    private void dispatch() {
        long now = System.currentTimeMillis();

        for (PartitionState state : partitions.values()) {
            while (!state.buffer.isEmpty() && inFlight.get() < maxParallel && state.inFlight.get() < maxPerPartition) {
                Pending next = state.buffer.peek();

                if (state.retryTopic) {
                    long due = KafkaHeaders.notBefore(next.headers());

                    if (due > now) {
                        hold(state, next);
                        break;
                    }
                }

                state.buffer.poll();
                state.heldOffset = -1;
                inFlight.incrementAndGet();
                state.inFlight.incrementAndGet();

                try {
                    executor.execute(() -> runJob(state, next));
                } catch (RuntimeException e) {
                    inFlight.decrementAndGet();
                    state.inFlight.decrementAndGet();
                    throw e;
                }
            }
        }
    }

    /** Keeps a not-yet-due retry record at the head of its partition and sends job heartbeats for it while it waits. */
    private void hold(PartitionState state, Pending pending) {
        Duration interval = options.jobHeartbeatInterval();
        boolean beat = interval != null && !interval.isZero() && !interval.isNegative();
        long nowNanos = System.nanoTime();

        if (state.heldOffset != pending.record().offset()) {
            state.heldOffset = pending.record().offset();
            state.nextWaitingHeartbeat = beat ? nowNanos + interval.toNanos() : Long.MAX_VALUE;
            LOG.debug("Holding retry record {} of {} until it is due.", pending.record().offset(), state.partition);
            return;
        }

        if (beat && nowNanos >= state.nextWaitingHeartbeat) {
            state.nextWaitingHeartbeat = nowNanos + interval.toNanos();
            processor.sendWaitingHeartbeat(pending.record().value(), pending.headers());
        }
    }

    private void runJob(PartitionState state, Pending pending) {
        ConsumerRecord<String, byte[]> record = pending.record();

        try {
            byte[] body = record.value() == null ? new byte[0] : record.value();
            processor.handle(new Delivery(body, pending.headers(), null, false, new RecordSettler(state, record)), null);
        } catch (Throwable e) {
            LOG.error("Unexpected error while running Kafka record {} of {}.", record.offset(), state.partition, e);
        } finally {
            state.inFlight.decrementAndGet();
            inFlight.decrementAndGet();
        }
    }

    /** Commits, per partition, the end of the contiguous range of finished records. Poll thread only. */
    private void commit(Collection<PartitionState> states) {
        lastCommit = System.currentTimeMillis();
        Map<TopicPartition, OffsetAndMetadata> offsets = new HashMap<>();
        Map<PartitionState, Long> positions = new HashMap<>();

        for (PartitionState state : states) {
            long position = state.tracker.uncommitted();

            if (position < 0) continue;

            offsets.put(state.partition, new OffsetAndMetadata(position));
            positions.put(state, position);
        }

        if (offsets.isEmpty()) return;

        try {
            consumer.commitSync(offsets, Duration.ofSeconds(10));
            positions.forEach((state, position) -> state.tracker.markCommitted(position));
        } catch (WakeupException e) {
            throw e;
        } catch (Exception e) {
            LOG.warn("Committing Kafka offsets failed; will retry: {}", e.getMessage());
        }
    }

    private final class RebalanceListener implements ConsumerRebalanceListener {
        @Override
        public void onPartitionsAssigned(Collection<TopicPartition> assigned) {
            for (TopicPartition partition : assigned) {
                partitions.put(partition, new PartitionState(partition, partition.topic().equals(topics.retry())));

                // A partition assigned while stopping must not be read.
                if (stopping) {
                    partitions.get(partition).paused = true;
                    consumer.pause(List.of(partition));
                }
            }

            if (!assigned.isEmpty()) LOG.info("Assigned Kafka partitions {}.", assigned);
        }

        @Override
        public void onPartitionsRevoked(Collection<TopicPartition> revoked) {
            List<PartitionState> states = new ArrayList<>();

            for (TopicPartition partition : revoked) {
                PartitionState state = partitions.get(partition);

                if (state != null) states.add(state);
            }

            commit(states);
            forget(revoked);

            if (!revoked.isEmpty()) LOG.info("Revoked Kafka partitions {}.", revoked);
        }

        @Override
        public void onPartitionsLost(Collection<TopicPartition> lost) {
            forget(lost);

            if (!lost.isEmpty()) LOG.warn("Lost Kafka partitions {}.", lost);
        }

        private void forget(Collection<TopicPartition> gone) {
            for (TopicPartition partition : gone) {
                PartitionState state = partitions.remove(partition);

                if (state == null) continue;

                // Jobs still running from it finish and report, but this instance can no longer commit them.
                state.tracker.revoke();
                state.buffer.clear();
            }
        }
    }

    // ---------------------------------------------------------------- settling records

    /** Settles one job record: done marks it finished for the commit tracker. */
    private final class RecordSettler implements Delivery.Settler {
        private final PartitionState state;
        private final ConsumerRecord<String, byte[]> record;

        RecordSettler(PartitionState state, ConsumerRecord<String, byte[]> record) {
            this.state = state;
            this.record = record;
        }

        @Override
        public void ack() {
            state.tracker.done(record.offset());
        }

        @Override
        public void nack(boolean requeue) throws Exception {
            if (!requeue) {
                reject("Rejected by the worker.");
                return;
            }

            // Kafka has no requeue: write the record back to its own topic, then it is done here.
            send(new ProducerRecord<>(record.topic(), null, record.key(), record.value(), copy(record.headers())));
            state.tracker.done(record.offset());
        }

        @Override
        public void reject(String reason) throws Exception {
            Headers headers = copy(record.headers());
            headers.remove(KafkaHeaders.ORIGINAL_TOPIC);
            headers.remove(KafkaHeaders.DLQ_REASON);
            headers.add(KafkaHeaders.ORIGINAL_TOPIC, record.topic().getBytes(StandardCharsets.UTF_8));
            headers.add(KafkaHeaders.DLQ_REASON, (reason == null ? "" : reason).getBytes(StandardCharsets.UTF_8));

            String occurrenceId = KafkaHeaders.occurrenceId(headerMap(record.headers()));
            String key = occurrenceId != null ? occurrenceId : record.key();

            // Forwarded unchanged so nothing is lost; the API's DLQ consumer decides what to do with it.
            send(new ProducerRecord<>(topics.dlq(), null, key, record.value(), headers));
            state.tracker.done(record.offset());
        }
    }

    private static Headers copy(Headers original) {
        org.apache.kafka.common.header.internals.RecordHeaders copy = new org.apache.kafka.common.header.internals.RecordHeaders();

        for (Header header : original) copy.add(header.key(), header.value());

        return copy;
    }

    /** Header values as UTF-8 strings; the last value of a repeated header wins. */
    static Map<String, Object> headerMap(Headers headers) {
        Map<String, Object> map = new LinkedHashMap<>();

        for (Header header : headers) {
            if (header.value() != null) map.put(header.key(), new String(header.value(), StandardCharsets.UTF_8));
        }

        return map;
    }

    // ---------------------------------------------------------------- producing

    private void send(ProducerRecord<String, byte[]> record) throws Exception {
        KafkaProducer<String, byte[]> current = producer;

        if (current == null) throw new IllegalStateException("Not connected to Kafka.");

        try {
            current.send(record).get(SEND_TIMEOUT_SECONDS, TimeUnit.SECONDS);
        } catch (ExecutionException e) {
            throw e.getCause() instanceof Exception cause ? cause : e;
        }
    }

    private void send(String topic, String key, byte[] body, Map<String, String> headers) throws Exception {
        ProducerRecord<String, byte[]> record = new ProducerRecord<>(topic, null, key, body);

        if (headers != null) headers.forEach((name, value) -> record.headers().add(name, value.getBytes(StandardCharsets.UTF_8)));

        send(record);
    }

    @Override
    public void publishRegistration(byte[] body) throws Exception {
        send(topics.registrations(), options.instanceId(), body, null);
    }

    @Override
    public void publishHeartbeat(byte[] body) throws Exception {
        send(topics.heartbeats(), options.instanceId(), body, null);
    }

    @Override
    public void publishLogs(String key, byte[] body) throws Exception {
        send(topics.logs(), key == null || key.isEmpty() ? null : key, body, null);
    }

    private final class KafkaJobTransport implements JobTransport {
        @Override
        public void publishStatus(String occurrenceId, byte[] body) throws Exception {
            send(topics.status(), occurrenceId, body, null);
        }

        @Override
        public boolean waitsForRetryDelay() {
            return false;
        }

        @Override
        public void publishRetry(Delivery delivery, ResolvedJob consumer, String jobId, String occurrenceId, int retryCount, int maxRetries, Duration delay) throws Exception {
            long notBefore = System.currentTimeMillis() + delay.toMillis();
            send(topics.retry(), jobId, delivery.body(), KafkaHeaders.retry(delivery.headers(), occurrenceId, retryCount, maxRetries, notBefore));
        }

        @Override
        public void publishDeadLetter(String jobId, String occurrenceId, byte[] body, int retryCount, int maxRetries) throws Exception {
            send(topics.dlq(), occurrenceId, body, KafkaHeaders.outcome(occurrenceId, retryCount, maxRetries));
        }
    }

    // ---------------------------------------------------------------- lifecycle

    @Override
    public void stopConsuming() {
        stopping = true;
    }

    @Override
    public void close() {
        stopping = true;
        closing = true;

        if (pollThread != null) {
            try {
                pollThread.join(20_000);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }

            if (pollThread.isAlive()) {
                LOG.warn("The Kafka poll thread did not stop in time.");

                try {
                    consumer.wakeup();
                } catch (Exception ignored) {
                    // Already closed
                }
            }
        }

        if (executor != null) executor.shutdownNow();

        KafkaProducer<String, byte[]> current = producer;
        producer = null;

        if (current != null) {
            try {
                current.close(Duration.ofSeconds(10));
            } catch (Exception e) {
                LOG.debug("Closing the Kafka producer failed: {}", e.getMessage());
            }
        }
    }

    private static void sleepQuietly(long millis) {
        try {
            Thread.sleep(millis);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }
}
