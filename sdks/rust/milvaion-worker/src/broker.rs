//! The broker as the processor sees it: a publisher with confirms, and deliveries to settle.
//! Keeping lapin and rdkafka behind these traits lets the processor be tested without a broker.
//! Destinations are named the RabbitMQ way (exchange and routing key); the Kafka transport maps
//! them to its topics.

use std::collections::HashMap;
use std::future::Future;
use std::pin::Pin;

pub(crate) type BoxFuture<'a, T> = Pin<Box<dyn Future<Output = T> + Send + 'a>>;

/// A header value, as read from a delivery or written to a message.
#[derive(Debug, Clone, PartialEq)]
pub(crate) enum HeaderValue {
    Str(String),
    Bytes(Vec<u8>),
    Int(i64),
    Other,
}

#[derive(Debug, Clone, Default)]
pub(crate) struct PublishOptions {
    pub persistent: bool,
    pub correlation_id: Option<String>,
    pub headers: Vec<(String, HeaderValue)>,
    /// The Kafka record key. RabbitMQ ignores it.
    #[cfg_attr(not(feature = "kafka"), allow(dead_code))]
    pub key: Option<String>,
}

/// The next attempt of a failed job, for a broker that schedules retries itself (Kafka).
#[derive(Debug, Clone)]
#[cfg_attr(not(feature = "kafka"), allow(dead_code))]
pub(crate) struct RetryRequest {
    pub job_id: String,
    pub occurrence_id: String,
    /// Retry count of the next attempt.
    pub retry_count: u32,
    pub max_retries: u32,
    /// Unix time in milliseconds before which the next attempt must not run.
    pub not_before_ms: i64,
}

/// Publishes a message and waits for the broker to confirm it.
pub(crate) trait Publisher: Send + Sync + 'static {
    fn publish<'a>(
        &'a self,
        exchange: &'a str,
        routing_key: &'a str,
        body: Vec<u8>,
        options: PublishOptions,
    ) -> BoxFuture<'a, Result<(), String>>;
}

/// Acknowledges or rejects one delivery.
pub(crate) trait Settler: Send + Sync + 'static {
    fn ack(&self) -> BoxFuture<'_, Result<(), String>>;
    /// `nack(true)` returns the message to its queue so it runs again; `nack(false)` dead-letters it.
    fn nack(&self, requeue: bool) -> BoxFuture<'_, Result<(), String>>;

    /// Dead-letters a message that cannot be processed and says why.
    fn reject(&self, _reason: String) -> BoxFuture<'_, Result<(), String>> {
        self.nack(false)
    }

    /// Hands a failed job to the broker for a delayed retry, instead of waiting in process. `None`
    /// means the broker does not schedule retries (RabbitMQ). The future completes once the broker
    /// has accepted the retry.
    fn schedule_retry(&self, _retry: RetryRequest) -> Option<BoxFuture<'_, Result<(), String>>> {
        None
    }
}

/// One message taken from a job queue.
pub(crate) struct Delivery {
    pub body: Vec<u8>,
    pub headers: HashMap<String, HeaderValue>,
    pub correlation_id: Option<String>,
    pub redelivered: bool,
    pub settler: Box<dyn Settler>,
}

pub(crate) mod amqp {
    use super::*;
    use lapin::options::{BasicAckOptions, BasicNackOptions, BasicPublishOptions};
    use lapin::types::{AMQPValue, FieldTable, LongString, ShortString};
    use lapin::{Acker, BasicProperties, Channel};
    use std::sync::RwLock;

    /// Publishes on the current confirm channel. Detached while RabbitMQ is unreachable.
    #[derive(Default)]
    pub(crate) struct AmqpPublisher {
        channel: RwLock<Option<Channel>>,
    }

    impl AmqpPublisher {
        pub fn attach(&self, channel: Channel) {
            *self.channel.write().unwrap() = Some(channel);
        }

        pub fn detach(&self) {
            *self.channel.write().unwrap() = None;
        }
    }

    impl Publisher for AmqpPublisher {
        fn publish<'a>(
            &'a self,
            exchange: &'a str,
            routing_key: &'a str,
            body: Vec<u8>,
            options: PublishOptions,
        ) -> BoxFuture<'a, Result<(), String>> {
            Box::pin(async move {
                let channel = self.channel.read().unwrap().clone().ok_or("not connected to RabbitMQ")?;

                let mut properties = BasicProperties::default()
                    .with_content_type(ShortString::from("application/json"))
                    .with_delivery_mode(if options.persistent { 2 } else { 1 });

                if let Some(id) = options.correlation_id {
                    properties = properties.with_correlation_id(ShortString::from(id));
                }

                if !options.headers.is_empty() {
                    let mut table = FieldTable::default();

                    for (key, value) in options.headers {
                        let value = match value {
                            HeaderValue::Str(s) => AMQPValue::LongString(LongString::from(s)),
                            HeaderValue::Bytes(b) => AMQPValue::LongString(LongString::from(b)),
                            HeaderValue::Int(i) => AMQPValue::LongInt(i as i32),
                            HeaderValue::Other => continue,
                        };
                        table.insert(ShortString::from(key), value);
                    }

                    properties = properties.with_headers(table);
                }

                let confirm = channel
                    .basic_publish(
                        ShortString::from(exchange),
                        ShortString::from(routing_key),
                        BasicPublishOptions::default(),
                        &body,
                        properties,
                    )
                    .await
                    .map_err(|e| e.to_string())?
                    .await
                    .map_err(|e| e.to_string())?;

                if confirm.is_nack() {
                    return Err("RabbitMQ rejected the message".to_string());
                }

                Ok(())
            })
        }
    }

    pub(crate) struct AmqpSettler(pub Acker);

    impl Settler for AmqpSettler {
        fn ack(&self) -> BoxFuture<'_, Result<(), String>> {
            Box::pin(async move { self.0.ack(BasicAckOptions::default()).await.map(|_| ()).map_err(|e| e.to_string()) })
        }

        fn nack(&self, requeue: bool) -> BoxFuture<'_, Result<(), String>> {
            Box::pin(async move {
                self.0
                    .nack(BasicNackOptions { requeue, ..Default::default() })
                    .await
                    .map(|_| ())
                    .map_err(|e| e.to_string())
            })
        }
    }

    pub(crate) fn delivery(d: lapin::message::Delivery) -> Delivery {
        let mut headers = HashMap::new();

        if let Some(table) = d.properties.headers() {
            for (key, value) in table.inner() {
                headers.insert(key.as_str().to_string(), header_value(value));
            }
        }

        Delivery {
            body: d.data,
            headers,
            correlation_id: d.properties.correlation_id().as_ref().map(|c| c.as_str().to_string()),
            redelivered: d.redelivered,
            settler: Box::new(AmqpSettler(d.acker)),
        }
    }

    fn header_value(value: &AMQPValue) -> HeaderValue {
        match value {
            AMQPValue::LongString(s) => HeaderValue::Bytes(s.as_bytes().to_vec()),
            AMQPValue::ShortString(s) => HeaderValue::Str(s.as_str().to_string()),
            AMQPValue::ByteArray(b) => HeaderValue::Bytes(b.as_slice().to_vec()),
            AMQPValue::ShortShortInt(i) => HeaderValue::Int(*i as i64),
            AMQPValue::ShortShortUInt(i) => HeaderValue::Int(*i as i64),
            AMQPValue::ShortInt(i) => HeaderValue::Int(*i as i64),
            AMQPValue::ShortUInt(i) => HeaderValue::Int(*i as i64),
            AMQPValue::LongInt(i) => HeaderValue::Int(*i as i64),
            AMQPValue::LongUInt(i) => HeaderValue::Int(*i as i64),
            AMQPValue::LongLongInt(i) => HeaderValue::Int(*i),
            _ => HeaderValue::Other,
        }
    }
}
