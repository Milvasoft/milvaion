"""Integration tests against a real Kafka. They run only when MILVAION_KAFKA_BROKERS is set, e.g.

    docker run -d --name milvaion-kafka -p 29092:9092 \\
      -e KAFKA_NODE_ID=1 -e KAFKA_PROCESS_ROLES=broker,controller \\
      -e KAFKA_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093 \\
      -e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://localhost:29092 \\
      -e KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER \\
      -e KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT \\
      -e KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:9093 \\
      -e KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 -e KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0 \\
      apache/kafka:3.9.0
    MILVAION_KAFKA_BROKERS=localhost:29092 pytest tests/test_kafka_integration.py

Every test uses its own topic prefix, so the tests do not see each other's records.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import secrets
import time
import uuid
from collections.abc import AsyncIterator, Callable
from typing import Any

import pytest

from milvaion import JobContext, JobOptions, KafkaOptions, Status, Worker, WorkerOptions
from milvaion.kafka import KafkaTopics

BROKERS = os.environ.get("MILVAION_KAFKA_BROKERS", "").strip()

pytestmark = pytest.mark.skipif(not BROKERS, reason="set MILVAION_KAFKA_BROKERS (e.g. localhost:29092) to run the Kafka integration tests")

if BROKERS:
    from aiokafka import AIOKafkaConsumer, AIOKafkaProducer
    from aiokafka.admin import AIOKafkaAdminClient

WORKER_ID = "Py-Kafka-Worker"


def guid() -> str:
    return str(uuid.uuid4())


class Fixture:
    def __init__(self, handler: Callable[..., Any], job_options: JobOptions, **options: Any) -> None:
        self.prefix = f"it{secrets.token_hex(4)}."
        self.topics = KafkaTopics.create(self.prefix, WORKER_ID)
        in_flight = options.pop("max_in_flight_per_partition", None)
        settings: dict[str, Any] = {
            "worker_id": WORKER_ID,
            "transport": "kafka",
            "kafka": KafkaOptions(brokers=BROKERS, topic_prefix=self.prefix, num_partitions=2, replication_factor=1,
                                 max_in_flight_per_partition=in_flight),
            "redis_url": None,
            "max_parallel_jobs": 4,
            "heartbeat_interval": 1,
            "job_heartbeat_interval": 0.15,
            "shutdown_timeout": 5,
            "startup_timeout": 30,
            "logger": logging.getLogger("milvaion-kafka-test"),
        }
        settings.update(options)
        self.worker = Worker(WorkerOptions(**settings))
        self.worker.add_job("TestJob", handler, job_options)
        self.producer: Any = None
        self.stopped = False

    async def start(self) -> None:
        self.producer = AIOKafkaProducer(bootstrap_servers=BROKERS)
        await self.producer.start()
        await self.worker.start()

        # Registration is produced after the topics have been created.
        await self.read(self.topics.registrations, lambda records: any(r.key == self.worker.instance_id.encode() for r in records))

        # Wait until the worker owns its partitions, so a record produced next is read.
        deadline = time.monotonic() + 30

        while len(self.worker._kafka._assigned) < 4:  # 2 job + 2 retry partitions
            if time.monotonic() > deadline:
                raise AssertionError("worker did not join its group")

            await asyncio.sleep(0.1)

    async def stop(self) -> None:
        if not self.stopped:
            self.stopped = True
            await asyncio.wait_for(self.worker.stop(), 60)

    async def close(self) -> None:
        await self.stop()

        if self.producer is not None:
            await self.producer.stop()

    async def produce_job(self, job_id: str, occurrence_id: str, **fields: Any) -> None:
        body = {"id": job_id, "displayName": "Kafka test", "jobNameInWorker": "TestJob", "jobData": '{"n":1}', "workerId": WORKER_ID, **fields}
        headers = [("OccurrenceId", occurrence_id.encode()), ("CorrelationId", occurrence_id.encode()), ("JobType", b"TestJob"),
                   ("WorkerId", WORKER_ID.encode()), ("Priority", b"0")]
        await self.producer.send_and_wait(self.topics.jobs, json.dumps(body).encode(), key=job_id.encode(), headers=headers)

    async def read(self, topic: str, done: Callable[[list[Any]], bool], timeout: float = 30) -> list[Any]:
        """Reads a topic from the start until ``done`` accepts what was read."""
        reader = AIOKafkaConsumer(topic, bootstrap_servers=BROKERS, auto_offset_reset="earliest", enable_auto_commit=False)
        await reader.start()
        records: list[Any] = []
        deadline = time.monotonic() + timeout

        try:
            while not done(records):
                if time.monotonic() > deadline:
                    raise AssertionError(f"timed out reading {topic}; got {len(records)} record(s): {describe(records)}")

                for batch in (await reader.getmany(timeout_ms=250)).values():
                    records.extend(batch)

            return records
        finally:
            await reader.stop()

    async def statuses(self, occurrence_id: str, until: Status) -> list[dict[str, Any]]:
        found: list[dict[str, Any]] = []

        def done(records: list[Any]) -> bool:
            found.clear()

            for r in records:
                body = json.loads(r.value)

                if body.get("occurrenceId") == occurrence_id:
                    assert r.key == occurrence_id.encode(), "status records are keyed by occurrence id"
                    found.append(body)

            return bool(found) and found[-1]["status"] == until

        await self.read(self.topics.status, done)
        return found

    async def committed(self, topic: str) -> int:
        admin = AIOKafkaAdminClient(bootstrap_servers=BROKERS)
        await admin.start()

        try:
            offsets = await admin.list_consumer_group_offsets(self.topics.group)
        finally:
            await admin.close()

        return sum(meta.offset for tp, meta in offsets.items() if tp.topic == topic and meta.offset > 0)

    async def expect_committed(self, topic: str, expected: int) -> None:
        deadline = time.monotonic() + 10

        while True:
            got = await self.committed(topic)

            if got == expected:
                return

            if time.monotonic() > deadline:
                raise AssertionError(f"committed offsets on {topic} = {got}, want {expected}")

            await asyncio.sleep(0.1)


def header(record: Any, key: str) -> str | None:
    for name, value in reversed(record.headers or ()):
        if name == key:
            return value.decode()

    return None


def describe(records: list[Any]) -> str:
    return " ".join(f"[{r.topic}/{r.partition}@{r.offset} key={r.key!r} {r.value!r}]" for r in records)


@pytest.fixture
async def kafka() -> AsyncIterator[Callable[..., Any]]:
    fixtures: list[Fixture] = []

    async def create(handler: Callable[..., Any], job_options: JobOptions | None = None, **options: Any) -> Fixture:
        fixture = Fixture(handler, job_options or JobOptions(), **options)
        fixtures.append(fixture)
        await fixture.start()
        return fixture

    yield create

    for fixture in fixtures:
        await fixture.close()


async def test_completes_job(kafka: Callable[..., Any]) -> None:
    async def handler(ctx: JobContext) -> dict[str, Any]:
        ctx.log("hello from kafka")
        return {"ok": True}

    f: Fixture = await kafka(handler)
    job_id, occurrence_id = guid(), guid()
    await f.produce_job(job_id, occurrence_id)

    statuses = await f.statuses(occurrence_id, Status.COMPLETED)
    assert statuses[0]["status"] == Status.RUNNING and statuses[0]["startTime"]

    final = statuses[-1]
    assert final["jobId"] == job_id and final["result"] == '{"ok": true}' and final["instanceId"] == f.worker.instance_id
    assert final["durationMs"] is not None

    await f.expect_committed(f.topics.jobs, 1)

    await f.read(f.topics.logs, lambda records: any(r.key == occurrence_id.encode() and b"hello from kafka" in r.value for r in records))
    await f.read(f.topics.heartbeats, lambda records: bool(records) and records[0].key == f.worker.instance_id.encode())

    registrations = await f.read(f.topics.registrations, lambda records: bool(records))
    registration = json.loads(registrations[0].value)
    assert registration["workerId"] == WORKER_ID and registration["jobTypes"] == ["TestJob"]


async def test_retries_then_dead_letters(kafka: Callable[..., Any]) -> None:
    attempts: list[float] = []

    async def handler(ctx: JobContext) -> None:
        attempts.append(time.monotonic())
        raise RuntimeError("always fails")

    f: Fixture = await kafka(handler, JobOptions(max_retries=2, base_retry_delay=0.4))
    job_id, occurrence_id = guid(), guid()
    await f.produce_job(job_id, occurrence_id)

    dlq = await f.read(f.topics.dlq, lambda records: bool(records))
    assert len(dlq) == 1 and dlq[0].key == occurrence_id.encode(), describe(dlq)

    for key, expected in {"OccurrenceId": occurrence_id, "CorrelationId": occurrence_id, "MaxRetries": "2", "x-retry-count": "2"}.items():
        assert header(dlq[0], key) == expected, key

    dead = json.loads(dlq[0].value)
    assert dead["id"] == job_id and dead["status"] == Status.FAILED and dead["exception"].startswith("Max retries (2) exceeded.")

    retries = await f.read(f.topics.retry, lambda records: len(records) == 2)

    for i, r in enumerate(sorted(retries, key=lambda r: int(header(r, "x-retry-count") or 0))):
        assert header(r, "x-retry-count") == str(i + 1) and header(r, "MaxRetries") == "2"
        assert header(r, "OccurrenceId") == occurrence_id and header(r, "JobType") == "TestJob" and r.key == job_id.encode()

        # base * 2^retryCount of the failed attempt: 400ms, then 800ms.
        delay = int(header(r, "x-not-before") or 0) - r.timestamp
        expected = 400 * 2**i
        assert expected - 50 <= delay <= expected + 500, f"retry {i + 1} is due {delay}ms after it was written, want about {expected}ms"

    assert len(attempts) == 3
    assert attempts[1] - attempts[0] >= 0.4 - 0.02, "the second attempt must wait 400ms"
    assert attempts[2] - attempts[1] >= 0.8 - 0.02, "the third attempt must wait 800ms"

    statuses = await f.statuses(occurrence_id, Status.FAILED)
    assert all(s["status"] == Status.RUNNING for s in statuses[:-1])
    assert len(statuses) - 1 > 3, "job heartbeats continue while the retries wait"

    await f.expect_committed(f.topics.jobs, 1)
    await f.expect_committed(f.topics.retry, 2)


async def test_forwards_poison_record_to_dead_letter_topic(kafka: Callable[..., Any]) -> None:
    f: Fixture = await kafka(lambda ctx: "ok")

    await f.producer.send_and_wait(f.topics.jobs, b"not json", key=b"poison", headers=[("JobType", b"TestJob")])

    dlq = await f.read(f.topics.dlq, lambda records: bool(records))
    r = dlq[0]
    assert r.value == b"not json" and r.key == b"poison" and header(r, "JobType") == "TestJob"
    assert header(r, "x-original-topic") == f.topics.jobs and header(r, "x-dlq-reason")

    await f.expect_committed(f.topics.jobs, 1)


async def test_shutdown_writes_interrupted_job_back(kafka: Callable[..., Any]) -> None:
    started = asyncio.Event()

    async def handler(ctx: JobContext) -> None:
        started.set()
        await asyncio.sleep(3600)

    f: Fixture = await kafka(handler, shutdown_timeout=0.3)
    job_id, occurrence_id = guid(), guid()
    await f.produce_job(job_id, occurrence_id)
    await asyncio.wait_for(started.wait(), 30)

    await f.stop()

    jobs = await f.read(f.topics.jobs, lambda records: len(records) == 2)
    requeued = max(jobs, key=lambda r: r.offset)
    assert header(requeued, "OccurrenceId") == occurrence_id and requeued.key == job_id.encode()
    assert await f.committed(f.topics.jobs) == 1, "the interrupted record is committed once written back"

    statuses = await f.statuses(occurrence_id, Status.RUNNING)
    assert all(s["status"] == Status.RUNNING for s in statuses), "an interrupted job must not report a final status"


async def test_runs_jobs_of_one_partition_in_parallel_up_to_the_limit(kafka: Callable[..., Any]) -> None:
    running = 0
    peak = 0
    release = asyncio.Event()

    async def handler(ctx: JobContext) -> str:
        nonlocal running, peak
        running += 1
        peak = max(peak, running)
        await release.wait()
        running -= 1
        return "ok"

    f: Fixture = await kafka(handler, max_in_flight_per_partition=2)
    job_id = guid()  # one key, so every record lands in the same partition
    occurrences = [guid() for _ in range(5)]

    for occurrence_id in occurrences:
        await f.produce_job(job_id, occurrence_id)

    deadline = time.monotonic() + 20

    while running < 2:
        assert time.monotonic() < deadline, f"expected 2 jobs running from one partition, got {running}"
        await asyncio.sleep(0.02)

    await asyncio.sleep(0.5)  # a third job must not start meanwhile
    release.set()

    for occurrence_id in occurrences:
        await f.statuses(occurrence_id, Status.COMPLETED)

    assert peak == 2
    await f.expect_committed(f.topics.jobs, 5)
