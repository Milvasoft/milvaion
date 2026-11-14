//! Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in Rust.
//!
//! A Rust worker shows up in the Milvaion dashboard like a .NET worker. The scheduler dispatches
//! jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.
//!
//! ```no_run
//! use milvaion_worker::{JobContext, JobError, Worker, WorkerOptions};
//! use serde::Deserialize;
//!
//! #[derive(Deserialize)]
//! struct Email { to: String, subject: String }
//!
//! #[tokio::main]
//! async fn main() -> Result<(), Box<dyn std::error::Error>> {
//!     let mut worker = Worker::new(WorkerOptions::new("mail-worker"))?;
//!
//!     worker.handle("SendEmailJob", |ctx: JobContext| async move {
//!         let email: Email = ctx.data()?; // invalid data is a permanent error
//!         ctx.info(format!("Sending {:?} to {}", email.subject, email.to));
//!         Ok::<_, JobError>(serde_json::json!({ "sent": true }))
//!     })?;
//!
//!     worker.run_until_signal().await?;
//!     Ok(())
//! }
//! ```
//!
//! The SDK implements the language-neutral
//! [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md).

mod broker;
mod cancellation;
mod context;
mod error;
#[cfg(feature = "kafka")]
mod kafka;
mod log_buffer;
mod metrics;
mod options;
mod processor;
mod protocol;
mod worker;

#[cfg(test)]
mod tests;

pub use context::JobContext;
pub use error::{AbortReason, JobError, Permanent, WorkerError};
pub use options::{
    generate_routing_pattern, queue_name_for_pattern, JobOptions, KafkaOptions, KafkaSaslMechanism, QueueType,
    Transport, WorkerOptions,
};
pub use protocol::{LogLevel, ScheduledJob, Status, SDK_VERSION};
pub use worker::{Job, Worker};

/// Re-exported so handlers can name the token type without adding `tokio-util`.
pub use tokio_util::sync::CancellationToken;
