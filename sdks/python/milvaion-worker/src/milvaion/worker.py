from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import os
import platform
import signal
import socket
from collections.abc import Callable
from typing import Any, TypeVar
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit

import aio_pika
from aio_pika.abc import AbstractChannel, AbstractIncomingMessage, AbstractQueue, AbstractRobustConnection

from .cancellation import CancellationListener
from .errors import AbortReason
from .log_buffer import LogBuffer
from .metrics import ResourceSampler, local_ip_address
from .options import JobOptions, ResolvedJob, WorkerOptions, resolve_job
from .processor import CancellationRegistry, Delivery, Processor
from .protocol import (
    DEAD_LETTER_ROUTING_KEY,
    EXCHANGE_DEAD_LETTER,
    EXCHANGE_JOBS,
    QUEUE_FAILED_OCCURRENCES,
    QUEUE_STATUS_UPDATES,
    QUEUE_WORKER_HEARTBEAT,
    QUEUE_WORKER_LOGS,
    QUEUE_WORKER_REGISTRATION,
)

F = TypeVar("F", bound=Callable[..., Any])


class Worker:
    """A Milvaion worker. Register jobs with ``@worker.job(...)`` or ``add_job``, then call
    ``run()`` (blocking) or ``await start()`` / ``await stop()``."""

    def __init__(self, options: WorkerOptions | None = None, **kwargs: Any) -> None:
        self.options = options if options is not None else WorkerOptions(**kwargs)
        self.logger: logging.Logger = self.options.logger  # type: ignore[assignment]
        self._jobs: dict[str, ResolvedJob] = {}
        self._cancellations = CancellationRegistry()
        self._sampler = ResourceSampler()
        self._publisher = _AmqpPublisher()
        self._connection: AbstractRobustConnection | None = None
        self._consumers: list[tuple[AbstractQueue, str]] = []
        self._listener: CancellationListener | None = None
        self._logs: LogBuffer | None = None
        self._processor: Processor | None = None
        self._heartbeat_task: asyncio.Task[None] | None = None
        self._tasks: set[asyncio.Task[Any]] = set()
        self._pending_heartbeat: asyncio.TimerHandle | None = None
        self._last_immediate = 0.0
        self._state = "idle"
        self._connected = False
        self._stop_task: asyncio.Task[None] | None = None

    @property
    def worker_id(self) -> str:
        return self.options.worker_id

    @property
    def instance_id(self) -> str:
        return self.options.instance_id or ""

    @property
    def running_jobs(self) -> int:
        return self._processor.running if self._processor else 0

    def job(self, name: str | None = None, **options: Any) -> Callable[[F], F]:
        """Decorator form of ``add_job``. The name defaults to the function name."""

        def register(handler: F) -> F:
            self.add_job(name or handler.__name__, handler, JobOptions(**options))
            return handler

        return register

    def add_job(self, name: str, handler: Callable[..., Any], options: JobOptions | None = None) -> Worker:
        """Registers a handler under the name the dashboard dispatches the job by (JobNameInWorker).

        Names are unique within a worker and matched case-insensitively. The handler receives a
        JobContext and may be ``async def`` or a plain function (run in a thread)."""
        if self._state != "idle":
            raise RuntimeError("Jobs must be added before the worker starts.")

        name = (name or "").strip()

        if not name:
            raise ValueError("Job name is required.")

        if name.lower() in self._jobs:
            raise ValueError(f"Job {name!r} is already registered. Job names must be unique within a worker (case-insensitive).")

        self._jobs[name.lower()] = resolve_job(name, handler, options or JobOptions(), self.options)

        return self

    def run(self) -> None:
        """Starts the worker and blocks until SIGINT or SIGTERM, then shuts down gracefully."""
        asyncio.run(self._run_until_signal())

    async def _run_until_signal(self) -> None:
        stop = asyncio.Event()
        loop = asyncio.get_running_loop()

        for sig in (signal.SIGINT, signal.SIGTERM):
            with contextlib.suppress(NotImplementedError, RuntimeError, ValueError):
                loop.add_signal_handler(sig, stop.set)

        await self.start()

        try:
            await stop.wait()
        finally:
            await self.stop()

    async def start(self) -> None:
        """Connects, registers this instance and starts consuming. Raises if RabbitMQ or Redis
        cannot be reached within ``startup_timeout``."""
        if self._state != "idle":
            raise RuntimeError(f"Worker cannot be started from state {self._state!r}.")

        if not self._jobs:
            raise RuntimeError("Add at least one job before starting the worker.")

        self._state = "starting"
        self.logger.info("Starting worker %s (instance %s) with %d job type(s).", self.worker_id, self.instance_id, len(self._jobs))

        self._logs = LogBuffer(self._send_logs, self.logger)
        self._processor = Processor(self.options, self._jobs, self._publisher, self._logs, self._cancellations)
        self._processor.on_count_changed = self._send_immediate_heartbeat

        try:
            if self.options.redis_url:
                self._listener = CancellationListener(self.options.redis_url, self.options.cancellation_channel, self._cancellations, self.logger)
                await asyncio.wait_for(self._listener.start(), self.options.startup_timeout)
            else:
                self.logger.warning("Redis is disabled: jobs cannot be cancelled from the dashboard.")

            await self._connect()
        except BaseException:
            self._state = "stopped"
            await self._close()
            raise

        self._heartbeat_task = asyncio.get_running_loop().create_task(self._heartbeat_loop())
        self._state = "running"

    async def stop(self) -> None:
        """Stops taking new jobs, lets running ones finish for up to ``shutdown_timeout``, returns
        the rest to the queue and disconnects. Safe to call more than once."""
        if self._state in ("idle", "stopped"):
            return

        if self._stop_task is None:
            self._stop_task = asyncio.get_running_loop().create_task(self._shutdown())

        await asyncio.shield(self._stop_task)

    async def _connect(self) -> None:
        deadline = asyncio.get_running_loop().time() + self.options.startup_timeout
        last_error: BaseException | None = None

        while True:
            try:
                connection = await aio_pika.connect_robust(
                    _with_heartbeat(self.options.rabbitmq_url, 60),
                    client_properties={"connection_name": self.instance_id},
                    timeout=10,
                )
                break
            except Exception as error:  # noqa: BLE001
                last_error = error
                self.logger.warning("Could not connect to RabbitMQ: %s", error)

                if asyncio.get_running_loop().time() + 2 > deadline:
                    raise ConnectionError(f"Could not connect to RabbitMQ within {self.options.startup_timeout}s: {last_error}") from error

                await asyncio.sleep(2)

        self._connection = connection
        connection.reconnect_callbacks.add(self._on_reconnect)
        connection.close_callbacks.add(self._on_connection_lost)

        await self._setup(connection)
        self._connected = True
        self.logger.info("Connected to RabbitMQ.")

    async def _setup(self, connection: AbstractRobustConnection) -> None:
        """Declares the topology and starts the consumers. The robust connection restores both after a reconnect."""
        queue_args = {"x-queue-type": "quorum"} if self.options.queue_type == "quorum" else None
        channel = await connection.channel(publisher_confirms=True)

        for queue in (QUEUE_STATUS_UPDATES, QUEUE_WORKER_LOGS, QUEUE_WORKER_HEARTBEAT, QUEUE_WORKER_REGISTRATION):
            await channel.declare_queue(queue, durable=True, arguments=queue_args)

        await channel.declare_exchange(EXCHANGE_JOBS, aio_pika.ExchangeType.TOPIC, durable=True)
        dead_letter = await channel.declare_exchange(EXCHANGE_DEAD_LETTER, aio_pika.ExchangeType.DIRECT, durable=True)
        failed = await channel.declare_queue(QUEUE_FAILED_OCCURRENCES, durable=True, arguments=queue_args)
        await failed.bind(dead_letter, DEAD_LETTER_ROUTING_KEY)

        # Before consuming, so the first delivery can already report its status.
        self._publisher.attach(channel, {EXCHANGE_DEAD_LETTER: dead_letter})

        for job in self._jobs.values():
            consumer_channel = await connection.channel()
            await consumer_channel.set_qos(prefetch_count=job.max_parallel_jobs)

            arguments: dict[str, Any] = {"x-dead-letter-exchange": EXCHANGE_DEAD_LETTER, "x-dead-letter-routing-key": DEAD_LETTER_ROUTING_KEY}
            arguments.update(queue_args or {})

            queue = await consumer_channel.declare_queue(job.queue, durable=True, arguments=arguments)
            await queue.bind(EXCHANGE_JOBS, job.routing_pattern)

            tag = await queue.consume(self._make_consumer(job), no_ack=False, consumer_tag=f"{self.instance_id}.{job.consumer_id}")
            self._consumers.append((queue, tag))

            self.logger.info("Consuming %s from %s (pattern %s, prefetch %d).", job.name, job.queue, job.routing_pattern, job.max_parallel_jobs)

        await self._register()

    def _make_consumer(self, job: ResolvedJob) -> Callable[[AbstractIncomingMessage], Any]:
        async def on_message(message: AbstractIncomingMessage) -> None:
            if self._processor is None:
                return

            async def ack() -> None:
                await message.ack()

            async def nack(requeue: bool) -> None:
                await message.nack(requeue=requeue)

            delivery = Delivery(
                body=message.body,
                headers=dict(message.headers or {}),
                correlation_id=message.correlation_id,
                redelivered=bool(message.redelivered),
                ack=ack,
                nack=nack,
            )

            # Run in its own task: aio-pika awaits this callback, and jobs must run in parallel.
            task = asyncio.get_running_loop().create_task(self._processor.handle(delivery, job))
            self._tasks.add(task)
            task.add_done_callback(self._tasks.discard)

        return on_message

    async def _on_reconnect(self, *_: Any) -> None:
        self._connected = True
        self.logger.info("Reconnected to RabbitMQ.")

        try:
            await self._register()
        except Exception as error:  # noqa: BLE001
            self.logger.warning("Re-registering after a reconnect failed: %s", error)

    def _on_connection_lost(self, *args: Any) -> None:
        # Every failed reconnect attempt closes again; only report the loss of a working connection.
        if self._connected and self._state in ("running", "stopping"):
            self._connected = False
            reason = args[-1] if args else None
            self.logger.warning("Lost the RabbitMQ connection (%s). Reconnecting...", reason)

    async def _register(self) -> None:
        await self._publisher.publish("", QUEUE_WORKER_REGISTRATION, json.dumps(self._build_registration()).encode(), persistent=True)
        await self._send_heartbeat(False)
        self.logger.info("Registered %s (instance %s).", self.worker_id, self.instance_id)

    def _build_registration(self) -> dict[str, Any]:
        jobs = list(self._jobs.values())

        def schema(value: Any) -> str:
            return value if isinstance(value, str) else json.dumps(value)

        metadata = {
            "IsExternal": False,
            "ExternalScheduler": None,
            "ProcessorCount": os.cpu_count() or 1,
            "OSVersion": f"{platform.system()} {platform.release()}",
            "RuntimeVersion": f"Python {platform.python_version()}",
            "HeartbeatInterval": int(self.options.heartbeat_interval),
            "JobConfigs": [
                {"JobType": j.name, "ConsumerId": j.consumer_id, "MaxParallelJobs": j.max_parallel_jobs, "ExecutionTimeoutSeconds": int(j.execution_timeout)}
                for j in jobs
            ],
        }

        return {
            "workerId": self.worker_id,
            "instanceId": self.instance_id,
            "displayName": f"{self.worker_id} ({self.instance_id})",
            "hostName": socket.gethostname(),
            "ipAddress": local_ip_address(),
            "routingPatterns": {j.name: j.routing_pattern for j in jobs},
            "jobDataDefinitions": {j.name: schema(j.data_schema) for j in jobs if j.data_schema is not None},
            "jobResultDefinitions": {j.name: schema(j.result_schema) for j in jobs if j.result_schema is not None},
            "jobTypes": [j.name for j in jobs],
            "maxParallelJobs": self.options.max_parallel_jobs,
            "version": self.options.version,
            # The API reads this case-sensitively, so its properties are PascalCase.
            "metadata": json.dumps(metadata),
        }

    async def _heartbeat_loop(self) -> None:
        while True:
            await asyncio.sleep(self.options.heartbeat_interval)
            await self._send_heartbeat(False)

    async def _send_heartbeat(self, stopping: bool) -> None:
        body = json.dumps({
            "workerId": self.worker_id,
            "instanceId": self.instance_id,
            "currentJobs": 0 if stopping else self.running_jobs,
            "memoryBytes": self._sampler.memory_bytes(),
            "cpuUsagePercent": self._sampler.cpu_usage_percent(),
            "timestamp": _utc_now(),
            "isStopping": stopping,
        }).encode()

        try:
            await self._publisher.publish("", QUEUE_WORKER_HEARTBEAT, body, persistent=False)
        except Exception as error:  # noqa: BLE001
            self.logger.debug("Heartbeat failed; will retry on the next interval: %s", error)

    def _send_immediate_heartbeat(self) -> None:
        """Reports a changed job count at most once a second; the last change is always sent."""
        if self._state not in ("running", "stopping") or self._pending_heartbeat is not None:
            return

        loop = asyncio.get_running_loop()
        wait = max(0.0, self._last_immediate + 1 - loop.time())

        def fire() -> None:
            self._pending_heartbeat = None
            self._last_immediate = loop.time()
            task = loop.create_task(self._send_heartbeat(False))
            self._tasks.add(task)
            task.add_done_callback(self._tasks.discard)

        self._pending_heartbeat = loop.call_later(wait, fire)

    async def _send_logs(self, batch: dict[str, Any]) -> None:
        await self._publisher.publish("", QUEUE_WORKER_LOGS, json.dumps(batch, default=str).encode(), persistent=True)

    async def _shutdown(self) -> None:
        self._state = "stopping"
        self.logger.info("Stopping worker instance %s...", self.instance_id)

        for queue, tag in self._consumers:
            with contextlib.suppress(Exception):
                await queue.cancel(tag)

        self._consumers.clear()
        assert self._processor is not None
        self._processor.begin_shutdown()

        if not await self._processor.wait_for_idle(self.options.shutdown_timeout):
            self.logger.warning("%d job(s) still running after %ss. Returning them to the queue.", self._processor.running, self.options.shutdown_timeout)
            self._cancellations.abort_all(AbortReason.SHUTDOWN, "Worker is shutting down.")
            await self._processor.wait_for_idle(10)

        if self._heartbeat_task is not None:
            self._heartbeat_task.cancel()

        if self._pending_heartbeat is not None:
            self._pending_heartbeat.cancel()

        await self._send_heartbeat(True)

        if self._logs is not None:
            try:
                await asyncio.wait_for(self._logs.flush(), 10)
            except Exception as error:  # noqa: BLE001
                self.logger.warning("Some job logs could not be sent before shutdown: %s", error)

            self._logs.close()

        await self._close()
        self._state = "stopped"
        self.logger.info("Worker instance %s stopped.", self.instance_id)

    async def _close(self) -> None:
        # Closing on purpose: the close callback must not report it as a lost connection.
        self._connected = False

        if self._listener is not None:
            await self._listener.stop()

        self._publisher.detach()

        if self._connection is not None:
            with contextlib.suppress(Exception):
                await self._connection.close()


class _AmqpPublisher:
    """Publishes on the current confirm channel; aio-pika waits for the broker's confirmation."""

    def __init__(self) -> None:
        self._channel: AbstractChannel | None = None
        self._exchanges: dict[str, Any] = {}

    def attach(self, channel: AbstractChannel, exchanges: dict[str, Any]) -> None:
        self._channel, self._exchanges = channel, exchanges

    def detach(self) -> None:
        self._channel, self._exchanges = None, {}

    async def publish(self, exchange: str, routing_key: str, body: bytes, *, persistent: bool = True,
                      correlation_id: str | None = None, headers: dict[str, Any] | None = None) -> None:
        channel = self._channel

        if channel is None:
            raise ConnectionError("Not connected to RabbitMQ.")

        target = channel.default_exchange if exchange == "" else self._exchanges.get(exchange) or await channel.get_exchange(exchange, ensure=False)

        message = aio_pika.Message(
            body,
            content_type="application/json",
            delivery_mode=aio_pika.DeliveryMode.PERSISTENT if persistent else aio_pika.DeliveryMode.NOT_PERSISTENT,
            correlation_id=correlation_id,
            headers=headers,
        )

        await asyncio.wait_for(target.publish(message, routing_key=routing_key), 30)


def _with_heartbeat(url: str, seconds: int) -> str:
    """Adds the AMQP heartbeat to the URL unless it already sets one."""
    parts = urlsplit(url)
    query = dict(parse_qsl(parts.query))
    query.setdefault("heartbeat", str(seconds))

    return urlunsplit(parts._replace(query=urlencode(query)))


def _utc_now() -> str:
    from .context import utc_now

    return utc_now()

