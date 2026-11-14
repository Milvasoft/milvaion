//! Runs jobs end to end against a real Kafka, playing the part of the Milvaion API. Needs the
//! `kafka` feature and runs only when `MILVAION_KAFKA_BROKERS` is set, for example:
//!
//! ```text
//! docker run -d --name milvaion-kafka -p 29092:9092 \
//!   -e KAFKA_NODE_ID=1 -e KAFKA_PROCESS_ROLES=broker,controller \
//!   -e KAFKA_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093 \
//!   -e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://localhost:29092 \
//!   -e KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER \
//!   -e KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT \
//!   -e KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:9093 \
//!   -e KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 -e KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0 \
//!   apache/kafka:3.9.0
//! MILVAION_KAFKA_BROKERS=localhost:29092 cargo test --features kafka --test kafka
//! ```
//!
//! `MILVAION_TEST_REQUIRE_BROKERS=1` (CI sets it) turns a missing `MILVAION_KAFKA_BROKERS` into a failure.
//! Every test uses its own topic prefix, so the tests do not see each other's records.

#![cfg(feature = "kafka")]

use milvaion_worker::{JobContext, JobError, JobOptions, Transport, Worker, WorkerOptions, SDK_VERSION};
use rdkafka::config::ClientConfig;
use rdkafka::consumer::{BaseConsumer, Consumer};
use rdkafka::message::{Header, Headers, Message, OwnedHeaders};
use rdkafka::producer::{FutureProducer, FutureRecord};
use rdkafka::{Offset, TopicPartitionList};
use serde_json::{json, Value};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const WORKER_ID: &str = "Rust-Kafka-Worker";

fn brokers() -> Option<String> {
    match std::env::var("MILVAION_KAFKA_BROKERS").map(|b| b.trim().to_string()) {
        Ok(brokers) if !brokers.is_empty() => Some(brokers),
        _ => {
            let required = std::env::var("MILVAION_TEST_REQUIRE_BROKERS").is_ok_and(|v| v == "1");
            assert!(!required, "MILVAION_TEST_REQUIRE_BROKERS=1 but MILVAION_KAFKA_BROKERS is not set");
            eprintln!("skipping the Kafka integration test: set MILVAION_KAFKA_BROKERS (e.g. localhost:29092)");
            None
        }
    }
}

fn guid() -> String {
    uuid::Uuid::new_v4().to_string()
}

#[derive(Debug, Clone)]
struct Rec {
    partition: i32,
    offset: i64,
    key: Option<String>,
    value: Vec<u8>,
    headers: Vec<(String, String)>,
    timestamp: i64,
}

impl Rec {
    fn header(&self, key: &str) -> Option<&str> {
        self.headers.iter().rev().find(|(k, _)| k == key).map(|(_, v)| v.as_str())
    }

    fn json(&self) -> Value {
        serde_json::from_slice(&self.value).unwrap_or(Value::Null)
    }
}

struct Fixture {
    brokers: String,
    prefix: String,
    instance_id: String,
    producer: FutureProducer,
    stop: Option<tokio::sync::oneshot::Sender<()>>,
    run: Option<tokio::task::JoinHandle<Result<(), milvaion_worker::WorkerError>>>,
}

impl Fixture {
    async fn start<F, Fut>(
        job_options: JobOptions,
        configure: impl FnOnce(&mut WorkerOptions),
        handler: F,
    ) -> Option<Self>
    where
        F: Fn(JobContext) -> Fut + Send + Sync + 'static,
        Fut: std::future::Future<Output = Result<Value, JobError>> + Send + 'static,
    {
        let _ = tracing_subscriber::fmt().with_env_filter("milvaion_worker=info").with_test_writer().try_init();

        let brokers = brokers()?;
        let prefix = format!("it{}.", &uuid::Uuid::new_v4().simple().to_string()[..8]);

        let mut options = WorkerOptions::new(WORKER_ID);
        options.transport = Transport::Kafka;
        options.kafka.brokers = vec![brokers.clone()];
        options.kafka.topic_prefix = prefix.clone();
        options.kafka.num_partitions = 2;
        options.kafka.replication_factor = 1;
        options.redis_url = None;
        options.max_parallel_jobs = 4;
        options.heartbeat_interval = Duration::from_secs(1);
        options.job_heartbeat_interval = Duration::from_millis(150);
        options.shutdown_timeout = Duration::from_secs(5);
        options.startup_timeout = Duration::from_secs(60);
        configure(&mut options);

        let mut worker = Worker::new(options).unwrap();
        worker.handle_with("TestJob", job_options, handler).unwrap();
        let instance_id = worker.instance_id().to_string();

        let producer: FutureProducer = ClientConfig::new().set("bootstrap.servers", &brokers).create().unwrap();

        let (stop_tx, stop_rx) = tokio::sync::oneshot::channel::<()>();
        let run = tokio::spawn(worker.run(async {
            let _ = stop_rx.await;
        }));

        let f = Fixture { brokers, prefix, instance_id, producer, stop: Some(stop_tx), run: Some(run) };

        // Registration is produced after the topics have been created.
        let instance = f.instance_id.clone();
        f.wait_for("milvaion.registrations", 60, move |records| {
            records.iter().any(|r| r.key.as_deref() == Some(instance.as_str()))
        })
        .await;

        Some(f)
    }

    fn topic(&self, name: &str) -> String {
        format!("{}{name}", self.prefix)
    }

    fn jobs(&self) -> String {
        self.topic("milvaion.jobs.rust-kafka-worker")
    }

    fn retry(&self) -> String {
        self.topic("milvaion.jobs.rust-kafka-worker.retry")
    }

    fn group(&self) -> String {
        self.topic("milvaion.worker.rust-kafka-worker")
    }

    async fn stop_worker(&mut self) {
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(());
        }

        if let Some(run) = self.run.take() {
            tokio::time::timeout(Duration::from_secs(60), run)
                .await
                .expect("the worker stopped")
                .unwrap()
                .expect("run returned Ok");
        }
    }

    async fn produce(&self, topic: &str, key: &str, value: &[u8], headers: &[(&str, &str)]) {
        let mut owned = OwnedHeaders::new();

        for (k, v) in headers {
            owned = owned.insert(Header { key: k, value: Some(*v) });
        }

        self.producer
            .send(FutureRecord::to(topic).key(key).payload(value).headers(owned), Duration::from_secs(10))
            .await
            .map_err(|(e, _)| e)
            .unwrap();
    }

    async fn produce_job(&self, job_id: &str, occurrence_id: &str, extra: Value) {
        let mut body = json!({
            "id": job_id,
            "displayName": "Kafka test",
            "jobNameInWorker": "TestJob",
            "jobData": r#"{"n":1}"#,
            "workerId": WORKER_ID,
        });

        if let (Some(body), Value::Object(extra)) = (body.as_object_mut(), extra) {
            body.extend(extra);
        }

        let value = serde_json::to_vec(&body).unwrap();
        let headers = [
            ("OccurrenceId", occurrence_id),
            ("CorrelationId", occurrence_id),
            ("JobType", "TestJob"),
            ("WorkerId", WORKER_ID),
            ("Priority", "0"),
        ];

        self.produce(&self.jobs(), job_id, &value, &headers).await;
    }

    /// Reads a topic (named without the prefix, or with it) from the start until `done` accepts
    /// what was read, and returns the records.
    async fn wait_for(
        &self,
        topic: &str,
        timeout_secs: u64,
        done: impl Fn(&[Rec]) -> bool + Send + 'static,
    ) -> Vec<Rec> {
        let topic = if topic.starts_with(&self.prefix) { topic.to_string() } else { self.topic(topic) };
        let brokers = self.brokers.clone();

        tokio::task::spawn_blocking(move || read_until(&brokers, &topic, Duration::from_secs(timeout_secs), done))
            .await
            .unwrap()
    }

    /// The status updates of an occurrence, once the last one is `until`.
    async fn statuses(&self, occurrence_id: &str, until: u8) -> Vec<Value> {
        let occurrence = occurrence_id.to_string();

        let records = self
            .wait_for("milvaion.status", 30, move |records| {
                let mine: Vec<Value> =
                    records.iter().map(Rec::json).filter(|s| s["occurrenceId"] == occurrence.as_str()).collect();
                mine.last().is_some_and(|s| s["status"] == until)
            })
            .await;

        let mine: Vec<&Rec> = records.iter().filter(|r| r.json()["occurrenceId"] == occurrence_id).collect();

        for r in &mine {
            assert_eq!(r.key.as_deref(), Some(occurrence_id), "a status record is keyed by its occurrence id");
        }

        mine.into_iter().map(Rec::json).collect()
    }

    /// The sum of the committed offsets of the worker's group on a topic.
    fn committed(&self, topic: &str) -> i64 {
        let consumer: BaseConsumer = ClientConfig::new()
            .set("bootstrap.servers", &self.brokers)
            .set("group.id", self.group())
            .set("enable.auto.commit", "false")
            .create()
            .unwrap();

        let mut list = TopicPartitionList::new();

        for partition in 0..2 {
            list.add_partition(topic, partition);
        }

        let Ok(committed) = consumer.committed_offsets(list, Duration::from_secs(5)) else { return -1 };

        committed
            .elements()
            .iter()
            .filter_map(|e| match e.offset() {
                Offset::Offset(o) if o > 0 => Some(o),
                _ => None,
            })
            .sum()
    }

    async fn expect_committed(&self, topic: &str, want: i64) {
        let deadline = Instant::now() + Duration::from_secs(15);

        loop {
            let got = tokio::task::block_in_place(|| self.committed(topic));

            if got == want {
                return;
            }

            assert!(Instant::now() < deadline, "committed offsets on {topic} = {got}, want {want}");
            tokio::time::sleep(Duration::from_millis(200)).await;
        }
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(());
        }
    }
}

fn read_until(brokers: &str, topic: &str, timeout: Duration, done: impl Fn(&[Rec]) -> bool) -> Vec<Rec> {
    let consumer: BaseConsumer = ClientConfig::new()
        .set("bootstrap.servers", brokers)
        .set("group.id", format!("reader-{}", guid()))
        .set("enable.auto.commit", "false")
        .create()
        .unwrap();

    let deadline = Instant::now() + timeout;

    // The worker creates its topics at startup; wait until this one exists.
    let partitions = loop {
        let partitions = consumer
            .fetch_metadata(Some(topic), Duration::from_secs(5))
            .ok()
            .and_then(|m| m.topics().first().filter(|t| t.error().is_none()).map(|t| t.partitions().len()))
            .unwrap_or(0);

        if partitions > 0 {
            break partitions;
        }

        assert!(Instant::now() < deadline, "topic {topic} was not created");
        std::thread::sleep(Duration::from_millis(100));
    };

    let mut list = TopicPartitionList::new();

    for partition in 0..partitions as i32 {
        list.add_partition_offset(topic, partition, Offset::Beginning).unwrap();
    }

    consumer.assign(&list).unwrap();

    let mut records = Vec::new();

    loop {
        if done(&records) {
            return records;
        }

        assert!(Instant::now() < deadline, "timed out reading {topic}; got {} records: {records:?}", records.len());

        if let Some(Ok(m)) = consumer.poll(Duration::from_millis(100)) {
            records.push(Rec {
                partition: m.partition(),
                offset: m.offset(),
                key: m.key().map(|k| String::from_utf8_lossy(k).to_string()),
                value: m.payload().unwrap_or_default().to_vec(),
                headers: m
                    .headers()
                    .map(|h| {
                        h.iter()
                            .map(|h| {
                                (h.key.to_string(), String::from_utf8_lossy(h.value.unwrap_or_default()).to_string())
                            })
                            .collect()
                    })
                    .unwrap_or_default(),
                timestamp: m.timestamp().to_millis().unwrap_or_default(),
            });
        }
    }
}

fn status_list(statuses: &[Value]) -> Vec<u64> {
    statuses.iter().map(|s| s["status"].as_u64().unwrap_or(99)).collect()
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn completes_a_job() {
    let Some(f) = Fixture::start(
        JobOptions::default(),
        |_| {},
        |ctx: JobContext| async move {
            ctx.info("hello from kafka");
            Ok(json!({"ok": true}))
        },
    )
    .await
    else {
        return;
    };

    // Registration, keyed by the instance id
    let instance = f.instance_id.clone();
    let registrations = f
        .wait_for("milvaion.registrations", 10, move |r| r.iter().any(|r| r.key.as_deref() == Some(instance.as_str())))
        .await;
    let registration = registrations.iter().find(|r| r.key.as_deref() == Some(f.instance_id.as_str())).unwrap().json();
    assert_eq!(registration["workerId"], WORKER_ID);
    assert_eq!(registration["instanceId"], f.instance_id.as_str());
    assert_eq!(registration["jobTypes"], json!(["TestJob"]));
    let metadata: Value = serde_json::from_str(registration["metadata"].as_str().unwrap()).unwrap();
    assert_eq!(metadata["Sdk"], "rust");
    assert_eq!(metadata["SdkVersion"], SDK_VERSION);

    let (job_id, occurrence_id) = (guid(), guid());
    f.produce_job(&job_id, &occurrence_id, json!({})).await;

    let statuses = f.statuses(&occurrence_id, 2).await;
    assert_eq!(statuses[0]["status"], 1, "first status must be Running: {:?}", status_list(&statuses));
    assert!(statuses[0]["startTime"].is_string());

    let last = statuses.last().unwrap();
    assert_eq!(last["jobId"], job_id.as_str());
    assert_eq!(last["result"], r#"{"ok":true}"#);
    assert_eq!(last["instanceId"], f.instance_id.as_str());
    assert_eq!(last["workerId"], WORKER_ID);
    assert!(last["durationMs"].is_number());

    f.expect_committed(&f.jobs(), 1).await;

    // Logs, keyed by the occurrence id of their first entry
    let occurrence = occurrence_id.clone();
    f.wait_for("milvaion.logs", 10, move |records| {
        records.iter().any(|r| {
            r.key.as_deref() == Some(occurrence.as_str())
                && String::from_utf8_lossy(&r.value).contains("hello from kafka")
        })
    })
    .await;

    // Heartbeats, keyed by the instance id
    let instance = f.instance_id.clone();
    let heartbeats = f
        .wait_for("milvaion.heartbeats", 10, move |records| {
            !records.is_empty() && records.iter().all(|r| r.key.as_deref() == Some(instance.as_str()))
        })
        .await;
    assert_eq!(heartbeats[0].json()["workerId"], WORKER_ID);

    let mut f = f;
    f.stop_worker().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn retries_then_dead_letters() {
    let attempts = Arc::new(Mutex::new(Vec::<Instant>::new()));
    let seen = attempts.clone();

    let Some(f) = Fixture::start(
        JobOptions::new().max_retries(2).base_retry_delay(Duration::from_millis(400)),
        |_| {},
        move |_ctx: JobContext| {
            let seen = seen.clone();
            async move {
                seen.lock().unwrap().push(Instant::now());
                Err(JobError::new("always fails"))
            }
        },
    )
    .await
    else {
        return;
    };

    let (job_id, occurrence_id) = (guid(), guid());
    f.produce_job(&job_id, &occurrence_id, json!({})).await;

    let dlq = f.wait_for("milvaion.dlq", 30, |records| !records.is_empty()).await;
    assert_eq!(dlq.len(), 1, "{dlq:?}");
    let dead = &dlq[0];
    assert_eq!(dead.key.as_deref(), Some(occurrence_id.as_str()));

    for (key, want) in [
        ("OccurrenceId", occurrence_id.as_str()),
        ("CorrelationId", occurrence_id.as_str()),
        ("MaxRetries", "2"),
        ("x-retry-count", "2"),
    ] {
        assert_eq!(dead.header(key), Some(want), "dead letter header {key}");
    }

    let body = dead.json();
    assert_eq!(body["id"], job_id.as_str());
    assert_eq!(body["status"], 3);
    assert!(body["exception"].as_str().unwrap().starts_with("Max retries (2) exceeded."), "{body}");

    let retries = f.wait_for(&f.retry(), 10, |records| records.len() == 2).await;
    let mut retries = retries;
    retries.sort_by_key(|r| r.header("x-retry-count").map(str::to_string));

    for (i, r) in retries.iter().enumerate() {
        assert_eq!(r.header("x-retry-count"), Some((i + 1).to_string().as_str()), "{r:?}");
        assert_eq!(r.header("MaxRetries"), Some("2"));
        assert_eq!(r.header("OccurrenceId"), Some(occurrence_id.as_str()));
        assert_eq!(r.header("CorrelationId"), Some(occurrence_id.as_str()));
        assert_eq!(r.header("JobType"), Some("TestJob"), "the original headers are kept");
        assert_eq!(r.key.as_deref(), Some(job_id.as_str()));
        assert_eq!(r.json()["id"], job_id.as_str(), "the job body is written unchanged");

        // base * 2^retryCount of the failed attempt: 400 ms, then 800 ms.
        let not_before: i64 = r.header("x-not-before").unwrap().parse().unwrap();
        let delay = not_before - r.timestamp;
        let want = 400 << i;
        assert!((want - 50..want + 500).contains(&delay), "retry {} is due {delay} ms after it was written", i + 1);
    }

    let got = attempts.lock().unwrap().clone();
    assert_eq!(got.len(), 3, "attempts");
    assert!(got[1] - got[0] >= Duration::from_millis(400), "the second attempt must wait 400 ms");
    assert!(got[2] - got[1] >= Duration::from_millis(800), "the third attempt must wait 800 ms");

    let statuses = f.statuses(&occurrence_id, 3).await;
    let (last, running) = statuses.split_last().unwrap();
    assert_eq!(last["status"], 3);
    assert!(running.iter().all(|s| s["status"] == 1), "only Running may precede Failed: {:?}", status_list(&statuses));
    // One per attempt, plus job heartbeats while the retries waited.
    assert!(running.len() > 3, "expected job heartbeats during the retry waits: {:?}", status_list(&statuses));

    f.expect_committed(&f.jobs(), 1).await;
    f.expect_committed(&f.retry(), 2).await;

    let mut f = f;
    f.stop_worker().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn forwards_a_poison_record_to_the_dead_letter_topic() {
    let Some(f) = Fixture::start(JobOptions::default(), |_| {}, |_ctx: JobContext| async { Ok(json!("ok")) }).await
    else {
        return;
    };

    f.produce(&f.jobs(), "poison", b"not json", &[("JobType", "TestJob")]).await;

    let dlq = f.wait_for("milvaion.dlq", 30, |records| !records.is_empty()).await;
    let dead = &dlq[0];
    assert_eq!(dead.value, b"not json");
    assert_eq!(dead.key.as_deref(), Some("poison"));
    assert_eq!(dead.header("x-original-topic"), Some(f.jobs().as_str()));
    assert_eq!(dead.header("x-dlq-reason"), Some("the message is not a valid job"));
    assert_eq!(dead.header("JobType"), Some("TestJob"));

    f.expect_committed(&f.jobs(), 1).await;

    let mut f = f;
    f.stop_worker().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn times_out() {
    let Some(f) = Fixture::start(
        JobOptions::default(),
        |_| {},
        |ctx: JobContext| async move {
            ctx.cancelled().await;
            Err(JobError::new("stopped"))
        },
    )
    .await
    else {
        return;
    };

    let (job_id, occurrence_id) = (guid(), guid());
    f.produce_job(&job_id, &occurrence_id, json!({"executionTimeoutSeconds": 0.5})).await;

    let statuses = f.statuses(&occurrence_id, 5).await;
    let exception = statuses.last().unwrap()["exception"].as_str().unwrap().to_string();
    assert!(exception.contains("exceeded maximum timeout of 0.5 seconds"), "{exception}");

    f.expect_committed(&f.jobs(), 1).await;

    let mut f = f;
    f.stop_worker().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn shutdown_writes_the_interrupted_job_back() {
    let (started_tx, started_rx) = tokio::sync::mpsc::unbounded_channel::<()>();

    let Some(mut f) = Fixture::start(
        JobOptions::default(),
        |o| o.shutdown_timeout = Duration::from_millis(300),
        move |ctx: JobContext| {
            let started = started_tx.clone();
            async move {
                let _ = started.send(());
                ctx.cancelled().await;
                Err(JobError::new("stopped"))
            }
        },
    )
    .await
    else {
        return;
    };

    let (job_id, occurrence_id) = (guid(), guid());
    f.produce_job(&job_id, &occurrence_id, json!({})).await;

    let mut started_rx = started_rx;
    tokio::time::timeout(Duration::from_secs(30), started_rx.recv()).await.expect("the job started");

    f.stop_worker().await;

    let jobs = f.wait_for(&f.jobs(), 10, |records| records.len() == 2).await;
    let requeued = jobs.iter().max_by_key(|r| (r.partition, r.offset)).unwrap();
    assert_eq!(requeued.header("OccurrenceId"), Some(occurrence_id.as_str()));
    assert_eq!(requeued.key.as_deref(), Some(job_id.as_str()));
    assert_eq!(requeued.value, jobs[0].value);

    let committed = tokio::task::block_in_place(|| f.committed(&f.jobs()));
    assert_eq!(committed, 1, "the interrupted record, now written back, is committed");

    let statuses = f.statuses(&occurrence_id, 1).await;
    assert!(
        statuses.iter().all(|s| s["status"] == 1),
        "an interrupted job must not report a final status: {:?}",
        status_list(&statuses)
    );

    // The instance says it is leaving.
    let instance = f.instance_id.clone();
    f.wait_for("milvaion.heartbeats", 10, move |records| {
        records.iter().any(|r| r.key.as_deref() == Some(instance.as_str()) && r.json()["isStopping"] == true)
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn runs_jobs_of_one_partition_in_parallel_up_to_the_limit() {
    let running = Arc::new(Mutex::new((0usize, 0usize))); // (running, peak)
    let release = Arc::new(tokio::sync::Notify::new());
    let (r, n) = (running.clone(), release.clone());

    let Some(f) = Fixture::start(
        JobOptions::default(),
        |o| o.kafka.max_in_flight_per_partition = 2,
        move |_ctx: JobContext| {
            let (running, release) = (r.clone(), n.clone());
            async move {
                {
                    let mut state = running.lock().unwrap();
                    state.0 += 1;
                    state.1 = state.1.max(state.0);
                }

                let released = release.notified();
                released.await;
                running.lock().unwrap().0 -= 1;
                Ok(json!("ok"))
            }
        },
    )
    .await
    else {
        return;
    };

    let job_id = guid(); // one key, so every record lands in the same partition
    let occurrences: Vec<String> = (0..5).map(|_| guid()).collect();

    for occurrence_id in &occurrences {
        f.produce_job(&job_id, occurrence_id, json!({})).await;
    }

    let deadline = Instant::now() + Duration::from_secs(30);

    while running.lock().unwrap().0 < 2 {
        assert!(Instant::now() < deadline, "expected 2 jobs running from one partition");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }

    tokio::time::sleep(Duration::from_millis(500)).await; // a third job must not start meanwhile
    assert_eq!(running.lock().unwrap().1, 2, "peak parallel jobs of one partition");

    // Let them all finish, two at a time.
    let finisher = {
        let release = release.clone();
        tokio::spawn(async move {
            loop {
                release.notify_waiters();
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
        })
    };

    for occurrence_id in &occurrences {
        f.statuses(occurrence_id, 2).await;
    }

    finisher.abort();
    assert_eq!(running.lock().unwrap().1, 2, "peak parallel jobs of one partition");
    f.expect_committed(&f.jobs(), 5).await;

    let mut f = f;
    f.stop_worker().await;
}
