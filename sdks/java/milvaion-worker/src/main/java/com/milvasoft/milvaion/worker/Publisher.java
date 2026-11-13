package com.milvasoft.milvaion.worker;

import java.util.Map;

/** Publishing side of the broker, with publisher confirms. */
interface Publisher {
    void publish(String exchange, String routingKey, byte[] body, boolean persistent, String correlationId, Map<String, Object> headers) throws Exception;

    default void publish(String exchange, String routingKey, byte[] body, boolean persistent) throws Exception {
        publish(exchange, routingKey, body, persistent, null, null);
    }
}

/** One message taken from a job queue. */
record Delivery(byte[] body, Map<String, Object> headers, String correlationId, boolean redelivered, Settler settler) {
    interface Settler {
        void ack() throws Exception;

        void nack(boolean requeue) throws Exception;
    }
}
