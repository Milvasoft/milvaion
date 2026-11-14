//! Errors returned by job handlers and by the worker itself.

use std::fmt;

/// The error a job handler returns.
///
/// Any `std::error::Error` converts into it with `?`, so handlers can use `?` freely. A plain
/// failure is retried with exponential backoff. A [permanent](JobError::permanent) failure skips
/// the retries and goes straight to the dead letter queue: use it for invalid data and rejected
/// business rules. A [retry-after](JobError::retry_after) failure asks Milvaion to run the job
/// again at a given time instead.
pub struct JobError {
    message: String,
    type_name: String,
    permanent: bool,
    retry_at: Option<chrono::DateTime<chrono::Utc>>,
    source: Option<Box<dyn std::error::Error + Send + Sync + 'static>>,
    stack_trace: Option<String>,
}

impl JobError {
    /// A failure with a message. It is retried.
    pub fn new(message: impl fmt::Display) -> Self {
        Self {
            message: message.to_string(),
            type_name: "milvaion_worker::JobError".to_string(),
            permanent: false,
            retry_at: None,
            source: None,
            stack_trace: None,
        }
    }

    /// A failure that retrying cannot fix. The job is not retried and goes to the dead letter queue.
    pub fn permanent(message: impl fmt::Display) -> Self {
        Self::new(message).into_permanent()
    }

    /// A failure that asks to be run again at `at` (UTC), e.g. when an API quota resets.
    ///
    /// The job is not retried by the worker and does not go to the dead letter queue. The
    /// occurrence ends as `Failed` and the requested time is sent to Milvaion, which reruns
    /// workflow steps at that time. A standalone job ends there.
    pub fn retry_after(at: chrono::DateTime<chrono::Utc>, message: impl fmt::Display) -> Self {
        Self::new(message).into_retry_after(at)
    }

    /// Wraps an error, keeping it as the source so the dashboard shows its chain.
    pub fn from_error<E: std::error::Error + Send + Sync + 'static>(error: E) -> Self {
        Self {
            message: error.to_string(),
            type_name: std::any::type_name::<E>().to_string(),
            permanent: false,
            retry_at: None,
            source: Some(Box::new(error)),
            stack_trace: None,
        }
    }

    /// Marks this failure as permanent.
    pub fn into_permanent(mut self) -> Self {
        self.permanent = true;
        self
    }

    /// Asks Milvaion to run the job again at `at` (UTC) instead of retrying it now.
    pub fn into_retry_after(mut self, at: chrono::DateTime<chrono::Utc>) -> Self {
        self.retry_at = Some(at);
        self
    }

    /// Adds context in front of the message; the original error stays the source.
    pub fn context(mut self, context: impl fmt::Display) -> Self {
        self.message = format!("{context}: {}", self.message);
        self
    }

    /// Whether the job will skip retries.
    pub fn is_permanent(&self) -> bool {
        self.permanent
    }

    /// The time this failure asked to be run again at, if it is a retry-after failure.
    pub fn retry_at(&self) -> Option<chrono::DateTime<chrono::Utc>> {
        self.retry_at
    }

    /// The error message.
    pub fn message(&self) -> &str {
        &self.message
    }

    /// The Rust type of the wrapped error, shown in the dashboard as the exception type.
    pub fn type_name(&self) -> &str {
        &self.type_name
    }

    /// The wrapped error, if any.
    pub fn source(&self) -> Option<&(dyn std::error::Error + Send + Sync + 'static)> {
        self.source.as_deref()
    }

    pub(crate) fn with_message_suffix(mut self, suffix: &str) -> Self {
        self.message.push_str(suffix);
        self
    }

    pub(crate) fn panic(message: String) -> Self {
        let mut error = Self::new(message);
        error.type_name = "panic".to_string();
        error
    }

    /// Renders the error the way the .NET SDK formats exceptions, so the dashboard shows both alike.
    pub(crate) fn format(&self) -> String {
        let mut formatted = format!(
            "Type: {}\nMessage: {}\nStackTrace: {}\n",
            self.type_name,
            self.message,
            self.stack_trace.as_deref().unwrap_or("")
        );

        let mut inner: Option<&(dyn std::error::Error + 'static)> = match &self.source {
            // The wrapped error is this error's own message; its causes are the inner exceptions.
            Some(source) => source.source(),
            None => None,
        };

        while let Some(error) = inner {
            formatted.push_str(&format!("\nInner Exception:\nType: error\nMessage: {error}\nStackTrace: \n"));
            inner = error.source();
        }

        formatted
    }

    pub(crate) fn inner_message(&self) -> Option<String> {
        self.source.as_ref().and_then(|s| s.source()).map(|e| e.to_string())
    }
}

impl fmt::Display for JobError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl fmt::Debug for JobError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("JobError")
            .field("message", &self.message)
            .field("type_name", &self.type_name)
            .field("permanent", &self.permanent)
            .field("retry_at", &self.retry_at)
            .finish()
    }
}

impl<E: std::error::Error + Send + Sync + 'static> From<E> for JobError {
    fn from(error: E) -> Self {
        Self::from_error(error)
    }
}

/// Adds `.permanent()` to results, so `?` fails the job without retrying it.
///
/// ```
/// use milvaion_worker::{JobError, Permanent};
///
/// fn parse(raw: &str) -> Result<u32, JobError> {
///     Ok(raw.parse::<u32>().permanent()?)
/// }
/// assert!(parse("x").unwrap_err().is_permanent());
/// ```
pub trait Permanent<T> {
    /// Turns an error into a permanent [`JobError`].
    fn permanent(self) -> Result<T, JobError>;
}

impl<T, E: Into<JobError>> Permanent<T> for Result<T, E> {
    fn permanent(self) -> Result<T, JobError> {
        self.map_err(|e| e.into().into_permanent())
    }
}

/// Why a running job was stopped before its handler returned.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AbortReason {
    /// The job was cancelled from the dashboard; holds the reason the API sent.
    Cancelled(Option<String>),
    /// The job ran past its execution timeout.
    TimedOut,
    /// The worker is shutting down; the job goes back to the queue.
    WorkerStopping,
}

impl fmt::Display for AbortReason {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            AbortReason::Cancelled(Some(reason)) => write!(f, "job was cancelled: {reason}"),
            AbortReason::Cancelled(None) => f.write_str("job was cancelled"),
            AbortReason::TimedOut => f.write_str("job exceeded its execution timeout"),
            AbortReason::WorkerStopping => f.write_str("worker is shutting down"),
        }
    }
}

/// An error starting or configuring the worker.
#[derive(Debug)]
pub enum WorkerError {
    /// The options or a job registration are invalid.
    Config(String),
    /// RabbitMQ or Redis could not be reached within the startup timeout.
    Connection(String),
}

impl fmt::Display for WorkerError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            WorkerError::Config(message) => write!(f, "milvaion: {message}"),
            WorkerError::Connection(message) => write!(f, "milvaion: {message}"),
        }
    }
}

impl std::error::Error for WorkerError {}
