package com.milvasoft.milvaion.worker;

import com.fasterxml.jackson.databind.JsonNode;
import org.apache.kafka.clients.admin.AdminClient;
import org.apache.kafka.clients.admin.AdminClientConfig;
import org.apache.kafka.clients.consumer.ConsumerConfig;
import org.apache.kafka.clients.consumer.ConsumerRecord;
import org.apache.kafka.clients.consumer.KafkaConsumer;
import org.apache.kafka.clients.consumer.OffsetAndMetadata;
import org.apache.kafka.clients.producer.KafkaProducer;
import org.apache.kafka.clients.producer.ProducerConfig;
import org.apache.kafka.clients.producer.ProducerRecord;
import org.apache.kafka.clients.producer.RecordMetadata;
import org.apache.kafka.common.PartitionInfo;
import org.apache.kafka.common.TopicPartition;
import org.apache.kafka.common.header.Header;
import org.apache.kafka.common.serialization.ByteArrayDeserializer;
import org.apache.kafka.common.serialization.ByteArraySerializer;
import org.apache.kafka.common.serialization.StringDeserializer;
import org.apache.kafka.common.serialization.StringSerializer;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.EnabledIfEnvironmentVariable;

import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Properties;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;

/**
 * Runs a worker against a real Kafka broker. Skipped unless MILVAION_KAFKA_BROKERS is set, for example:
 * <pre>
 * docker run -d --name milvaion-kafka -p 39092:9092 -e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://localhost:39092 ... apache/kafka:3.8.0
 * MILVAION_KAFKA_BROKERS=localhost:39092 mvn test
 * </pre>
 * Every test uses its own topic prefix, so tests do not see each other's records.
 */
@EnabledIfEnvironmentVariable(named = "MILVAION_KAFKA_BROKERS", matches = ".+")
class KafkaIntegrationTest {
    static final String WORKER_ID = "Kafka-IT Worker";

    final String brokers = System.getenv("MILVAION_KAFKA_BROKERS");
    final List<AutoCloseable> closeables = new ArrayList<>();
    String prefix;
    KafkaTopics topics;
    KafkaProducer<String, byte[]> producer;
    Recorder recorder;

    @BeforeEach
    void setUp() {
        prefix = "it" + HexFormat.of().formatHex(new byte[]{(byte) ThreadLocalRandom.current().nextInt(), (byte) ThreadLocalRandom.current().nextInt(),
                (byte) ThreadLocalRandom.current().nextInt(), (byte) ThreadLocalRandom.current().nextInt()}) + ".";
        topics = KafkaTopics.of(prefix, WORKER_ID);

        Properties props = new Properties();
        props.put(ProducerConfig.BOOTSTRAP_SERVERS_CONFIG, brokers);
        props.put(ProducerConfig.KEY_SERIALIZER_CLASS_CONFIG, StringSerializer.class.getName());
        props.put(ProducerConfig.VALUE_SERIALIZER_CLASS_CONFIG, ByteArraySerializer.class.getName());
        producer = new KafkaProducer<>(props);
        closeables.add(producer);
    }

    @AfterEach
    void tearDown() {
        Collections.reverse(closeables);

        for (AutoCloseable closeable : closeables) {
            try {
                closeable.close();
            } catch (Exception ignored) {
                // best effort
            }
        }
    }

    MilvaionWorker startWorker(JobHandler handler, JobOptions jobOptions, Duration shutdownTimeout) throws Exception {
        MilvaionWorker worker = new MilvaionWorker(new WorkerOptions()
                .workerId(WORKER_ID)
                .transport(TransportType.KAFKA)
                .kafka(new KafkaOptions().bootstrapServers(brokers).topicPrefix(prefix).numPartitions(3))
                .redisUri(null)
                .maxParallelJobs(4)
                .jobHeartbeatInterval(Duration.ofMillis(300))
                .shutdownTimeout(shutdownTimeout));
        worker.job("TestJob", handler, jobOptions);
        worker.start();
        closeables.add(worker);

        recorder = new Recorder(brokers, List.of(topics.status(), topics.dlq(), topics.retry(), topics.jobs(), topics.registrations()));
        closeables.add(recorder);

        return worker;
    }

    RecordMetadata produceJob(String jobId, String occurrenceId, Map<String, Object> extra) throws Exception {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("id", jobId);
        body.put("displayName", "Integration test");
        body.put("jobNameInWorker", "TestJob");
        body.put("jobData", "{\"n\":1}");
        body.put("executeAt", Json.now());
        body.put("workerId", WORKER_ID);
        body.putAll(extra);

        return produceRaw(jobId, Json.bytes(body), Map.of("OccurrenceId", occurrenceId, "CorrelationId", occurrenceId, "JobType", "TestJob",
                "WorkerId", WORKER_ID, "Priority", "0"));
    }

    RecordMetadata produceRaw(String key, byte[] value, Map<String, String> headers) throws Exception {
        ProducerRecord<String, byte[]> record = new ProducerRecord<>(topics.jobs(), key, value);
        headers.forEach((name, text) -> record.headers().add(name, text.getBytes(StandardCharsets.UTF_8)));

        return producer.send(record).get(10, TimeUnit.SECONDS);
    }

    static String header(ConsumerRecord<String, byte[]> record, String name) {
        Header header = record.headers().lastHeader(name);
        return header == null ? null : new String(header.value(), StandardCharsets.UTF_8);
    }

    static int status(ConsumerRecord<String, byte[]> record) {
        return Json.parse(record.value()).get("status").asInt();
    }

    List<Integer> statuses(String occurrenceId) {
        return recorder.on(topics.status()).stream().filter(r -> occurrenceId.equals(r.key())).map(KafkaIntegrationTest::status).toList();
    }

    long committed(TopicPartition partition) throws Exception {
        Properties props = new Properties();
        props.put(AdminClientConfig.BOOTSTRAP_SERVERS_CONFIG, brokers);

        try (AdminClient admin = AdminClient.create(props)) {
            OffsetAndMetadata offset = admin.listConsumerGroupOffsets(topics.group()).partitionsToOffsetAndMetadata().get(10, TimeUnit.SECONDS).get(partition);
            return offset == null ? -1 : offset.offset();
        }
    }

    void awaitCommitted(TopicPartition partition, long expected) throws Exception {
        long deadline = System.currentTimeMillis() + 15_000;
        long last = -1;

        while (System.currentTimeMillis() < deadline) {
            last = committed(partition);

            if (last >= expected) return;

            Thread.sleep(200);
        }

        fail("Offset of " + partition + " is " + last + ", expected " + expected);
    }

    @Test
    void runsAJobEndToEndAndCommitsIt() throws Exception {
        startWorker(ctx -> Map.of("ok", true), new JobOptions(), Duration.ofSeconds(5));

        String jobId = UUID.randomUUID().toString();
        String occurrenceId = UUID.randomUUID().toString();
        RecordMetadata sent = produceJob(jobId, occurrenceId, Map.of());

        recorder.await("Completed status", () -> statuses(occurrenceId).contains(JobStatus.COMPLETED.code()));
        assertEquals(List.of(JobStatus.RUNNING.code(), JobStatus.COMPLETED.code()), statuses(occurrenceId));

        ConsumerRecord<String, byte[]> completed = recorder.on(topics.status()).stream()
                .filter(r -> occurrenceId.equals(r.key()) && status(r) == JobStatus.COMPLETED.code()).findFirst().orElseThrow();
        JsonNode body = Json.parse(completed.value());
        assertEquals(jobId, body.get("jobId").asText());
        assertEquals(WORKER_ID, body.get("workerId").asText());
        assertEquals("{\"ok\":true}", body.get("result").asText());

        // Registration keyed by instance id
        assertTrue(recorder.on(topics.registrations()).stream().anyMatch(r -> r.key() != null && r.key().startsWith(WORKER_ID + "-")));

        awaitCommitted(new TopicPartition(sent.topic(), sent.partition()), sent.offset() + 1);
    }

    @Test
    void runsJobsOfOnePartitionInParallelAndCommitsInOrder() throws Exception {
        CountDownLatch releaseSlow = new CountDownLatch(1);
        startWorker(ctx -> {
            if (ctx.data().get("slow").asBoolean()) releaseSlow.await(30, TimeUnit.SECONDS);
            return null;
        }, new JobOptions(), Duration.ofSeconds(5));

        // Same key, so both records land in the same partition, slow one first.
        String jobId = UUID.randomUUID().toString();
        String slow = UUID.randomUUID().toString();
        String fast = UUID.randomUUID().toString();
        RecordMetadata first = produceJob(jobId, slow, Map.of("jobData", "{\"slow\":true}"));
        RecordMetadata second = produceJob(jobId, fast, Map.of("jobData", "{\"slow\":false}"));
        TopicPartition partition = new TopicPartition(first.topic(), first.partition());
        assertEquals(first.partition(), second.partition());

        recorder.await("fast job completed", () -> statuses(fast).contains(JobStatus.COMPLETED.code()));
        assertTrue(statuses(slow).stream().noneMatch(s -> s == JobStatus.COMPLETED.code()), "the slow job is still running");

        // The finished second record cannot be committed while the first is running.
        Thread.sleep(1200);
        assertTrue(committed(partition) <= first.offset(), "commit must not pass the running record");

        releaseSlow.countDown();
        recorder.await("slow job completed", () -> statuses(slow).contains(JobStatus.COMPLETED.code()));
        awaitCommitted(partition, second.offset() + 1);
    }

    @Test
    void retriesThroughTheDelayTopicThenDeadLetters() throws Exception {
        List<Long> attempts = Collections.synchronizedList(new ArrayList<>());
        startWorker(ctx -> {
            attempts.add(System.currentTimeMillis());
            throw new IllegalStateException("always fails");
        }, new JobOptions().maxRetries(2).baseRetryDelay(Duration.ofMillis(400)), Duration.ofSeconds(5));

        String jobId = UUID.randomUUID().toString();
        String occurrenceId = UUID.randomUUID().toString();
        RecordMetadata sent = produceJob(jobId, occurrenceId, Map.of());

        recorder.await("dead letter record", () -> !recorder.on(topics.dlq()).isEmpty());
        recorder.await("Failed status", () -> statuses(occurrenceId).contains(JobStatus.FAILED.code()));

        List<ConsumerRecord<String, byte[]>> retries = recorder.on(topics.retry());
        assertEquals(2, retries.size());

        for (int i = 0; i < 2; i++) {
            ConsumerRecord<String, byte[]> retry = retries.get(i);
            assertEquals(jobId, retry.key());
            assertEquals(occurrenceId, header(retry, "OccurrenceId"));
            assertEquals(occurrenceId, header(retry, "CorrelationId"));
            assertEquals(String.valueOf(i + 1), header(retry, "x-retry-count"));
            assertEquals("2", header(retry, "MaxRetries"));
            assertEquals("TestJob", header(retry, "JobType"));

            long notBefore = Long.parseLong(header(retry, "x-not-before"));
            long delay = 400L << i;
            assertTrue(notBefore >= retry.timestamp() + delay - 100, "x-not-before must be about now + " + delay + " ms");
            // The retry attempt did not run before it was due.
            assertTrue(attempts.get(i + 1) >= notBefore, "attempt " + (i + 2) + " ran early");
        }

        assertEquals(3, attempts.size());

        ConsumerRecord<String, byte[]> dead = recorder.on(topics.dlq()).get(0);
        assertEquals(occurrenceId, dead.key());
        assertEquals(occurrenceId, header(dead, "OccurrenceId"));
        assertEquals(occurrenceId, header(dead, "CorrelationId"));
        assertEquals("2", header(dead, "x-retry-count"));
        assertEquals("2", header(dead, "MaxRetries"));
        JsonNode deadBody = Json.parse(dead.value());
        assertEquals(jobId, deadBody.get("id").asText());
        assertEquals(JobStatus.FAILED.code(), deadBody.get("status").asInt());
        assertTrue(deadBody.get("exception").asText().startsWith("Max retries (2) exceeded."));

        List<Integer> statuses = statuses(occurrenceId);
        assertEquals(JobStatus.FAILED.code(), statuses.get(statuses.size() - 1));
        assertTrue(statuses.stream().noneMatch(s -> s == JobStatus.COMPLETED.code()));

        awaitCommitted(new TopicPartition(sent.topic(), sent.partition()), sent.offset() + 1);
        ConsumerRecord<String, byte[]> lastRetry = retries.get(1);
        awaitCommitted(new TopicPartition(lastRetry.topic(), lastRetry.partition()), lastRetry.offset() + 1);
    }

    @Test
    void reportsATimeout() throws Exception {
        startWorker(ctx -> {
            Thread.sleep(10_000);
            return null;
        }, new JobOptions(), Duration.ofSeconds(5));

        String occurrenceId = UUID.randomUUID().toString();
        RecordMetadata sent = produceJob(UUID.randomUUID().toString(), occurrenceId, Map.of("executionTimeoutSeconds", 0.5));

        recorder.await("TimedOut status", () -> statuses(occurrenceId).contains(JobStatus.TIMED_OUT.code()));
        // Running, job heartbeats (Running) while the handler sleeps, then TimedOut
        List<Integer> statuses = statuses(occurrenceId);
        assertEquals(JobStatus.TIMED_OUT.code(), statuses.get(statuses.size() - 1));
        assertTrue(statuses.subList(0, statuses.size() - 1).stream().allMatch(s -> s == JobStatus.RUNNING.code()));
        awaitCommitted(new TopicPartition(sent.topic(), sent.partition()), sent.offset() + 1);
    }

    @Test
    void forwardsAPoisonRecordToTheDeadLetterTopic() throws Exception {
        startWorker(ctx -> null, new JobOptions(), Duration.ofSeconds(5));

        byte[] garbage = "not json at all".getBytes(StandardCharsets.UTF_8);
        RecordMetadata sent = produceRaw("poison-key", garbage, Map.of("JobType", "TestJob"));

        recorder.await("dead letter record", () -> !recorder.on(topics.dlq()).isEmpty());
        ConsumerRecord<String, byte[]> dead = recorder.on(topics.dlq()).get(0);

        assertArrayEquals(garbage, dead.value());
        assertEquals("poison-key", dead.key());
        assertEquals(topics.jobs(), header(dead, "x-original-topic"));
        assertNotNull(header(dead, "x-dlq-reason"));
        assertEquals("TestJob", header(dead, "JobType"));
        awaitCommitted(new TopicPartition(sent.topic(), sent.partition()), sent.offset() + 1);
    }

    @Test
    void requeuesAnInterruptedJobOnShutdown() throws Exception {
        CountDownLatch started = new CountDownLatch(1);
        MilvaionWorker worker = startWorker(ctx -> {
            started.countDown();
            Thread.sleep(60_000);
            return null;
        }, new JobOptions(), Duration.ofMillis(300));

        String jobId = UUID.randomUUID().toString();
        String occurrenceId = UUID.randomUUID().toString();
        RecordMetadata sent = produceJob(jobId, occurrenceId, Map.of());

        assertTrue(started.await(30, TimeUnit.SECONDS));
        worker.close();

        recorder.await("requeued job record", () -> recorder.on(topics.jobs()).size() >= 2);
        ConsumerRecord<String, byte[]> original = recorder.on(topics.jobs()).get(0);
        ConsumerRecord<String, byte[]> requeued = recorder.on(topics.jobs()).get(1);

        assertEquals(jobId, requeued.key());
        assertArrayEquals(original.value(), requeued.value());
        assertEquals(occurrenceId, header(requeued, "OccurrenceId"));
        // Only Running (and job heartbeats): an interrupted job reports no final status.
        assertTrue(statuses(occurrenceId).stream().allMatch(s -> s == JobStatus.RUNNING.code()));
        awaitCommitted(new TopicPartition(sent.topic(), sent.partition()), sent.offset() + 1);
    }

    /** Reads topics from the beginning without a consumer group and keeps every record. */
    static final class Recorder implements AutoCloseable {
        private final List<ConsumerRecord<String, byte[]>> records = Collections.synchronizedList(new ArrayList<>());
        private final Thread thread;
        private volatile boolean closed;

        Recorder(String brokers, List<String> topicNames) {
            Properties props = new Properties();
            props.put(ConsumerConfig.BOOTSTRAP_SERVERS_CONFIG, brokers);
            props.put(ConsumerConfig.KEY_DESERIALIZER_CLASS_CONFIG, StringDeserializer.class.getName());
            props.put(ConsumerConfig.VALUE_DESERIALIZER_CLASS_CONFIG, ByteArrayDeserializer.class.getName());
            props.put(ConsumerConfig.ENABLE_AUTO_COMMIT_CONFIG, "false");
            KafkaConsumer<String, byte[]> consumer = new KafkaConsumer<>(props);

            List<TopicPartition> partitions = new ArrayList<>();

            for (String topic : topicNames) {
                for (PartitionInfo info : consumer.partitionsFor(topic, Duration.ofSeconds(10)))
                    partitions.add(new TopicPartition(topic, info.partition()));
            }

            consumer.assign(partitions);
            consumer.seekToBeginning(partitions);

            thread = new Thread(() -> {
                try (consumer) {
                    while (!closed) consumer.poll(Duration.ofMillis(100)).forEach(records::add);
                }
            }, "kafka-it-recorder");
            thread.setDaemon(true);
            thread.start();
        }

        List<ConsumerRecord<String, byte[]>> on(String topic) {
            synchronized (records) {
                return records.stream().filter(r -> r.topic().equals(topic)).sorted((a, b) -> Long.compare(a.timestamp(), b.timestamp())).toList();
            }
        }

        void await(String what, java.util.function.BooleanSupplier condition) throws InterruptedException {
            long deadline = System.currentTimeMillis() + 30_000;

            while (System.currentTimeMillis() < deadline) {
                if (condition.getAsBoolean()) return;

                Thread.sleep(100);
            }

            fail("Timed out waiting for " + what);
        }

        @Override
        public void close() throws InterruptedException {
            closed = true;
            thread.join(5000);
        }
    }
}
