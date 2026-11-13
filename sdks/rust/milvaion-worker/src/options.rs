//! Worker and job options.

use crate::error::WorkerError;
use crate::protocol::QUEUE_JOBS;
use serde_json::Value;
use std::time::Duration;

/// The RabbitMQ queue type. It must match the API's `MilvaionConfig:RabbitMQ:QueueType`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum QueueType {
    #[default]
    Classic,
    Quorum,
}

/// Configures a [`Worker`](crate::Worker). Only `worker_id` is required.
///
/// A duration of zero disables the timeout or interval it sets, where the field says so.
#[derive(Debug, Clone)]
pub struct WorkerOptions {
    /// Identifies the worker application. Use the same value on every replica.
    pub worker_id: String,
    /// For example `amqp://guest:guest@localhost:5672/`. Default: guest on localhost.
    pub rabbitmq_url: String,
    /// Must match the API's `MilvaionConfig:RabbitMQ:QueueType`. Default: classic.
    pub queue_type: QueueType,
    /// For example `redis://:password@localhost:6379/0`. Redis only carries cancellation requests.
    /// `None` runs without it, and jobs then cannot be cancelled from the dashboard.
    /// Default: `redis://localhost:6379`.
    pub redis_url: Option<String>,
    /// Must match the API's `MilvaionConfig:Redis:KeyPrefix`. Default: `Milvaion:JobScheduler:`.
    pub redis_key_prefix: String,
    /// Capacity reported for this instance. 0 means the CPU count times 2.
    pub max_parallel_jobs: usize,
    /// Default job timeout. Zero disables it. Default: 1 hour.
    pub execution_timeout: Duration,
    /// Instance heartbeat interval. Default: 30 s.
    pub heartbeat_interval: Duration,
    /// Keeps long jobs from being flagged as zombies. Zero disables it. Default: 60 s.
    pub job_heartbeat_interval: Duration,
    /// How long the worker lets running jobs finish when it is asked to stop. Default: 30 s.
    pub shutdown_timeout: Duration,
    /// How long `run` waits for the first RabbitMQ and Redis connection. Default: 30 s.
    pub startup_timeout: Duration,
    /// Shown in the dashboard. Default: `1.0.0`.
    pub version: String,
    /// Overrides the generated `{worker_id}-{8 hex}` instance id. It must be unique per process.
    pub instance_id: Option<String>,
}

impl Default for WorkerOptions {
    fn default() -> Self {
        Self {
            worker_id: String::new(),
            rabbitmq_url: "amqp://guest:guest@localhost:5672/".to_string(),
            queue_type: QueueType::Classic,
            redis_url: Some("redis://localhost:6379".to_string()),
            redis_key_prefix: "Milvaion:JobScheduler:".to_string(),
            max_parallel_jobs: 0,
            execution_timeout: Duration::from_secs(3600),
            heartbeat_interval: Duration::from_secs(30),
            job_heartbeat_interval: Duration::from_secs(60),
            shutdown_timeout: Duration::from_secs(30),
            startup_timeout: Duration::from_secs(30),
            version: "1.0.0".to_string(),
            instance_id: None,
        }
    }
}

impl WorkerOptions {
    /// Default options for a worker id.
    pub fn new(worker_id: impl Into<String>) -> Self {
        Self { worker_id: worker_id.into(), ..Self::default() }
    }

    /// Default options overridden by these environment variables, for containers:
    ///
    /// - `MILVAION_WORKER_ID`
    /// - `MILVAION_RABBITMQ_URL`
    /// - `MILVAION_RABBITMQ_QUEUE_TYPE` (`classic` or `quorum`)
    /// - `MILVAION_REDIS_URL` (`false` disables Redis)
    /// - `MILVAION_REDIS_KEY_PREFIX`
    /// - `MILVAION_MAX_PARALLEL_JOBS`
    /// - `MILVAION_EXECUTION_TIMEOUT_SECONDS` (0 or less disables the timeout)
    /// - `MILVAION_HEARTBEAT_INTERVAL_SECONDS`
    ///
    /// Change fields afterwards to override them in code.
    pub fn from_env() -> Result<Self, WorkerError> {
        Self::from_lookup(|key| std::env::var(key).ok())
    }

    pub(crate) fn from_lookup(lookup: impl Fn(&str) -> Option<String>) -> Result<Self, WorkerError> {
        let env = |key: &str| lookup(key).map(|v| v.trim().to_string()).filter(|v| !v.is_empty());
        let int = |key: &str| -> Result<Option<i64>, WorkerError> {
            match env(key) {
                None => Ok(None),
                Some(value) => value
                    .parse::<i64>()
                    .map(Some)
                    .map_err(|_| WorkerError::Config(format!("{key} must be an integer, got {value:?}"))),
            }
        };

        let mut o = Self::default();

        if let Some(v) = env("MILVAION_WORKER_ID") {
            o.worker_id = v;
        }

        if let Some(v) = env("MILVAION_RABBITMQ_URL") {
            o.rabbitmq_url = v;
        }

        if let Some(v) = env("MILVAION_RABBITMQ_QUEUE_TYPE") {
            o.queue_type = parse_queue_type(&v)?;
        }

        match env("MILVAION_REDIS_URL") {
            Some(v) if v.eq_ignore_ascii_case("false") => o.redis_url = None,
            Some(v) => o.redis_url = Some(v),
            None => {}
        }

        if let Some(v) = env("MILVAION_REDIS_KEY_PREFIX") {
            o.redis_key_prefix = v;
        }

        if let Some(v) = int("MILVAION_MAX_PARALLEL_JOBS")? {
            o.max_parallel_jobs = v.max(0) as usize;
        }

        if let Some(v) = int("MILVAION_EXECUTION_TIMEOUT_SECONDS")? {
            o.execution_timeout = Duration::from_secs(v.max(0) as u64);
        }

        if let Some(v) = int("MILVAION_HEARTBEAT_INTERVAL_SECONDS")? {
            if v > 0 {
                o.heartbeat_interval = Duration::from_secs(v as u64);
            }
        }

        Ok(o)
    }
}

fn parse_queue_type(value: &str) -> Result<QueueType, WorkerError> {
    match value.trim().to_ascii_lowercase().as_str() {
        "" | "classic" => Ok(QueueType::Classic),
        "quorum" => Ok(QueueType::Quorum),
        _ => Err(WorkerError::Config(format!(
            "MILVAION_RABBITMQ_QUEUE_TYPE must be \"classic\" or \"quorum\", got {value:?}"
        ))),
    }
}

/// Configures one job type. `JobOptions::default()` uses the defaults.
#[derive(Debug, Clone, Default)]
pub struct JobOptions {
    /// RabbitMQ routing pattern. Default: `{worker_id}.{name without Job}.*`.
    pub routing_pattern: Option<String>,
    /// Consumer id shown in the dashboard. Default: `{name without Job}-consumer`.
    pub consumer_id: Option<String>,
    /// Jobs of this type that may run at once on this instance (the prefetch).
    /// Default: min(10, worker `max_parallel_jobs`).
    pub max_parallel_jobs: Option<usize>,
    /// Timeout of this job type. Zero disables it. A timeout set on the job in the dashboard wins.
    /// Default: the worker's `execution_timeout`.
    pub execution_timeout: Option<Duration>,
    /// Retries after the first failure before the job goes to the dead letter queue. Default: 3.
    pub max_retries: Option<u32>,
    /// Retry n waits `base_retry_delay * 2^(n-1)`. Default: 5 s.
    pub base_retry_delay: Option<Duration>,
    /// JSON Schema of the job data. The dashboard builds the job data form from it.
    pub data_schema: Option<Value>,
    /// JSON Schema of the job result.
    pub result_schema: Option<Value>,
}

impl JobOptions {
    /// Default job options.
    pub fn new() -> Self {
        Self::default()
    }

    /// Sets the routing pattern.
    pub fn routing_pattern(mut self, pattern: impl Into<String>) -> Self {
        self.routing_pattern = Some(pattern.into());
        self
    }

    /// Sets the consumer id shown in the dashboard.
    pub fn consumer_id(mut self, id: impl Into<String>) -> Self {
        self.consumer_id = Some(id.into());
        self
    }

    /// Sets how many jobs of this type may run at once on this instance.
    pub fn max_parallel_jobs(mut self, n: usize) -> Self {
        self.max_parallel_jobs = Some(n);
        self
    }

    /// Sets the timeout of this job type. Zero disables it.
    pub fn execution_timeout(mut self, timeout: Duration) -> Self {
        self.execution_timeout = Some(timeout);
        self
    }

    /// Sets the number of retries. 0 disables retries.
    pub fn max_retries(mut self, n: u32) -> Self {
        self.max_retries = Some(n);
        self
    }

    /// Sets the delay before the first retry; later retries double it.
    pub fn base_retry_delay(mut self, delay: Duration) -> Self {
        self.base_retry_delay = Some(delay);
        self
    }

    /// Sets the JSON Schema of the job data.
    pub fn data_schema(mut self, schema: Value) -> Self {
        self.data_schema = Some(schema);
        self
    }

    /// Sets the JSON Schema of the job result.
    pub fn result_schema(mut self, schema: Value) -> Self {
        self.result_schema = Some(schema);
        self
    }
}

#[derive(Debug, Clone)]
pub(crate) struct ResolvedOptions {
    pub worker_id: String,
    pub instance_id: String,
    pub rabbitmq_url: String,
    pub quorum: bool,
    pub redis_url: Option<String>,
    pub cancellation_channel: String,
    pub max_parallel_jobs: usize,
    pub execution_timeout: Duration,
    pub heartbeat_interval: Duration,
    pub job_heartbeat_interval: Duration,
    pub shutdown_timeout: Duration,
    pub startup_timeout: Duration,
    pub version: String,
}

pub(crate) fn resolve_options(o: WorkerOptions) -> Result<ResolvedOptions, WorkerError> {
    let worker_id = o.worker_id.trim().to_string();

    if worker_id.is_empty() {
        return Err(WorkerError::Config("worker_id is required".to_string()));
    }

    let max_parallel_jobs = if o.max_parallel_jobs == 0 { cpu_count() * 2 } else { o.max_parallel_jobs };

    let rabbitmq_url = normalize_amqp_url(&o.rabbitmq_url)?;

    let redis_url = o.redis_url.map(|u| u.trim().to_string()).filter(|u| !u.is_empty());

    let key_prefix =
        if o.redis_key_prefix.is_empty() { "Milvaion:JobScheduler:".to_string() } else { o.redis_key_prefix };

    let instance_id = o
        .instance_id
        .map(|i| i.trim().to_string())
        .filter(|i| !i.is_empty())
        .unwrap_or_else(|| generate_instance_id(&worker_id));

    Ok(ResolvedOptions {
        worker_id,
        instance_id,
        rabbitmq_url,
        quorum: o.queue_type == QueueType::Quorum,
        redis_url,
        cancellation_channel: format!("{key_prefix}cancellation_channel"),
        max_parallel_jobs,
        execution_timeout: o.execution_timeout,
        heartbeat_interval: positive_or(o.heartbeat_interval, Duration::from_secs(30)),
        job_heartbeat_interval: o.job_heartbeat_interval,
        shutdown_timeout: o.shutdown_timeout,
        startup_timeout: positive_or(o.startup_timeout, Duration::from_secs(30)),
        version: if o.version.trim().is_empty() { "1.0.0".to_string() } else { o.version },
    })
}

#[derive(Debug, Clone)]
pub(crate) struct ResolvedJob {
    pub name: String,
    pub routing_pattern: String,
    pub queue: String,
    pub consumer_id: String,
    pub max_parallel_jobs: usize,
    pub execution_timeout: Duration,
    pub max_retries: u32,
    pub base_retry_delay: Duration,
    pub data_schema: Option<String>,
    pub result_schema: Option<String>,
}

pub(crate) fn resolve_job(name: &str, o: JobOptions, w: &ResolvedOptions) -> Result<ResolvedJob, WorkerError> {
    let max_parallel_jobs = o.max_parallel_jobs.unwrap_or_else(|| w.max_parallel_jobs.min(10));

    if !(1..=65535).contains(&max_parallel_jobs) {
        return Err(WorkerError::Config(format!(
            "max_parallel_jobs of job {name:?} must be between 1 and 65535, got {max_parallel_jobs}"
        )));
    }

    let routing_pattern = o
        .routing_pattern
        .map(|p| p.trim().to_string())
        .filter(|p| !p.is_empty())
        .unwrap_or_else(|| generate_routing_pattern(name, &w.worker_id));

    Ok(ResolvedJob {
        name: name.to_string(),
        queue: queue_name_for_pattern(&routing_pattern),
        routing_pattern,
        consumer_id: o.consumer_id.filter(|c| !c.trim().is_empty()).unwrap_or_else(|| default_consumer_id(name)),
        max_parallel_jobs,
        execution_timeout: o.execution_timeout.unwrap_or(w.execution_timeout),
        max_retries: o.max_retries.unwrap_or(3),
        base_retry_delay: o.base_retry_delay.unwrap_or(Duration::from_secs(5)),
        data_schema: o.data_schema.map(|s| schema_string(&s)),
        result_schema: o.result_schema.map(|s| schema_string(&s)),
    })
}

/// A schema given as a JSON string is used as-is; anything else is serialized.
fn schema_string(schema: &Value) -> String {
    match schema {
        Value::String(s) => s.clone(),
        other => other.to_string(),
    }
}

/// The routing pattern a job gets when it does not set one, identical to the .NET SDK:
/// `SendEmailJob` in worker `Mail-Worker` becomes `mail-worker.sendemail.*`.
pub fn generate_routing_pattern(job_name: &str, worker_id: &str) -> String {
    format!("{}.{}.*", worker_id.to_lowercase(), trim_job_suffix(job_name).to_lowercase())
}

/// The queue a job type is consumed from: `*` becomes `wildcard` and `#` becomes `all`.
pub fn queue_name_for_pattern(pattern: &str) -> String {
    format!("{QUEUE_JOBS}.{}", pattern.replace('*', "wildcard").replace('#', "all"))
}

fn default_consumer_id(job_name: &str) -> String {
    format!("{}-consumer", trim_job_suffix(job_name).to_lowercase())
}

fn trim_job_suffix(name: &str) -> &str {
    let len = name.len();

    if len >= 3 && name.is_char_boundary(len - 3) && name[len - 3..].eq_ignore_ascii_case("job") {
        &name[..len - 3]
    } else {
        name
    }
}

fn generate_instance_id(worker_id: &str) -> String {
    let id = uuid::Uuid::new_v4().simple().to_string();
    format!("{worker_id}-{}", &id[..8])
}

/// lapin reads the vhost from the URL path; `amqp://host/` would select the vhost "" instead of "/".
fn normalize_amqp_url(url: &str) -> Result<String, WorkerError> {
    let url = url.trim();

    let rest = url
        .strip_prefix("amqp://")
        .or_else(|| url.strip_prefix("amqps://"))
        .ok_or_else(|| WorkerError::Config(format!("rabbitmq_url must start with amqp:// or amqps://, got {url:?}")))?;

    // The authority ends at the first '/' after any credentials.
    let after_at = rest.rfind('@').map(|i| i + 1).unwrap_or(0);

    match rest[after_at..].find('/') {
        None => Ok(format!("{url}/%2f")),
        Some(slash) => {
            let path_start = after_at + slash;
            let path_and_query = &rest[path_start..];

            if path_and_query == "/" || path_and_query.starts_with("/?") {
                let prefix_len = url.len() - rest.len() + path_start;
                Ok(format!("{}/%2f{}", &url[..prefix_len], &path_and_query[1..]))
            } else {
                Ok(url.to_string())
            }
        }
    }
}

pub(crate) fn rabbitmq_host_port(url: &str) -> Option<(String, u16)> {
    let tls = url.starts_with("amqps://");
    let rest = url.split_once("://")?.1;
    let rest = &rest[rest.rfind('@').map(|i| i + 1).unwrap_or(0)..];
    let authority = rest.split(['/', '?']).next()?;
    let default_port = if tls { 5671 } else { 5672 };

    if let Some(ipv6) = authority.strip_prefix('[') {
        let (host, after) = ipv6.split_once(']')?;
        let port = after.strip_prefix(':').and_then(|p| p.parse().ok()).unwrap_or(default_port);
        return Some((host.to_string(), port));
    }

    match authority.rsplit_once(':') {
        Some((host, port)) => Some((host.to_string(), port.parse().ok()?)),
        None => Some((authority.to_string(), default_port)),
    }
}

pub(crate) fn cpu_count() -> usize {
    std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1)
}

fn positive_or(value: Duration, fallback: Duration) -> Duration {
    if value.is_zero() {
        fallback
    } else {
        value
    }
}
