//! Batches occurrence logs. A batch that fails to send is kept and sent first next time, so logs
//! keep their order.

use crate::broker::{PublishOptions, Publisher};
use crate::protocol::{now, OccurrenceLog, WorkerLogBatchMessage, WorkerLogMessage, QUEUE_WORKER_LOGS};
use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

pub(crate) struct LogBuffer {
    publisher: Arc<dyn Publisher>,
    pub(crate) batch_size: usize,
    pub(crate) max_buffered: usize,
    flush_interval: Duration,
    state: Mutex<State>,
    unsent: tokio::sync::Mutex<Vec<WorkerLogMessage>>,
    full: Notify,
}

#[derive(Default)]
struct State {
    buffer: VecDeque<WorkerLogMessage>,
    dropped: usize,
}

impl LogBuffer {
    pub fn new(publisher: Arc<dyn Publisher>) -> Self {
        Self {
            publisher,
            batch_size: 50,
            max_buffered: 10_000,
            flush_interval: Duration::from_millis(500),
            state: Mutex::new(State::default()),
            unsent: tokio::sync::Mutex::new(Vec::new()),
            full: Notify::new(),
        }
    }

    pub fn add(&self, occurrence_id: &str, instance_id: &str, log: OccurrenceLog) {
        let mut state = self.state.lock().unwrap();

        if state.buffer.len() >= self.max_buffered {
            state.buffer.pop_front();
            state.dropped += 1;

            if state.dropped % 1000 == 1 {
                tracing::warn!(dropped = state.dropped, "log buffer is full; dropping the oldest logs");
            }
        }

        state.buffer.push_back(WorkerLogMessage {
            occurrence_id: occurrence_id.to_string(),
            worker_id: instance_id.to_string(),
            log,
            message_timestamp: now(),
        });

        if state.buffer.len() >= self.batch_size {
            self.full.notify_one();
        }
    }

    pub fn len(&self) -> usize {
        self.state.lock().unwrap().buffer.len()
    }

    /// Sends everything buffered so far. On error the unsent batch stays buffered.
    pub async fn flush(&self) -> Result<(), String> {
        let mut unsent = self.unsent.lock().await;
        let per_round = self.batch_size * 2;

        // Bounded by what is buffered now, so producers that keep logging cannot keep this looping.
        let mut rounds = (self.len() + unsent.len()).div_ceil(per_round) + 1;

        while rounds > 0 {
            rounds -= 1;

            if unsent.is_empty() {
                let mut state = self.state.lock().unwrap();
                let n = state.buffer.len().min(per_round);
                unsent.extend(state.buffer.drain(..n));
            }

            if unsent.is_empty() {
                return Ok(());
            }

            let batch = WorkerLogBatchMessage { logs: unsent.clone(), batch_timestamp: now(), count: unsent.len() };
            let body = serde_json::to_vec(&batch).map_err(|e| e.to_string())?;

            self.publisher
                .publish("", QUEUE_WORKER_LOGS, body, PublishOptions { persistent: true, ..Default::default() })
                .await?;

            unsent.clear();
        }

        Ok(())
    }

    /// Flushes every flush interval, or as soon as a batch is full, until `stop` is cancelled.
    pub async fn run(self: Arc<Self>, stop: CancellationToken) {
        loop {
            tokio::select! {
                _ = stop.cancelled() => return,
                _ = tokio::time::sleep(self.flush_interval) => {}
                _ = self.full.notified() => {}
            }

            if let Err(error) = self.flush().await {
                tracing::debug!(%error, "failed to send logs; will retry");
            }
        }
    }
}
