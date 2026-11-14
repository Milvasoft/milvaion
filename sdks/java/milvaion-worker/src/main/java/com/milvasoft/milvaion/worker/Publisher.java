package com.milvasoft.milvaion.worker;

import java.time.Duration;
import java.util.LinkedHashMap;
import java.util.Map;

/** Publishing side of the broker, with publisher confirms. */
interface Publisher {
    void publish(String exchange, String routingKey, byte[] body, boolean persistent, String correlationId, Map<String, Object> headers) throws Exception;

    default void publish(String exchange, String routingKey, byte[] body, boolean persistent) throws Exception {
        publish(exchange, routingKey, body, persistent, null, null);
    }
}

/** One message taken from a job queue (RabbitMQ) or job topic (Kafka). */
record Delivery(byte[] body, Map<String, Object> headers, String correlationId, boolean redelivered, Settler settler) {
    interface Settler {
        /** The outcome has been reported: the message is done. */
        void ack() throws Exception;

        /** {@code requeue}: give the message back so it runs again. Otherwise reject it to the dead letter queue as is. */
        void nack(boolean requeue) throws Exception;

        /** Rejects a message that cannot be processed, sending it to the dead letter queue unchanged. */
        default void reject(String reason) throws Exception {
            nack(false);
        }
    }
}

/**
 * What the job processor sends to the broker. RabbitMQ and Kafka implement it differently, so the processor
 * itself does not depend on the transport.
 */
interface JobTransport {
    /** Publishes a status update of an occurrence. Returns once the broker has accepted it. */
    void publishStatus(String occurrenceId, byte[] body) throws Exception;

    /**
     * True when the processor waits out the retry delay before republishing, holding the message (RabbitMQ).
     * False when the retry is published at once and the broker side holds it until due (Kafka's retry topic).
     */
    boolean waitsForRetryDelay();

    /** Publishes the job again for attempt {@code retryCount}. Returns once the broker has accepted it. */
    void publishRetry(Delivery delivery, ResolvedJob consumer, String jobId, String occurrenceId, int retryCount, int maxRetries, Duration delay) throws Exception;

    /** Publishes the dead letter message of a failed occurrence. Returns once the broker has accepted it. */
    void publishDeadLetter(String jobId, String occurrenceId, byte[] body, int retryCount, int maxRetries) throws Exception;

    /** The RabbitMQ side, over a confirming publisher. */
    static JobTransport rabbitMq(Publisher publisher) {
        return new JobTransport() {
            @Override
            public void publishStatus(String occurrenceId, byte[] body) throws Exception {
                publisher.publish("", Protocol.QUEUE_STATUS_UPDATES, body, true);
            }

            @Override
            public boolean waitsForRetryDelay() {
                return true;
            }

            @Override
            public void publishRetry(Delivery delivery, ResolvedJob consumer, String jobId, String occurrenceId, int retryCount, int maxRetries, Duration delay) throws Exception {
                publisher.publish("", consumer.queue(), delivery.body(), true, occurrenceId, headers(occurrenceId, retryCount, maxRetries));
            }

            @Override
            public void publishDeadLetter(String jobId, String occurrenceId, byte[] body, int retryCount, int maxRetries) throws Exception {
                publisher.publish(Protocol.EXCHANGE_DEAD_LETTER, Protocol.DEAD_LETTER_ROUTING_KEY, body, true, occurrenceId,
                        headers(occurrenceId, retryCount, maxRetries));
            }

            private Map<String, Object> headers(String occurrenceId, int retryCount, int maxRetries) {
                Map<String, Object> headers = new LinkedHashMap<>();
                headers.put("CorrelationId", occurrenceId);
                headers.put("x-retry-count", retryCount);
                headers.put("MaxRetries", maxRetries);
                return headers;
            }
        };
    }
}
