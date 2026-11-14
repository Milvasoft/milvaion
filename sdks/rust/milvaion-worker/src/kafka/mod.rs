//! The Kafka wire contract shared with the Milvaion API and the other SDKs: topic and group
//! names, headers and the ordered offset tracker. See `docs/githubdocs/KAFKA-TRANSPORT.md` in
//! the repository.

mod transport;

pub(crate) use transport::KafkaTransport;

use crate::broker::HeaderValue;
use std::collections::{BTreeMap, HashMap, VecDeque};

pub(crate) const HEADER_OCCURRENCE_ID: &str = "OccurrenceId";
pub(crate) const HEADER_CORRELATION_ID: &str = "CorrelationId";
pub(crate) const HEADER_RETRY_COUNT: &str = "x-retry-count";
pub(crate) const HEADER_MAX_RETRIES: &str = "MaxRetries";
pub(crate) const HEADER_NOT_BEFORE: &str = "x-not-before";
pub(crate) const HEADER_ORIGINAL_TOPIC: &str = "x-original-topic";
pub(crate) const HEADER_DLQ_REASON: &str = "x-dlq-reason";

/// The topic and consumer group names of one worker.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct KafkaTopics {
    pub jobs: String,
    pub retry: String,
    pub status: String,
    pub logs: String,
    pub heartbeats: String,
    pub registrations: String,
    pub dlq: String,
    pub group: String,
}

impl KafkaTopics {
    pub fn new(prefix: &str, worker_id: &str) -> Self {
        let worker = worker_segment(worker_id);

        Self {
            jobs: format!("{prefix}milvaion.jobs.{worker}"),
            retry: format!("{prefix}milvaion.jobs.{worker}.retry"),
            status: format!("{prefix}milvaion.status"),
            logs: format!("{prefix}milvaion.logs"),
            heartbeats: format!("{prefix}milvaion.heartbeats"),
            registrations: format!("{prefix}milvaion.registrations"),
            dlq: format!("{prefix}milvaion.dlq"),
            group: format!("{prefix}milvaion.worker.{worker}"),
        }
    }
}

/// The worker id as it appears in topic and group names: lower case, with every character outside
/// `a-z 0-9 . _ -` replaced by `_`.
pub(crate) fn worker_segment(worker_id: &str) -> String {
    worker_id
        .chars()
        .flat_map(char::to_lowercase)
        .map(|c| if c.is_ascii_lowercase() || c.is_ascii_digit() || matches!(c, '.' | '_' | '-') { c } else { '_' })
        .collect()
}

/// Turns record headers into the map the processor reads. Values stay strings; the last header of
/// a name wins.
pub(crate) fn header_map(headers: &[(String, Vec<u8>)]) -> HashMap<String, HeaderValue> {
    headers.iter().map(|(k, v)| (k.clone(), HeaderValue::Bytes(v.clone()))).collect()
}

/// Encodes a header value as UTF-8, numbers as decimal strings.
pub(crate) fn header_bytes(value: &HeaderValue) -> Option<Vec<u8>> {
    match value {
        HeaderValue::Str(s) => Some(s.as_bytes().to_vec()),
        HeaderValue::Bytes(b) => Some(b.clone()),
        HeaderValue::Int(i) => Some(i.to_string().into_bytes()),
        HeaderValue::Other => None,
    }
}

/// Headers in a stable (sorted) order, the last value of a name winning.
pub(crate) fn sorted_headers(headers: impl IntoIterator<Item = (String, Vec<u8>)>) -> Vec<(String, Vec<u8>)> {
    headers.into_iter().collect::<BTreeMap<_, _>>().into_iter().collect()
}

/// Reads `x-not-before` (Unix milliseconds). `None` when it is missing or invalid.
pub(crate) fn not_before(headers: &HashMap<String, HeaderValue>) -> Option<i64> {
    match headers.get(HEADER_NOT_BEFORE)? {
        HeaderValue::Bytes(b) => std::str::from_utf8(b).ok()?.trim().parse().ok(),
        HeaderValue::Str(s) => s.trim().parse().ok(),
        HeaderValue::Int(i) => Some(*i),
        HeaderValue::Other => None,
    }
}

/// Follows the records of one partition that this instance has taken, in offset order, and knows
/// how far the partition may be committed: up to the first record that is not finished, because a
/// Kafka commit covers every record before it.
#[derive(Debug)]
pub(crate) struct OffsetTracker {
    pending: VecDeque<(i64, bool)>,
    last: i64,
    position: i64,
}

impl Default for OffsetTracker {
    fn default() -> Self {
        Self { pending: VecDeque::new(), last: -1, position: -1 }
    }
}

impl OffsetTracker {
    /// Registers a record taken from the partition. Offsets must increase; a repeated or older
    /// offset returns false and is ignored.
    pub fn add(&mut self, offset: i64) -> bool {
        if offset <= self.last {
            return false;
        }

        self.last = offset;
        self.pending.push_back((offset, false));
        true
    }

    /// Marks a record finished. Returns false for an offset the tracker does not hold.
    pub fn done(&mut self, offset: i64) -> bool {
        let Ok(index) = self.pending.binary_search_by_key(&offset, |(o, _)| *o) else { return false };
        self.pending[index].1 = true;

        while let Some(&(first, true)) = self.pending.front() {
            self.position = first + 1;
            self.pending.pop_front();
        }

        true
    }

    /// The offset to commit: one past the end of the contiguous finished range. `None` until the
    /// first record has finished.
    pub fn commit_position(&self) -> Option<i64> {
        (self.position >= 0).then_some(self.position)
    }

    /// Taken records that are not ready to be committed yet.
    #[cfg(test)]
    pub fn outstanding(&self) -> usize {
        self.pending.len()
    }
}
