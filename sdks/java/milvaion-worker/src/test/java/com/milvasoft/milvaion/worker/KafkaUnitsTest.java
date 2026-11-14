package com.milvasoft.milvaion.worker;

import org.junit.jupiter.api.Test;

import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

class KafkaUnitsTest {
    static final String GUID = "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00";
    static final String JOB_ID = "0193a6c2-0000-7c41-9a3b-2f6e8d4c1b99";

    @Test
    void topicNamesMatchTheContract() {
        KafkaTopics topics = KafkaTopics.of("", "Report-Worker");

        assertEquals("milvaion.jobs.report-worker", topics.jobs());
        assertEquals("milvaion.jobs.report-worker.retry", topics.retry());
        assertEquals("milvaion.status", topics.status());
        assertEquals("milvaion.logs", topics.logs());
        assertEquals("milvaion.heartbeats", topics.heartbeats());
        assertEquals("milvaion.registrations", topics.registrations());
        assertEquals("milvaion.dlq", topics.dlq());
        assertEquals("milvaion.worker.report-worker", topics.group());

        KafkaTopics prefixed = KafkaTopics.of("prod.", "My Worker:1/ü");

        assertEquals("prod.milvaion.jobs.my_worker_1__", prefixed.jobs());
        assertEquals("prod.milvaion.jobs.my_worker_1__.retry", prefixed.retry());
        assertEquals("prod.milvaion.status", prefixed.status());
        assertEquals("prod.milvaion.dlq", prefixed.dlq());
        assertEquals("prod.milvaion.worker.my_worker_1__", prefixed.group());
        assertEquals("a.b_c-d9", KafkaTopics.workerSegment("A.B_C-D9"));
        assertEquals(7, prefixed.all().size());
    }

    @Test
    void parsesHeaders() {
        Map<String, Object> headers = new HashMap<>();
        assertNull(KafkaHeaders.occurrenceId(headers));
        assertEquals(0, KafkaHeaders.retryCount(headers));
        assertEquals(0, KafkaHeaders.notBefore(headers));

        headers.put("CorrelationId", GUID);
        assertEquals(GUID, KafkaHeaders.occurrenceId(headers));

        headers.put("OccurrenceId", "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b11");
        assertEquals("0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b11", KafkaHeaders.occurrenceId(headers));

        headers.put("OccurrenceId", "not-a-guid");
        assertEquals(GUID, KafkaHeaders.occurrenceId(headers));

        headers.put("x-retry-count", "12");
        headers.put("x-not-before", "1763100000123");
        assertEquals(12, KafkaHeaders.retryCount(headers));
        assertEquals(1763100000123L, KafkaHeaders.notBefore(headers));

        headers.put("x-retry-count", "x");
        headers.put("x-not-before", "soon");
        assertEquals(0, KafkaHeaders.retryCount(headers));
        assertEquals(0, KafkaHeaders.notBefore(headers));
    }

    @Test
    void buildsRetryAndDeadLetterHeaders() {
        Map<String, Object> original = new LinkedHashMap<>();
        original.put("OccurrenceId", GUID);
        original.put("JobType", "TestJob");
        original.put("x-retry-count", "1");

        Map<String, String> retry = KafkaHeaders.retry(original, GUID, 2, 3, 5000L);

        assertEquals("TestJob", retry.get("JobType"));
        assertEquals(GUID, retry.get("OccurrenceId"));
        assertEquals(GUID, retry.get("CorrelationId"));
        assertEquals("2", retry.get("x-retry-count"));
        assertEquals("3", retry.get("MaxRetries"));
        assertEquals("5000", retry.get("x-not-before"));

        assertEquals(Map.of("OccurrenceId", GUID, "CorrelationId", GUID, "x-retry-count", "0", "MaxRetries", "0"), KafkaHeaders.outcome(GUID, 0, 0));
    }

    @Test
    void offsetTrackerCommitsContiguousRange() {
        OffsetTracker tracker = new OffsetTracker();
        assertEquals(-1, tracker.commitPosition());

        for (long offset : new long[]{10, 11, 12, 15}) tracker.add(offset); // gaps are allowed

        tracker.done(11);
        assertEquals(-1, tracker.commitPosition());

        tracker.done(10);
        assertEquals(12, tracker.commitPosition());
        assertEquals(12, tracker.uncommitted());

        tracker.markCommitted(12);
        assertEquals(-1, tracker.uncommitted());

        tracker.done(15);
        assertEquals(12, tracker.commitPosition());
        assertEquals(1, tracker.outstanding());

        tracker.done(12);
        assertEquals(16, tracker.commitPosition());
        assertEquals(16, tracker.uncommitted());
        assertEquals(0, tracker.outstanding());

        tracker.done(99); // never seen
        assertEquals(16, tracker.commitPosition());

        tracker.add(16);
        tracker.revoke();
        tracker.done(16);
        assertEquals(16, tracker.commitPosition());
        assertTrue(tracker.revoked());
    }

    @Test
    void readsKafkaOptionsFromEnvironment() {
        WorkerOptions options = WorkerOptions.fromEnv(Map.of(
                "MILVAION_WORKER_ID", "env-worker",
                "MILVAION_TRANSPORT", "Kafka",
                "MILVAION_MAX_PARALLEL_JOBS", "6",
                "KAFKA_BOOTSTRAP_SERVERS", "k1:9092,k2:9092",
                "MILVAION_KAFKA_SECURITY_PROTOCOL", "SaslSsl",
                "MILVAION_KAFKA_SASL_MECHANISM", "ScramSha512",
                "MILVAION_KAFKA_TOPIC_PREFIX", "prod.",
                "MILVAION_KAFKA_AUTO_CREATE_TOPICS", "false",
                "MILVAION_KAFKA_PARTITIONS", "3",
                "MILVAION_KAFKA_REPLICATION_FACTOR", "2")).resolve();

        assertEquals(TransportType.KAFKA, options.transport());
        KafkaOptions kafka = options.kafka();
        assertEquals("k1:9092,k2:9092", kafka.bootstrapServers());
        assertEquals("SASL_SSL", kafka.securityProtocol());
        assertEquals("SCRAM-SHA-512", kafka.saslMechanism());
        assertEquals("prod.", kafka.topicPrefix());
        assertFalse(kafka.autoCreateTopics());
        assertEquals(3, kafka.numPartitions());
        assertEquals(2, kafka.replicationFactor());
        assertEquals(6, kafka.maxInFlightPerPartition());
        assertEquals("a:1", KafkaOptions.fromEnv(Map.of("MILVAION_KAFKA_BOOTSTRAP_SERVERS", "a:1", "KAFKA_BOOTSTRAP_SERVERS", "b:2")).bootstrapServers());

        WorkerOptions defaults = new WorkerOptions().workerId("w").resolve();
        assertEquals(TransportType.RABBITMQ, defaults.transport());
        assertEquals("", defaults.kafka().topicPrefix());
        assertTrue(defaults.kafka().autoCreateTopics());
        assertEquals(12, defaults.kafka().numPartitions());
        assertEquals(-1, defaults.kafka().replicationFactor());

        assertThrows(IllegalArgumentException.class, () -> TransportType.parse("nats"));
        assertThrows(IllegalArgumentException.class,
                () -> new WorkerOptions().workerId("w").transport(TransportType.KAFKA).kafka(new KafkaOptions().topicPrefix("bad prefix")).resolve());
        assertThrows(IllegalArgumentException.class, () -> KafkaOptions.normalizeProtocol("TLS"));
        assertEquals("SASL_PLAINTEXT", KafkaOptions.normalizeProtocol("sasl_plaintext"));
        assertEquals("SCRAM-SHA-256", KafkaOptions.normalizeMechanism("SCRAM-SHA-256"));
    }

    /** The processor with a transport that does not wait for retries, as on Kafka. */
    @Test
    void retryWithoutWaitingPublishesAtOnceAndSettles() {
        List<String> calls = Collections.synchronizedList(new ArrayList<>());
        JobTransport transport = new JobTransport() {
            @Override
            public void publishStatus(String occurrenceId, byte[] body) {
                calls.add("status " + Json.parse(body).get("status").asInt());
            }

            @Override
            public boolean waitsForRetryDelay() {
                return false;
            }

            @Override
            public void publishRetry(Delivery delivery, ResolvedJob consumer, String jobId, String occurrenceId, int retryCount, int maxRetries, Duration delay) {
                calls.add("retry " + jobId + " " + occurrenceId + " " + retryCount + "/" + maxRetries + " " + delay.toMillis());
            }

            @Override
            public void publishDeadLetter(String jobId, String occurrenceId, byte[] body, int retryCount, int maxRetries) {
                calls.add("dlq " + retryCount + "/" + maxRetries);
            }
        };

        WorkerOptions options = new WorkerOptions().workerId("w").redisUri(null).jobHeartbeatInterval(Duration.ZERO).instanceId("w-1").resolve();
        Map<String, ResolvedJob> jobs = Map.of("testjob", new JobOptions().maxRetries(2).baseRetryDelay(Duration.ofMillis(100))
                .resolve("TestJob", ctx -> { throw new IllegalStateException("boom"); }, options));
        LogBuffer logs = new LogBuffer(batch -> { }, 50, 5, 10_000);

        try (JobProcessor processor = new JobProcessor(options, jobs, transport, logs, new CancellationRegistry())) {
            processor.sleeper = (duration, latch) -> { throw new AssertionError("must not wait"); };

            List<String> settled = new ArrayList<>();
            byte[] body = Json.bytes(Map.of("id", JOB_ID, "jobNameInWorker", "TestJob"));
            Delivery.Settler settler = new Delivery.Settler() {
                @Override
                public void ack() {
                    settled.add("ack");
                }

                @Override
                public void nack(boolean requeue) {
                    settled.add(requeue ? "requeue" : "nack");
                }

                @Override
                public void reject(String reason) {
                    settled.add("reject");
                }
            };

            processor.handle(new Delivery(body, Map.of("OccurrenceId", GUID, "x-retry-count", "1"), null, false, settler), null);
            assertEquals(List.of("ack"), settled);
            assertEquals(List.of("status 1", "retry " + JOB_ID + " " + GUID + " 2/2 200"), calls);

            calls.clear();
            settled.clear();
            processor.handle(new Delivery(body, Map.of("OccurrenceId", GUID, "x-retry-count", "2"), null, false, settler), null);
            assertEquals(List.of("ack"), settled);
            assertEquals(List.of("status 1", "status 3", "dlq 2/2"), calls);

            settled.clear();
            processor.handle(new Delivery("{oops".getBytes(StandardCharsets.UTF_8), Map.of(), null, false, settler), null);
            assertEquals(List.of("reject"), settled);
        } finally {
            logs.close();
        }
    }
}
