//! Runs jobs end to end against real RabbitMQ and Redis, playing the part of the Milvaion API.
//!
//! The brokers come from `MILVAION_TEST_RABBITMQ_URL` and `MILVAION_TEST_REDIS_URL` when both are
//! set. Otherwise the test starts them with Docker, and is skipped when Docker is not available,
//! unless `MILVAION_TEST_REQUIRE_BROKERS=1` makes that a failure (CI sets it).

use lapin::options::{BasicGetOptions, BasicPublishOptions, QueueDeclareOptions};
use lapin::types::{AMQPValue, FieldTable, LongString, ShortString};
use lapin::{BasicProperties, Channel, Connection, ConnectionProperties};
use milvaion_worker::{JobContext, JobError, JobOptions, Worker, WorkerOptions, SDK_VERSION};
use serde_json::{json, Value};
use std::process::Command;
use std::time::{Duration, Instant};

const WORKER_ID: &str = "it-worker";

struct Container(String);

impl Drop for Container {
    fn drop(&mut self) {
        let _ = Command::new("docker").args(["rm", "-f", &self.0]).output();
    }
}

fn docker(args: &[&str]) -> Option<String> {
    let output = Command::new("docker").args(args).output().ok()?;
    output.status.success().then(|| String::from_utf8_lossy(&output.stdout).trim().to_string())
}

/// Starts a container and returns it with the host address of `port`.
fn start(image: &str, port: u16) -> Option<(Container, String)> {
    let id = docker(&["run", "-d", "-p", &format!("127.0.0.1::{port}"), image])?;
    let container = Container(id);
    let mapped = docker(&["port", &container.0, &format!("{port}/tcp")])?;
    let address = mapped.lines().next()?.trim().to_string();
    Some((container, address))
}

struct Brokers {
    rabbitmq_url: String,
    redis_url: String,
    rabbit_container: Option<Container>,
    redis_container: Option<Container>,
}

fn brokers() -> Option<Brokers> {
    if let (Ok(rabbitmq_url), Ok(redis_url)) =
        (std::env::var("MILVAION_TEST_RABBITMQ_URL"), std::env::var("MILVAION_TEST_REDIS_URL"))
    {
        return Some(Brokers { rabbitmq_url, redis_url, rabbit_container: None, redis_container: None });
    }

    let required = std::env::var("MILVAION_TEST_REQUIRE_BROKERS").is_ok_and(|v| v == "1");

    if docker(&["version", "--format", "{{.Server.Version}}"]).is_none() {
        assert!(!required, "Docker is not available and MILVAION_TEST_REQUIRE_BROKERS=1");
        eprintln!("skipping the integration test: Docker is not available");
        return None;
    }

    let (rabbit, rabbit_address) = start("rabbitmq:4-alpine", 5672).expect("could not start RabbitMQ");
    let (redis, redis_address) = start("redis:7-alpine", 6379).expect("could not start Redis");

    Some(Brokers {
        rabbitmq_url: format!("amqp://guest:guest@{rabbit_address}/%2f"),
        redis_url: format!("redis://{redis_address}"),
        rabbit_container: Some(rabbit),
        redis_container: Some(redis),
    })
}

async fn connect(url: &str, timeout: Duration) -> Connection {
    let deadline = Instant::now() + timeout;

    loop {
        match Connection::connect(url, ConnectionProperties::default()).await {
            Ok(connection) => return connection,
            Err(error) if Instant::now() > deadline => panic!("RabbitMQ did not come up: {error}"),
            Err(_) => tokio::time::sleep(Duration::from_millis(500)).await,
        }
    }
}

/// The API's side of the broker.
struct Api {
    url: String,
    connection: Connection,
    channel: Channel,
}

impl Api {
    async fn new(url: &str) -> Self {
        let connection = connect(url, Duration::from_secs(90)).await;
        let channel = connection.create_channel().await.unwrap();

        for queue in [
            "worker_registration_queue",
            "job_status_updates_queue",
            "worker_logs_queue",
            "worker_heartbeat_queue",
            "failed_jobs_queue",
        ] {
            channel
                .queue_declare(
                    ShortString::from(queue),
                    QueueDeclareOptions { durable: true, ..Default::default() },
                    FieldTable::default(),
                )
                .await
                .unwrap();
        }

        Self { url: url.to_string(), connection, channel }
    }

    async fn reconnect(&mut self) {
        *self = Api::new(&self.url).await;
    }

    /// Takes messages from `queue` until one matches, discarding the others.
    async fn wait_for(&self, queue: &str, what: &str, matches: impl Fn(&Value) -> bool) -> Value {
        let deadline = Instant::now() + Duration::from_secs(30);

        loop {
            let message =
                self.channel.basic_get(ShortString::from(queue), BasicGetOptions { no_ack: true }).await.unwrap();

            match message {
                Some(message) => {
                    let body: Value = serde_json::from_slice(&message.delivery.data).unwrap();

                    if matches(&body) {
                        return body;
                    }
                }
                None if Instant::now() > deadline => panic!("timed out waiting for {what} on {queue}"),
                None => tokio::time::sleep(Duration::from_millis(50)).await,
            }
        }
    }

    async fn wait_for_status(&self, occurrence: &str, status: u8) -> Value {
        self.wait_for("job_status_updates_queue", &format!("status {status} of {occurrence}"), |s| {
            s["occurrenceId"] == occurrence && s["status"] == status
        })
        .await
    }

    /// Dispatches a job the way the API does.
    async fn dispatch(&self, job_name: &str, routing_key: &str, data: Value) -> String {
        let occurrence = uuid::Uuid::new_v4().to_string();
        let body = json!({
            "id": uuid::Uuid::new_v4().to_string(),
            "displayName": job_name,
            "jobNameInWorker": job_name,
            "jobData": data.to_string(),
            "executeAt": "2026-11-13T09:00:00Z",
            "workerId": WORKER_ID,
            "version": 1
        });

        let mut headers = FieldTable::default();
        headers.insert(ShortString::from("CorrelationId"), AMQPValue::LongString(LongString::from(occurrence.clone())));

        self.channel
            .basic_publish(
                ShortString::from("jobs.topic"),
                ShortString::from(routing_key),
                BasicPublishOptions::default(),
                &serde_json::to_vec(&body).unwrap(),
                BasicProperties::default()
                    .with_delivery_mode(2)
                    .with_correlation_id(ShortString::from(occurrence.clone()))
                    .with_headers(headers),
            )
            .await
            .unwrap()
            .await
            .unwrap();

        occurrence
    }
}

/// Publishes a cancellation until a subscriber receives it.
async fn cancel(redis_url: &str, occurrence: &str) {
    let client = redis::Client::open(redis_url).unwrap();
    let mut connection = client.get_multiplexed_async_connection().await.unwrap();
    let message =
        json!({"CorrelationId": occurrence, "JobId": "x", "OccurrenceId": occurrence, "Reason": "Cancelled by test"})
            .to_string();
    let deadline = Instant::now() + Duration::from_secs(30);

    loop {
        let receivers: i64 = redis::cmd("PUBLISH")
            .arg("Milvaion:JobScheduler:cancellation_channel")
            .arg(&message)
            .query_async(&mut connection)
            .await
            .unwrap();

        if receivers > 0 {
            return;
        }

        assert!(Instant::now() < deadline, "the worker never resubscribed to cancellations");
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn runs_jobs_end_to_end() {
    let _ = tracing_subscriber::fmt().with_env_filter("milvaion_worker=debug").with_test_writer().try_init();

    let Some(brokers) = brokers() else { return };
    let mut api = Api::new(&brokers.rabbitmq_url).await;

    let mut options = WorkerOptions::new(WORKER_ID);
    options.rabbitmq_url = brokers.rabbitmq_url.clone();
    options.redis_url = Some(brokers.redis_url.clone());
    options.heartbeat_interval = Duration::from_secs(1);
    options.startup_timeout = Duration::from_secs(60);
    options.shutdown_timeout = Duration::from_secs(5);

    let mut worker = Worker::new(options).unwrap();

    worker
        .handle_with(
            "EchoJob",
            JobOptions::new().data_schema(json!({"type": "object", "properties": {"text": {"type": "string"}}})),
            |ctx: JobContext| async move {
                #[derive(serde::Deserialize)]
                struct Data {
                    text: String,
                }
                let data: Data = ctx.data()?;
                ctx.info(format!("echoing {}", data.text));
                Ok::<_, JobError>(json!({"echo": data.text}))
            },
        )
        .unwrap()
        .handle("WaitJob", |ctx: JobContext| async move {
            ctx.info("waiting to be cancelled");
            ctx.cancelled().await;
            Ok::<_, JobError>(())
        })
        .unwrap()
        .handle_with(
            "BrokenJob",
            JobOptions::new().max_retries(1).base_retry_delay(Duration::from_millis(100)),
            |ctx: JobContext| async move {
                if ctx.retry_count() == 0 {
                    Err::<(), _>(JobError::new("first attempt fails"))
                } else {
                    Err(JobError::permanent("bad input"))
                }
            },
        )
        .unwrap();

    let (stop_tx, stop_rx) = tokio::sync::oneshot::channel::<()>();
    let run = tokio::spawn(worker.run(async {
        let _ = stop_rx.await;
    }));

    // Registration
    let registration = api.wait_for("worker_registration_queue", "registration", |r| r["workerId"] == WORKER_ID).await;
    assert_eq!(registration["routingPatterns"]["EchoJob"], "it-worker.echo.*");
    assert!(registration["jobDataDefinitions"]["EchoJob"].as_str().unwrap().contains("\"text\""));
    let metadata: Value = serde_json::from_str(registration["metadata"].as_str().unwrap()).unwrap();
    assert_eq!(metadata["Sdk"], "rust");
    assert_eq!(metadata["SdkVersion"], SDK_VERSION);

    // Success, with logs
    let occurrence = api.dispatch("EchoJob", "it-worker.echo.job", json!({"text": "hello"})).await;
    api.wait_for_status(&occurrence, 1).await;
    let completed = api.wait_for_status(&occurrence, 2).await;
    assert_eq!(completed["result"], r#"{"echo":"hello"}"#);
    let log = api
        .wait_for("worker_logs_queue", "the job's log", |batch| {
            batch["logs"].as_array().is_some_and(|logs| {
                logs.iter().any(|l| l["occurrenceId"] == occurrence && l["log"]["message"] == "echoing hello")
            })
        })
        .await;
    assert!(log["count"].as_u64().unwrap() >= 1);

    // Retry, then a permanent failure into the DLQ
    let occurrence = api.dispatch("BrokenJob", "it-worker.broken.job", json!({})).await;
    let failed = api.wait_for_status(&occurrence, 3).await;
    assert!(failed["exception"].as_str().unwrap().contains("bad input"));
    let dead = api.wait_for("failed_jobs_queue", "the dead letter", |m| m["jobNameInWorker"] == "BrokenJob").await;
    assert!(dead["exception"].as_str().unwrap().starts_with("[PERMANENT FAILURE] "));

    // Cancellation through Redis
    let occurrence = api.dispatch("WaitJob", "it-worker.wait.job", json!({})).await;
    api.wait_for_status(&occurrence, 1).await;
    cancel(&brokers.redis_url, &occurrence).await;
    api.wait_for_status(&occurrence, 4).await;

    // Both connections drop; the worker reconnects, registers again and keeps working.
    let rabbit = brokers.rabbit_container.as_ref().map(|c| c.0.clone());
    let redis = brokers.redis_container.as_ref().map(|c| c.0.clone());

    if let (Some(rabbit), Some(redis)) = (rabbit, redis) {
        docker(&["exec", &rabbit, "rabbitmqctl", "close_all_connections", "integration test"])
            .expect("closing connections");
        docker(&["exec", &redis, "redis-cli", "CLIENT", "KILL", "TYPE", "pubsub"]).expect("killing Redis subscribers");

        api.reconnect().await;
        api.wait_for("worker_registration_queue", "re-registration", |r| r["workerId"] == WORKER_ID).await;

        let occurrence = api.dispatch("EchoJob", "it-worker.echo.job", json!({"text": "again"})).await;
        api.wait_for_status(&occurrence, 2).await;

        let occurrence = api.dispatch("WaitJob", "it-worker.wait.job", json!({})).await;
        api.wait_for_status(&occurrence, 1).await;
        cancel(&brokers.redis_url, &occurrence).await;
        api.wait_for_status(&occurrence, 4).await;
    }

    // Graceful shutdown tells the API the instance has left.
    stop_tx.send(()).unwrap();
    tokio::time::timeout(Duration::from_secs(30), run).await.expect("worker stopped").unwrap().unwrap();
    api.wait_for("worker_heartbeat_queue", "the stopping heartbeat", |h| {
        h["isStopping"] == true && h["currentJobs"] == 0
    })
    .await;

    let _ = api.connection.close(200, ShortString::from("done")).await;
}
