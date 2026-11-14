"""Kafka transport of the worker (docs/githubdocs/KAFKA-TRANSPORT.md).

Imported only when ``transport="kafka"``, so RabbitMQ users never need aiokafka.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import time
from collections import deque
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any, cast

try:
    from aiokafka import AIOKafkaConsumer, AIOKafkaProducer, ConsumerRebalanceListener, TopicPartition
    from aiokafka.admin import AIOKafkaAdminClient, NewTopic
    from aiokafka.coordinator.assignors.sticky.sticky_assignor import StickyPartitionAssignor
    from aiokafka.errors import ConsumerStoppedError
except ImportError as error:  # pragma: no cover - depends on the installed extras
    raise ImportError('The Kafka transport needs aiokafka. Install it with: pip install "milvaion-worker[kafka]"') from error

from .kafka import (
    HEADER_CORRELATION_ID,
    HEADER_DLQ_REASON,
    HEADER_MAX_RETRIES,
    HEADER_NOT_BEFORE,
    HEADER_OCCURRENCE_ID,
    HEADER_ORIGINAL_TOPIC,
    HEADER_RETRY_COUNT,
    KafkaTopics,
    OffsetTracker,
    ResolvedKafka,
    header_map,
    not_before_ms,
    record_headers,
)
from .processor import Delivery, RetryRequest, parse_occurrence_id
from .protocol import (
    DEAD_LETTER_ROUTING_KEY,
    EXCHANGE_DEAD_LETTER,
    QUEUE_STATUS_UPDATES,
    QUEUE_WORKER_HEARTBEAT,
    QUEUE_WORKER_LOGS,
    QUEUE_WORKER_REGISTRATION,
)

if TYPE_CHECKING:
    from .worker import Worker

COMMIT_INTERVAL = 0.5
MAX_POLL_RECORDS = 1000
_TOPIC_ALREADY_EXISTS = 36


class KafkaNotConnected(ConnectionError):
    """Raised by publish while the producer is not running."""


@dataclass
class _Record:
    record: Any
    """The aiokafka ConsumerRecord."""
    headers: dict[str, Any]


@dataclass
class _Held:
    offset: int
    timer: asyncio.TimerHandle
    stop: asyncio.Event


@dataclass(eq=False)
class _PartitionState:
    """What this instance holds of one assigned partition."""

    tp: TopicPartition
    retry: bool
    tracker: OffsetTracker = field(default_factory=OffsetTracker)
    backlog: deque[_Record] = field(default_factory=deque)
    """Taken from the client, not dispatched yet, in offset order."""
    in_flight: int = 0
    paused: bool = False
    held: _Held | None = None
    """Head of a retry partition that is not due yet."""
    committed: int = -1


@dataclass
class _TopicSource:
    """Stands in for the RabbitMQ consumer the processor names in its logs."""

    queue: str


class KafkaTransport:
    """Runs a worker on Kafka. One consumer group member reads the worker's job and retry topics;
    records are buffered per partition, dispatched up to max_parallel_jobs (and
    max_in_flight_per_partition per partition) and committed in order once their outcome has been
    reported. It is also the worker's publisher."""

    def __init__(self, worker: Worker) -> None:
        cfg = worker.options.kafka_resolved
        assert cfg is not None
        self.w = worker
        self.cfg: ResolvedKafka = cfg
        self.topics = KafkaTopics.create(cfg.topic_prefix, worker.worker_id)
        self.logger: logging.Logger = worker.logger
        self._producer: AIOKafkaProducer | None = None
        self._consumer: AIOKafkaConsumer | None = None
        self._assigned: set[TopicPartition] = set()
        self._parts: dict[TopicPartition, _PartitionState] = {}
        self._revoked: dict[TopicPartition, _PartitionState] = {}
        self._running = 0
        self._cursor = 0
        self._stopping = False
        self._poll_task: asyncio.Task[None] | None = None
        self._commit_task: asyncio.Task[None] | None = None
        self._commit_lock = asyncio.Lock()
        self._tasks: set[asyncio.Task[Any]] = set()
        self._heartbeat_failed = False

    # Lifecycle

    async def start(self) -> None:
        """Connects, creates the topics, registers the instance and starts consuming."""
        loop = asyncio.get_running_loop()
        deadline = loop.time() + self.w.options.startup_timeout
        connection = self.cfg.connection_options()

        while True:
            producer = AIOKafkaProducer(client_id=self.w.instance_id, acks="all", request_timeout_ms=30_000, **connection)

            try:
                await asyncio.wait_for(producer.start(), max(1.0, deadline - loop.time()))
                break
            except Exception as error:  # noqa: BLE001
                with contextlib.suppress(Exception):
                    await producer.stop()

                self.logger.warning("Could not connect to Kafka at %s: %s", ",".join(self.cfg.brokers), error)

                if loop.time() + 1 > deadline:
                    raise ConnectionError(f"Could not connect to Kafka within {self.w.options.startup_timeout}s: {error}") from error

                await asyncio.sleep(1)

        self._producer = producer

        if self.cfg.auto_create_topics:
            await self._create_topics()

        await self.w._register()

        consumer = AIOKafkaConsumer(
            client_id=self.w.instance_id,
            group_id=self.topics.group,
            enable_auto_commit=False,
            auto_offset_reset="earliest",
            # aiokafka has no cooperative-sticky assignor; the eager sticky one keeps partitions
            # where they are, and _on_revoked / _on_assigned keep the work of a partition that
            # comes straight back (see the README).
            partition_assignment_strategy=(StickyPartitionAssignor,),
            max_poll_records=MAX_POLL_RECORDS,
            **connection,
        )
        consumer.subscribe([self.topics.jobs, self.topics.retry], listener=_RebalanceListener(self))
        self._consumer = consumer
        await consumer.start()

        self._poll_task = loop.create_task(self._poll_loop())
        self._commit_task = loop.create_task(self._commit_loop())

        self.logger.info("Consuming %s and %s (group %s, max parallel jobs %d, max in flight per partition %d).", self.topics.jobs,
                         self.topics.retry, self.topics.group, self.w.options.max_parallel_jobs, self.cfg.max_in_flight_per_partition)

    async def _create_topics(self) -> None:
        """Creates the worker's job and retry topics and the shared topics it writes to. Topics
        that exist already are left as they are."""
        t = self.topics
        names = [t.jobs, t.retry, t.status, t.logs, t.heartbeats, t.registrations, t.dlq]
        replication = self.cfg.replication_factor
        # aiokafka's NewTopic wants replica assignments whenever a count is -1; an empty map asks
        # the broker for its default.replication.factor.
        topics = [NewTopic(name, self.cfg.num_partitions, replication, replica_assignments={} if replication == -1 else None) for name in names]
        admin = AIOKafkaAdminClient(client_id=self.w.instance_id, request_timeout_ms=30_000, **self.cfg.connection_options())
        await admin.start()

        try:
            response = await admin.create_topics(topics)
        finally:
            with contextlib.suppress(Exception):
                await admin.close()

        for entry in getattr(response, "topic_errors", None) or getattr(response, "topic_error_codes", None) or []:
            name, code = entry[0], entry[1]
            message = entry[2] if len(entry) > 2 else ""

            if code not in (0, _TOPIC_ALREADY_EXISTS):
                raise RuntimeError(f"Creating Kafka topic {name} failed with error {code}: {message}")

    async def stop_consuming(self) -> None:
        """Stops taking new records. Running jobs carry on and can still report their outcome."""
        self._stopping = True

        for ps in self._parts.values():
            self._release_hold(ps)

        if self._consumer is not None and self._assigned:
            with contextlib.suppress(Exception):
                self._consumer.pause(*self._assigned)

        await _cancel(self._poll_task)
        self._poll_task = None

    async def drained(self) -> None:
        """Runs once running jobs have finished or have been written back: commits, then leaves the group."""
        await _cancel(self._commit_task)
        self._commit_task = None
        consumer = self._consumer

        if consumer is None:
            return

        await self._commit()
        self._consumer = None

        with contextlib.suppress(Exception):
            await consumer.stop()

    async def close(self) -> None:
        """Disconnects after the final heartbeat and logs have been sent."""
        self._stopping = True
        await _cancel(self._poll_task)
        await _cancel(self._commit_task)
        self._poll_task = self._commit_task = None

        consumer, self._consumer = self._consumer, None
        producer, self._producer = self._producer, None

        if consumer is not None:
            with contextlib.suppress(Exception):
                await consumer.stop()

        if producer is not None:
            with contextlib.suppress(Exception):
                await producer.stop()

    # Publishing

    async def publish(self, exchange: str, routing_key: str, body: bytes, *, persistent: bool = True,
                      correlation_id: str | None = None, headers: dict[str, Any] | None = None, key: str | None = None) -> None:
        """Maps the processor's RabbitMQ-named destinations to the Kafka topics."""
        heartbeat = exchange == "" and routing_key == QUEUE_WORKER_HEARTBEAT

        if exchange == EXCHANGE_DEAD_LETTER and routing_key == DEAD_LETTER_ROUTING_KEY:
            topic = self.topics.dlq
        elif exchange == "" and routing_key in self._shared_topics():
            topic = self._shared_topics()[routing_key]
        else:
            raise ValueError(f"No Kafka topic for exchange {exchange!r} and routing key {routing_key!r}")

        values = dict(headers or {})

        if correlation_id:
            values[HEADER_OCCURRENCE_ID] = correlation_id
            values[HEADER_CORRELATION_ID] = correlation_id

        record_key = key or correlation_id

        try:
            await self._produce(topic, body, record_key.encode() if record_key else None, record_headers(values))
        except Exception as error:
            if heartbeat:
                self._after_heartbeat(error)

            raise

        if heartbeat:
            self._after_heartbeat(None)

    def _shared_topics(self) -> dict[str, str]:
        return {
            QUEUE_STATUS_UPDATES: self.topics.status,
            QUEUE_WORKER_LOGS: self.topics.logs,
            QUEUE_WORKER_HEARTBEAT: self.topics.heartbeats,
            QUEUE_WORKER_REGISTRATION: self.topics.registrations,
        }

    async def _produce(self, topic: str, value: bytes, key: bytes | None, headers: list[tuple[str, bytes]]) -> None:
        producer = self._producer

        if producer is None:
            raise KafkaNotConnected("Not connected to Kafka.")

        await asyncio.wait_for(producer.send_and_wait(topic, value=value, key=key, headers=headers), 30)

    async def _produce_reliably(self, topic: str, value: bytes, key: bytes | None, headers: list[tuple[str, bytes]]) -> None:
        """Retries a produce whose failure would otherwise leave the record's offset stuck."""
        last: Exception | None = None

        for delay in (0, 1, 2, 5):
            if delay:
                await asyncio.sleep(delay)

            try:
                await self._produce(topic, value, key, headers)
                return
            except KafkaNotConnected:
                raise
            except Exception as error:  # noqa: BLE001
                last = error

        assert last is not None
        raise last

    def _after_heartbeat(self, error: BaseException | None) -> None:
        """Registers again once heartbeats get through after failing, the Kafka counterpart of
        registering after a RabbitMQ reconnect: the API may have dropped the instance meanwhile."""
        if error is not None:
            if not isinstance(error, KafkaNotConnected):
                self._heartbeat_failed = True

            return

        if not self._heartbeat_failed:
            return

        self._heartbeat_failed = False

        async def register() -> None:
            try:
                await self.w._register()
            except Exception as failure:  # noqa: BLE001
                self.logger.warning("Could not register again after Kafka recovered: %s", failure)
                self._heartbeat_failed = True

        self._spawn(register())

    # Consuming

    async def _poll_loop(self) -> None:
        consumer = self._consumer
        assert consumer is not None

        while not self._stopping:
            try:
                batches = await consumer.getmany(timeout_ms=500, max_records=MAX_POLL_RECORDS)
            except ConsumerStoppedError:
                return
            except Exception as error:  # noqa: BLE001
                self.logger.warning("Kafka fetch failed: %s", error)
                await asyncio.sleep(1)
                continue

            if batches:
                self._accept(batches)

    def _accept(self, batches: dict[TopicPartition, list[Any]]) -> None:
        """Buffers polled records per partition and dispatches what fits."""
        if self._stopping:
            return

        for tp, records in batches.items():
            # A poll can still hold records of a partition revoked since; its new owner reads them.
            if tp not in self._assigned:
                continue

            ps = self._parts.get(tp)

            if ps is None:
                ps = self._parts[tp] = _PartitionState(tp=tp, retry=tp.topic == self.topics.retry)

            for record in records:
                if ps.tracker.add(record.offset):
                    ps.backlog.append(_Record(record, header_map(record.headers)))

        self._dispatch()

    def _dispatch(self) -> None:
        """Starts buffered records while instance and partition slots are free, holds retry records
        that are not due, and pauses every partition that has records waiting here, so the client
        keeps polling (and the member stays in the group) without buffering more."""
        consumer = self._consumer

        if self._stopping or consumer is None:
            return

        states = sorted(self._parts.values(), key=lambda ps: (ps.tp.topic, ps.tp.partition))
        pause: list[TopicPartition] = []
        resume: list[TopicPartition] = []
        now_ms = time.time() * 1000
        start = 0

        if states:
            start = self._cursor % len(states)
            self._cursor += 1

        limit = self.w.options.max_parallel_jobs

        for i in range(len(states)):
            ps = states[(start + i) % len(states)]

            while self._running < limit and ps.in_flight < self.cfg.max_in_flight_per_partition and ps.backlog:
                head = ps.backlog[0]

                if ps.retry:
                    due = not_before_ms(head.headers)

                    if due is not None and due > now_ms:
                        self._hold(ps, head, due / 1000)
                        break

                self._release_hold(ps)
                ps.backlog.popleft()
                ps.in_flight += 1
                self._running += 1
                self._spawn(self._run(ps, head))

            want_paused = bool(ps.backlog)

            if want_paused != ps.paused:
                ps.paused = want_paused
                (pause if want_paused else resume).append(ps.tp)

        try:
            if pause:
                consumer.pause(*pause)

            if resume:
                consumer.resume(*resume)
        except Exception as error:  # noqa: BLE001 - a partition revoked meanwhile
            self.logger.debug("Could not pause or resume Kafka partitions: %s", error)

    def _hold(self, ps: _PartitionState, head: _Record, due: float) -> None:
        """Keeps the head of a retry partition until it is due, sending the occurrence's job
        heartbeats meanwhile, and dispatches again when it is."""
        if ps.held is not None and ps.held.offset == head.record.offset:
            return

        self._release_hold(ps)
        loop = asyncio.get_running_loop()
        stop = asyncio.Event()
        ps.held = _Held(offset=head.record.offset, timer=loop.call_later(max(0.0, due - time.time()), self._dispatch), stop=stop)

        processor = self.w._processor

        if processor is not None:
            self._spawn(processor.hold_heartbeats(head.record.value, head.headers, due, stop))

    @staticmethod
    def _release_hold(ps: _PartitionState) -> None:
        if ps.held is None:
            return

        ps.held.timer.cancel()
        ps.held.stop.set()
        ps.held = None

    async def _run(self, ps: _PartitionState, item: _Record) -> None:
        record = item.record
        processor = self.w._processor

        async def ack() -> None:
            self._mark_done(ps, record.offset)

        async def nack(requeue: bool) -> None:
            if requeue:
                await self._requeue(ps, item)
            else:
                await self._dead_letter_raw(ps, item, "rejected by the worker")

        async def reject(reason: str) -> None:
            await self._dead_letter_raw(ps, item, reason)

        async def schedule_retry(request: RetryRequest) -> None:
            await self._produce_retry(item, request)

        try:
            if processor is not None:
                delivery = Delivery(body=record.value or b"", headers=item.headers, correlation_id=None, redelivered=False,
                                    ack=ack, nack=nack, reject=reject, schedule_retry=schedule_retry)
                await processor.handle(delivery, cast(Any, _TopicSource(record.topic)))
        finally:
            ps.in_flight -= 1
            self._running -= 1
            self._dispatch()

    def _mark_done(self, ps: _PartitionState, offset: int) -> None:
        """Lets the record's offset be committed. A partition that went to another instance is
        ignored: its new owner runs the record again."""
        if self._parts.get(ps.tp) is ps or self._revoked.get(ps.tp) is ps:
            ps.tracker.done(offset)

    async def _requeue(self, ps: _PartitionState, item: _Record) -> None:
        """Writes an interrupted job back to its own topic, the counterpart of a RabbitMQ requeue."""
        record = item.record

        try:
            await self._produce_reliably(record.topic, record.value, record.key, list(record.headers or ()))
        except Exception as error:
            raise RuntimeError(f"Writing the interrupted job back to {record.topic} failed: {error}") from error

        self._mark_done(ps, record.offset)

    async def _dead_letter_raw(self, ps: _PartitionState, item: _Record, reason: str) -> None:
        """Forwards a record the worker cannot process to the dead letter topic unchanged."""
        record = item.record
        headers = list(record.headers or ()) + [(HEADER_ORIGINAL_TOPIC, record.topic.encode()), (HEADER_DLQ_REASON, reason.encode())]
        occurrence_id = parse_occurrence_id(item.headers, None)
        key = occurrence_id.encode() if occurrence_id else record.key

        try:
            await self._produce_reliably(self.topics.dlq, record.value, key, headers)
        except Exception as error:
            raise RuntimeError(f"Forwarding a record to {self.topics.dlq} failed: {error}") from error

        self._mark_done(ps, record.offset)

    async def _produce_retry(self, item: _Record, request: RetryRequest) -> None:
        """Writes the next attempt to the retry topic, due at request.not_before."""
        headers = dict(item.headers)
        headers[HEADER_OCCURRENCE_ID] = request.occurrence_id
        headers[HEADER_CORRELATION_ID] = request.occurrence_id
        headers[HEADER_RETRY_COUNT] = request.retry_count
        headers[HEADER_MAX_RETRIES] = request.max_retries
        headers[HEADER_NOT_BEFORE] = int(request.not_before * 1000)
        key = request.job_id.encode() if request.job_id else item.record.key

        await self._produce(self.topics.retry, item.record.value, key, record_headers(headers))

    # Commits and rebalancing

    async def _commit_loop(self) -> None:
        while True:
            await asyncio.sleep(COMMIT_INTERVAL)
            await self._commit()

    async def _commit(self, only: set[TopicPartition] | None = None) -> None:
        """Commits the finished position of every partition (or only of the given ones) that moved."""
        async with self._commit_lock:
            consumer = self._consumer

            if consumer is None:
                return

            offsets: dict[TopicPartition, int] = {}
            states: dict[TopicPartition, _PartitionState] = {}

            for tp, ps in self._parts.items():
                if only is not None and tp not in only:
                    continue

                position = ps.tracker.commit_position()

                if position is None or position <= ps.committed:
                    continue

                offsets[tp] = position
                states[tp] = ps

            if not offsets:
                return

            try:
                await consumer.commit(offsets)
            except Exception as error:  # noqa: BLE001
                self.logger.warning("Kafka offset commit failed; will retry: %s", error)
                return

            for tp, ps in states.items():
                ps.committed = max(ps.committed, offsets[tp])

    async def _on_revoked(self, revoked: set[TopicPartition]) -> None:
        """Commits what has finished on the revoked partitions. Jobs still running on them finish
        and report; if a partition comes straight back to this instance (the sticky assignor's
        usual outcome) its state is kept, otherwise the new owner runs the unfinished records."""
        await self._commit(revoked)

        for tp in revoked:
            self._assigned.discard(tp)
            ps = self._parts.pop(tp, None)

            if ps is not None:
                self._release_hold(ps)

                if not self._stopping:
                    self._revoked[tp] = ps

        if revoked:
            self.logger.info("Kafka partitions revoked: %s", _describe(revoked))

    def _on_assigned(self, assigned: set[TopicPartition]) -> None:
        previous, self._revoked = self._revoked, {}
        consumer = self._consumer

        for tp in assigned:
            self._assigned.add(tp)
            ps = previous.pop(tp, None)

            if ps is None or self._stopping:
                continue

            # Kept: continue after what was already taken instead of reading it again.
            ps.paused = False
            self._parts[tp] = ps

            if ps.tracker.last >= 0 and consumer is not None:
                try:
                    consumer.seek(tp, ps.tracker.last + 1)
                except Exception as error:  # noqa: BLE001
                    self.logger.warning("Could not keep the position of %s: %s", tp, error)
                    del self._parts[tp]

        self.logger.info("Kafka partitions assigned: %s", _describe(assigned))
        self._dispatch()

    def _spawn(self, coroutine: Any) -> None:
        task = asyncio.get_running_loop().create_task(coroutine)
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)


class _RebalanceListener(ConsumerRebalanceListener):
    def __init__(self, transport: KafkaTransport) -> None:
        self.transport = transport

    async def on_partitions_revoked(self, revoked: Any) -> None:
        await self.transport._on_revoked(set(revoked))

    async def on_partitions_assigned(self, assigned: Any) -> None:
        self.transport._on_assigned(set(assigned))


async def _cancel(task: asyncio.Task[Any] | None) -> None:
    if task is None or task.done():
        return

    task.cancel()

    with contextlib.suppress(asyncio.CancelledError, Exception):
        await task


def _describe(partitions: set[TopicPartition]) -> str:
    return ", ".join(f"{tp.topic}[{tp.partition}]" for tp in sorted(partitions, key=lambda tp: (tp.topic, tp.partition))) or "none"
