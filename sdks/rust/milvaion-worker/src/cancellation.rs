//! Receives the cancellation requests the API publishes on Redis, reconnecting when Redis drops.

use crate::processor::CancelRegistry;
use crate::protocol::CancellationRequest;
use futures_util::StreamExt;
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::sync::oneshot;
use tokio_util::sync::CancellationToken;

/// Listens until `stop` is cancelled. `ready` receives `Ok` after the first subscription, or `Err`
/// for an invalid URL; failed attempts leave their error in `last_error`.
pub(crate) async fn listen(
    url: String,
    channel: String,
    cancels: Arc<CancelRegistry>,
    stop: CancellationToken,
    ready: oneshot::Sender<Result<(), String>>,
    last_error: Arc<Mutex<String>>,
) {
    let mut ready = Some(ready);

    let client = match redis::Client::open(url.as_str()) {
        Ok(client) => client,
        Err(error) => {
            if let Some(ready) = ready.take() {
                let _ = ready.send(Err(format!("invalid redis_url: {error}")));
            }
            return;
        }
    };

    let mut attempt: u32 = 0;

    while !stop.is_cancelled() {
        let subscribed = async {
            let mut pubsub = client.get_async_pubsub().await?;
            pubsub.subscribe(&channel).await?;
            Ok::<_, redis::RedisError>(pubsub)
        };

        let mut pubsub = match tokio::select! {
            _ = stop.cancelled() => return,
            result = subscribed => result,
        } {
            Ok(pubsub) => pubsub,
            Err(error) => {
                // Until the first subscription succeeds, the worker's startup timeout decides when to give up.
                tracing::warn!(%error, "could not subscribe to Redis cancellations; retrying");
                *last_error.lock().unwrap() = error.to_string();

                attempt += 1;
                if sleep_or_stop(backoff(attempt), &stop).await {
                    return;
                }
                continue;
            }
        };

        attempt = 0;
        tracing::info!(channel = %channel, "listening for cancellations");

        if let Some(ready) = ready.take() {
            let _ = ready.send(Ok(()));
        }

        let mut messages = pubsub.on_message();

        loop {
            tokio::select! {
                _ = stop.cancelled() => return,
                message = messages.next() => match message {
                    Some(message) => handle_message(&message.get_payload::<String>().unwrap_or_default(), &cancels),
                    None => break,
                },
            }
        }

        tracing::warn!("lost the Redis connection; resubscribing to cancellations");
    }
}

pub(crate) fn handle_message(payload: &str, cancels: &CancelRegistry) {
    let Ok(request) = serde_json::from_str::<CancellationRequest>(payload) else {
        tracing::debug!("ignored a cancellation message that is not JSON");
        return;
    };

    let occurrence_id =
        request.correlation_id.filter(|id| !id.is_empty()).or(request.occurrence_id).unwrap_or_default();

    if !occurrence_id.is_empty() && cancels.cancel(&occurrence_id, request.reason.clone()) {
        tracing::info!(occurrence = %occurrence_id, reason = ?request.reason, "cancelling occurrence");
    }
}

pub(crate) fn backoff(attempt: u32) -> Duration {
    Duration::from_secs(1u64 << attempt.min(4)).min(Duration::from_secs(10))
}

/// Returns true when `stop` was cancelled.
pub(crate) async fn sleep_or_stop(delay: Duration, stop: &CancellationToken) -> bool {
    tokio::select! {
        _ = stop.cancelled() => true,
        _ = tokio::time::sleep(delay) => false,
    }
}
