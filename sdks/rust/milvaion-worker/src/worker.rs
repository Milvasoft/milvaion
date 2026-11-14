//! The worker: connections, consumers, registration, heartbeats and graceful shutdown.
//! RabbitMQ is driven from here; the Kafka transport lives in `crate::kafka`.

use crate::broker::amqp::{self, AmqpPublisher};
use crate::broker::{PublishOptions, Publisher};
use crate::cancellation;
use crate::context::JobContext;
use crate::error::{AbortReason, JobError, WorkerError};
use crate::log_buffer::LogBuffer;
use crate::metrics::{self, ResourceSampler};
use crate::options::{self, JobOptions, ResolvedOptions, Transport, WorkerOptions};
use crate::processor::{CancelRegistry, Handler, Processor, Registered};
use crate::protocol::*;
use futures_util::StreamExt;
use lapin::options::{
    BasicCancelOptions, BasicConsumeOptions, BasicQosOptions, ConfirmSelectOptions, ExchangeDeclareOptions,
    QueueBindOptions, QueueDeclareOptions,
};
use lapin::types::{AMQPValue, FieldTable, LongString, ShortString};
use lapin::{Channel, Connection, ConnectionProperties, ExchangeKind};
use serde::Serialize;
use std::collections::{BTreeMap, HashMap, HashSet};
use std::future::Future;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock, Weak};
use std::time::{Duration, Instant};
use tokio::sync::oneshot;
use tokio_util::sync::CancellationToken;

/// A job implemented as a type, for handlers that hold state such as a database pool.
///
/// ```
/// use milvaion_worker::{Job, JobContext, JobError};
///
/// struct SendEmail;
///
/// impl Job for SendEmail {
///     type Output = String;
///
///     async fn execute(&self, ctx: JobContext) -> Result<String, JobError> {
///         ctx.info("sending");
///         Ok("sent".to_string())
///     }
/// }
/// ```
pub trait Job: Send + Sync + 'static {
    /// The result, stored on the occurrence. A string is stored as-is, `()` or `None` becomes
    /// `Job {name} completed successfully`, and anything else is stored as JSON.
    type Output: Serialize + Send + 'static;

    /// Runs one execution.
    fn execute(&self, ctx: JobContext) -> impl Future<Output = Result<Self::Output, JobError>> + Send;
}

/// Runs Milvaion jobs. Register handlers, then call [`run`](Worker::run).
pub struct Worker {
    opts: Arc<ResolvedOptions>,
    jobs: Vec<Arc<Registered>>,
    names: HashSet<String>,
}

impl Worker {
    /// Validates the options and creates a worker.
    pub fn new(options: WorkerOptions) -> Result<Self, WorkerError> {
        Ok(Self { opts: Arc::new(options::resolve_options(options)?), jobs: Vec::new(), names: HashSet::new() })
    }

    /// Id of the worker application.
    pub fn worker_id(&self) -> &str {
        &self.opts.worker_id
    }

    /// Id of this process.
    pub fn instance_id(&self) -> &str {
        &self.opts.instance_id
    }

    /// Registers an async handler under the name the dashboard dispatches the job by
    /// (`JobNameInWorker`). Names are unique within a worker and matched case-insensitively.
    pub fn handle<F, Fut, T>(&mut self, name: &str, handler: F) -> Result<&mut Self, WorkerError>
    where
        F: Fn(JobContext) -> Fut + Send + Sync + 'static,
        Fut: Future<Output = Result<T, JobError>> + Send + 'static,
        T: Serialize + Send + 'static,
    {
        self.handle_with(name, JobOptions::default(), handler)
    }

    /// Registers an async handler with options.
    pub fn handle_with<F, Fut, T>(
        &mut self,
        name: &str,
        options: JobOptions,
        handler: F,
    ) -> Result<&mut Self, WorkerError>
    where
        F: Fn(JobContext) -> Fut + Send + Sync + 'static,
        Fut: Future<Output = Result<T, JobError>> + Send + 'static,
        T: Serialize + Send + 'static,
    {
        let handler: Handler = Arc::new(move |ctx| {
            let future = handler(ctx);
            Box::pin(async move { to_json(future.await?) })
        });

        self.add(name, options, handler)
    }

    /// Registers a [`Job`] implementation.
    pub fn register<J: Job>(&mut self, name: &str, options: JobOptions, job: J) -> Result<&mut Self, WorkerError> {
        let job = Arc::new(job);

        let handler: Handler = Arc::new(move |ctx| {
            let job = job.clone();
            Box::pin(async move { to_json(job.execute(ctx).await?) })
        });

        self.add(name, options, handler)
    }

    fn add(&mut self, name: &str, options: JobOptions, handler: Handler) -> Result<&mut Self, WorkerError> {
        let name = name.trim();

        if name.is_empty() {
            return Err(WorkerError::Config("job name is required".to_string()));
        }

        if !self.names.insert(name.to_lowercase()) {
            return Err(WorkerError::Config(format!(
                "job {name:?} is already registered; job names must be unique within a worker (case-insensitive)"
            )));
        }

        let job = options::resolve_job(name, options, &self.opts)?;
        self.jobs.push(Arc::new(Registered { job, handler }));

        Ok(self)
    }

    /// Runs until Ctrl+C or, on Unix, SIGTERM, then shuts down gracefully.
    pub async fn run_until_signal(self) -> Result<(), WorkerError> {
        self.run(shutdown_signal()).await
    }

    /// Connects, registers this instance and processes jobs until `shutdown` completes. It then
    /// stops taking jobs, lets running ones finish for up to `shutdown_timeout`, returns the rest
    /// to the queue, tells the API the instance has left and returns `Ok`. It returns an error if
    /// the broker (RabbitMQ or Kafka) or Redis cannot be reached within `startup_timeout`.
    pub async fn run(self, shutdown: impl Future<Output = ()> + Send) -> Result<(), WorkerError> {
        if self.jobs.is_empty() {
            return Err(WorkerError::Config("register at least one job before run".to_string()));
        }

        let opts = self.opts.clone();
        tracing::info!(worker = %opts.worker_id, instance = %opts.instance_id, jobs = self.jobs.len(), "starting worker");

        let amqp_publisher = Arc::new(AmqpPublisher::default());
        #[cfg(feature = "kafka")]
        let kafka = (opts.transport == Transport::Kafka).then(|| crate::kafka::KafkaTransport::new(opts.clone()));

        let publisher: Arc<dyn Publisher> = match opts.transport {
            Transport::RabbitMq => amqp_publisher.clone(),
            #[cfg(feature = "kafka")]
            Transport::Kafka => kafka.clone().expect("created above"),
            #[cfg(not(feature = "kafka"))]
            Transport::Kafka => unreachable!("resolve_options rejects Kafka without the kafka feature"),
        };
        let logs = Arc::new(LogBuffer::new(publisher.clone()));
        let cancels = Arc::new(CancelRegistry::default());
        let heartbeats = Arc::new(Heartbeats::new(opts.clone(), publisher.clone()));

        let jobs: HashMap<String, Arc<Registered>> =
            self.jobs.iter().map(|r| (r.job.name.to_lowercase(), r.clone())).collect();

        let mut processor = Processor::new(opts.clone(), jobs, publisher.clone(), logs.clone(), cancels.clone());
        let hb = heartbeats.clone();
        processor.on_count_changed = Some(Box::new(move || hb.request_immediate()));
        let processor = Arc::new(processor);
        let _ = heartbeats.processor.set(Arc::downgrade(&processor));

        #[cfg(feature = "kafka")]
        if let Some(kafka) = &kafka {
            kafka.set_processor(&processor);
        }

        let stop_background = CancellationToken::new();
        let _stop_background_on_exit = stop_background.clone().drop_guard();

        tokio::spawn(logs.clone().run(stop_background.clone()));

        let deadline = tokio::time::Instant::now() + opts.startup_timeout;
        tokio::pin!(shutdown);

        // Redis
        if let Some(url) = opts.redis_url.clone() {
            let (ready_tx, ready_rx) = oneshot::channel();
            let last_error = Arc::new(Mutex::new(String::from("no attempt finished")));

            tokio::spawn(cancellation::listen(
                url,
                opts.cancellation_channel.clone(),
                cancels.clone(),
                stop_background.clone(),
                ready_tx,
                last_error.clone(),
            ));

            tokio::select! {
                _ = &mut shutdown => return Ok(()),
                ready = tokio::time::timeout_at(deadline, ready_rx) => match ready {
                    Ok(Ok(Ok(()))) => {}
                    Ok(Ok(Err(error))) => return Err(WorkerError::Config(error)),
                    Ok(Err(_)) => return Err(WorkerError::Connection("the Redis listener stopped".to_string())),
                    Err(_) => {
                        let error = last_error.lock().unwrap().clone();
                        return Err(WorkerError::Connection(format!(
                            "could not connect to Redis within {:?}: {error}",
                            opts.startup_timeout
                        )));
                    }
                },
            }
        } else {
            tracing::warn!("Redis is disabled: jobs cannot be cancelled from the dashboard");
        }

        let broker = match opts.transport {
            Transport::RabbitMq => {
                let link = Arc::new(Link {
                    opts: opts.clone(),
                    jobs: self.jobs.clone(),
                    publisher: amqp_publisher.clone(),
                    processor: processor.clone(),
                    heartbeats: heartbeats.clone(),
                    stopping: AtomicBool::new(false),
                    consumers: Mutex::new(Vec::new()),
                    last_error: Mutex::new(String::from("no attempt finished")),
                });

                let stop_connection = CancellationToken::new();
                let (ready_tx, ready_rx) = oneshot::channel();
                let connection_task = tokio::spawn(link.clone().connection_loop(stop_connection.clone(), ready_tx));

                let connected = tokio::select! {
                    _ = &mut shutdown => None,
                    ready = tokio::time::timeout_at(deadline, ready_rx) => Some(ready.is_ok()),
                };

                if connected != Some(true) {
                    stop_connection.cancel();
                    let _ = connection_task.await;

                    // Asked to stop before the first connection.
                    if connected.is_none() {
                        return Ok(());
                    }

                    let error = link.last_error.lock().unwrap().clone();
                    return Err(WorkerError::Connection(format!(
                        "could not connect to RabbitMQ within {:?}: {error}",
                        opts.startup_timeout
                    )));
                }

                Broker::RabbitMq { link, stop_connection, connection_task }
            }
            #[cfg(feature = "kafka")]
            Transport::Kafka => {
                let kafka = kafka.clone().expect("created above");

                let started = tokio::select! {
                    _ = &mut shutdown => None,
                    started = start_kafka(&kafka, &opts, &self.jobs, &heartbeats, deadline) => Some(started),
                };

                match started {
                    Some(Ok(())) => Broker::Kafka(kafka),
                    Some(Err(error)) => {
                        kafka.close().await;
                        return Err(error);
                    }
                    None => {
                        kafka.close().await;
                        return Ok(());
                    }
                }
            }
            #[cfg(not(feature = "kafka"))]
            Transport::Kafka => unreachable!("resolve_options rejects Kafka without the kafka feature"),
        };

        heartbeats.enabled.store(true, Ordering::SeqCst);

        let heartbeat_task = {
            let heartbeats = heartbeats.clone();
            let stop = stop_background.clone();
            let interval = opts.heartbeat_interval;

            tokio::spawn(async move {
                let mut ticker = tokio::time::interval_at(tokio::time::Instant::now() + interval, interval);

                loop {
                    tokio::select! {
                        _ = stop.cancelled() => return,
                        _ = ticker.tick() => heartbeats.send(false).await,
                    }
                }
            })
        };

        shutdown.await;

        tracing::info!("stopping worker");
        broker.stop_consuming().await;
        drain(&processor, &opts).await;
        broker.drained().await;

        heartbeat_task.abort();
        heartbeats.enabled.store(false, Ordering::SeqCst);
        heartbeats.send(true).await;

        match tokio::time::timeout(Duration::from_secs(10), logs.flush()).await {
            Ok(Ok(())) => {}
            Ok(Err(error)) => tracing::warn!(%error, "some job logs could not be sent before shutdown"),
            Err(_) => tracing::warn!("timed out sending job logs before shutdown"),
        }

        stop_background.cancel();
        broker.close().await;

        tracing::info!("worker stopped");
        Ok(())
    }
}

/// The broker side of a running worker.
enum Broker {
    RabbitMq {
        link: Arc<Link>,
        stop_connection: CancellationToken,
        connection_task: tokio::task::JoinHandle<()>,
    },
    #[cfg(feature = "kafka")]
    Kafka(Arc<crate::kafka::KafkaTransport>),
}

impl Broker {
    /// Stops taking new jobs. Running jobs carry on and can still report their outcome.
    async fn stop_consuming(&self) {
        match self {
            Broker::RabbitMq { link, .. } => link.stop_consuming().await,
            #[cfg(feature = "kafka")]
            Broker::Kafka(kafka) => kafka.stop_consuming().await,
        }
    }

    /// Runs once running jobs have finished or have been returned to the broker.
    async fn drained(&self) {
        match self {
            // Messages of interrupted jobs were already nacked with requeue.
            Broker::RabbitMq { .. } => {}
            #[cfg(feature = "kafka")]
            Broker::Kafka(kafka) => kafka.drained().await,
        }
    }

    /// Disconnects after the final heartbeat and logs have been sent.
    async fn close(self) {
        match self {
            Broker::RabbitMq { stop_connection, connection_task, .. } => {
                stop_connection.cancel();
                let _ = connection_task.await;
            }
            #[cfg(feature = "kafka")]
            Broker::Kafka(kafka) => kafka.close().await,
        }
    }
}

/// Connects to Kafka, creates the topics, registers the instance and joins the consumer group.
#[cfg(feature = "kafka")]
async fn start_kafka(
    kafka: &Arc<crate::kafka::KafkaTransport>,
    opts: &ResolvedOptions,
    jobs: &[Arc<Registered>],
    heartbeats: &Heartbeats,
    deadline: tokio::time::Instant,
) -> Result<(), WorkerError> {
    kafka.connect(deadline).await?;

    let registration = registration_body(opts, jobs);
    kafka.set_registration(registration.clone());
    register(kafka.as_ref(), opts, registration).await.map_err(WorkerError::Connection)?;

    heartbeats.send(false).await;
    tracing::info!(worker = %opts.worker_id, transport = "kafka", "registered");

    kafka.consume()
}

/// Lets running jobs finish for up to `shutdown_timeout`, then stops the rest, which go back to
/// the broker.
async fn drain(processor: &Processor, opts: &ResolvedOptions) {
    processor.shutdown.cancel();

    if !processor.wait_for_idle(opts.shutdown_timeout).await {
        tracing::warn!(
            running = processor.running_jobs(),
            timeout = ?opts.shutdown_timeout,
            "jobs still running after the shutdown timeout; returning them to the queue"
        );
        processor.cancels.abort_all(AbortReason::WorkerStopping);
        processor.wait_for_idle(Duration::from_secs(10)).await;
    }
}

fn registration_body(opts: &ResolvedOptions, jobs: &[Arc<Registered>]) -> Vec<u8> {
    serde_json::to_vec(&build_registration(opts, jobs)).expect("a registration always serializes")
}

/// Publishes this instance's registration.
async fn register(publisher: &dyn Publisher, opts: &ResolvedOptions, registration: Vec<u8>) -> Result<(), String> {
    let options = PublishOptions { persistent: true, key: Some(opts.instance_id.clone()), ..Default::default() };

    tokio::time::timeout(
        Duration::from_secs(30),
        publisher.publish("", QUEUE_WORKER_REGISTRATION, registration, options),
    )
    .await
    .map_err(|_| "registering: timed out".to_string())?
    .map_err(|e| format!("registering: {e}"))
}

fn to_json<T: Serialize>(value: T) -> Result<serde_json::Value, JobError> {
    serde_json::to_value(value).map_err(|e| JobError::new(format!("could not serialize the job result: {e}")))
}

async fn shutdown_signal() {
    #[cfg(unix)]
    {
        let mut terminate = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
            .expect("could not listen for SIGTERM");

        tokio::select! {
            _ = tokio::signal::ctrl_c() => {}
            _ = terminate.recv() => {}
        }
    }

    #[cfg(not(unix))]
    {
        let _ = tokio::signal::ctrl_c().await;
    }
}

/// The RabbitMQ side of a running worker.
struct Link {
    opts: Arc<ResolvedOptions>,
    jobs: Vec<Arc<Registered>>,
    publisher: Arc<AmqpPublisher>,
    processor: Arc<Processor>,
    heartbeats: Arc<Heartbeats>,
    stopping: AtomicBool,
    consumers: Mutex<Vec<(Channel, String)>>,
    last_error: Mutex<String>,
}

impl Link {
    /// Keeps a RabbitMQ connection up, rebuilding the topology, consumers and registration after
    /// every reconnect. Sends on `ready` after the first successful setup.
    async fn connection_loop(self: Arc<Self>, stop: CancellationToken, ready: oneshot::Sender<()>) {
        let mut ready = Some(ready);
        let mut attempt: u32 = 0;

        while !stop.is_cancelled() {
            let setup = tokio::select! {
                _ = stop.cancelled() => return,
                setup = self.connect_and_setup() => setup,
            };

            let (connection, channels) = match setup {
                Ok(setup) => setup,
                Err(error) => {
                    tracing::warn!(%error, "could not connect to RabbitMQ");
                    *self.last_error.lock().unwrap() = error;
                    attempt += 1;

                    if cancellation::sleep_or_stop(cancellation::backoff(attempt), &stop).await {
                        return;
                    }

                    continue;
                }
            };

            attempt = 0;
            tracing::info!("connected to RabbitMQ");

            if let Some(ready) = ready.take() {
                let _ = ready.send(());
            }

            let mut events = connection.events_listener();

            loop {
                let healthy = connection.status().connected() && channels.iter().all(|c| c.status().connected());

                if !healthy {
                    break;
                }

                tokio::select! {
                    _ = stop.cancelled() => {
                        self.publisher.detach();
                        let _ = connection.close(200, ShortString::from("worker stopped")).await;
                        return;
                    }
                    event = events.next() => {
                        if let Some(lapin::Event::Error(error)) = event {
                            tracing::warn!(%error, "RabbitMQ connection error");
                        }
                    }
                    _ = tokio::time::sleep(Duration::from_millis(500)) => {}
                }
            }

            self.publisher.detach();
            self.consumers.lock().unwrap().clear();
            let _ = connection.close(320, ShortString::from("reconnecting")).await;
            tracing::warn!("lost the RabbitMQ connection; reconnecting");
        }
    }

    async fn connect_and_setup(&self) -> Result<(Connection, Vec<Channel>), String> {
        let properties =
            ConnectionProperties::default().with_connection_name(LongString::from(self.opts.instance_id.clone()));
        let connection = Connection::connect(&self.opts.rabbitmq_url, properties).await.map_err(|e| e.to_string())?;

        match self.setup(&connection).await {
            Ok(channels) => Ok((connection, channels)),
            Err(error) => {
                let _ = connection.close(200, ShortString::from("setup failed")).await;
                Err(error.to_string())
            }
        }
    }

    async fn setup(&self, connection: &Connection) -> Result<Vec<Channel>, Box<dyn std::error::Error + Send + Sync>> {
        let mut queue_args = FieldTable::default();

        if self.opts.quorum {
            queue_args.insert(ShortString::from("x-queue-type"), AMQPValue::LongString(LongString::from("quorum")));
        }

        let durable = QueueDeclareOptions { durable: true, ..Default::default() };

        let publish_channel = connection.create_channel().await?;
        publish_channel.confirm_select(ConfirmSelectOptions::default()).await?;

        for queue in [QUEUE_STATUS_UPDATES, QUEUE_WORKER_LOGS, QUEUE_WORKER_HEARTBEAT, QUEUE_WORKER_REGISTRATION] {
            publish_channel
                .queue_declare(ShortString::from(queue), durable, queue_args.clone())
                .await
                .map_err(|e| format!("declaring {queue}: {e}"))?;
        }

        let durable_exchange = ExchangeDeclareOptions { durable: true, ..Default::default() };
        publish_channel
            .exchange_declare(
                ShortString::from(EXCHANGE_JOBS),
                ExchangeKind::Topic,
                durable_exchange,
                FieldTable::default(),
            )
            .await?;
        publish_channel
            .exchange_declare(
                ShortString::from(EXCHANGE_DEAD_LETTER),
                ExchangeKind::Direct,
                durable_exchange,
                FieldTable::default(),
            )
            .await?;
        publish_channel.queue_declare(ShortString::from(QUEUE_FAILED_OCCURRENCES), durable, queue_args.clone()).await?;
        publish_channel
            .queue_bind(
                ShortString::from(QUEUE_FAILED_OCCURRENCES),
                ShortString::from(EXCHANGE_DEAD_LETTER),
                ShortString::from(DEAD_LETTER_ROUTING_KEY),
                QueueBindOptions::default(),
                FieldTable::default(),
            )
            .await?;

        // Before consuming, so the first delivery can already report its status.
        self.publisher.attach(publish_channel.clone());

        let mut channels = vec![publish_channel];
        let mut consumers = Vec::new();

        for registered in &self.jobs {
            let job = &registered.job;
            let channel = connection.create_channel().await?;
            channel.basic_qos(job.max_parallel_jobs as u16, BasicQosOptions::default()).await?;

            let mut args = queue_args.clone();
            args.insert(
                ShortString::from("x-dead-letter-exchange"),
                AMQPValue::LongString(LongString::from(EXCHANGE_DEAD_LETTER)),
            );
            args.insert(
                ShortString::from("x-dead-letter-routing-key"),
                AMQPValue::LongString(LongString::from(DEAD_LETTER_ROUTING_KEY)),
            );

            channel
                .queue_declare(ShortString::from(job.queue.as_str()), durable, args)
                .await
                .map_err(|e| format!("declaring {}: {e}", job.queue))?;
            channel
                .queue_bind(
                    ShortString::from(job.queue.as_str()),
                    ShortString::from(EXCHANGE_JOBS),
                    ShortString::from(job.routing_pattern.as_str()),
                    QueueBindOptions::default(),
                    FieldTable::default(),
                )
                .await?;

            channels.push(channel.clone());

            // A reconnect while stopping restores publishing for running jobs, but takes no new ones.
            if self.stopping.load(Ordering::SeqCst) {
                continue;
            }

            let tag = format!("{}.{}", self.opts.instance_id, job.consumer_id);
            let mut consumer = channel
                .basic_consume(
                    ShortString::from(job.queue.as_str()),
                    ShortString::from(tag.as_str()),
                    BasicConsumeOptions::default(),
                    FieldTable::default(),
                )
                .await?;

            let processor = self.processor.clone();
            let registered = registered.clone();

            tokio::spawn(async move {
                while let Some(delivery) = consumer.next().await {
                    match delivery {
                        Ok(delivery) => {
                            let queue = registered.job.queue.clone();
                            tokio::spawn(processor.clone().handle(amqp::delivery(delivery), queue));
                        }
                        Err(error) => {
                            tracing::debug!(%error, "consumer stopped");
                            break;
                        }
                    }
                }
            });

            tracing::info!(
                job = %job.name,
                queue = %job.queue,
                pattern = %job.routing_pattern,
                prefetch = job.max_parallel_jobs,
                "consuming"
            );

            consumers.push((channel, tag));
        }

        *self.consumers.lock().unwrap() = consumers;

        register(self.publisher.as_ref(), &self.opts, registration_body(&self.opts, &self.jobs)).await?;

        self.heartbeats.send(false).await;
        tracing::info!(worker = %self.opts.worker_id, "registered");

        Ok(channels)
    }

    /// Stops taking jobs. Running jobs carry on.
    async fn stop_consuming(&self) {
        self.stopping.store(true, Ordering::SeqCst);

        let consumers = std::mem::take(&mut *self.consumers.lock().unwrap());

        for (channel, tag) in consumers {
            let _ = channel.basic_cancel(ShortString::from(tag), BasicCancelOptions::default()).await;
        }
    }
}

pub(crate) fn build_registration(opts: &ResolvedOptions, jobs: &[Arc<Registered>]) -> RegistrationMessage {
    let mut routing = BTreeMap::new();
    let mut data_schemas = BTreeMap::new();
    let mut result_schemas = BTreeMap::new();
    let mut job_types = Vec::new();
    let mut configs = Vec::new();

    for registered in jobs {
        let job = &registered.job;
        routing.insert(job.name.clone(), job.routing_pattern.clone());
        job_types.push(job.name.clone());
        configs.push(JobConfigMetadata {
            job_type: job.name.clone(),
            consumer_id: job.consumer_id.clone(),
            max_parallel_jobs: job.max_parallel_jobs,
            execution_timeout_seconds: job.execution_timeout.as_secs(),
        });

        if let Some(schema) = &job.data_schema {
            data_schemas.insert(job.name.clone(), schema.clone());
        }

        if let Some(schema) = &job.result_schema {
            result_schemas.insert(job.name.clone(), schema.clone());
        }
    }

    let metadata = WorkerMetadata {
        is_external: false,
        external_scheduler: None,
        processor_count: options::cpu_count(),
        os_version: format!("{}/{}", std::env::consts::OS, std::env::consts::ARCH),
        runtime_version: format!("Rust {}", env!("MILVAION_RUSTC_VERSION")),
        heartbeat_interval: opts.heartbeat_interval.as_secs(),
        sdk: "rust".to_string(),
        sdk_version: SDK_VERSION.to_string(),
        job_configs: configs,
    };

    RegistrationMessage {
        worker_id: opts.worker_id.clone(),
        instance_id: opts.instance_id.clone(),
        display_name: format!("{} ({})", opts.worker_id, opts.instance_id),
        host_name: metrics::host_name(),
        ip_address: metrics::local_ip_address(options::broker_host_port(opts)),
        routing_patterns: routing,
        job_data_definitions: data_schemas,
        job_result_definitions: result_schemas,
        job_types,
        max_parallel_jobs: opts.max_parallel_jobs,
        version: opts.version.clone(),
        metadata: serde_json::to_string(&metadata).expect("metadata always serializes"),
    }
}

/// Instance heartbeats: on an interval, and right after the job count changes (at most once a second).
struct Heartbeats {
    opts: Arc<ResolvedOptions>,
    publisher: Arc<dyn Publisher>,
    processor: OnceLock<Weak<Processor>>,
    sampler: Mutex<ResourceSampler>,
    debounce: Mutex<(bool, Option<Instant>)>, // pending, last immediate heartbeat
    enabled: AtomicBool,
}

impl Heartbeats {
    fn new(opts: Arc<ResolvedOptions>, publisher: Arc<dyn Publisher>) -> Self {
        Self {
            opts,
            publisher,
            processor: OnceLock::new(),
            sampler: Mutex::new(ResourceSampler::default()),
            debounce: Mutex::new((false, None)),
            enabled: AtomicBool::new(false),
        }
    }

    async fn send(&self, stopping: bool) {
        let (memory, cpu) = {
            let mut sampler = self.sampler.lock().unwrap();
            (sampler.memory_bytes(), sampler.cpu_usage_percent())
        };

        let current = if stopping {
            0
        } else {
            self.processor.get().and_then(Weak::upgrade).map(|p| p.running_jobs()).unwrap_or(0)
        };

        let body = serde_json::to_vec(&HeartbeatMessage {
            worker_id: self.opts.worker_id.clone(),
            instance_id: self.opts.instance_id.clone(),
            current_jobs: current,
            memory_bytes: memory,
            cpu_usage_percent: cpu,
            timestamp: now(),
            is_stopping: stopping,
        })
        .expect("a heartbeat always serializes");

        let sent = tokio::time::timeout(
            Duration::from_secs(10),
            self.publisher.publish(
                "",
                QUEUE_WORKER_HEARTBEAT,
                body,
                PublishOptions { key: Some(self.opts.instance_id.clone()), ..Default::default() },
            ),
        )
        .await
        .unwrap_or_else(|_| Err("timed out".to_string()));

        if let Err(error) = sent {
            tracing::debug!(%error, "heartbeat failed; will retry on the next interval");
        }
    }

    /// Reports a changed job count at most once a second; the last change is always sent.
    fn request_immediate(self: &Arc<Self>) {
        if !self.enabled.load(Ordering::SeqCst) {
            return;
        }

        let wait = {
            let mut debounce = self.debounce.lock().unwrap();

            if debounce.0 {
                return;
            }

            debounce.0 = true;
            debounce
                .1
                .map(|last| (last + Duration::from_secs(1)).saturating_duration_since(Instant::now()))
                .unwrap_or_default()
        };

        let this = self.clone();

        tokio::spawn(async move {
            tokio::time::sleep(wait).await;
            *this.debounce.lock().unwrap() = (false, Some(Instant::now()));
            this.send(false).await;
        });
    }
}
