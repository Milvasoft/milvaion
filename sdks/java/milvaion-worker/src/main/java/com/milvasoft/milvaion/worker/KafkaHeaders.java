package com.milvasoft.milvaion.worker;

import java.util.LinkedHashMap;
import java.util.Map;

/** Header names and parsing of the Kafka transport. Header values are UTF-8 strings; numbers are decimal strings. */
final class KafkaHeaders {
    static final String OCCURRENCE_ID = "OccurrenceId";
    static final String CORRELATION_ID = "CorrelationId";
    static final String RETRY_COUNT = "x-retry-count";
    static final String MAX_RETRIES = "MaxRetries";
    static final String NOT_BEFORE = "x-not-before";
    static final String ORIGINAL_TOPIC = "x-original-topic";
    static final String DLQ_REASON = "x-dlq-reason";

    private KafkaHeaders() {
    }

    /** Occurrence id from {@code OccurrenceId}, then {@code CorrelationId}; null when neither holds a GUID. */
    static String occurrenceId(Map<String, Object> headers) {
        return JobProcessor.parseOccurrenceId(headers, null);
    }

    /** {@code x-retry-count}; absent or unreadable means 0. */
    static int retryCount(Map<String, Object> headers) {
        return JobProcessor.parseRetryCount(headers);
    }

    /** {@code x-not-before} in Unix milliseconds, or 0 when absent or unreadable (due now). */
    static long notBefore(Map<String, Object> headers) {
        String text = headers == null ? null : JobProcessor.headerString(headers.get(NOT_BEFORE));

        if (text == null) return 0;

        try {
            return Long.parseLong(text.trim());
        } catch (NumberFormatException e) {
            return 0;
        }
    }

    /**
     * Headers of a retry record: the original job headers with the occurrence id, the incremented retry count,
     * the retry limit and the time it becomes due.
     */
    static Map<String, String> retry(Map<String, Object> original, String occurrenceId, int retryCount, int maxRetries, long notBeforeMs) {
        Map<String, String> headers = new LinkedHashMap<>();

        if (original != null) {
            original.forEach((name, value) -> {
                String text = JobProcessor.headerString(value);

                if (text != null) headers.put(name, text);
            });
        }

        headers.putAll(outcome(occurrenceId, retryCount, maxRetries));
        headers.put(NOT_BEFORE, Long.toString(notBeforeMs));

        return headers;
    }

    /** Headers of a dead letter record ({@code maxRetries} is 0 for a permanent failure). */
    static Map<String, String> outcome(String occurrenceId, int retryCount, int maxRetries) {
        Map<String, String> headers = new LinkedHashMap<>();
        headers.put(OCCURRENCE_ID, occurrenceId);
        headers.put(CORRELATION_ID, occurrenceId);
        headers.put(RETRY_COUNT, Integer.toString(retryCount));
        headers.put(MAX_RETRIES, Integer.toString(maxRetries));
        return headers;
    }
}
