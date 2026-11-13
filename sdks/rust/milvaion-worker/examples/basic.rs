//! A sample Milvaion worker.
//!
//! ```bash
//! MILVAION_WORKER_ID=rust-sample-worker \
//! MILVAION_RABBITMQ_URL=amqp://guest:guest@localhost:5672 \
//! MILVAION_REDIS_URL=redis://localhost:6379 \
//! cargo run --example basic
//! ```

use milvaion_worker::{Job, JobContext, JobError, JobOptions, LogLevel, Worker, WorkerOptions};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::time::Duration;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct GreetData {
    name: Option<String>,
    #[serde(default = "default_delay")]
    delay_ms: u64,
}

fn default_delay() -> u64 {
    1000
}

#[derive(Serialize)]
struct Greeting {
    greeting: String,
}

/// A job as a type, for handlers that hold state such as a client or a pool.
struct ReportJob {
    title: String,
}

impl Job for ReportJob {
    type Output = String;

    async fn execute(&self, ctx: JobContext) -> Result<String, JobError> {
        ctx.info(format!("Building {}", self.title));
        Ok(format!("{} is ready", self.title))
    }
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    tracing_subscriber::fmt().with_env_filter(std::env::var("RUST_LOG").unwrap_or_else(|_| "info".into())).init();

    let mut options = WorkerOptions::from_env()?;

    if options.worker_id.is_empty() {
        options.worker_id = "rust-sample-worker".into();
    }

    options.heartbeat_interval = Duration::from_secs(5);

    let mut worker = Worker::new(options)?;

    worker.handle_with(
        "GreetJob",
        JobOptions::new().execution_timeout(Duration::from_secs(60)).data_schema(json!({
            "type": "object",
            "properties": {
                "name": { "type": "string", "description": "Who to greet" },
                "delayMs": { "type": "integer", "description": "How long the job takes" }
            },
            "required": ["name"]
        })),
        |ctx: JobContext| async move {
            let data: GreetData = ctx.data()?;
            let name = data.name.ok_or_else(|| JobError::permanent("job data needs a \"name\""))?;

            ctx.log(
                LogLevel::Information,
                format!("Greeting {name}"),
                Some(json!({ "attempt": ctx.retry_count() + 1 })),
            );

            // Honour the token so cancelling from the dashboard stops the wait.
            tokio::select! {
                _ = tokio::time::sleep(Duration::from_millis(data.delay_ms)) => {}
                _ = ctx.cancelled() => return Err(JobError::new("stopped")),
            }

            Ok(Greeting { greeting: format!("Hello, {name}!") })
        },
    )?;

    worker.handle_with(
        "FlakyJob",
        JobOptions::new().max_retries(2).base_retry_delay(Duration::from_secs(2)),
        |ctx: JobContext| async move {
            // Fails on the first attempt to show retries in the dashboard.
            if ctx.retry_count() == 0 {
                return Err(JobError::new("simulated transient failure"));
            }

            ctx.info("Succeeded on a retry");
            Ok(())
        },
    )?;

    worker.register("ReportJob", JobOptions::default(), ReportJob { title: "Monthly report".into() })?;

    println!("worker {} ({}) is running; press Ctrl+C to stop", worker.worker_id(), worker.instance_id());

    worker.run_until_signal().await?;
    Ok(())
}
