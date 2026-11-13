package com.milvasoft.milvaion.worker;

import java.util.Locale;

/** The message broker a worker talks to the API over. The API and all workers of a deployment must use the same one. */
public enum TransportType {
    RABBITMQ,
    KAFKA;

    /** Accepts "rabbitmq", "RabbitMQ", "kafka" and so on. */
    public static TransportType parse(String value) {
        String normalized = value == null ? "" : value.trim().toLowerCase(Locale.ROOT).replace("_", "").replace("-", "");

        return switch (normalized) {
            case "", "rabbitmq", "rabbit", "amqp" -> RABBITMQ;
            case "kafka" -> KAFKA;
            default -> throw new IllegalArgumentException("transport must be \"rabbitmq\" or \"kafka\", got '" + value + "'");
        };
    }
}
