//! Wire-level names and message shapes shared with the Milvaion API.
//! See `docs/githubdocs/WORKER-PROTOCOL.md` in the repository for the full protocol.

use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;

/// Version of this SDK, reported to the dashboard. A test keeps it equal to the crate version.
pub const SDK_VERSION: &str = "1.1.0";

pub(crate) const EXCHANGE_JOBS: &str = "jobs.topic";
pub(crate) const EXCHANGE_DEAD_LETTER: &str = "dlx_scheduled_jobs";
pub(crate) const DEAD_LETTER_ROUTING_KEY: &str = "failed_jobs";

pub(crate) const QUEUE_JOBS: &str = "scheduled_jobs_queue";
pub(crate) const QUEUE_WORKER_LOGS: &str = "worker_logs_queue";
pub(crate) const QUEUE_WORKER_HEARTBEAT: &str = "worker_heartbeat_queue";
pub(crate) const QUEUE_WORKER_REGISTRATION: &str = "worker_registration_queue";
pub(crate) const QUEUE_STATUS_UPDATES: &str = "job_status_updates_queue";
pub(crate) const QUEUE_FAILED_OCCURRENCES: &str = "failed_jobs_queue";

/// The API's `JobOccurrenceStatus`, sent as a number.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum Status {
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5,
    Unknown = 6,
    Skipped = 7,
}

impl Serialize for Status {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_u8(*self as u8)
    }
}

impl<'de> Deserialize<'de> for Status {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        Ok(match u8::deserialize(deserializer)? {
            0 => Status::Queued,
            1 => Status::Running,
            2 => Status::Completed,
            3 => Status::Failed,
            4 => Status::Cancelled,
            5 => Status::TimedOut,
            7 => Status::Skipped,
            _ => Status::Unknown,
        })
    }
}

/// Level of an occurrence log entry as the dashboard shows it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum LogLevel {
    Debug,
    Information,
    Warning,
    Error,
}

/// The body of a job delivery. The API sends more fields than these.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScheduledJob {
    #[serde(default, deserialize_with = "string_or_null")]
    pub id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub display_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tags: Option<String>,
    #[serde(default, deserialize_with = "string_or_null")]
    pub job_name_in_worker: String,
    /// Job data as a JSON string.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub job_data: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub execute_at: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cron_expression: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub worker_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub routing_pattern: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub execution_timeout_seconds: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub zombie_timeout_minutes: Option<i64>,
    #[serde(default)]
    pub version: i64,
}

fn string_or_null<'de, D: serde::Deserializer<'de>>(deserializer: D) -> Result<String, D::Error> {
    Ok(Option::<String>::deserialize(deserializer)?.unwrap_or_default())
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct OccurrenceLog {
    pub timestamp: String,
    pub level: LogLevel,
    pub message: String,
    pub data: Option<Value>,
    pub category: String,
    pub exception_type: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct WorkerLogMessage {
    pub occurrence_id: String,
    pub worker_id: String,
    pub log: OccurrenceLog,
    pub message_timestamp: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct WorkerLogBatchMessage {
    pub logs: Vec<WorkerLogMessage>,
    pub batch_timestamp: String,
    pub count: usize,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct StatusUpdateMessage {
    pub occurrence_id: String,
    pub job_id: String,
    pub worker_id: String,
    pub instance_id: String,
    pub status: Status,
    pub start_time: Option<String>,
    pub end_time: Option<String>,
    pub duration_ms: Option<i64>,
    pub result: Option<String>,
    pub exception: Option<String>,
    pub message_timestamp: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct HeartbeatMessage {
    pub worker_id: String,
    pub instance_id: String,
    pub current_jobs: usize,
    pub memory_bytes: i64,
    pub cpu_usage_percent: f64,
    pub timestamp: String,
    pub is_stopping: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct RegistrationMessage {
    pub worker_id: String,
    pub instance_id: String,
    pub display_name: String,
    pub host_name: String,
    pub ip_address: String,
    pub routing_patterns: BTreeMap<String, String>,
    pub job_data_definitions: BTreeMap<String, String>,
    pub job_result_definitions: BTreeMap<String, String>,
    pub job_types: Vec<String>,
    pub max_parallel_jobs: usize,
    pub version: String,
    /// JSON string. The API reads it case-sensitively, so its fields are PascalCase.
    pub metadata: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub(crate) struct WorkerMetadata {
    pub is_external: bool,
    pub external_scheduler: Option<String>,
    pub processor_count: usize,
    #[serde(rename = "OSVersion")]
    pub os_version: String,
    pub runtime_version: String,
    pub heartbeat_interval: u64,
    pub sdk: String,
    pub sdk_version: String,
    pub job_configs: Vec<JobConfigMetadata>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub(crate) struct JobConfigMetadata {
    pub job_type: String,
    pub consumer_id: String,
    pub max_parallel_jobs: usize,
    pub execution_timeout_seconds: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct DlqMessage {
    pub id: String,
    pub display_name: Option<String>,
    pub job_name_in_worker: String,
    pub job_data: Option<String>,
    pub execute_at: Option<String>,
    pub status: Status,
    pub exception: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub(crate) struct CancellationRequest {
    #[serde(rename = "CorrelationId", alias = "correlationId", default)]
    pub correlation_id: Option<String>,
    #[serde(rename = "OccurrenceId", alias = "occurrenceId", default)]
    pub occurrence_id: Option<String>,
    #[serde(rename = "Reason", alias = "reason", default)]
    pub reason: Option<String>,
}

pub(crate) fn now() -> String {
    time_string(chrono::Utc::now())
}

pub(crate) fn time_string(time: chrono::DateTime<chrono::Utc>) -> String {
    time.to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
}
