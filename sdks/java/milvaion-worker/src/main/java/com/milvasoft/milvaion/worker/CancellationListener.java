package com.milvasoft.milvaion.worker;

import com.fasterxml.jackson.databind.JsonNode;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import redis.clients.jedis.Jedis;
import redis.clients.jedis.JedisPubSub;

import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/** Receives the cancellation requests the API publishes on Redis, reconnecting when Redis goes away. */
final class CancellationListener implements AutoCloseable {
    private static final Logger LOG = LoggerFactory.getLogger(CancellationListener.class);

    private final URI uri;
    private final String channel;
    private final CancellationRegistry registry;
    private final CountDownLatch subscribed = new CountDownLatch(1);
    private volatile boolean closed;
    private volatile JedisPubSub pubSub;
    private Thread thread;

    CancellationListener(String uri, String channel, CancellationRegistry registry) {
        this.uri = URI.create(uri);
        this.channel = channel;
        this.registry = registry;
    }

    /** Connects and subscribes; throws if that does not happen within the timeout. */
    void start(Duration timeout) throws Exception {
        try (Jedis probe = new Jedis(uri)) {
            probe.ping();
        }

        thread = LogBuffer.daemon(this::listen, "milvaion-cancellation");
        thread.start();

        if (!subscribed.await(timeout.toMillis(), TimeUnit.MILLISECONDS))
            throw new IllegalStateException("Could not subscribe to " + channel + " within " + timeout);

        LOG.info("Listening for cancellations on {}", channel);
    }

    private void listen() {
        while (!closed) {
            try (Jedis jedis = new Jedis(uri)) {
                JedisPubSub listener = new JedisPubSub() {
                    @Override
                    public void onSubscribe(String name, int count) {
                        subscribed.countDown();
                    }

                    @Override
                    public void onMessage(String name, String message) {
                        handle(message);
                    }
                };

                pubSub = listener;
                jedis.subscribe(listener, channel); // blocks until unsubscribed or disconnected
            } catch (Exception e) {
                if (closed) return;

                LOG.warn("Redis cancellation listener error: {}. Reconnecting...", e.getMessage());

                try {
                    Thread.sleep(2000);
                } catch (InterruptedException interrupted) {
                    return;
                }
            }
        }
    }

    void handle(String message) {
        JsonNode request = Json.parse(message.getBytes(StandardCharsets.UTF_8));

        if (request == null || !request.isObject()) {
            LOG.debug("Ignored a cancellation message that is not JSON.");
            return;
        }

        String occurrenceId = request.hasNonNull("CorrelationId") ? request.get("CorrelationId").asText() : request.path("OccurrenceId").asText(null);
        String reason = request.hasNonNull("Reason") ? request.get("Reason").asText() : null;

        if (occurrenceId != null && registry.cancel(occurrenceId, reason)) LOG.info("Cancelling occurrence {}: {}", occurrenceId, reason);
    }

    @Override
    public void close() {
        closed = true;

        JedisPubSub current = pubSub;

        if (current != null && current.isSubscribed()) {
            try {
                current.unsubscribe();
            } catch (Exception e) {
                // Already disconnected
            }
        }

        if (thread != null) thread.interrupt();
    }
}
