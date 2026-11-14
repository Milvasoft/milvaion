//! Runs a worker on Kafka (`docs/githubdocs/KAFKA-TRANSPORT.md`). One consumer group member reads
//! the worker's job and retry topics on a polling thread; records are buffered per partition,
//! dispatched up to `max_parallel_jobs` (and `max_in_flight_per_partition` per partition) and
//! committed in order once their outcome has been reported.

use super::*;
use crate::broker::{BoxFuture, Delivery, PublishOptions, Publisher, RetryRequest, Settler};
use crate::error::WorkerError;
use crate::options::{ResolvedKafka, ResolvedOptions};
use crate::processor::{parse_occurrence_id, Processor};
use crate::protocol::*;
use rdkafka::admin::{AdminClient, AdminOptions, NewTopic, TopicReplication};
use rdkafka::client::{ClientContext, DefaultClientContext};
use rdkafka::config::ClientConfig;
use rdkafka::consumer::{BaseConsumer, CommitMode, Consumer, ConsumerContext, Rebalance};
use rdkafka::error::RDKafkaErrorCode;
use rdkafka::message::{BorrowedMessage, Header, Headers, Message, OwnedHeaders};
use rdkafka::producer::{FutureProducer, FutureRecord, Producer};
use rdkafka::util::Timeout;
use rdkafka::{Offset, TopicPartitionList};
use std::collections::{BTreeMap, HashSet};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock, RwLock, Weak};
use std::time::{Duration, Instant};
use tokio_util::sync::CancellationToken;

const COMMIT_INTERVAL: Duration = Duration::from_millis(500);
const POLL_TIMEOUT: Duration = Duration::from_millis(100);
const NOT_CONNECTED: &str = "not connected to Kafka";

/// A topic and partition.
type Tp = (String, i32);

pub(crate) struct KafkaTransport {
    me: Weak<KafkaTransport>,
    opts: Arc<ResolvedOptions>,
    cfg: ResolvedKafka,
    topics: KafkaTopics,
    producer: RwLock<Option<FutureProducer>>,
    consumer: RwLock<Option<Arc<BaseConsumer<Context>>>>,
    state: Mutex<State>,
    processor: OnceLock<Weak<Processor>>,
    registration: OnceLock<Vec<u8>>,
    heartbeat_failed: AtomicBool,
    poll_stop: Arc<AtomicBool>,
    poll_thread: Mutex<Option<std::thread::JoinHandle<()>>>,
}

#[derive(Default)]
struct State {
    assigned: HashSet<Tp>,
    parts: BTreeMap<Tp, PartitionState>,
    running: usize,
    cursor: usize,
    stopping: bool,
    next_epoch: u64,
}

/// What this instance holds of one assigned partition. The epoch tells a partition apart from the
/// same partition assigned again after a rebalance.
struct PartitionState {
    epoch: u64,
    retry: bool,
    tracker: OffsetTracker,
    /// Taken from the consumer, not dispatched yet, in offset order.
    backlog: VecDeque<Arc<Record>>,
    in_flight: usize,
    paused: bool,
    /// The head of a retry partition that is not due yet.
    held: Option<(i64, CancellationToken)>,
    committed: i64,
}

/// A record taken from the job or retry topic.
pub(crate) struct Record {
    topic: String,
    partition: i32,
    offset: i64,
    key: Option<Vec<u8>>,
    value: Vec<u8>,
    headers: Vec<(String, Vec<u8>)>,
    header_map: HashMap<String, HeaderValue>,
}

impl Record {
    fn from_message(message: &BorrowedMessage<'_>) -> Self {
        let headers: Vec<(String, Vec<u8>)> = message
            .headers()
            .map(|headers| {
                headers.iter().map(|h| (h.key.to_string(), h.value.map(<[u8]>::to_vec).unwrap_or_default())).collect()
            })
            .unwrap_or_default();

        Record {
            topic: message.topic().to_string(),
            partition: message.partition(),
            offset: message.offset(),
            key: message.key().map(<[u8]>::to_vec),
            value: message.payload().map(<[u8]>::to_vec).unwrap_or_default(),
            header_map: header_map(&headers),
            headers,
        }
    }

    fn tp(&self) -> Tp {
        (self.topic.clone(), self.partition)
    }
}

impl KafkaTransport {
    pub fn new(opts: Arc<ResolvedOptions>) -> Arc<Self> {
        let cfg = opts.kafka.clone().expect("the Kafka transport needs Kafka options");
        let topics = KafkaTopics::new(&cfg.topic_prefix, &opts.worker_id);

        Arc::new_cyclic(|me| Self {
            me: me.clone(),
            opts,
            cfg,
            topics,
            producer: RwLock::new(None),
            consumer: RwLock::new(None),
            state: Mutex::new(State::default()),
            processor: OnceLock::new(),
            registration: OnceLock::new(),
            heartbeat_failed: AtomicBool::new(false),
            poll_stop: Arc::new(AtomicBool::new(false)),
            poll_thread: Mutex::new(None),
        })
    }

    pub fn set_processor(&self, processor: &Arc<Processor>) {
        let _ = self.processor.set(Arc::downgrade(processor));
    }

    /// The registration sent again when heartbeats get through after failing.
    pub fn set_registration(&self, registration: Vec<u8>) {
        let _ = self.registration.set(registration);
    }

    fn processor(&self) -> Option<Arc<Processor>> {
        self.processor.get().and_then(Weak::upgrade)
    }

    fn client_config(&self) -> ClientConfig {
        let mut config = ClientConfig::new();

        for (key, value) in self.cfg.client_settings(&self.opts.instance_id) {
            config.set(key, value);
        }

        config
    }

    /// Connects the producer and creates the topics. Gives up at `deadline`.
    pub async fn connect(&self, deadline: tokio::time::Instant) -> Result<(), WorkerError> {
        let producer: FutureProducer = self
            .client_config()
            // Uncompressed, so every client library of the deployment can read what this worker writes.
            .set("compression.type", "none")
            .create()
            .map_err(|e| WorkerError::Config(format!("invalid Kafka settings: {e}")))?;

        loop {
            let remaining = deadline.saturating_duration_since(tokio::time::Instant::now());
            let probe = producer.clone();
            let timeout = remaining.clamp(Duration::from_millis(100), Duration::from_secs(5));

            let error = match tokio::task::spawn_blocking(move || {
                probe.client().fetch_metadata(None, timeout).map(|m| m.brokers().len())
            })
            .await
            {
                Ok(Ok(brokers)) if brokers > 0 => break,
                Ok(Ok(_)) => "no broker answered".to_string(),
                Ok(Err(error)) => error.to_string(),
                Err(error) => error.to_string(),
            };

            tracing::warn!(brokers = ?self.cfg.brokers, %error, "could not connect to Kafka");

            if tokio::time::Instant::now() + Duration::from_secs(1) >= deadline {
                return Err(WorkerError::Connection(format!(
                    "could not connect to Kafka within {:?}: {error}",
                    self.opts.startup_timeout
                )));
            }

            tokio::time::sleep(Duration::from_secs(1)).await;
        }

        if self.cfg.auto_create_topics {
            self.create_topics().await?;
        }

        *self.producer.write().unwrap() = Some(producer);
        Ok(())
    }

    /// Creates the worker's job and retry topics and the shared topics it writes to. Topics that
    /// exist already are left as they are.
    async fn create_topics(&self) -> Result<(), WorkerError> {
        let admin: AdminClient<DefaultClientContext> =
            self.client_config().create().map_err(|e| WorkerError::Config(format!("invalid Kafka settings: {e}")))?;

        let t = &self.topics;
        let names = [&t.jobs, &t.retry, &t.status, &t.logs, &t.heartbeats, &t.registrations, &t.dlq];
        let topics: Vec<NewTopic<'_>> = names
            .iter()
            .map(|name| {
                NewTopic::new(name, self.cfg.num_partitions, TopicReplication::Fixed(self.cfg.replication_factor))
            })
            .collect();
        let options = AdminOptions::new().operation_timeout(Some(Duration::from_secs(30)));

        let results = admin
            .create_topics(&topics, &options)
            .await
            .map_err(|e| WorkerError::Connection(format!("creating Kafka topics: {e}")))?;

        for result in results {
            match result {
                Ok(_) | Err((_, RDKafkaErrorCode::TopicAlreadyExists)) => {}
                Err((topic, code)) => {
                    return Err(WorkerError::Connection(format!("creating Kafka topic {topic}: {code}")));
                }
            }
        }

        Ok(())
    }

    /// Joins the worker's consumer group and starts the polling thread.
    pub fn consume(&self) -> Result<(), WorkerError> {
        let consumer: BaseConsumer<Context> = self
            .client_config()
            .set("group.id", &self.topics.group)
            .set("enable.auto.commit", "false")
            .set("enable.auto.offset.store", "false")
            .set("auto.offset.reset", "earliest")
            .set("partition.assignment.strategy", "cooperative-sticky")
            .create_with_context(Context { transport: self.me.clone() })
            .map_err(|e| WorkerError::Config(format!("creating the Kafka consumer: {e}")))?;

        consumer
            .subscribe(&[&self.topics.jobs, &self.topics.retry])
            .map_err(|e| WorkerError::Connection(format!("subscribing to the Kafka topics: {e}")))?;

        let consumer = Arc::new(consumer);
        *self.consumer.write().unwrap() = Some(consumer.clone());

        let this = self.me.upgrade().expect("the transport is alive");
        let stop = self.poll_stop.clone();
        let runtime = tokio::runtime::Handle::current();

        let thread = std::thread::Builder::new()
            .name("milvaion-kafka-poll".to_string())
            .spawn(move || {
                let _runtime = runtime.enter();
                this.poll_loop(&consumer, &stop);
            })
            .map_err(|e| WorkerError::Config(format!("starting the Kafka polling thread: {e}")))?;

        *self.poll_thread.lock().unwrap() = Some(thread);

        tracing::info!(
            topics = ?[&self.topics.jobs, &self.topics.retry],
            group = %self.topics.group,
            max_parallel_jobs = self.opts.max_parallel_jobs,
            max_in_flight_per_partition = self.cfg.max_in_flight_per_partition,
            "consuming"
        );

        Ok(())
    }

    /// Polls until stopped, so the member stays in the group even while every partition is paused,
    /// and commits finished offsets every 500 ms.
    fn poll_loop(&self, consumer: &BaseConsumer<Context>, stop: &AtomicBool) {
        let mut last_commit = Instant::now();

        while !stop.load(Ordering::SeqCst) {
            match consumer.poll(POLL_TIMEOUT) {
                Some(Ok(message)) => self.accept(Record::from_message(&message)),
                Some(Err(error)) => {
                    tracing::warn!(%error, "Kafka consumer error");
                    std::thread::sleep(Duration::from_millis(200));
                }
                None => {}
            }

            if last_commit.elapsed() >= COMMIT_INTERVAL {
                self.commit(None, consumer);
                last_commit = Instant::now();
            }
        }
    }

    /// Buffers a polled record in its partition and dispatches what fits.
    fn accept(&self, record: Record) {
        let mut st = self.state.lock().unwrap();

        if st.stopping {
            return;
        }

        let tp = record.tp();

        // A poll can still hold records of a partition revoked since; its new owner reads them.
        if !st.assigned.contains(&tp) {
            return;
        }

        if !st.parts.contains_key(&tp) {
            st.next_epoch += 1;
            let state = PartitionState {
                epoch: st.next_epoch,
                retry: tp.0 == self.topics.retry,
                tracker: OffsetTracker::default(),
                backlog: VecDeque::new(),
                in_flight: 0,
                paused: false,
                held: None,
                committed: -1,
            };
            st.parts.insert(tp.clone(), state);
        }

        let ps = st.parts.get_mut(&tp).expect("inserted above");

        if ps.tracker.add(record.offset) {
            ps.backlog.push_back(Arc::new(record));
        }

        self.dispatch_locked(&mut st);
    }

    fn dispatch(&self) {
        let mut st = self.state.lock().unwrap();
        self.dispatch_locked(&mut st);
    }

    /// Starts buffered records while instance and partition slots are free, holds retry records
    /// that are not due, and pauses every partition that has records waiting here, so the consumer
    /// keeps polling (and stays in the group) without buffering more.
    fn dispatch_locked(&self, st: &mut State) {
        let consumer = self.consumer.read().unwrap();

        let Some(consumer) = consumer.as_ref() else { return };

        if st.stopping || st.parts.is_empty() {
            return;
        }

        let State { parts, running, cursor, .. } = st;
        let order: Vec<Tp> = parts.keys().cloned().collect();
        let start = *cursor % order.len();
        *cursor = cursor.wrapping_add(1);

        let now = chrono::Utc::now().timestamp_millis();
        let mut pause = TopicPartitionList::new();
        let mut resume = TopicPartitionList::new();

        for i in 0..order.len() {
            let tp = &order[(start + i) % order.len()];
            let ps = parts.get_mut(tp).expect("listed above");

            while *running < self.opts.max_parallel_jobs && ps.in_flight < self.cfg.max_in_flight_per_partition {
                let Some(next) = ps.backlog.front().cloned() else { break };

                if ps.retry {
                    if let Some(due) = not_before(&next.header_map).filter(|due| *due > now) {
                        self.hold_locked(ps, &next, due);
                        break;
                    }
                }

                release_hold(ps);
                ps.backlog.pop_front();
                ps.in_flight += 1;
                *running += 1;

                self.spawn_run(tp.clone(), ps.epoch, next);
            }

            let want_paused = !ps.backlog.is_empty();

            if want_paused != ps.paused {
                ps.paused = want_paused;
                let list = if want_paused { &mut pause } else { &mut resume };
                list.add_partition(&tp.0, tp.1);
            }
        }

        if pause.count() > 0 {
            if let Err(error) = consumer.pause(&pause) {
                tracing::debug!(%error, "could not pause Kafka partitions");
            }
        }

        if resume.count() > 0 {
            if let Err(error) = consumer.resume(&resume) {
                tracing::debug!(%error, "could not resume Kafka partitions");
            }
        }
    }

    /// Keeps the head of a retry partition until it is due, sending the occurrence's job heartbeats
    /// meanwhile, and dispatches again when it is.
    fn hold_locked(&self, ps: &mut PartitionState, record: &Arc<Record>, due_ms: i64) {
        if ps.held.as_ref().is_some_and(|(offset, _)| *offset == record.offset) {
            return;
        }

        release_hold(ps);

        let stop = CancellationToken::new();
        ps.held = Some((record.offset, stop.clone()));

        let me = self.me.clone();
        let timer_stop = stop.clone();

        tokio::spawn(async move {
            loop {
                let wait = due_ms - chrono::Utc::now().timestamp_millis();

                if wait <= 0 {
                    break;
                }

                tokio::select! {
                    _ = tokio::time::sleep(Duration::from_millis(wait as u64 + 1)) => {}
                    _ = timer_stop.cancelled() => return,
                }
            }

            if let Some(transport) = me.upgrade() {
                transport.dispatch();
            }
        });

        if let Some(processor) = self.processor() {
            let wait = (due_ms - chrono::Utc::now().timestamp_millis()).max(0) as u64;
            let due = tokio::time::Instant::now() + Duration::from_millis(wait);
            processor.hold(&record.value, &record.header_map, due, stop);
        }
    }

    fn spawn_run(&self, tp: Tp, epoch: u64, record: Arc<Record>) {
        let (Some(transport), Some(processor)) = (self.me.upgrade(), self.processor()) else { return };
        let settled = Arc::new(AtomicBool::new(false));

        let settler = KafkaSettler {
            transport: transport.clone(),
            tp: tp.clone(),
            epoch,
            record: record.clone(),
            settled: settled.clone(),
        };

        tokio::spawn(async move {
            let delivery = Delivery {
                body: record.value.clone(),
                headers: record.header_map.clone(),
                correlation_id: None,
                redelivered: false,
                settler: Box::new(settler),
            };

            processor.handle(delivery, record.topic.clone()).await;

            // The processor stopped before settling the record (a panic in the SDK). Dead-letter it,
            // so the partition can still be committed past it.
            if !settled.load(Ordering::SeqCst) {
                let reason = "the worker failed while processing the record";

                if let Err(error) = transport.dead_letter_raw(&tp, epoch, &record, reason).await {
                    tracing::error!(%error, "could not settle a Kafka record");
                }
            }

            transport.release(&tp, epoch);
        });
    }

    /// Frees the instance and partition slot of a finished record.
    fn release(&self, tp: &Tp, epoch: u64) {
        let mut st = self.state.lock().unwrap();
        st.running = st.running.saturating_sub(1);

        if let Some(ps) = st.parts.get_mut(tp).filter(|ps| ps.epoch == epoch) {
            ps.in_flight = ps.in_flight.saturating_sub(1);
        }

        self.dispatch_locked(&mut st);
    }

    /// Lets the record's offset be committed. A partition revoked meanwhile is ignored: its new
    /// owner runs the record again.
    fn mark_done(&self, tp: &Tp, epoch: u64, offset: i64) {
        let mut st = self.state.lock().unwrap();

        if let Some(ps) = st.parts.get_mut(tp).filter(|ps| ps.epoch == epoch) {
            ps.tracker.done(offset);
        }
    }

    /// Commits the finished position of every partition (or of the given ones) that moved.
    fn commit(&self, only: Option<&HashSet<Tp>>, consumer: &BaseConsumer<Context>) {
        let pending: Vec<(Tp, u64, i64)> = {
            let st = self.state.lock().unwrap();

            st.parts
                .iter()
                .filter(|(tp, _)| only.is_none_or(|only| only.contains(*tp)))
                .filter_map(|(tp, ps)| {
                    let position = ps.tracker.commit_position()?;
                    (position > ps.committed).then(|| (tp.clone(), ps.epoch, position))
                })
                .collect()
        };

        if pending.is_empty() {
            return;
        }

        let mut list = TopicPartitionList::new();

        for ((topic, partition), _, position) in &pending {
            let _ = list.add_partition_offset(topic, *partition, Offset::Offset(*position));
        }

        if let Err(error) = consumer.commit(&list, CommitMode::Sync) {
            tracing::warn!(%error, "Kafka offset commit failed; will retry");
            return;
        }

        let mut st = self.state.lock().unwrap();

        for (tp, epoch, position) in pending {
            if let Some(ps) = st.parts.get_mut(&tp).filter(|ps| ps.epoch == epoch) {
                ps.committed = ps.committed.max(position);
            }
        }
    }

    fn assigned(&self, partitions: &[Tp]) {
        self.state.lock().unwrap().assigned.extend(partitions.iter().cloned());
    }

    fn forget(&self, partitions: &[Tp]) {
        let mut st = self.state.lock().unwrap();

        for tp in partitions {
            st.assigned.remove(tp);

            if let Some(mut ps) = st.parts.remove(tp) {
                release_hold(&mut ps);
            }
        }
    }

    /// Stops taking records. Running jobs carry on and can still report their outcome.
    pub async fn stop_consuming(&self) {
        {
            let mut st = self.state.lock().unwrap();
            st.stopping = true;

            for ps in st.parts.values_mut() {
                release_hold(ps);
            }
        }

        self.poll_stop.store(true, Ordering::SeqCst);
        let thread = self.poll_thread.lock().unwrap().take();

        if let Some(thread) = thread {
            let _ = tokio::task::spawn_blocking(move || thread.join()).await;
        }

        if let Some(consumer) = self.consumer.read().unwrap().as_ref() {
            if let Ok(assignment) = consumer.assignment() {
                let _ = consumer.pause(&assignment);
            }
        }
    }

    /// Runs once jobs have finished or have been written back: commits what has finished, then
    /// leaves the group.
    pub async fn drained(&self) {
        let consumer = self.consumer.write().unwrap().take();
        let (Some(consumer), Some(this)) = (consumer, self.me.upgrade()) else { return };

        let _ = tokio::task::spawn_blocking(move || {
            this.commit(None, &consumer);
            // Dropping the consumer closes it: it leaves the group after the revoke callback.
            drop(consumer);
        })
        .await;
    }

    /// Disconnects the producer after the final heartbeat and logs have been sent.
    pub async fn close(&self) {
        let producer = self.producer.write().unwrap().take();

        if let Some(producer) = producer {
            let _ = tokio::task::spawn_blocking(move || {
                let _ = producer.flush(Duration::from_secs(10));
            })
            .await;
        }
    }

    async fn produce(
        &self,
        topic: &str,
        key: Option<&[u8]>,
        value: &[u8],
        headers: &[(String, Vec<u8>)],
    ) -> Result<(), String> {
        let producer = self.producer.read().unwrap().clone().ok_or_else(|| NOT_CONNECTED.to_string())?;
        let mut owned = OwnedHeaders::new_with_capacity(headers.len());

        for (key, value) in headers {
            owned = owned.insert(Header { key, value: Some(value.as_slice()) });
        }

        let mut record = FutureRecord::<[u8], [u8]>::to(topic).payload(value).headers(owned);

        if let Some(key) = key {
            record = record.key(key);
        }

        producer.send(record, Timeout::After(Duration::from_secs(10))).await.map(|_| ()).map_err(|(e, _)| e.to_string())
    }

    /// Retries a produce whose failure would otherwise leave the record's offset stuck.
    async fn produce_reliably(
        &self,
        topic: &str,
        key: Option<&[u8]>,
        value: &[u8],
        headers: &[(String, Vec<u8>)],
    ) -> Result<(), String> {
        let mut result = Ok(());

        for (attempt, delay) in [0u64, 1, 2, 5].into_iter().enumerate() {
            if attempt > 0 {
                tokio::time::sleep(Duration::from_secs(delay)).await;
            }

            result = tokio::time::timeout(Duration::from_secs(30), self.produce(topic, key, value, headers))
                .await
                .unwrap_or_else(|_| Err("timed out".to_string()));

            match &result {
                Ok(()) => return result,
                Err(error) if error == NOT_CONNECTED => return result,
                Err(_) => {}
            }
        }

        result
    }

    /// Writes an interrupted job back to its own topic, the counterpart of a RabbitMQ requeue.
    async fn requeue(&self, tp: &Tp, epoch: u64, record: &Record) -> Result<(), String> {
        self.produce_reliably(&record.topic, record.key.as_deref(), &record.value, &record.headers)
            .await
            .map_err(|e| format!("writing the interrupted job back to {}: {e}", record.topic))?;

        self.mark_done(tp, epoch, record.offset);
        Ok(())
    }

    /// Forwards a record the worker cannot process to the dead letter topic unchanged.
    async fn dead_letter_raw(&self, tp: &Tp, epoch: u64, record: &Record, reason: &str) -> Result<(), String> {
        let mut headers = record.headers.clone();
        headers.push((HEADER_ORIGINAL_TOPIC.to_string(), record.topic.as_bytes().to_vec()));
        headers.push((HEADER_DLQ_REASON.to_string(), reason.as_bytes().to_vec()));

        let occurrence_id = parse_occurrence_id(&record.header_map, None);
        let key = occurrence_id.as_ref().map(|id| id.as_bytes()).or(record.key.as_deref());

        self.produce_reliably(&self.topics.dlq, key, &record.value, &headers)
            .await
            .map_err(|e| format!("forwarding a record to {}: {e}", self.topics.dlq))?;

        self.mark_done(tp, epoch, record.offset);
        Ok(())
    }

    /// Writes the next attempt to the retry topic, due at `retry.not_before_ms`.
    async fn produce_retry(&self, record: &Record, retry: RetryRequest) -> Result<(), String> {
        let mut headers: BTreeMap<String, Vec<u8>> = record.headers.iter().cloned().collect();

        for (key, value) in [
            (HEADER_OCCURRENCE_ID, retry.occurrence_id.clone()),
            (HEADER_CORRELATION_ID, retry.occurrence_id.clone()),
            (HEADER_RETRY_COUNT, retry.retry_count.to_string()),
            (HEADER_MAX_RETRIES, retry.max_retries.to_string()),
            (HEADER_NOT_BEFORE, retry.not_before_ms.to_string()),
        ] {
            headers.insert(key.to_string(), value.into_bytes());
        }

        let headers: Vec<(String, Vec<u8>)> = headers.into_iter().collect();
        let key = if retry.job_id.is_empty() { record.key.as_deref() } else { Some(retry.job_id.as_bytes()) };

        self.produce(&self.topics.retry, key, &record.value, &headers).await
    }

    /// Registers again once heartbeats get through after failing, the Kafka counterpart of
    /// registering after a RabbitMQ reconnect: the API may have dropped the instance meanwhile.
    fn after_heartbeat(&self, result: &Result<(), String>) {
        match result {
            Err(error) if error != NOT_CONNECTED => self.heartbeat_failed.store(true, Ordering::SeqCst),
            Err(_) => {}
            Ok(()) => {
                if !self.heartbeat_failed.swap(false, Ordering::SeqCst) {
                    return;
                }

                let (Some(this), Some(registration)) = (self.me.upgrade(), self.registration.get().cloned()) else {
                    return;
                };

                tokio::spawn(async move {
                    let options = PublishOptions {
                        persistent: true,
                        key: Some(this.opts.instance_id.clone()),
                        ..Default::default()
                    };
                    let registered = tokio::time::timeout(
                        Duration::from_secs(30),
                        this.publish("", QUEUE_WORKER_REGISTRATION, registration, options),
                    )
                    .await
                    .unwrap_or_else(|_| Err("timed out".to_string()));

                    if let Err(error) = registered {
                        tracing::warn!(%error, "could not register again after Kafka recovered");
                        this.heartbeat_failed.store(true, Ordering::SeqCst);
                    }
                });
            }
        }
    }
}

fn release_hold(ps: &mut PartitionState) {
    if let Some((_, stop)) = ps.held.take() {
        stop.cancel();
    }
}

impl Publisher for KafkaTransport {
    /// Maps the processor's RabbitMQ-named destinations to the Kafka topics.
    fn publish<'a>(
        &'a self,
        exchange: &'a str,
        routing_key: &'a str,
        body: Vec<u8>,
        options: PublishOptions,
    ) -> BoxFuture<'a, Result<(), String>> {
        Box::pin(async move {
            let t = &self.topics;
            let topic = match (exchange, routing_key) {
                (EXCHANGE_DEAD_LETTER, _) => &t.dlq,
                ("", QUEUE_STATUS_UPDATES) => &t.status,
                ("", QUEUE_WORKER_LOGS) => &t.logs,
                ("", QUEUE_WORKER_HEARTBEAT) => &t.heartbeats,
                ("", QUEUE_WORKER_REGISTRATION) => &t.registrations,
                _ => return Err(format!("no Kafka topic for exchange {exchange:?} and routing key {routing_key:?}")),
            };

            let key = options.key.clone().or_else(|| options.correlation_id.clone()).filter(|k| !k.is_empty());
            let mut headers: Vec<(String, Vec<u8>)> =
                options.headers.iter().filter_map(|(k, v)| Some((k.clone(), header_bytes(v)?))).collect();

            if let Some(id) = &options.correlation_id {
                headers.push((HEADER_OCCURRENCE_ID.to_string(), id.as_bytes().to_vec()));
                headers.push((HEADER_CORRELATION_ID.to_string(), id.as_bytes().to_vec()));
            }

            let headers = sorted_headers(headers);
            let result = self.produce(topic, key.as_deref().map(str::as_bytes), &body, &headers).await;

            if exchange.is_empty() && routing_key == QUEUE_WORKER_HEARTBEAT {
                self.after_heartbeat(&result);
            }

            result
        })
    }
}

/// Settles one record: done (committable), written back, retried or dead-lettered.
struct KafkaSettler {
    transport: Arc<KafkaTransport>,
    tp: Tp,
    epoch: u64,
    record: Arc<Record>,
    settled: Arc<AtomicBool>,
}

impl Settler for KafkaSettler {
    fn ack(&self) -> BoxFuture<'_, Result<(), String>> {
        self.settled.store(true, Ordering::SeqCst);
        self.transport.mark_done(&self.tp, self.epoch, self.record.offset);
        Box::pin(async { Ok(()) })
    }

    fn nack(&self, requeue: bool) -> BoxFuture<'_, Result<(), String>> {
        if requeue {
            self.settled.store(true, Ordering::SeqCst);
            Box::pin(self.transport.requeue(&self.tp, self.epoch, &self.record))
        } else {
            self.reject("rejected by the worker".to_string())
        }
    }

    fn reject(&self, reason: String) -> BoxFuture<'_, Result<(), String>> {
        self.settled.store(true, Ordering::SeqCst);
        Box::pin(async move { self.transport.dead_letter_raw(&self.tp, self.epoch, &self.record, &reason).await })
    }

    fn schedule_retry(&self, retry: RetryRequest) -> Option<BoxFuture<'_, Result<(), String>>> {
        Some(Box::pin(self.transport.produce_retry(&self.record, retry)))
    }
}

/// Receives rebalances on the polling thread.
pub(crate) struct Context {
    transport: Weak<KafkaTransport>,
}

impl ClientContext for Context {}

impl ConsumerContext for Context {
    /// Commits what has finished on revoked partitions and forgets them. Jobs still running on them
    /// finish and report, but their offsets are left to the new owner.
    fn pre_rebalance(&self, consumer: &BaseConsumer<Self>, rebalance: &Rebalance<'_>) {
        let Some(transport) = self.transport.upgrade() else { return };

        match rebalance {
            Rebalance::Revoke(list) => {
                let revoked = partitions(list);

                if consumer.assignment_lost() {
                    tracing::warn!(partitions = ?revoked, "Kafka partitions lost");
                } else {
                    transport.commit(Some(&revoked.iter().cloned().collect()), consumer);

                    if !revoked.is_empty() {
                        tracing::info!(partitions = ?revoked, "Kafka partitions revoked");
                    }
                }

                transport.forget(&revoked);
            }
            Rebalance::Error(error) => tracing::warn!(%error, "Kafka rebalance failed"),
            Rebalance::Assign(_) => {}
        }
    }

    fn post_rebalance(&self, consumer: &BaseConsumer<Self>, rebalance: &Rebalance<'_>) {
        let (Some(transport), Rebalance::Assign(list)) = (self.transport.upgrade(), rebalance) else { return };
        let assigned = partitions(list);

        transport.assigned(&assigned);

        // A partition paused by an earlier owner of this member must flow again.
        let _ = consumer.resume(list);

        if !assigned.is_empty() {
            tracing::info!(partitions = ?assigned, "Kafka partitions assigned");
        }
    }
}

fn partitions(list: &TopicPartitionList) -> Vec<Tp> {
    list.elements().iter().map(|e| (e.topic().to_string(), e.partition())).collect()
}
