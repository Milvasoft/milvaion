//! Unit tests. The processor runs against an in-memory broker; see `tests/integration.rs` for
//! a run against real RabbitMQ and Redis.

use crate::broker::{BoxFuture, Delivery, HeaderValue, PublishOptions, Publisher, Settler};
use crate::context::JobContext;
use crate::error::{AbortReason, JobError, Permanent, WorkerError};
use crate::log_buffer::LogBuffer;
use crate::options::*;
use crate::processor::*;
use crate::protocol::*;
use crate::worker::{build_registration, Worker};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

const OCCURRENCE: &str = "0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";

#[derive(Debug, Clone)]
struct Published {
    exchange: String,
    routing_key: String,
    body: Value,
    options: PublishOptions,
}

#[derive(Default)]
struct FakePublisher {
    messages: Mutex<Vec<Published>>,
    /// Publishes to the status queue fail while this is above zero.
    fail_statuses: AtomicUsize,
    /// Every publish fails while this is set.
    fail_all: std::sync::atomic::AtomicBool,
}

impl Publisher for FakePublisher {
    fn publish<'a>(
        &'a self,
        exchange: &'a str,
        routing_key: &'a str,
        body: Vec<u8>,
        options: PublishOptions,
    ) -> BoxFuture<'a, Result<(), String>> {
        Box::pin(async move {
            if self.fail_all.load(Ordering::SeqCst) {
                return Err("broker down".to_string());
            }

            if routing_key == QUEUE_STATUS_UPDATES {
                let remaining = self.fail_statuses.load(Ordering::SeqCst);

                if remaining > 0 {
                    self.fail_statuses.store(remaining - 1, Ordering::SeqCst);
                    return Err("refused".to_string());
                }
            }

            self.messages.lock().unwrap().push(Published {
                exchange: exchange.to_string(),
                routing_key: routing_key.to_string(),
                body: serde_json::from_slice(&body).unwrap(),
                options,
            });

            Ok(())
        })
    }
}

impl FakePublisher {
    fn to(&self, routing_key: &str) -> Vec<Published> {
        self.messages.lock().unwrap().iter().filter(|m| m.routing_key == routing_key).cloned().collect()
    }

    fn statuses(&self) -> Vec<Value> {
        self.to(QUEUE_STATUS_UPDATES).into_iter().map(|m| m.body).collect()
    }

    fn final_status(&self) -> Option<Value> {
        self.statuses().into_iter().rev().find(|s| s["status"] != 1)
    }

    fn logs(&self) -> Vec<Value> {
        self.to(QUEUE_WORKER_LOGS).into_iter().flat_map(|m| m.body["logs"].as_array().unwrap().clone()).collect()
    }

    fn dead_letters(&self) -> Vec<Published> {
        self.messages.lock().unwrap().iter().filter(|m| m.exchange == EXCHANGE_DEAD_LETTER).cloned().collect()
    }
}

#[derive(Clone, Default)]
struct Settled(Arc<Mutex<Vec<String>>>);

impl Settled {
    fn get(&self) -> Vec<String> {
        self.0.lock().unwrap().clone()
    }
}

struct FakeSettler(Settled);

impl Settler for FakeSettler {
    fn ack(&self) -> BoxFuture<'_, Result<(), String>> {
        self.0 .0.lock().unwrap().push("ack".into());
        Box::pin(async { Ok(()) })
    }

    fn nack(&self, requeue: bool) -> BoxFuture<'_, Result<(), String>> {
        self.0 .0.lock().unwrap().push(if requeue { "requeue" } else { "reject" }.into());
        Box::pin(async { Ok(()) })
    }
}

fn worker_options() -> ResolvedOptions {
    let mut o = WorkerOptions::new("test-worker");
    o.instance_id = Some("test-worker-0000abcd".into());
    o.max_parallel_jobs = 4;
    o.job_heartbeat_interval = Duration::ZERO;
    resolve_options(o).unwrap()
}

type TestHandler = Arc<dyn Fn(JobContext) -> BoxFuture<'static, Result<Value, JobError>> + Send + Sync>;

fn handler<F, Fut>(f: F) -> TestHandler
where
    F: Fn(JobContext) -> Fut + Send + Sync + 'static,
    Fut: std::future::Future<Output = Result<Value, JobError>> + Send + 'static,
{
    Arc::new(move |ctx| Box::pin(f(ctx)))
}

struct Harness {
    processor: Arc<Processor>,
    publisher: Arc<FakePublisher>,
    consumer: Arc<Registered>,
}

fn harness(name: &str, options: JobOptions, h: TestHandler) -> Harness {
    harness_with(worker_options(), name, options, h)
}

fn harness_with(opts: ResolvedOptions, name: &str, options: JobOptions, h: TestHandler) -> Harness {
    let publisher = Arc::new(FakePublisher::default());
    let dyn_publisher: Arc<dyn Publisher> = publisher.clone();
    let job = resolve_job(name, options, &opts).unwrap();
    let registered = Arc::new(Registered { job, handler: h });
    let mut jobs = HashMap::new();
    jobs.insert(name.to_lowercase(), registered.clone());

    let mut processor = Processor::new(
        Arc::new(opts),
        jobs,
        dyn_publisher.clone(),
        Arc::new(LogBuffer::new(dyn_publisher)),
        Arc::new(CancelRegistry::default()),
    );
    processor.status_retry_delays = vec![Duration::from_millis(1); 3];

    Harness { processor: Arc::new(processor), publisher, consumer: registered }
}

fn delivery(job_name: &str, data: Option<&str>, retry_count: Option<i64>, settled: &Settled) -> Delivery {
    let body = json!({
        "id": "0193a1b2-0000-7000-8000-000000000001",
        "displayName": "Test job",
        "jobNameInWorker": job_name,
        "jobData": data,
        "executeAt": "2026-11-13T09:00:00Z",
        "workerId": "test-worker",
        "unknownField": 42
    });

    let mut headers = HashMap::new();
    headers.insert("CorrelationId".to_string(), HeaderValue::Bytes(OCCURRENCE.as_bytes().to_vec()));

    if let Some(n) = retry_count {
        headers.insert("x-retry-count".to_string(), HeaderValue::Int(n));
    }

    Delivery {
        body: serde_json::to_vec(&body).unwrap(),
        headers,
        correlation_id: None,
        redelivered: false,
        settler: Box::new(FakeSettler(settled.clone())),
    }
}

impl Harness {
    async fn run(&self, d: Delivery) {
        self.processor.clone().handle(d, self.consumer.job.queue.clone()).await;
    }
}

fn header<'a>(p: &'a Published, key: &str) -> Option<&'a HeaderValue> {
    p.options.headers.iter().find(|(k, _)| k == key).map(|(_, v)| v)
}

// ---- processor ----

#[tokio::test]
async fn completes_and_reports_result() {
    let h = harness(
        "SendEmailJob",
        JobOptions::default(),
        handler(|ctx: JobContext| async move {
            #[derive(serde::Deserialize)]
            struct Data {
                to: String,
            }
            let data: Data = ctx.data()?;
            ctx.log(LogLevel::Information, "Mail sent", Some(json!({"to": data.to})));
            assert_eq!(ctx.occurrence_id(), OCCURRENCE);
            assert_eq!(ctx.retry_count(), 0);
            assert_eq!(ctx.job_name(), "SendEmailJob");
            Ok(json!({"sent": true}))
        }),
    );
    let settled = Settled::default();

    h.run(delivery("SendEmailJob", Some(r#"{"to":"user@example.com"}"#), None, &settled)).await;

    assert_eq!(settled.get(), vec!["ack"]);
    let statuses = h.publisher.statuses();
    assert_eq!(statuses[0]["status"], 1);
    assert!(statuses[0]["startTime"].is_string());
    let last = statuses.last().unwrap();
    assert_eq!(last["status"], 2);
    assert_eq!(last["result"], r#"{"sent":true}"#);
    assert_eq!(last["occurrenceId"], OCCURRENCE);
    assert_eq!(last["workerId"], "test-worker");
    assert_eq!(last["instanceId"], "test-worker-0000abcd");
    assert!(last["durationMs"].is_number());
    assert!(last["endTime"].is_string());

    let logs = h.publisher.logs();
    let log = logs.iter().find(|l| l["log"]["message"] == "Mail sent").expect("user log");
    assert_eq!(log["workerId"], "test-worker-0000abcd");
    assert_eq!(log["log"]["level"], "Information");
    assert_eq!(log["log"]["category"], "UserCode");
    assert_eq!(log["log"]["data"]["to"], "user@example.com");
    assert!(h.publisher.to(QUEUE_STATUS_UPDATES).iter().all(|m| m.options.persistent));
}

#[tokio::test]
async fn default_and_string_results() {
    let h = harness("NothingJob", JobOptions::default(), handler(|_| async { Ok(Value::Null) }));
    let settled = Settled::default();
    h.run(delivery("NothingJob", None, None, &settled)).await;
    assert_eq!(h.publisher.final_status().unwrap()["result"], "Job NothingJob completed successfully");

    let h = harness("TextJob", JobOptions::default(), handler(|_| async { Ok(json!("plain text")) }));
    h.run(delivery("TextJob", None, None, &settled)).await;
    assert_eq!(h.publisher.final_status().unwrap()["result"], "plain text");
}

#[tokio::test]
async fn retries_with_backoff() {
    let h = harness(
        "FlakyJob",
        JobOptions::new().max_retries(3).base_retry_delay(Duration::from_millis(20)),
        handler(|_| async { Err(JobError::new("smtp timeout")) }),
    );
    let settled = Settled::default();
    let started = std::time::Instant::now();

    h.run(delivery("FlakyJob", None, Some(1), &settled)).await;

    // retry 1 waits base * 2^1
    assert!(started.elapsed() >= Duration::from_millis(40));
    assert_eq!(settled.get(), vec!["ack"]);
    assert!(h.publisher.final_status().is_none(), "the occurrence stays Running while it is retried");

    let republished = h.publisher.to(&h.consumer.job.queue);
    assert_eq!(republished.len(), 1);
    let message = &republished[0];
    assert_eq!(message.exchange, "");
    assert!(message.options.persistent);
    assert_eq!(message.options.correlation_id.as_deref(), Some(OCCURRENCE));
    assert_eq!(header(message, "x-retry-count"), Some(&HeaderValue::Int(2)));
    assert_eq!(header(message, "MaxRetries"), Some(&HeaderValue::Int(3)));
    assert_eq!(header(message, "CorrelationId"), Some(&HeaderValue::Bytes(OCCURRENCE.as_bytes().to_vec())));
    assert_eq!(message.body["jobNameInWorker"], "FlakyJob");
    assert_eq!(message.body["unknownField"], 42, "the job body is republished unchanged");

    let retry_log = h.publisher.logs().into_iter().find(|l| l["log"]["category"] == "Retry").unwrap();
    assert_eq!(retry_log["log"]["level"], "Warning");
    assert_eq!(retry_log["log"]["message"], "⚠️ Job failed (attempt 2/3). Retrying in 0.04s...");
}

#[tokio::test]
async fn dead_letters_after_retries() {
    let h = harness(
        "FlakyJob",
        JobOptions::new().max_retries(3),
        handler(|_| async { Err(JobError::new("still broken")) }),
    );
    let settled = Settled::default();

    h.run(delivery("FlakyJob", Some(r#"{"a":1}"#), Some(3), &settled)).await;

    assert_eq!(settled.get(), vec!["ack"]);
    let status = h.publisher.final_status().unwrap();
    assert_eq!(status["status"], 3);
    assert!(status["exception"].as_str().unwrap().starts_with("Max retries (3) exceeded. Original exception: Type:"));

    let dlq = h.publisher.dead_letters();
    assert_eq!(dlq.len(), 1);
    assert_eq!(dlq[0].routing_key, DEAD_LETTER_ROUTING_KEY);
    assert_eq!(dlq[0].body["status"], 3);
    assert_eq!(dlq[0].body["jobData"], r#"{"a":1}"#);
    assert_eq!(dlq[0].body["displayName"], "Test job");
    assert!(dlq[0].body["exception"].as_str().unwrap().contains("still broken"));
    assert_eq!(header(&dlq[0], "x-retry-count"), Some(&HeaderValue::Int(3)));
    assert_eq!(header(&dlq[0], "MaxRetries"), Some(&HeaderValue::Int(3)));
    assert!(dlq[0].options.persistent);
}

#[tokio::test]
async fn permanent_failure_skips_retries() {
    let h = harness(
        "StrictJob",
        JobOptions::default(),
        handler(|_| async { Err(JobError::permanent("customer does not exist")) }),
    );
    let settled = Settled::default();

    h.run(delivery("StrictJob", None, None, &settled)).await;

    assert_eq!(settled.get(), vec!["ack"]);
    assert_eq!(h.publisher.final_status().unwrap()["status"], 3);
    assert!(h.publisher.to(&h.consumer.job.queue).is_empty());
    let dlq = h.publisher.dead_letters();
    assert!(dlq[0].body["exception"].as_str().unwrap().starts_with("[PERMANENT FAILURE] Type:"));
    assert_eq!(header(&dlq[0], "MaxRetries"), Some(&HeaderValue::Int(0)));
}

#[tokio::test]
async fn retry_after_skips_retries_and_dead_letter() {
    let at = chrono::DateTime::parse_from_rfc3339("2026-11-14T11:00:00Z").unwrap().with_timezone(&chrono::Utc);
    let h = harness(
        "QuotaJob",
        JobOptions::new().max_retries(3),
        handler(move |_| async move { Err(JobError::retry_after(at, "quota exceeded")) }),
    );
    let settled = Settled::default();

    h.run(delivery("QuotaJob", None, None, &settled)).await;

    assert_eq!(settled.get(), vec!["ack"]);
    assert!(h.publisher.to(&h.consumer.job.queue).is_empty());
    assert!(h.publisher.dead_letters().is_empty());

    let finals: Vec<_> = h.publisher.statuses().into_iter().filter(|s| s["status"] != 1).collect();
    assert_eq!(finals.len(), 1);
    let status = &finals[0];
    assert_eq!(status["status"], 3);
    assert_eq!(status["retryAfter"], "2026-11-14T11:00:00.000Z");
    assert!(status["endTime"].is_string() && status["durationMs"].is_number());
    let exception = status["exception"].as_str().unwrap();
    assert!(
        exception.starts_with(
            "Type: milvaion_worker::JobError
Message: quota exceeded"
        ),
        "{exception}"
    );
    assert!(exception.contains("2026-11-14T11:00:00.000Z"));

    let log = h.publisher.logs().into_iter().find(|l| l["log"]["category"] == "Retry").unwrap();
    assert_eq!(log["log"]["level"], "Warning");
    assert!(log["log"]["message"]
        .as_str()
        .unwrap()
        .starts_with("⏰ Job asked to be retried at 2026-11-14T11:00:00.000Z."));
    assert_eq!(log["log"]["data"]["RetryAfter"], "2026-11-14T11:00:00.000Z");

    // Other status updates leave the field out.
    assert!(h.publisher.statuses().iter().filter(|s| s["status"] == 1).all(|s| s.get("retryAfter").is_none()));
}

#[tokio::test]
async fn invalid_job_data_is_permanent() {
    let h = harness(
        "TypedJob",
        JobOptions::default(),
        handler(|ctx: JobContext| async move {
            let n: u32 = ctx.data()?;
            Ok(json!(n))
        }),
    );
    let settled = Settled::default();

    h.run(delivery("TypedJob", Some(r#"{"not":"a number"}"#), None, &settled)).await;

    assert_eq!(h.publisher.dead_letters().len(), 1);
    assert!(h.publisher.to(&h.consumer.job.queue).is_empty());
}

#[tokio::test]
async fn panic_is_a_failure() {
    let h = harness(
        "PanicJob",
        JobOptions::new().max_retries(0),
        handler(|_| async {
            if true {
                panic!("boom");
            }
            Ok(Value::Null)
        }),
    );
    let settled = Settled::default();

    h.run(delivery("PanicJob", None, None, &settled)).await;

    assert_eq!(settled.get(), vec!["ack"]);
    let status = h.publisher.final_status().unwrap();
    assert_eq!(status["status"], 3);
    assert!(status["exception"].as_str().unwrap().contains("Type: panic\nMessage: panic: boom"));
}

#[tokio::test]
async fn times_out_even_if_handler_ignores_cancellation() {
    let h = harness(
        "SlowJob",
        JobOptions::new().execution_timeout(Duration::from_millis(50)),
        handler(|_| async {
            tokio::time::sleep(Duration::from_secs(30)).await;
            Ok(Value::Null)
        }),
    );
    let settled = Settled::default();
    let started = std::time::Instant::now();

    h.run(delivery("SlowJob", None, None, &settled)).await;

    assert!(started.elapsed() < Duration::from_secs(5));
    assert_eq!(settled.get(), vec!["ack"]);
    let status = h.publisher.final_status().unwrap();
    assert_eq!(status["status"], 5);
    assert!(status["exception"]
        .as_str()
        .unwrap()
        .starts_with("Job execution exceeded maximum timeout of 0.05 seconds"));
}

#[tokio::test]
async fn job_timeout_from_the_dashboard_wins() {
    let h = harness(
        "SlowJob",
        JobOptions::new().execution_timeout(Duration::from_millis(20)),
        handler(|_| async {
            tokio::time::sleep(Duration::from_millis(100)).await;
            Ok(Value::Null)
        }),
    );
    let settled = Settled::default();
    let mut d = delivery("SlowJob", None, None, &settled);
    let mut body: Value = serde_json::from_slice(&d.body).unwrap();
    body["executionTimeoutSeconds"] = json!(0); // 0 disables the timeout
    d.body = serde_json::to_vec(&body).unwrap();

    h.run(d).await;

    assert_eq!(h.publisher.final_status().unwrap()["status"], 2);
}

#[tokio::test]
async fn cancels_running_job() {
    let (started_tx, started_rx) = tokio::sync::oneshot::channel::<()>();
    let started_tx = Arc::new(Mutex::new(Some(started_tx)));

    let h = harness(
        "LongJob",
        JobOptions::default(),
        handler(move |ctx: JobContext| {
            let started_tx = started_tx.clone();
            async move {
                if let Some(tx) = started_tx.lock().unwrap().take() {
                    let _ = tx.send(());
                }
                ctx.cancelled().await;
                assert!(matches!(ctx.abort_reason(), Some(AbortReason::Cancelled(_))));
                Err(JobError::new("stopped"))
            }
        }),
    );
    let settled = Settled::default();
    let processor = h.processor.clone();
    let run = tokio::spawn({
        let consumer = h.consumer.job.queue.clone();
        let d = delivery("LongJob", None, None, &settled);
        async move { processor.handle(d, consumer).await }
    });

    started_rx.await.unwrap();
    assert!(!h.processor.cancels.cancel("00000000-0000-0000-0000-000000000000", None));
    // The API sends the occurrence id in whatever case; the message is the one from Redis.
    crate::cancellation::handle_message(
        &json!({"CorrelationId": OCCURRENCE.to_uppercase(), "JobId": "x", "OccurrenceId": OCCURRENCE, "Reason": "Cancelled by user"}).to_string(),
        &h.processor.cancels,
    );
    run.await.unwrap();

    assert_eq!(settled.get(), vec!["ack"]);
    let status = h.publisher.final_status().unwrap();
    assert_eq!(status["status"], 4);
    assert_eq!(status["result"], "Job cancelled by user or system");
    let log = h.publisher.logs().into_iter().find(|l| l["log"]["message"] == "Job execution was cancelled").unwrap();
    assert_eq!(log["log"]["data"]["Reason"], "job was cancelled: Cancelled by user");
}

#[tokio::test]
async fn shutdown_returns_job_to_queue() {
    let h = harness(
        "LongJob",
        JobOptions::default(),
        handler(|_| async {
            tokio::time::sleep(Duration::from_secs(30)).await;
            Ok(Value::Null)
        }),
    );
    let settled = Settled::default();
    let processor = h.processor.clone();
    let run = tokio::spawn({
        let consumer = h.consumer.job.queue.clone();
        let d = delivery("LongJob", None, None, &settled);
        async move { processor.handle(d, consumer).await }
    });

    while h.processor.running_jobs() == 0 || h.publisher.statuses().is_empty() {
        tokio::time::sleep(Duration::from_millis(5)).await;
    }
    assert!(!h.processor.wait_for_idle(Duration::from_millis(20)).await);

    h.processor.cancels.abort_all(AbortReason::WorkerStopping);
    assert!(h.processor.wait_for_idle(Duration::from_secs(5)).await);
    run.await.unwrap();

    assert_eq!(settled.get(), vec!["requeue"]);
    assert!(h.publisher.final_status().is_none(), "no final status for a job that goes back to the queue");
}

#[tokio::test]
async fn shutdown_during_retry_delay_requeues() {
    let h = harness(
        "FlakyJob",
        JobOptions::new().base_retry_delay(Duration::from_secs(30)),
        handler(|_| async { Err(JobError::new("fails")) }),
    );
    let settled = Settled::default();
    let processor = h.processor.clone();
    let run = tokio::spawn({
        let consumer = h.consumer.job.queue.clone();
        let d = delivery("FlakyJob", None, None, &settled);
        async move { processor.handle(d, consumer).await }
    });

    while !h.publisher.logs().iter().any(|l| l["log"]["category"] == "Retry") {
        tokio::time::sleep(Duration::from_millis(5)).await;
    }
    h.processor.shutdown.cancel();
    run.await.unwrap();

    assert_eq!(settled.get(), vec!["requeue"]);
    assert!(h.publisher.to(&h.consumer.job.queue).is_empty());
}

#[tokio::test]
async fn unknown_job_is_dead_lettered() {
    let h = harness("KnownJob", JobOptions::default(), handler(|_| async { Ok(Value::Null) }));
    let settled = Settled::default();

    h.run(delivery("OtherJob", None, None, &settled)).await;

    assert_eq!(settled.get(), vec!["ack"]);
    assert_eq!(
        h.publisher.final_status().unwrap()["exception"],
        "No IJob implementation registered for type: OtherJob"
    );
    let dlq = h.publisher.dead_letters();
    assert_eq!(dlq[0].body["exception"], "[PERMANENT FAILURE] No IJob implementation registered for type: OtherJob");
    assert_eq!(header(&dlq[0], "MaxRetries"), Some(&HeaderValue::Int(0)));
}

#[tokio::test]
async fn job_names_are_case_insensitive() {
    let h = harness("SendEmailJob", JobOptions::default(), handler(|_| async { Ok(json!("ok")) }));
    let settled = Settled::default();

    h.run(delivery("sendemailjob", None, None, &settled)).await;

    assert_eq!(h.publisher.final_status().unwrap()["status"], 2);
}

#[tokio::test]
async fn rejects_body_that_is_not_a_job() {
    let h = harness("AJob", JobOptions::default(), handler(|_| async { Ok(Value::Null) }));
    let settled = Settled::default();
    let mut d = delivery("AJob", None, None, &settled);
    d.body = b"not json".to_vec();
    h.run(d).await;
    assert_eq!(settled.get(), vec!["reject"]);

    // Valid job, but no occurrence id anywhere.
    let mut d = delivery("AJob", None, None, &settled);
    d.headers.clear();
    d.correlation_id = Some("not-a-guid".into());
    h.run(d).await;
    assert_eq!(settled.get(), vec!["reject", "reject"]);
    assert!(h.publisher.statuses().is_empty());
}

#[tokio::test]
async fn reports_redelivery() {
    let h = harness("AJob", JobOptions::default(), handler(|_| async { Ok(Value::Null) }));
    let settled = Settled::default();
    let mut d = delivery("AJob", None, None, &settled);
    d.redelivered = true;

    h.run(d).await;

    let log = h.publisher.logs().into_iter().find(|l| l["log"]["category"] == "Redelivery").unwrap();
    assert_eq!(log["log"]["level"], "Warning");
    assert_eq!(h.publisher.statuses().iter().filter(|s| s["status"] == 1).count(), 2);
}

#[tokio::test]
async fn retries_refused_final_status() {
    let h = harness("AJob", JobOptions::default(), handler(|_| async { Ok(Value::Null) }));
    let settled = Settled::default();
    h.publisher.fail_statuses.store(1, Ordering::SeqCst); // the Running status

    h.run(delivery("AJob", None, None, &settled)).await;
    assert_eq!(h.publisher.final_status().unwrap()["status"], 2);

    h.publisher.fail_statuses.store(3, Ordering::SeqCst); // Running, then the first two Completed attempts
    h.publisher.messages.lock().unwrap().clear();
    h.run(delivery("AJob", None, None, &settled)).await;
    assert_eq!(h.publisher.final_status().unwrap()["status"], 2);
}

#[tokio::test]
async fn sends_job_heartbeats_while_running() {
    let mut opts = worker_options();
    opts.job_heartbeat_interval = Duration::from_millis(20);
    let h = harness_with(
        opts,
        "AJob",
        JobOptions::default(),
        handler(|_| async {
            tokio::time::sleep(Duration::from_millis(110)).await;
            Ok(Value::Null)
        }),
    );
    let settled = Settled::default();

    h.run(delivery("AJob", None, None, &settled)).await;

    let running = h.publisher.statuses().iter().filter(|s| s["status"] == 1).count();
    assert!(running >= 3, "expected the start status plus job heartbeats, got {running}");
}

#[tokio::test]
async fn wait_for_idle_tracks_running_jobs() {
    let h = harness(
        "AJob",
        JobOptions::default(),
        handler(|_| async {
            tokio::time::sleep(Duration::from_millis(50)).await;
            Ok(Value::Null)
        }),
    );
    assert!(h.processor.wait_for_idle(Duration::ZERO).await);
    let settled = Settled::default();
    let processor = h.processor.clone();
    let consumer = h.consumer.job.queue.clone();
    let d = delivery("AJob", None, None, &settled);
    let run = tokio::spawn(async move { processor.handle(d, consumer).await });

    while h.processor.running_jobs() == 0 {
        tokio::task::yield_now().await;
    }
    assert!(h.processor.wait_for_idle(Duration::from_secs(5)).await);
    assert_eq!(h.processor.running_jobs(), 0);
    run.await.unwrap();
}

// ---- units ----

#[test]
fn routing_matches_dotnet_sdk() {
    assert_eq!(generate_routing_pattern("SendEmailJob", "Mail-Worker"), "mail-worker.sendemail.*");
    assert_eq!(generate_routing_pattern("ReportJOB", "w"), "w.report.*");
    assert_eq!(generate_routing_pattern("Cleanup", "w"), "w.cleanup.*");
    assert_eq!(
        queue_name_for_pattern("report-worker.sendemail.*"),
        "scheduled_jobs_queue.report-worker.sendemail.wildcard"
    );
    assert_eq!(queue_name_for_pattern("a.#"), "scheduled_jobs_queue.a.all");

    let job = resolve_job("SendEmailJob", JobOptions::default(), &worker_options()).unwrap();
    assert_eq!(job.consumer_id, "sendemail-consumer");
    assert_eq!(job.queue, "scheduled_jobs_queue.test-worker.sendemail.wildcard");
    assert_eq!(job.max_parallel_jobs, 4);
    assert_eq!(job.max_retries, 3);
    assert_eq!(job.base_retry_delay, Duration::from_secs(5));
    assert_eq!(job.execution_timeout, Duration::from_secs(3600));

    let custom =
        resolve_job("X", JobOptions::new().routing_pattern("custom.x.*").consumer_id("c"), &worker_options()).unwrap();
    assert_eq!(custom.queue, "scheduled_jobs_queue.custom.x.wildcard");
    assert_eq!(custom.consumer_id, "c");
    assert!(resolve_job("X", JobOptions::new().max_parallel_jobs(0), &worker_options()).is_err());
}

#[test]
fn parse_occurrence_id_order() {
    let mut headers = HashMap::new();
    assert_eq!(parse_occurrence_id(&headers, Some(OCCURRENCE)).as_deref(), Some(OCCURRENCE));
    assert_eq!(parse_occurrence_id(&headers, Some("nope")), None);

    headers.insert("CorrelationId".into(), HeaderValue::Str("0193a1b2c3d47e5f8a9b0c1d2e3f4a5b".into()));
    assert_eq!(parse_occurrence_id(&headers, Some(OCCURRENCE)).as_deref(), Some("0193a1b2c3d47e5f8a9b0c1d2e3f4a5b"));

    headers.insert("OccurrenceId".into(), HeaderValue::Bytes(b" 11111111-2222-3333-4444-555555555555 ".to_vec()));
    assert_eq!(parse_occurrence_id(&headers, None).as_deref(), Some("11111111-2222-3333-4444-555555555555"));

    headers.insert("OccurrenceId".into(), HeaderValue::Str("garbage".into()));
    assert_eq!(parse_occurrence_id(&headers, None).as_deref(), Some("0193a1b2c3d47e5f8a9b0c1d2e3f4a5b"));
}

#[test]
fn parse_retry_count_encodings() {
    let count = |v: Option<HeaderValue>| {
        let mut headers = HashMap::new();
        if let Some(v) = v {
            headers.insert("x-retry-count".to_string(), v);
        }
        parse_retry_count(&headers)
    };

    assert_eq!(count(None), 0);
    assert_eq!(count(Some(HeaderValue::Int(2))), 2);
    assert_eq!(count(Some(HeaderValue::Int(-1))), 0);
    assert_eq!(count(Some(HeaderValue::Str("3".into()))), 3);
    assert_eq!(count(Some(HeaderValue::Bytes(b"12".to_vec()))), 12);
    assert_eq!(count(Some(HeaderValue::Bytes(5i32.to_le_bytes().to_vec()))), 5);
    assert_eq!(count(Some(HeaderValue::Other)), 0);
}

#[test]
fn format_error_includes_inner_errors() {
    #[derive(Debug)]
    struct Outer(std::io::Error);
    impl std::fmt::Display for Outer {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            f.write_str("could not send")
        }
    }
    impl std::error::Error for Outer {
        fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
            Some(&self.0)
        }
    }

    let error: JobError = Outer(std::io::Error::other("connection reset")).into();
    let formatted = error.format();
    assert!(formatted.starts_with(
        "Type: milvaion_worker::tests::format_error_includes_inner_errors::Outer\nMessage: could not send\n"
    ));
    assert!(formatted.contains("Inner Exception:\nType: error\nMessage: connection reset"));
    assert_eq!(error.inner_message().as_deref(), Some("connection reset"));
    assert!(!error.is_permanent());

    let permanent: Result<u8, JobError> = "x".parse::<u8>().permanent();
    assert!(permanent.unwrap_err().is_permanent());
    assert_eq!(JobError::new("a").context("b").message(), "b: a");

    let at = chrono::Utc::now();
    let later = JobError::retry_after(at, "quota");
    assert_eq!(later.retry_at(), Some(at));
    assert!(!later.is_permanent());
    assert_eq!(JobError::new("x").retry_at(), None);
    assert_eq!(JobError::new("x").into_retry_after(at).retry_at(), Some(at));
}

#[tokio::test]
async fn log_buffer_keeps_order_after_failed_send() {
    let publisher = Arc::new(FakePublisher::default());
    let buffer = LogBuffer::new(publisher.clone());
    let log = |m: &str| OccurrenceLog {
        timestamp: now(),
        level: LogLevel::Information,
        message: m.into(),
        data: None,
        category: "UserCode".into(),
        exception_type: None,
    };

    buffer.add("o", "i", log("1"));
    buffer.add("o", "i", log("2"));
    publisher.fail_all.store(true, Ordering::SeqCst);
    assert!(buffer.flush().await.is_err());
    buffer.add("o", "i", log("3"));
    publisher.fail_all.store(false, Ordering::SeqCst);
    buffer.flush().await.unwrap();

    let messages: Vec<_> = publisher.logs().iter().map(|l| l["log"]["message"].as_str().unwrap().to_string()).collect();
    assert_eq!(messages, vec!["1", "2", "3"]);
    let batch = &publisher.to(QUEUE_WORKER_LOGS)[0];
    assert_eq!(batch.body["count"], 2);
    assert!(batch.body["batchTimestamp"].is_string());
    assert!(batch.options.persistent);
}

#[tokio::test]
async fn log_buffer_drops_oldest_beyond_limit() {
    let publisher = Arc::new(FakePublisher::default());
    let mut buffer = LogBuffer::new(publisher.clone());
    buffer.max_buffered = 3;

    for i in 0..5 {
        buffer.add(
            "o",
            "i",
            OccurrenceLog {
                timestamp: now(),
                level: LogLevel::Debug,
                message: i.to_string(),
                data: None,
                category: "UserCode".into(),
                exception_type: None,
            },
        );
    }

    buffer.flush().await.unwrap();
    let messages: Vec<_> = publisher.logs().iter().map(|l| l["log"]["message"].as_str().unwrap().to_string()).collect();
    assert_eq!(messages, vec!["2", "3", "4"]);
}

#[test]
fn options_defaults() {
    let o = resolve_options(WorkerOptions::new(" report-worker ")).unwrap();
    assert_eq!(o.worker_id, "report-worker");
    assert!(o.instance_id.starts_with("report-worker-"));
    assert_eq!(o.instance_id.len(), "report-worker-".len() + 8);
    assert_eq!(o.rabbitmq_url, "amqp://guest:guest@localhost:5672/%2f");
    assert_eq!(o.redis_url.as_deref(), Some("redis://localhost:6379"));
    assert_eq!(o.cancellation_channel, "Milvaion:JobScheduler:cancellation_channel");
    assert_eq!(o.max_parallel_jobs, cpu_count() * 2);
    assert_eq!(o.execution_timeout, Duration::from_secs(3600));
    assert_eq!(o.heartbeat_interval, Duration::from_secs(30));
    assert_eq!(o.job_heartbeat_interval, Duration::from_secs(60));
    assert!(!o.quorum);

    assert!(matches!(resolve_options(WorkerOptions::default()), Err(WorkerError::Config(_))));
    let mut bad = WorkerOptions::new("w");
    bad.rabbitmq_url = "http://x".into();
    assert!(resolve_options(bad).is_err());
}

#[test]
fn amqp_urls_get_the_default_vhost() {
    let url = |u: &str| {
        let mut o = WorkerOptions::new("w");
        o.rabbitmq_url = u.into();
        resolve_options(o).unwrap().rabbitmq_url
    };

    assert_eq!(url("amqp://localhost"), "amqp://localhost/%2f");
    assert_eq!(url("amqp://u:p@rabbit:5672/"), "amqp://u:p@rabbit:5672/%2f");
    assert_eq!(url("amqp://u:p@rabbit:5672/?heartbeat=10"), "amqp://u:p@rabbit:5672/%2f?heartbeat=10");
    assert_eq!(url("amqps://rabbit/prod"), "amqps://rabbit/prod");

    assert_eq!(rabbitmq_host_port("amqp://u:p@rabbit:5673/%2f"), Some(("rabbit".into(), 5673)));
    assert_eq!(rabbitmq_host_port("amqps://rabbit/%2f"), Some(("rabbit".into(), 5671)));
    assert_eq!(rabbitmq_host_port("amqp://[::1]:5672/%2f"), Some(("::1".into(), 5672)));
}

#[test]
fn options_from_env() {
    let env: HashMap<&str, &str> = [
        ("MILVAION_WORKER_ID", "env-worker"),
        ("MILVAION_RABBITMQ_URL", "amqp://u:p@rabbit:5672/"),
        ("MILVAION_RABBITMQ_QUEUE_TYPE", "Quorum"),
        ("MILVAION_REDIS_URL", "false"),
        ("MILVAION_REDIS_KEY_PREFIX", "Custom:"),
        ("MILVAION_MAX_PARALLEL_JOBS", "7"),
        ("MILVAION_EXECUTION_TIMEOUT_SECONDS", "0"),
        ("MILVAION_HEARTBEAT_INTERVAL_SECONDS", "15"),
    ]
    .into_iter()
    .collect();

    let o = WorkerOptions::from_lookup(|k| env.get(k).map(|v| v.to_string())).unwrap();
    assert_eq!(o.worker_id, "env-worker");
    assert_eq!(o.queue_type, QueueType::Quorum);
    assert_eq!(o.redis_url, None);
    assert_eq!(o.redis_key_prefix, "Custom:");
    assert_eq!(o.max_parallel_jobs, 7);
    assert_eq!(o.execution_timeout, Duration::ZERO);
    assert_eq!(o.heartbeat_interval, Duration::from_secs(15));

    let resolved = resolve_options(o).unwrap();
    assert!(resolved.quorum);
    assert_eq!(resolved.cancellation_channel, "Custom:cancellation_channel");

    let defaults = WorkerOptions::from_lookup(|_| None).unwrap();
    assert_eq!(defaults.redis_url.as_deref(), Some("redis://localhost:6379"));

    assert!(WorkerOptions::from_lookup(|k| (k == "MILVAION_MAX_PARALLEL_JOBS").then(|| "many".to_string())).is_err());
    assert!(
        WorkerOptions::from_lookup(|k| (k == "MILVAION_RABBITMQ_QUEUE_TYPE").then(|| "stream".to_string())).is_err()
    );
}

#[test]
fn handle_rejects_duplicates() {
    let mut worker = Worker::new(WorkerOptions::new("w")).unwrap();
    worker.handle("SendEmailJob", |_ctx| async { Ok::<_, JobError>(()) }).unwrap();
    assert!(worker.handle("sendemailjob", |_ctx| async { Ok::<_, JobError>(()) }).is_err());
    assert!(worker.handle(" ", |_ctx| async { Ok::<_, JobError>(()) }).is_err());
}

#[test]
fn registration_metadata_is_pascal_case() {
    let opts = worker_options();
    let schema = json!({"type": "object", "properties": {"to": {"type": "string"}}});
    let jobs = vec![Arc::new(Registered {
        job: resolve_job(
            "SendEmailJob",
            JobOptions::new().data_schema(schema.clone()).result_schema(json!("{\"type\":\"string\"}")),
            &opts,
        )
        .unwrap(),
        handler: handler(|_| async { Ok(Value::Null) }),
    })];

    let registration = serde_json::to_value(build_registration(&opts, &jobs)).unwrap();
    assert_eq!(registration["workerId"], "test-worker");
    assert_eq!(registration["instanceId"], "test-worker-0000abcd");
    assert_eq!(registration["displayName"], "test-worker (test-worker-0000abcd)");
    assert_eq!(registration["routingPatterns"]["SendEmailJob"], "test-worker.sendemail.*");
    assert_eq!(registration["jobTypes"], json!(["SendEmailJob"]));
    assert_eq!(registration["maxParallelJobs"], 4);
    assert_eq!(registration["version"], "1.0.0");
    let data_schema: Value =
        serde_json::from_str(registration["jobDataDefinitions"]["SendEmailJob"].as_str().unwrap()).unwrap();
    assert_eq!(data_schema, schema);
    assert_eq!(registration["jobResultDefinitions"]["SendEmailJob"], "{\"type\":\"string\"}");

    let metadata: Value = serde_json::from_str(registration["metadata"].as_str().unwrap()).unwrap();
    assert_eq!(metadata["Sdk"], "rust");
    assert_eq!(metadata["SdkVersion"], SDK_VERSION);
    assert_eq!(metadata["IsExternal"], false);
    assert!(metadata["ExternalScheduler"].is_null());
    assert!(metadata["OSVersion"].is_string());
    assert!(metadata["RuntimeVersion"].as_str().unwrap().starts_with("Rust "));
    assert_eq!(metadata["HeartbeatInterval"], 30);
    assert_eq!(metadata["JobConfigs"][0]["JobType"], "SendEmailJob");
    assert_eq!(metadata["JobConfigs"][0]["ConsumerId"], "sendemail-consumer");
    assert_eq!(metadata["JobConfigs"][0]["MaxParallelJobs"], 4);
    assert_eq!(metadata["JobConfigs"][0]["ExecutionTimeoutSeconds"], 3600);
}

#[test]
fn sdk_version_matches_cargo_toml() {
    let manifest = include_str!("../Cargo.toml");
    let version = manifest
        .lines()
        .skip_while(|l| l.trim() != "[package]")
        .find_map(|l| l.trim().strip_prefix("version = \"")?.strip_suffix('"'))
        .expect("Cargo.toml has a package version");

    assert_eq!(SDK_VERSION, version);
    assert_eq!(SDK_VERSION, env!("CARGO_PKG_VERSION"));
}

#[test]
fn results_serialize_like_other_sdks() {
    assert_eq!(serialize_result(Value::Null, "A"), "Job A completed successfully");
    assert_eq!(serialize_result(json!("text"), "A"), "text");
    assert_eq!(serialize_result(json!({"n": 1}), "A"), r#"{"n":1}"#);
    assert_eq!(serialize_result(json!(5), "A"), "5");
}

#[test]
fn status_serializes_as_number() {
    assert_eq!(serde_json::to_value(Status::TimedOut).unwrap(), json!(5));
    assert_eq!(serde_json::from_value::<Status>(json!(4)).unwrap(), Status::Cancelled);
}

// ---- Kafka ----
// The Kafka transport plugs into the processor through Settler::schedule_retry and Settler::reject.

const JOB_ID: &str = "0193a1b2-0000-7000-8000-000000000001";

/// A settler like the Kafka transport's: it schedules retries itself and dead-letters with a reason.
#[derive(Clone, Default)]
struct BrokerSettler {
    events: Arc<Mutex<Vec<String>>>,
    retries: Arc<Mutex<Vec<crate::broker::RetryRequest>>>,
    fail_retry: bool,
}

impl Settler for BrokerSettler {
    fn ack(&self) -> BoxFuture<'_, Result<(), String>> {
        self.events.lock().unwrap().push("ack".into());
        Box::pin(async { Ok(()) })
    }

    fn nack(&self, requeue: bool) -> BoxFuture<'_, Result<(), String>> {
        self.events.lock().unwrap().push(format!("nack({requeue})"));
        Box::pin(async { Ok(()) })
    }

    fn reject(&self, reason: String) -> BoxFuture<'_, Result<(), String>> {
        self.events.lock().unwrap().push(format!("reject: {reason}"));
        Box::pin(async { Ok(()) })
    }

    fn schedule_retry(&self, retry: crate::broker::RetryRequest) -> Option<BoxFuture<'_, Result<(), String>>> {
        self.retries.lock().unwrap().push(retry);
        let fail = self.fail_retry;
        Some(Box::pin(async move {
            if fail {
                Err("broker down".to_string())
            } else {
                Ok(())
            }
        }))
    }
}

fn broker_delivery(job_name: &str, retry_count: Option<&str>, settler: &BrokerSettler) -> Delivery {
    let mut d = delivery(job_name, None, None, &Settled::default());
    d.headers.insert("OccurrenceId".to_string(), HeaderValue::Bytes(OCCURRENCE.as_bytes().to_vec()));

    if let Some(n) = retry_count {
        d.headers.insert("x-retry-count".to_string(), HeaderValue::Bytes(n.as_bytes().to_vec()));
    }

    d.settler = Box::new(settler.clone());
    d
}

#[tokio::test]
async fn scheduled_retry_is_handed_to_the_broker() {
    let mut opts = worker_options();
    opts.job_heartbeat_interval = Duration::from_millis(30);
    let h = harness_with(
        opts,
        "FlakyJob",
        JobOptions::new().max_retries(3).base_retry_delay(Duration::from_millis(100)),
        handler(|_| async { Err(JobError::new("boom")) }),
    );
    let settler = BrokerSettler::default();
    let before = chrono::Utc::now().timestamp_millis();
    let started = std::time::Instant::now();

    h.run(broker_delivery("FlakyJob", Some("1"), &settler)).await;

    assert!(started.elapsed() < Duration::from_millis(150), "a broker-scheduled retry must not wait in process");
    assert_eq!(*settler.events.lock().unwrap(), vec!["ack"]);
    assert!(h.publisher.to(&h.consumer.job.queue).is_empty(), "nothing may be republished");
    assert!(h.publisher.dead_letters().is_empty());
    assert!(h.publisher.final_status().is_none(), "the occurrence stays Running while it is retried");

    let retries = settler.retries.lock().unwrap().clone();
    assert_eq!(retries.len(), 1);
    let r = &retries[0];
    assert_eq!((r.retry_count, r.max_retries), (2, 3));
    assert_eq!(r.occurrence_id, OCCURRENCE);
    assert_eq!(r.job_id, JOB_ID);
    // base * 2^retry_count of the failed attempt: 100 ms * 2
    let wait = r.not_before_ms - before;
    assert!((200..400).contains(&wait), "due {wait} ms after the attempt");

    // Job heartbeats continue during the backoff, then stop.
    let running_before = h.publisher.statuses().len();
    tokio::time::sleep(Duration::from_millis(300)).await;
    let running_after = h.publisher.statuses().len();
    assert!(running_after >= running_before + 2, "expected job heartbeats during the backoff");
    tokio::time::sleep(Duration::from_millis(100)).await;
    assert_eq!(h.publisher.statuses().len(), running_after, "heartbeats stop once the retry is due");
}

#[tokio::test]
async fn failed_scheduled_retry_is_rejected() {
    let h = harness("FlakyJob", JobOptions::default(), handler(|_| async { Err(JobError::new("boom")) }));
    let settler = BrokerSettler { fail_retry: true, ..Default::default() };

    h.run(broker_delivery("FlakyJob", None, &settler)).await;

    let events = settler.events.lock().unwrap().clone();
    assert_eq!(events.len(), 1, "{events:?}");
    assert!(events[0].starts_with("reject: ") && events[0].contains("broker down"), "{events:?}");
}

#[tokio::test]
async fn retry_after_is_acked_without_scheduling_a_retry() {
    let at = chrono::Utc::now() + chrono::Duration::hours(1);
    let h = harness(
        "QuotaJob",
        JobOptions::default(),
        handler(move |_| async move { Err(JobError::retry_after(at, "later")) }),
    );
    let settler = BrokerSettler::default();

    h.run(broker_delivery("QuotaJob", None, &settler)).await;

    assert_eq!(*settler.events.lock().unwrap(), vec!["ack"]);
    assert!(settler.retries.lock().unwrap().is_empty());
    assert!(h.publisher.dead_letters().is_empty());
    assert_eq!(h.publisher.final_status().unwrap()["retryAfter"], crate::protocol::time_string(at));
}

#[tokio::test]
async fn reject_carries_the_reason() {
    let h = harness("AJob", JobOptions::default(), handler(|_| async { Ok(Value::Null) }));
    let settler = BrokerSettler::default();
    let mut d = broker_delivery("AJob", None, &settler);
    d.body = b"not json".to_vec();

    h.run(d).await;

    assert_eq!(*settler.events.lock().unwrap(), vec!["reject: the message is not a valid job"]);
}

#[tokio::test]
async fn status_logs_and_dead_letter_carry_the_record_key() {
    let h = harness(
        "StrictJob",
        JobOptions::default(),
        handler(|ctx: JobContext| async move {
            ctx.info("about to fail");
            Err(JobError::permanent("bad"))
        }),
    );
    let settled = Settled::default();

    h.run(delivery("StrictJob", None, None, &settled)).await;

    let statuses = h.publisher.to(QUEUE_STATUS_UPDATES);
    assert!(!statuses.is_empty());
    assert!(statuses.iter().all(|m| m.options.key.as_deref() == Some(OCCURRENCE)));

    let logs = h.publisher.to(QUEUE_WORKER_LOGS);
    assert!(!logs.is_empty());
    assert!(logs.iter().all(|m| m.options.key.as_deref() == Some(OCCURRENCE)));

    let dlq = h.publisher.dead_letters();
    assert_eq!(dlq.len(), 1);
    assert_eq!(dlq[0].options.key.as_deref(), Some(OCCURRENCE));
}

#[test]
fn kafka_options_resolve() {
    let k = resolve_kafka(KafkaOptions::default(), 8).unwrap();
    assert_eq!(k.brokers, vec!["localhost:9092"]);
    assert_eq!((k.num_partitions, k.replication_factor, k.max_in_flight_per_partition), (12, -1, 8));
    assert!(k.auto_create_topics && !k.tls && k.sasl.is_none());
    assert_eq!(k.topic_prefix, "");

    let k = resolve_kafka(
        KafkaOptions {
            brokers: vec!["a:9092, b:9093".into(), "c:9094".into()],
            topic_prefix: "prod.".into(),
            num_partitions: 0,
            replication_factor: 0,
            max_in_flight_per_partition: 3,
            tls_ca_file: Some("/ca.pem".into()),
            sasl_mechanism: Some(KafkaSaslMechanism::ScramSha512),
            sasl_username: "user".into(),
            sasl_password: "secret".into(),
            ..Default::default()
        },
        8,
    )
    .unwrap();
    assert_eq!(k.brokers, vec!["a:9092", "b:9093", "c:9094"]);
    assert_eq!((k.num_partitions, k.replication_factor, k.max_in_flight_per_partition), (12, -1, 3));
    assert!(k.tls, "a CA file turns TLS on");

    let settings: HashMap<&str, String> = k.client_settings("i-1").into_iter().collect();
    assert_eq!(settings["bootstrap.servers"], "a:9092,b:9093,c:9094");
    assert_eq!(settings["client.id"], "i-1");
    assert_eq!(settings["security.protocol"], "sasl_ssl");
    assert_eq!(settings["sasl.mechanism"], "SCRAM-SHA-512");
    assert_eq!(settings["sasl.username"], "user");
    assert_eq!(settings["sasl.password"], "secret");
    assert_eq!(settings["ssl.ca.location"], "/ca.pem");

    let plain: HashMap<&str, String> =
        resolve_kafka(KafkaOptions::default(), 1).unwrap().client_settings("i").into_iter().collect();
    assert_eq!(plain["security.protocol"], "plaintext");
    assert!(!plain.contains_key("sasl.mechanism"));

    let invalid = |o: KafkaOptions| resolve_kafka(o, 4).is_err();
    assert!(invalid(KafkaOptions { topic_prefix: "prod/".into(), ..Default::default() }));
    assert!(invalid(KafkaOptions { topic_prefix: "prød.".into(), ..Default::default() }));
    assert!(invalid(KafkaOptions { num_partitions: -1, ..Default::default() }));
    assert!(invalid(KafkaOptions { replication_factor: -2, ..Default::default() }));
    assert!(invalid(KafkaOptions { tls_cert_file: Some("c.pem".into()), ..Default::default() }));
    assert!(invalid(KafkaOptions { sasl_mechanism: Some(KafkaSaslMechanism::Plain), ..Default::default() }));
    assert!(!invalid(KafkaOptions { topic_prefix: "Prod_1-a.".into(), ..Default::default() }));

    assert_eq!(KafkaSaslMechanism::parse("SCRAM-SHA-256"), Some(KafkaSaslMechanism::ScramSha256));
    assert_eq!(KafkaSaslMechanism::parse("scram_sha_512"), Some(KafkaSaslMechanism::ScramSha512));
    assert_eq!(KafkaSaslMechanism::parse("Plain"), Some(KafkaSaslMechanism::Plain));
    assert_eq!(KafkaSaslMechanism::parse("gssapi"), None);
}

#[test]
fn kafka_options_from_env() {
    let env: HashMap<&str, &str> = [
        ("MILVAION_WORKER_ID", "env-worker"),
        ("MILVAION_TRANSPORT", "Kafka"),
        ("KAFKA_BOOTSTRAP_SERVERS", "ignored:9092"),
        ("MILVAION_KAFKA_BOOTSTRAP_SERVERS", "k1:9092,k2:9092"),
        ("MILVAION_KAFKA_TLS", "TRUE"),
        ("MILVAION_KAFKA_TLS_CA_FILE", "/ca.pem"),
        ("MILVAION_KAFKA_TLS_CERT_FILE", "/cert.pem"),
        ("MILVAION_KAFKA_TLS_KEY_FILE", "/key.pem"),
        ("MILVAION_KAFKA_SASL_MECHANISM", "scram-sha-256"),
        ("MILVAION_KAFKA_SASL_USERNAME", "user"),
        ("MILVAION_KAFKA_SASL_PASSWORD", "pa ss"),
        ("MILVAION_KAFKA_TOPIC_PREFIX", "stage."),
        ("MILVAION_KAFKA_AUTO_CREATE_TOPICS", "false"),
        ("MILVAION_KAFKA_PARTITIONS", "6"),
        ("MILVAION_KAFKA_REPLICATION_FACTOR", "3"),
        ("MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION", "2"),
    ]
    .into_iter()
    .collect();

    let o = WorkerOptions::from_lookup(|k| env.get(k).map(|v| v.to_string())).unwrap();
    assert_eq!(o.transport, Transport::Kafka);
    let k = &o.kafka;
    assert_eq!(k.brokers, vec!["k1:9092,k2:9092"]);
    assert!(k.tls);
    assert_eq!(k.tls_ca_file.as_deref(), Some("/ca.pem"));
    assert_eq!(k.tls_cert_file.as_deref(), Some("/cert.pem"));
    assert_eq!(k.tls_key_file.as_deref(), Some("/key.pem"));
    assert_eq!(k.sasl_mechanism, Some(KafkaSaslMechanism::ScramSha256));
    assert_eq!((k.sasl_username.as_str(), k.sasl_password.as_str()), ("user", "pa ss"));
    assert_eq!(k.topic_prefix, "stage.");
    assert!(!k.auto_create_topics);
    assert_eq!((k.num_partitions, k.replication_factor, k.max_in_flight_per_partition), (6, 3, 2));

    let fallback = WorkerOptions::from_lookup(|k| {
        [("KAFKA_BOOTSTRAP_SERVERS", "fallback:9092"), ("MILVAION_KAFKA_NUM_PARTITIONS", "4")]
            .into_iter()
            .find(|(key, _)| *key == k)
            .map(|(_, v)| v.to_string())
    })
    .unwrap();
    assert_eq!(fallback.transport, Transport::RabbitMq);
    assert_eq!(fallback.kafka.brokers, vec!["fallback:9092"]);
    assert_eq!(fallback.kafka.num_partitions, 4);
    assert!(fallback.kafka.auto_create_topics);

    let one = |key: &'static str, value: &'static str| {
        WorkerOptions::from_lookup(move |k| (k == key).then(|| value.to_string()))
    };
    assert!(one("MILVAION_TRANSPORT", "nats").is_err());
    assert!(one("MILVAION_KAFKA_SASL_MECHANISM", "gssapi").is_err());
    assert!(one("MILVAION_KAFKA_PARTITIONS", "many").is_err());
}

#[cfg(not(feature = "kafka"))]
#[test]
fn kafka_transport_needs_the_feature() {
    let options = WorkerOptions { transport: Transport::Kafka, ..WorkerOptions::new("w") };
    let error = Worker::new(options).err().expect("Kafka must be rejected without the kafka feature");
    assert!(error.to_string().contains("\"kafka\" feature"), "{error}");
}

#[cfg(feature = "kafka")]
mod kafka_wire {
    use super::*;
    use crate::kafka::*;

    #[test]
    fn worker_segment_is_sanitised() {
        assert_eq!(worker_segment("Report-Worker"), "report-worker");
        assert_eq!(worker_segment("mail worker/eu"), "mail_worker_eu");
        assert_eq!(worker_segment("a.b_c-D9"), "a.b_c-d9");
        assert_eq!(worker_segment("çalışan"), "_al__an");
    }

    #[test]
    fn topic_names() {
        let t = KafkaTopics::new("prod.", "Report Worker");
        assert_eq!(t.jobs, "prod.milvaion.jobs.report_worker");
        assert_eq!(t.retry, "prod.milvaion.jobs.report_worker.retry");
        assert_eq!(t.status, "prod.milvaion.status");
        assert_eq!(t.logs, "prod.milvaion.logs");
        assert_eq!(t.heartbeats, "prod.milvaion.heartbeats");
        assert_eq!(t.registrations, "prod.milvaion.registrations");
        assert_eq!(t.dlq, "prod.milvaion.dlq");
        assert_eq!(t.group, "prod.milvaion.worker.report_worker");

        let t = KafkaTopics::new("", "w");
        assert_eq!(t.jobs, "milvaion.jobs.w");
        assert_eq!(t.group, "milvaion.worker.w");
    }

    #[test]
    fn headers_are_strings() {
        assert_eq!(header_bytes(&HeaderValue::Int(3)), Some(b"3".to_vec()));
        assert_eq!(header_bytes(&HeaderValue::Int(-1)), Some(b"-1".to_vec()));
        assert_eq!(header_bytes(&HeaderValue::Str("x".into())), Some(b"x".to_vec()));
        assert_eq!(header_bytes(&HeaderValue::Bytes(b"y".to_vec())), Some(b"y".to_vec()));
        assert_eq!(header_bytes(&HeaderValue::Other), None);

        let sorted = sorted_headers(vec![
            ("b".to_string(), b"1".to_vec()),
            ("a".to_string(), b"2".to_vec()),
            ("b".to_string(), b"3".to_vec()),
        ]);
        assert_eq!(sorted, vec![("a".to_string(), b"2".to_vec()), ("b".to_string(), b"3".to_vec())]);

        let map = header_map(&[
            ("x-retry-count".to_string(), b"2".to_vec()),
            ("x-not-before".to_string(), b"1700000000123".to_vec()),
            ("OccurrenceId".to_string(), OCCURRENCE.as_bytes().to_vec()),
        ]);
        assert_eq!(parse_retry_count(&map), 2);
        assert_eq!(parse_occurrence_id(&map, None).as_deref(), Some(OCCURRENCE));
        assert_eq!(not_before(&map), Some(1_700_000_000_123));

        let bad = header_map(&[("x-not-before".to_string(), b"soon".to_vec())]);
        assert_eq!(not_before(&bad), None);
        assert_eq!(not_before(&HashMap::new()), None);
    }

    #[test]
    fn offset_tracker_commits_the_contiguous_range() {
        let mut t = OffsetTracker::default();
        assert_eq!(t.commit_position(), None);

        for offset in [10, 11, 12, 13] {
            assert!(t.add(offset));
        }

        assert!(!t.add(12), "older offsets are ignored");
        assert!(!t.add(13), "repeated offsets are ignored");

        assert!(t.done(11));
        assert_eq!(t.commit_position(), None, "10 is still running");

        assert!(t.done(10));
        assert_eq!(t.commit_position(), Some(12));
        assert_eq!(t.outstanding(), 2);

        assert!(!t.done(99), "unknown offset");
        assert!(t.done(13));
        assert_eq!(t.commit_position(), Some(12), "12 holds the position back");

        assert!(t.done(12));
        assert_eq!(t.commit_position(), Some(14));
        assert_eq!(t.outstanding(), 0);

        assert!(t.add(20), "gaps (compacted or transactional offsets) are fine");
        assert!(t.done(20));
        assert_eq!(t.commit_position(), Some(21));
    }
}
