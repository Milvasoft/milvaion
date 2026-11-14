//! Runs one job delivery: status reports, timeout, cancellation, retries and the dead letter queue.

use crate::broker::{BoxFuture, Delivery, HeaderValue, PublishOptions, Publisher, RetryRequest};
use crate::context::{JobContext, RunControl};
use crate::error::{AbortReason, JobError};
use crate::log_buffer::LogBuffer;
use crate::options::{ResolvedJob, ResolvedOptions};
use crate::protocol::*;
use serde_json::{json, Value};
use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

/// A registered handler: takes the context, returns the result serialized to JSON.
pub(crate) type Handler = Arc<dyn Fn(JobContext) -> BoxFuture<'static, Result<Value, JobError>> + Send + Sync>;

pub(crate) struct Registered {
    pub job: ResolvedJob,
    pub handler: Handler,
}

/// The stop handles of the jobs running on this instance, by lowercased occurrence id.
#[derive(Default)]
pub(crate) struct CancelRegistry {
    running: Mutex<HashMap<String, Arc<RunControl>>>,
}

impl CancelRegistry {
    fn register(&self, occurrence_id: &str, control: Arc<RunControl>) {
        self.running.lock().unwrap().insert(occurrence_id.to_lowercase(), control);
    }

    fn unregister(&self, occurrence_id: &str) {
        self.running.lock().unwrap().remove(&occurrence_id.to_lowercase());
    }

    /// Returns false when the job is not running on this instance.
    pub fn cancel(&self, occurrence_id: &str, reason: Option<String>) -> bool {
        let control = self.running.lock().unwrap().get(&occurrence_id.to_lowercase()).cloned();

        match control {
            Some(control) => {
                control.abort(AbortReason::Cancelled(reason));
                true
            }
            None => false,
        }
    }

    pub fn abort_all(&self, reason: AbortReason) {
        for control in self.running.lock().unwrap().values() {
            control.abort(reason.clone());
        }
    }
}

pub(crate) struct Processor {
    pub opts: Arc<ResolvedOptions>,
    pub jobs: HashMap<String, Arc<Registered>>,
    pub publisher: Arc<dyn Publisher>,
    pub logs: Arc<LogBuffer>,
    pub cancels: Arc<CancelRegistry>,
    pub on_count_changed: Option<Box<dyn Fn() + Send + Sync>>,
    /// Cancelled when the worker starts stopping: jobs waiting out a retry delay give their message back.
    pub shutdown: CancellationToken,
    /// Delays between attempts to publish a final status.
    pub status_retry_delays: Vec<Duration>,
    running: AtomicUsize,
    idle: Notify,
}

enum Outcome {
    Completed(String),
    Failed(JobError),
    Aborted(AbortReason),
}

#[derive(Default)]
struct StatusFields {
    start: Option<chrono::DateTime<chrono::Utc>>,
    end: Option<chrono::DateTime<chrono::Utc>>,
    duration: Option<Duration>,
    result: Option<String>,
    exception: Option<String>,
    retry_after: Option<chrono::DateTime<chrono::Utc>>,
}

impl Processor {
    pub fn new(
        opts: Arc<ResolvedOptions>,
        jobs: HashMap<String, Arc<Registered>>,
        publisher: Arc<dyn Publisher>,
        logs: Arc<LogBuffer>,
        cancels: Arc<CancelRegistry>,
    ) -> Self {
        Self {
            opts,
            jobs,
            publisher,
            logs,
            cancels,
            on_count_changed: None,
            shutdown: CancellationToken::new(),
            status_retry_delays: vec![Duration::from_millis(500), Duration::from_secs(2), Duration::from_secs(5)],
            running: AtomicUsize::new(0),
            idle: Notify::new(),
        }
    }

    pub fn running_jobs(&self) -> usize {
        self.running.load(Ordering::SeqCst)
    }

    /// Returns true once no job is running, or false after `timeout`.
    pub async fn wait_for_idle(&self, timeout: Duration) -> bool {
        let deadline = tokio::time::Instant::now() + timeout;

        loop {
            let notified = self.idle.notified();

            if self.running_jobs() == 0 {
                return true;
            }

            if tokio::time::timeout_at(deadline, notified).await.is_err() {
                return self.running_jobs() == 0;
            }
        }
    }

    /// Runs one delivery taken from `queue` (a RabbitMQ queue or a Kafka topic).
    pub async fn handle(self: Arc<Self>, delivery: Delivery, queue: String) {
        self.running.fetch_add(1, Ordering::SeqCst);
        self.count_changed();

        // A panic in the SDK itself must not leave the message unsettled or the count wrong.
        let this = self.clone();
        let task = tokio::spawn(async move { this.process(delivery, &queue).await });

        if let Err(error) = task.await {
            tracing::error!(%error, "unexpected panic while processing a job delivery; the broker will redeliver it");
        }

        if self.running.fetch_sub(1, Ordering::SeqCst) == 1 {
            self.idle.notify_waiters();
        }

        self.count_changed();
    }

    fn count_changed(&self) {
        if let Some(callback) = &self.on_count_changed {
            callback();
        }
    }

    async fn process(self: &Arc<Self>, d: Delivery, queue: &str) {
        let job: ScheduledJob = match serde_json::from_slice::<ScheduledJob>(&d.body) {
            Ok(job) if !job.id.is_empty() && !job.job_name_in_worker.is_empty() => job,
            _ => {
                tracing::error!(queue = %queue, "received a message that is not a valid job; sending it to the dead letter queue");
                settle(d.settler.reject("the message is not a valid job".to_string()).await);
                return;
            }
        };

        let Some(occurrence_id) = parse_occurrence_id(&d.headers, d.correlation_id.as_deref()) else {
            tracing::error!(job = %job.id, "job arrived without an occurrence id; sending it to the dead letter queue");
            settle(d.settler.reject("the message has no occurrence id".to_string()).await);
            return;
        };

        let run = JobRun { p: self.clone(), retry_count: parse_retry_count(&d.headers), occurrence_id, job };

        if d.redelivered {
            tracing::warn!(job = %run.job.id, occurrence = %run.occurrence_id, "job was redelivered; the previous attempt may have crashed");
            run.status(Status::Running, StatusFields { start: Some(chrono::Utc::now()), ..Default::default() }, true)
                .await;
            run.log(
                LogLevel::Warning,
                "⚠️ Job redelivered - previous execution may have crashed or timed out. Re-executing from the beginning.",
                Some(json!({"Reason": "RabbitMQ redelivery detected", "WorkerId": self.opts.instance_id})),
                "Redelivery",
            );
        }

        let control = Arc::new(RunControl::new());
        self.cancels.register(&run.occurrence_id, control.clone());

        self.execute(&d, queue, &run, &control).await;

        self.cancels.unregister(&run.occurrence_id);
    }

    async fn execute(&self, d: &Delivery, queue: &str, run: &JobRun, control: &Arc<RunControl>) {
        let start = chrono::Utc::now();
        let started = Instant::now();

        run.status(Status::Running, StatusFields { start: Some(start), ..Default::default() }, true).await;
        run.flush_logs().await;

        let Some(registered) = self.jobs.get(&run.job.job_name_in_worker.to_lowercase()).cloned() else {
            let exception = format!("No IJob implementation registered for type: {}", run.job.job_name_in_worker);
            tracing::error!("{exception}");

            run.status(
                Status::Failed,
                StatusFields {
                    end: Some(chrono::Utc::now()),
                    exception: Some(exception.clone()),
                    ..Default::default()
                },
                true,
            )
            .await;
            self.dead_letter(d, run, format!("[PERMANENT FAILURE] {exception}"), run.retry_count, 0).await;

            return;
        };

        let timeout = match run.job.execution_timeout_seconds {
            Some(seconds) if seconds > 0.0 => Duration::from_secs_f64(seconds),
            Some(_) => Duration::ZERO,
            None => registered.job.execution_timeout,
        };

        let outcome = self.run_handler(&registered, run, control, timeout).await;

        let mut timing = StatusFields {
            start: Some(start),
            end: Some(chrono::Utc::now()),
            duration: Some(started.elapsed()),
            ..Default::default()
        };

        match outcome {
            Outcome::Aborted(reason) => self.finish_aborted(d, run, reason, timing, timeout).await,
            Outcome::Failed(error) => self.finish_failed(d, queue, &registered, run, error, timing).await,
            Outcome::Completed(result) => {
                run.flush_logs().await;
                timing.result = Some(result);
                run.status(Status::Completed, timing, true).await;
                settle(d.settler.ack().await);
            }
        }
    }

    async fn run_handler(
        &self,
        registered: &Registered,
        run: &JobRun,
        control: &Arc<RunControl>,
        timeout: Duration,
    ) -> Outcome {
        let stop_heartbeat = CancellationToken::new();
        let _stop_heartbeat_on_exit = stop_heartbeat.clone().drop_guard();

        let interval = self.opts.job_heartbeat_interval;

        if !interval.is_zero() {
            let run = run.clone();
            let stop = stop_heartbeat.clone();

            tokio::spawn(async move {
                let mut ticker = tokio::time::interval_at(tokio::time::Instant::now() + interval, interval);

                loop {
                    tokio::select! {
                        _ = stop.cancelled() => return,
                        _ = ticker.tick() => run.status(Status::Running, StatusFields::default(), false).await,
                    }
                }
            });
        }

        let ctx = JobContext::new(
            run.occurrence_id.clone(),
            run.retry_count,
            self.opts.instance_id.clone(),
            run.job.clone(),
            control.clone(),
            self.logs.clone(),
        );

        let mut task = tokio::spawn((registered.handler)(ctx));

        let deadline = async {
            if timeout.is_zero() {
                std::future::pending::<()>().await
            } else {
                tokio::time::sleep(timeout).await
            }
        };

        let outcome = tokio::select! {
            biased;

            joined = &mut task => match joined {
                Ok(Ok(value)) => Outcome::Completed(serialize_result(value, &run.job.job_name_in_worker)),
                // A handler that fails because it was stopped reports the stop, not the error.
                Ok(Err(error)) => match control.reason() {
                    Some(reason) => Outcome::Aborted(reason),
                    None => Outcome::Failed(error),
                },
                Err(join_error) if join_error.is_panic() => {
                    let payload = join_error.into_panic();
                    let message = payload
                        .downcast_ref::<&str>()
                        .map(|s| s.to_string())
                        .or_else(|| payload.downcast_ref::<String>().cloned())
                        .unwrap_or_else(|| "handler panicked".to_string());
                    Outcome::Failed(JobError::panic(format!("panic: {message}")))
                }
                Err(_) => Outcome::Aborted(control.reason().unwrap_or(AbortReason::WorkerStopping)),
            },
            _ = control.token.cancelled() => Outcome::Aborted(control.reason().unwrap_or(AbortReason::Cancelled(None))),
            _ = deadline => {
                control.abort(AbortReason::TimedOut);
                Outcome::Aborted(control.reason().unwrap_or(AbortReason::TimedOut))
            }
        };

        // The occurrence settles as soon as the job is stopped; the handler is dropped at its next await.
        if matches!(outcome, Outcome::Aborted(_)) {
            task.abort();
        }

        outcome
    }

    async fn finish_aborted(
        &self,
        d: &Delivery,
        run: &JobRun,
        reason: AbortReason,
        mut timing: StatusFields,
        timeout: Duration,
    ) {
        match &reason {
            AbortReason::WorkerStopping => {
                // Nobody cancelled this job; it goes back to the queue and runs again.
                tracing::warn!(job = %run.job.id, occurrence = %run.occurrence_id, "job was interrupted by worker shutdown; returning it to the queue");
                run.flush_logs().await;
                settle(d.settler.nack(true).await);
                return;
            }
            AbortReason::TimedOut => {
                let seconds = format_seconds(timeout);
                run.log(
                    LogLevel::Error,
                    &format!("Job execution exceeded timeout limit of {seconds} seconds"),
                    None,
                    "UserDefined",
                );
                run.flush_logs().await;
                let elapsed = timing.duration.unwrap_or_default().as_millis();
                timing.exception =
                    Some(format!("Job execution exceeded maximum timeout of {seconds} seconds ({elapsed}ms elapsed)"));
                run.status(Status::TimedOut, timing, true).await;
            }
            AbortReason::Cancelled(_) => {
                run.log(
                    LogLevel::Warning,
                    "Job execution was cancelled",
                    Some(json!({"Reason": reason.to_string()})),
                    "UserCode",
                );
                run.flush_logs().await;
                timing.result = Some("Job cancelled by user or system".to_string());
                run.status(Status::Cancelled, timing, true).await;
            }
        }

        settle(d.settler.ack().await);
    }

    async fn finish_failed(
        &self,
        d: &Delivery,
        queue: &str,
        registered: &Registered,
        run: &JobRun,
        error: JobError,
        mut timing: StatusFields,
    ) {
        if let Some(retry_at) = error.retry_at() {
            // Not retried here and not dead-lettered: Milvaion decides whether to run it again.
            let at = time_string(retry_at);
            let error = error.with_message_suffix(&format!(" (retry requested at {at})"));
            tracing::warn!(job = %run.job.id, occurrence = %run.occurrence_id, retry_after = %at, "job asked to be retried later");
            run.log(
                LogLevel::Warning,
                &format!(
                    "⏰ Job asked to be retried at {at}. Milvaion reruns workflow steps at that time; a standalone job ends here as Failed."
                ),
                Some(json!({"RetryAfter": at})),
                "Retry",
            );
            run.flush_logs().await;
            timing.exception = Some(error.format());
            timing.retry_after = Some(retry_at);
            run.status(Status::Failed, timing, true).await;
            settle(d.settler.ack().await);
            return;
        }

        let exception = error.format();

        if error.is_permanent() {
            run.log_error(&format!("Permanent job failure (will not retry): {error}"), &error);
            run.log(
                LogLevel::Error,
                "❌ Permanent failure - job will not be retried. Moving to Dead Letter Queue (DLQ).",
                Some(json!({"FailureType": "Permanent", "FinalStatus": "MovedToDLQ"})),
                "Retry",
            );
            run.flush_logs().await;
            timing.exception = Some(exception.clone());
            run.status(Status::Failed, timing, true).await;
            self.dead_letter(d, run, format!("[PERMANENT FAILURE] {exception}"), run.retry_count, 0).await;
            return;
        }

        run.log_error(&format!("Job execution failed: {error}"), &error);

        let max_retries = registered.job.max_retries;

        if run.retry_count < max_retries {
            let delay = registered.job.base_retry_delay.saturating_mul(2u32.saturating_pow(run.retry_count));
            let next = chrono::Utc::now() + chrono::Duration::from_std(delay).unwrap_or_default();

            run.log(
                LogLevel::Warning,
                &format!(
                    "⚠️ Job failed (attempt {}/{}). Retrying in {}s...",
                    run.retry_count + 1,
                    max_retries,
                    format_seconds(delay)
                ),
                Some(json!({
                    "RetryAttempt": run.retry_count + 1,
                    "MaxRetries": max_retries,
                    "DelaySeconds": delay.as_secs_f64(),
                    "NextRetryTime": time_string(next),
                })),
                "Retry",
            );
            run.flush_logs().await;

            self.retry(d, queue, run, delay, max_retries).await;
            return;
        }

        let final_exception = format!("Max retries ({max_retries}) exceeded. Original exception: {exception}");

        run.log(
            LogLevel::Error,
            &format!("❌ Job failed after {max_retries} retries. Moving to Dead Letter Queue (DLQ)."),
            Some(json!({"TotalRetries": max_retries, "FinalStatus": "MovedToDLQ"})),
            "Retry",
        );
        run.flush_logs().await;

        timing.end = Some(chrono::Utc::now());
        timing.exception = Some(final_exception.clone());
        run.status(Status::Failed, timing, true).await;
        self.dead_letter(d, run, final_exception, max_retries, max_retries).await;
    }

    /// Waits out the backoff, republishes the job with a higher retry count and acks the original.
    /// The original stays unacked meanwhile, so a crash during the wait gets it redelivered. A broker
    /// that schedules retries itself (Kafka) gets the retry right away instead.
    async fn retry(&self, d: &Delivery, queue: &str, run: &JobRun, delay: Duration, max_retries: u32) {
        let due = tokio::time::Instant::now() + delay;
        let retry = RetryRequest {
            job_id: run.job.id.clone(),
            occurrence_id: run.occurrence_id.clone(),
            retry_count: run.retry_count + 1,
            max_retries,
            not_before_ms: (chrono::Utc::now() + chrono::Duration::from_std(delay).unwrap_or_default())
                .timestamp_millis(),
        };

        if let Some(scheduled) = d.settler.schedule_retry(retry) {
            let scheduled = tokio::time::timeout(Duration::from_secs(30), scheduled)
                .await
                .unwrap_or_else(|_| Err("timed out".to_string()));

            if let Err(error) = scheduled {
                tracing::error!(job = %run.job.id, %error, "failed to schedule a job for retry; dead-lettering the message");
                settle(d.settler.reject(format!("the job could not be scheduled for retry: {error}")).await);
                return;
            }

            settle(d.settler.ack().await);

            // The occurrence stays Running until the next attempt starts, so its heartbeat continues.
            tokio::spawn(heartbeat_while_waiting(run.clone(), due, CancellationToken::new()));
            return;
        }

        let interval = self.opts.job_heartbeat_interval;
        let mut remaining = delay;

        while !remaining.is_zero() {
            let step = if !interval.is_zero() && interval < remaining { interval } else { remaining };

            tokio::select! {
                _ = self.shutdown.cancelled() => {
                    tracing::warn!(job = %run.job.id, "worker is stopping during a retry delay; returning the job to the queue");
                    settle(d.settler.nack(true).await);
                    return;
                }
                _ = tokio::time::sleep(step) => {}
            }

            remaining -= step;

            if !remaining.is_zero() && !interval.is_zero() {
                run.status(Status::Running, StatusFields::default(), false).await;
            }
        }

        let published = self
            .publisher
            .publish(
                "",
                queue,
                d.body.clone(),
                PublishOptions {
                    persistent: true,
                    correlation_id: Some(run.occurrence_id.clone()),
                    headers: vec![
                        ("CorrelationId".into(), HeaderValue::Bytes(run.occurrence_id.clone().into_bytes())),
                        ("x-retry-count".into(), HeaderValue::Int(run.retry_count as i64 + 1)),
                        ("MaxRetries".into(), HeaderValue::Int(max_retries as i64)),
                    ],
                    ..Default::default()
                },
            )
            .await;

        if let Err(error) = published {
            tracing::error!(job = %run.job.id, %error, "failed to republish a job for retry; dead-lettering the message");
            settle(d.settler.reject(format!("the job could not be republished for retry: {error}")).await);
            return;
        }

        settle(d.settler.ack().await);
    }

    async fn dead_letter(&self, d: &Delivery, run: &JobRun, exception: String, retry_count: u32, max_retries: u32) {
        let body = serde_json::to_vec(&DlqMessage {
            id: run.job.id.clone(),
            display_name: run.job.display_name.clone(),
            job_name_in_worker: run.job.job_name_in_worker.clone(),
            job_data: run.job.job_data.clone(),
            execute_at: run.job.execute_at.clone(),
            status: Status::Failed,
            exception,
        })
        .expect("a DLQ message always serializes");

        let published = self
            .publisher
            .publish(
                EXCHANGE_DEAD_LETTER,
                DEAD_LETTER_ROUTING_KEY,
                body,
                PublishOptions {
                    persistent: true,
                    correlation_id: Some(run.occurrence_id.clone()),
                    headers: vec![
                        ("CorrelationId".into(), HeaderValue::Bytes(run.occurrence_id.clone().into_bytes())),
                        ("x-retry-count".into(), HeaderValue::Int(retry_count as i64)),
                        ("MaxRetries".into(), HeaderValue::Int(max_retries as i64)),
                    ],
                    key: Some(run.occurrence_id.clone()),
                },
            )
            .await;

        if let Err(error) = published {
            // The queue dead-letters a rejected message itself, just without the exception details.
            tracing::error!(job = %run.job.id, %error, "failed to publish to the dead letter queue; rejecting the message instead");
            settle(d.settler.reject(format!("the dead letter message could not be published: {error}")).await);
            return;
        }

        settle(d.settler.ack().await);
    }
}

impl Processor {
    /// Keeps the occurrence of a job record that is not due yet alive: reports it Running and
    /// sends its job heartbeats until `due` or until `stop` is cancelled. Kafka holds a retry
    /// record in memory until its time comes.
    #[cfg_attr(not(feature = "kafka"), allow(dead_code))]
    pub(crate) fn hold(
        self: &Arc<Self>,
        body: &[u8],
        headers: &HashMap<String, HeaderValue>,
        due: tokio::time::Instant,
        stop: CancellationToken,
    ) {
        let Ok(job) = serde_json::from_slice::<ScheduledJob>(body) else { return };
        let Some(occurrence_id) = parse_occurrence_id(headers, None) else { return };

        if job.id.is_empty() {
            return;
        }

        let run = JobRun { p: self.clone(), job, occurrence_id, retry_count: parse_retry_count(headers) };

        tokio::spawn(async move {
            if stop.is_cancelled() {
                return;
            }

            run.status(Status::Running, StatusFields::default(), false).await;
            heartbeat_while_waiting(run, due, stop).await;
        });
    }
}

/// Sends Running statuses every job heartbeat interval for an occurrence that waits for its next
/// attempt, until `due`, until `stop` is cancelled or until the worker stops.
async fn heartbeat_while_waiting(run: JobRun, due: tokio::time::Instant, stop: CancellationToken) {
    let interval = run.p.opts.job_heartbeat_interval;

    if interval.is_zero() {
        return;
    }

    loop {
        let now = tokio::time::Instant::now();

        if now >= due {
            return;
        }

        tokio::select! {
            _ = tokio::time::sleep((due - now).min(interval)) => {}
            _ = stop.cancelled() => return,
            _ = run.p.shutdown.cancelled() => return,
        }

        if tokio::time::Instant::now() < due {
            run.status(Status::Running, StatusFields::default(), false).await;
        }
    }
}

fn settle(result: Result<(), String>) {
    if let Err(error) = result {
        // An ack or nack on a channel that has closed: the broker redelivers the message.
        tracing::warn!(%error, "could not settle a message; the broker will redeliver it");
    }
}

/// The state and reporting helpers of one execution.
#[derive(Clone)]
struct JobRun {
    p: Arc<Processor>,
    job: ScheduledJob,
    occurrence_id: String,
    retry_count: u32,
}

impl JobRun {
    fn log(&self, level: LogLevel, message: &str, data: Option<Value>, category: &str) {
        self.p.logs.add(
            &self.occurrence_id,
            &self.p.opts.instance_id,
            OccurrenceLog {
                timestamp: now(),
                level,
                message: message.to_string(),
                data,
                category: category.to_string(),
                exception_type: None,
            },
        );
    }

    fn log_error(&self, message: &str, error: &JobError) {
        let mut data = json!({"ExceptionType": error.type_name(), "StackTrace": error.format()});

        if let Some(inner) = error.inner_message() {
            data["InnerException"] = Value::String(inner);
        }

        self.p.logs.add(
            &self.occurrence_id,
            &self.p.opts.instance_id,
            OccurrenceLog {
                timestamp: now(),
                level: LogLevel::Error,
                message: message.to_string(),
                data: Some(data),
                category: "UserDefined".to_string(),
                exception_type: Some(error.type_name().to_string()),
            },
        );
    }

    async fn flush_logs(&self) {
        if let Err(error) = tokio::time::timeout(Duration::from_secs(10), self.p.logs.flush())
            .await
            .unwrap_or_else(|_| Err("timed out".to_string()))
        {
            tracing::warn!(%error, "failed to flush job logs; they stay buffered and will be retried");
        }
    }

    /// Publishes a status update. Final statuses are retried, because losing one leaves the
    /// occurrence running until the zombie detector gives up on it.
    async fn status(&self, status: Status, f: StatusFields, retry: bool) {
        let message = StatusUpdateMessage {
            occurrence_id: self.occurrence_id.clone(),
            job_id: self.job.id.clone(),
            worker_id: self.p.opts.worker_id.clone(),
            instance_id: self.p.opts.instance_id.clone(),
            status,
            start_time: f.start.map(time_string),
            end_time: f.end.map(time_string),
            duration_ms: f.end.map(|_| f.duration.unwrap_or_default().as_millis() as i64),
            result: f.result,
            exception: f.exception,
            retry_after: f.retry_after.map(time_string),
            message_timestamp: now(),
        };

        let body = serde_json::to_vec(&message).expect("a status update always serializes");
        let delays: &[Duration] = if retry { &self.p.status_retry_delays } else { &[] };
        let mut attempt = 0;

        loop {
            let options =
                PublishOptions { persistent: true, key: Some(self.occurrence_id.clone()), ..Default::default() };

            let result = tokio::time::timeout(
                Duration::from_secs(30),
                self.p.publisher.publish("", QUEUE_STATUS_UPDATES, body.clone(), options),
            )
            .await
            .unwrap_or_else(|_| Err("timed out".to_string()));

            match result {
                Ok(()) => return,
                Err(error) if attempt >= delays.len() => {
                    tracing::warn!(status = status as u8, occurrence = %self.occurrence_id, %error, "failed to publish a status update");
                    return;
                }
                Err(_) => {
                    tokio::time::sleep(delays[attempt]).await;
                    attempt += 1;
                }
            }
        }
    }
}

fn is_guid(value: &str) -> bool {
    let bytes = value.as_bytes();
    let mut i = 0;

    for (index, len) in [8, 4, 4, 4, 12].into_iter().enumerate() {
        if index > 0 && bytes.get(i) == Some(&b'-') {
            i += 1;
        }

        for _ in 0..len {
            match bytes.get(i) {
                Some(b) if b.is_ascii_hexdigit() => i += 1,
                _ => return false,
            }
        }
    }

    i == bytes.len()
}

fn header_string(value: Option<&HeaderValue>) -> Option<String> {
    match value? {
        HeaderValue::Str(s) => Some(s.clone()),
        HeaderValue::Bytes(b) => String::from_utf8(b.clone()).ok(),
        _ => None,
    }
}

/// Reads the `OccurrenceId` header, the `CorrelationId` header or the correlation id property.
pub(crate) fn parse_occurrence_id(
    headers: &HashMap<String, HeaderValue>,
    correlation_id: Option<&str>,
) -> Option<String> {
    [
        header_string(headers.get("OccurrenceId")),
        header_string(headers.get("CorrelationId")),
        correlation_id.map(str::to_string),
    ]
    .into_iter()
    .flatten()
    .map(|candidate| candidate.trim().to_string())
    .find(|candidate| is_guid(candidate))
}

/// Reads `x-retry-count` in the encodings brokers and clients use.
pub(crate) fn parse_retry_count(headers: &HashMap<String, HeaderValue>) -> u32 {
    match headers.get("x-retry-count") {
        Some(HeaderValue::Int(n)) => (*n).clamp(0, u32::MAX as i64) as u32,
        Some(HeaderValue::Bytes(b)) if !b.is_empty() && b.iter().all(u8::is_ascii_digit) => {
            std::str::from_utf8(b).ok().and_then(|s| s.parse().ok()).unwrap_or(0)
        }
        // The .NET SDK reads a 4-byte value as a little-endian int.
        Some(HeaderValue::Bytes(b)) if b.len() == 4 => i32::from_le_bytes([b[0], b[1], b[2], b[3]]).max(0) as u32,
        Some(HeaderValue::Str(s)) => s.trim().parse().unwrap_or(0),
        _ => 0,
    }
}

/// A string result is stored as-is, nothing becomes the default message, anything else is JSON.
pub(crate) fn serialize_result(value: Value, job_name: &str) -> String {
    match value {
        Value::Null => format!("Job {job_name} completed successfully"),
        Value::String(s) => s,
        other => other.to_string(),
    }
}

fn format_seconds(d: Duration) -> String {
    let seconds = d.as_secs_f64();

    if seconds.fract() == 0.0 {
        format!("{}", seconds as u64)
    } else {
        format!("{seconds}")
    }
}
