package com.milvasoft.milvaion.worker;

import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.locks.ReentrantLock;

/**
 * Batches occurrence logs. A batch that fails to send is kept and sent first next time, so logs
 * keep their order.
 */
final class LogBuffer implements AutoCloseable {
    interface Sender {
        void send(ObjectNode batch) throws Exception;
    }

    private static final Logger LOG = LoggerFactory.getLogger(LogBuffer.class);

    private final Sender sender;
    final int batchSize;
    final long flushIntervalMs;
    final int maxBuffered;
    private final ScheduledExecutorService timer = Executors.newSingleThreadScheduledExecutor(r -> daemon(r, "milvaion-log-flush"));
    private final List<ObjectNode> buffer = new ArrayList<>();
    private final ReentrantLock flushLock = new ReentrantLock();
    private List<ObjectNode> unsent;
    private ScheduledFuture<?> scheduled;
    private long dropped;
    private volatile boolean closed;

    LogBuffer(Sender sender) {
        this(sender, 50, 500, 10_000);
    }

    LogBuffer(Sender sender, int batchSize, long flushIntervalMs, int maxBuffered) {
        this.sender = sender;
        this.batchSize = batchSize;
        this.flushIntervalMs = flushIntervalMs;
        this.maxBuffered = maxBuffered;
    }

    void add(String occurrenceId, String instanceId, ObjectNode log) {
        ObjectNode message = Json.object();
        message.put("occurrenceId", occurrenceId);
        message.put("workerId", instanceId);
        message.set("log", log);
        message.put("messageTimestamp", Json.now());

        boolean full;

        synchronized (buffer) {
            if (buffer.size() >= maxBuffered) {
                buffer.remove(0);

                if (++dropped % 1000 == 1) LOG.warn("Log buffer is full; dropped {} oldest log(s) so far.", dropped);
            }

            buffer.add(message);
            full = buffer.size() >= batchSize;

            if (!full && scheduled == null && !closed) scheduled = timer.schedule(this::onTimer, flushIntervalMs, TimeUnit.MILLISECONDS);
        }

        if (full && !closed) timer.execute(this::flushQuietly);
    }

    private void onTimer() {
        synchronized (buffer) {
            scheduled = null;
        }

        try {
            flush();
        } catch (Exception e) {
            LOG.debug("Failed to send logs; will retry: {}", e.getMessage());

            synchronized (buffer) {
                if (scheduled == null && !closed) scheduled = timer.schedule(this::onTimer, flushIntervalMs, TimeUnit.MILLISECONDS);
            }
        }
    }

    private void flushQuietly() {
        try {
            flush();
        } catch (Exception e) {
            LOG.debug("Failed to send logs; the timer will retry: {}", e.getMessage());
        }
    }

    int pending() {
        synchronized (buffer) {
            return buffer.size() + (unsent == null ? 0 : unsent.size());
        }
    }

    /** Sends everything buffered so far. Throws if a batch cannot be sent; it stays buffered. */
    void flush() throws Exception {
        flushLock.lock();

        try {
            int perRound = batchSize * 2;
            int rounds = (pending() + perRound - 1) / perRound + 1;

            for (; rounds > 0; rounds--) {
                List<ObjectNode> batch = unsent;

                if (batch == null) {
                    synchronized (buffer) {
                        int n = Math.min(buffer.size(), perRound);
                        batch = new ArrayList<>(buffer.subList(0, n));
                        buffer.subList(0, n).clear();
                    }
                }

                if (batch.isEmpty()) return;

                unsent = batch;

                ObjectNode message = Json.object();
                ArrayNode logs = message.putArray("logs");
                batch.forEach(logs::add);
                message.put("batchTimestamp", Json.now());
                message.put("count", batch.size());

                sender.send(message);
                unsent = null;
            }
        } finally {
            flushLock.unlock();
        }
    }

    @Override
    public void close() {
        closed = true;
        timer.shutdownNow();
    }

    static Thread daemon(Runnable runnable, String name) {
        Thread thread = new Thread(runnable, name);
        thread.setDaemon(true);
        return thread;
    }
}
