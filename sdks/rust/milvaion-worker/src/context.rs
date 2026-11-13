//! What a job handler receives.

use crate::error::{AbortReason, JobError};
use crate::log_buffer::LogBuffer;
use crate::protocol::{now, LogLevel, OccurrenceLog, ScheduledJob};
use serde::de::DeserializeOwned;
use serde_json::{Map, Value};
use std::sync::{Arc, Mutex};
use tokio_util::sync::CancellationToken;

/// Stops one running job and remembers why.
pub(crate) struct RunControl {
    pub token: CancellationToken,
    reason: Mutex<Option<AbortReason>>,
}

impl RunControl {
    pub fn new() -> Self {
        Self { token: CancellationToken::new(), reason: Mutex::new(None) }
    }

    /// The first reason wins; later aborts do not change it.
    pub fn abort(&self, reason: AbortReason) {
        {
            let mut current = self.reason.lock().unwrap();

            if current.is_none() {
                *current = Some(reason);
            }
        }

        self.token.cancel();
    }

    pub fn reason(&self) -> Option<AbortReason> {
        self.reason.lock().unwrap().clone()
    }
}

/// The context of one job execution, passed to the handler.
///
/// Cheap to clone. Its [cancellation token](JobContext::cancellation_token) is cancelled when the
/// job is cancelled from the dashboard, times out or the worker shuts down;
/// [`abort_reason`](JobContext::abort_reason) then says which.
#[derive(Clone)]
pub struct JobContext {
    inner: Arc<Inner>,
}

struct Inner {
    occurrence_id: String,
    retry_count: u32,
    instance_id: String,
    job: ScheduledJob,
    control: Arc<RunControl>,
    logs: Arc<LogBuffer>,
}

impl JobContext {
    pub(crate) fn new(
        occurrence_id: String,
        retry_count: u32,
        instance_id: String,
        job: ScheduledJob,
        control: Arc<RunControl>,
        logs: Arc<LogBuffer>,
    ) -> Self {
        Self { inner: Arc::new(Inner { occurrence_id, retry_count, instance_id, job, control, logs }) }
    }

    /// Id of this execution. It stays the same across retries and redeliveries, so it works as an
    /// idempotency key.
    pub fn occurrence_id(&self) -> &str {
        &self.inner.occurrence_id
    }

    /// 0 on the first attempt, then 1, 2 and so on.
    pub fn retry_count(&self) -> u32 {
        self.inner.retry_count
    }

    /// Id of this worker process.
    pub fn instance_id(&self) -> &str {
        &self.inner.instance_id
    }

    /// The scheduled job as the API sent it.
    pub fn job(&self) -> &ScheduledJob {
        &self.inner.job
    }

    /// Id of the scheduled job.
    pub fn job_id(&self) -> &str {
        &self.inner.job.id
    }

    /// Name the job is registered under (`JobNameInWorker`).
    pub fn job_name(&self) -> &str {
        &self.inner.job.job_name_in_worker
    }

    /// The job data as a JSON string, or `None` when the job has none.
    pub fn raw_data(&self) -> Option<&str> {
        self.inner.job.job_data.as_deref().filter(|d| !d.trim().is_empty())
    }

    /// Decodes the job data. A job without data decodes from `null`, so `Option<T>` and types
    /// with `#[serde(default)]` accept it. Data that does not match `T` is a permanent error, so
    /// `?` fails the job without retrying it.
    pub fn data<T: DeserializeOwned>(&self) -> Result<T, JobError> {
        let raw = self.raw_data().unwrap_or("null");

        serde_json::from_str(raw)
            .map_err(|e| JobError::permanent(format!("job data does not match {}: {e}", std::any::type_name::<T>())))
    }

    /// Cancelled when the job is cancelled, times out or the worker shuts down. Pass it on to
    /// everything that can block, or `select!` on [`cancelled`](JobContext::cancelled).
    pub fn cancellation_token(&self) -> &CancellationToken {
        &self.inner.control.token
    }

    /// Completes when the job is stopped.
    pub async fn cancelled(&self) {
        self.inner.control.token.cancelled().await
    }

    /// Whether the job has been stopped.
    pub fn is_cancelled(&self) -> bool {
        self.inner.control.token.is_cancelled()
    }

    /// Why the job was stopped, once it has been.
    pub fn abort_reason(&self) -> Option<AbortReason> {
        self.inner.control.reason()
    }

    /// Writes an Information entry to the occurrence log.
    pub fn info(&self, message: impl Into<String>) {
        self.log(LogLevel::Information, message, None);
    }

    /// Writes a Debug entry to the occurrence log.
    pub fn debug(&self, message: impl Into<String>) {
        self.log(LogLevel::Debug, message, None);
    }

    /// Writes a Warning entry to the occurrence log.
    pub fn warn(&self, message: impl Into<String>) {
        self.log(LogLevel::Warning, message, None);
    }

    /// Writes an Error entry to the occurrence log, with the details of `error` when given.
    pub fn error(&self, message: impl Into<String>, error: Option<&JobError>) {
        let mut data = Map::new();
        let mut exception_type = None;

        if let Some(error) = error {
            exception_type = Some(error.type_name().to_string());
            data.insert("ExceptionType".into(), Value::String(error.type_name().to_string()));
            data.insert("StackTrace".into(), Value::String(error.format()));

            if let Some(inner) = error.inner_message() {
                data.insert("InnerException".into(), Value::String(inner));
            }
        }

        self.write(LogLevel::Error, message.into(), Some(Value::Object(data)), "UserDefined", exception_type);
    }

    /// Writes an entry with structured data, shown next to the message in the dashboard.
    /// `data` should be a JSON object, for example `json!({"to": "user@example.com"})`.
    pub fn log(&self, level: LogLevel, message: impl Into<String>, data: Option<Value>) {
        self.write(level, message.into(), data, "UserCode", None);
    }

    fn write(
        &self,
        level: LogLevel,
        message: String,
        data: Option<Value>,
        category: &str,
        exception_type: Option<String>,
    ) {
        self.inner.logs.add(
            &self.inner.occurrence_id,
            &self.inner.instance_id,
            OccurrenceLog { timestamp: now(), level, message, data, category: category.to_string(), exception_type },
        );
    }
}
