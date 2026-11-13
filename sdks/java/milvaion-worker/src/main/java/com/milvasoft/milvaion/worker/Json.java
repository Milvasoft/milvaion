package com.milvasoft.milvaion.worker;

import com.fasterxml.jackson.annotation.JsonInclude;
import com.fasterxml.jackson.core.JsonProcessingException;
import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ObjectNode;

import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.time.temporal.ChronoUnit;

final class Json {
    static final ObjectMapper MAPPER = new ObjectMapper()
            .configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false)
            .setSerializationInclusion(JsonInclude.Include.ALWAYS);

    private Json() {
    }

    static ObjectNode object() {
        return MAPPER.createObjectNode();
    }

    static byte[] bytes(Object value) {
        try {
            return MAPPER.writeValueAsBytes(value);
        } catch (JsonProcessingException e) {
            throw new IllegalStateException(e);
        }
    }

    static String string(Object value) {
        return new String(bytes(value), StandardCharsets.UTF_8);
    }

    static JsonNode parse(byte[] body) {
        try {
            return MAPPER.readTree(body);
        } catch (Exception e) {
            return null;
        }
    }

    static String schema(Object schema) {
        if (schema == null) return null;
        if (schema instanceof String s) return s;

        return string(schema);
    }

    static String now() {
        return Instant.now().truncatedTo(ChronoUnit.MILLIS).toString();
    }

    static String time(Instant instant) {
        return instant == null ? null : instant.truncatedTo(ChronoUnit.MILLIS).toString();
    }
}
