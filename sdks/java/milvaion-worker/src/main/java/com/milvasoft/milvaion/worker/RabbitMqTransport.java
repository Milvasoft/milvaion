package com.milvasoft.milvaion.worker;

import com.rabbitmq.client.AMQP;
import com.rabbitmq.client.Channel;
import com.rabbitmq.client.Connection;
import com.rabbitmq.client.ConnectionFactory;
import com.rabbitmq.client.Recoverable;
import com.rabbitmq.client.RecoveryListener;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** The RabbitMQ transport of the worker protocol: job queues bound to {@code jobs.topic}, publisher confirms. */
final class RabbitMqTransport implements WorkerTransport {
    private static final Logger LOG = LoggerFactory.getLogger(RabbitMqTransport.class);

    private final WorkerOptions options;
    private final AmqpPublisher publisher = new AmqpPublisher();
    private final JobTransport jobTransport = JobTransport.rabbitMq(publisher);
    private final List<Consumer> consumers = new ArrayList<>();
    private Connection connection;

    private record Consumer(Channel channel, String tag, ExecutorService executor) {
    }

    RabbitMqTransport(WorkerOptions options) {
        this.options = options;
    }

    @Override
    public String name() {
        return "RabbitMQ";
    }

    @Override
    public JobTransport jobs() {
        return jobTransport;
    }

    @Override
    public void start(Map<String, ResolvedJob> jobs, JobProcessor processor, Hooks hooks) throws Exception {
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
                hooks.reregister();
            }

            @Override
            public void handleRecoveryStarted(Recoverable recoverable) {
                LOG.warn("Lost the RabbitMQ connection. Reconnecting...");
            }
        });

        setup(jobs, processor);
        hooks.register();
        LOG.info("Connected to RabbitMQ.");
    }

    /** Declares the topology and starts the consumers. Automatic recovery restores both after a reconnect. */
    private void setup(Map<String, ResolvedJob> jobs, JobProcessor processor) throws Exception {
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

    @Override
    public void publishRegistration(byte[] body) throws Exception {
        publisher.publish("", Protocol.QUEUE_WORKER_REGISTRATION, body, true);
    }

    @Override
    public void publishHeartbeat(byte[] body) throws Exception {
        publisher.publish("", Protocol.QUEUE_WORKER_HEARTBEAT, body, false);
    }

    @Override
    public void publishLogs(String key, byte[] body) throws Exception {
        publisher.publish("", Protocol.QUEUE_WORKER_LOGS, body, true);
    }

    @Override
    public void stopConsuming() {
        for (Consumer consumer : consumers) {
            try {
                synchronized (consumer.channel()) {
                    consumer.channel().basicCancel(consumer.tag());
                }
            } catch (Exception e) {
                // Channel already closed
            }
        }
    }

    @Override
    public void close() {
        publisher.detach();

        if (connection != null) {
            try {
                connection.close(5000);
            } catch (Exception e) {
                // Already closed
            }
        }

        consumers.forEach(consumer -> consumer.executor().shutdownNow());
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
