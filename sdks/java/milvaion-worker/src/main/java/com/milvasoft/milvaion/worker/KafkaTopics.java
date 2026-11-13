package com.milvasoft.milvaion.worker;

import java.util.List;
import java.util.Locale;

/**
 * Topic and consumer group names of the Kafka transport, identical in every SDK and the API.
 * See docs/githubdocs/KAFKA-TRANSPORT.md.
 */
record KafkaTopics(String jobs, String retry, String status, String logs, String heartbeats, String registrations, String dlq, String group) {

    static KafkaTopics of(String prefix, String workerId) {
        String p = prefix == null ? "" : prefix;
        String worker = workerSegment(workerId);

        return new KafkaTopics(
                p + "milvaion.jobs." + worker,
                p + "milvaion.jobs." + worker + ".retry",
                p + "milvaion.status",
                p + "milvaion.logs",
                p + "milvaion.heartbeats",
                p + "milvaion.registrations",
                p + "milvaion.dlq",
                p + "milvaion.worker." + worker);
    }

    /** The worker id in lower case with every character outside {@code a-z 0-9 . _ -} replaced by {@code _}. */
    static String workerSegment(String workerId) {
        String lower = workerId.toLowerCase(Locale.ROOT);
        StringBuilder out = new StringBuilder(lower.length());

        for (int i = 0; i < lower.length(); i++) {
            char c = lower.charAt(i);
            out.append((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' ? c : '_');
        }

        return out.toString();
    }

    /** Topics the worker creates at startup when auto-create is on: its own two and the shared ones it produces to. */
    List<String> all() {
        return List.of(jobs, retry, status, logs, heartbeats, registrations, dlq);
    }
}
