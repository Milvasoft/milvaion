from __future__ import annotations

import asyncio
import json
import logging
import time
from types import SimpleNamespace
from typing import Any

import pytest

from milvaion import KafkaOptions, Worker, WorkerOptions
from milvaion.kafka import (
    KafkaTopics,
    OffsetTracker,
    header_map,
    kafka_options_from_env,
    not_before_ms,
    record_headers,
    resolve_kafka,
    worker_segment,
)
from milvaion.processor import Delivery, RetryRequest, parse_occurrence_id, parse_retry_count

from test_processor import JOB_ID, OCCURRENCE_ID, Harness

aiokafka = pytest.importorskip("aiokafka")

from aiokafka import TopicPartition  # noqa: E402

from milvaion.kafka_transport import KafkaTransport  # noqa: E402


def test_worker_segment() -> None:
    cases = {
        "report-worker": "report-worker",
        "Report_Worker.v2": "report_worker.v2",
        "Mail Worker": "mail_worker",
        "a/b:c*d": "a_b_c_d",
        "ÇiçekWorker": "_i_ekworker",  # one "_" per character, not per byte
        "UPPER-123": "upper-123",
        "emoji😀worker": "emoji_worker",
        "tab\tand\nnewline": "tab_and_newline",
    }

    for value, expected in cases.items():
        assert worker_segment(value) == expected, value


def test_topic_names() -> None:
    topics = KafkaTopics.create("prod.", "Report Worker")

    assert topics == KafkaTopics(
        jobs="prod.milvaion.jobs.report_worker",
        retry="prod.milvaion.jobs.report_worker.retry",
        status="prod.milvaion.status",
        logs="prod.milvaion.logs",
        heartbeats="prod.milvaion.heartbeats",
        registrations="prod.milvaion.registrations",
        dlq="prod.milvaion.dlq",
        group="prod.milvaion.worker.report_worker",
    )

    plain = KafkaTopics.create("", "w")
    assert plain.jobs == "milvaion.jobs.w" and plain.group == "milvaion.worker.w"


def test_headers() -> None:
    headers = header_map([("CorrelationId", OCCURRENCE_ID.encode()), ("x-retry-count", b"2"), ("x-not-before", b"1794612341949")])

    assert parse_occurrence_id(headers, None) == OCCURRENCE_ID

    headers["OccurrenceId"] = "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b01"
    assert parse_occurrence_id(headers, None) == "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b01", "OccurrenceId wins over CorrelationId"
    assert parse_retry_count(headers) == 2
    assert parse_retry_count(header_map(None)) == 0
    assert not_before_ms(headers) == 1794612341949
    assert not_before_ms({"x-not-before": "soon"}) is None
    assert not_before_ms({}) is None

    encoded = record_headers({"x-retry-count": 3, "MaxRetries": 0, "CorrelationId": b"abc", "Priority": 9})

    assert [k for k, _ in encoded] == sorted(k for k, _ in encoded), "headers must be in a stable order"
    assert dict(encoded) == {"x-retry-count": b"3", "MaxRetries": b"0", "CorrelationId": b"abc", "Priority": b"9"}


def test_offset_tracker_commits_contiguous_range() -> None:
    tracker = OffsetTracker()
    assert tracker.commit_position() is None

    for offset in (10, 11, 12, 14):  # 13 is a gap, for example a transaction marker
        assert tracker.add(offset)

    assert not tracker.add(12) and not tracker.add(5), "repeated or older offsets are ignored"

    tracker.done(11)
    assert tracker.commit_position() is None, "11 finished before 10"

    tracker.done(10)
    assert tracker.commit_position() == 12

    tracker.done(14)
    assert tracker.commit_position() == 12 and tracker.outstanding == 2

    tracker.done(12)
    assert tracker.commit_position() == 15 and tracker.outstanding == 0

    assert not tracker.done(99)

    tracker.add(20)
    assert tracker.commit_position() == 15


def test_kafka_options() -> None:
    options = WorkerOptions(worker_id="w", transport="Kafka", max_parallel_jobs=6, redis_url=None)
    k = options.kafka_resolved

    assert options.transport == "kafka" and k is not None
    assert k.brokers == ["localhost:9092"] and k.topic_prefix == "" and k.auto_create_topics
    assert k.num_partitions == 12 and k.replication_factor == -1 and k.max_in_flight_per_partition == 6
    assert k.security_protocol == "PLAINTEXT" and k.ssl_context is None and k.sasl_mechanism is None

    options = WorkerOptions(worker_id="w", transport="kafka", kafka=KafkaOptions(
        brokers=["a:9092, b:9092", "c:9092"], tls=True, sasl_mechanism="SCRAM-SHA-512", sasl_username="u", sasl_password="p",
        topic_prefix="prod.", auto_create_topics=False, num_partitions=3, replication_factor=2, max_in_flight_per_partition=1))
    k = options.kafka_resolved

    assert k is not None
    assert k.brokers == ["a:9092", "b:9092", "c:9092"] and k.ssl_context is not None and k.security_protocol == "SASL_SSL"
    assert k.sasl_mechanism == "SCRAM-SHA-512" and not k.auto_create_topics
    assert k.num_partitions == 3 and k.replication_factor == 2 and k.max_in_flight_per_partition == 1

    connection = k.connection_options()
    assert connection["sasl_plain_username"] == "u" and connection["sasl_plain_password"] == "p" and connection["security_protocol"] == "SASL_SSL"

    assert resolve_kafka(KafkaOptions(brokers="x:1,y:2", sasl_mechanism="plain", sasl_username="u"), 2).security_protocol == "SASL_PLAINTEXT"
    assert WorkerOptions(worker_id="w").transport == "rabbitmq" and WorkerOptions(worker_id="w").kafka_resolved is None

    invalid = [
        {"transport": "nats"},
        {"kafka": KafkaOptions(topic_prefix="bad prefix")},
        {"kafka": KafkaOptions(sasl_mechanism="gssapi", sasl_username="u")},
        {"kafka": KafkaOptions(sasl_mechanism="plain")},
        {"kafka": KafkaOptions(replication_factor=-2)},
        {"kafka": KafkaOptions(num_partitions=-1)},
        {"kafka": KafkaOptions(max_in_flight_per_partition=-1)},
        {"kafka": KafkaOptions(tls_cert_file="only-cert.pem")},
        {"kafka": KafkaOptions(tls_ca_file="does-not-exist.pem")},
    ]

    for case in invalid:
        with pytest.raises(ValueError):
            WorkerOptions(worker_id="w", **{"transport": "kafka", **case})


def test_kafka_options_from_env(monkeypatch: pytest.MonkeyPatch) -> None:
    for key, value in {
        "MILVAION_WORKER_ID": "env-worker",
        "MILVAION_TRANSPORT": "kafka",
        "KAFKA_BOOTSTRAP_SERVERS": "fallback:9092",
        "MILVAION_KAFKA_SASL_MECHANISM": "scram-sha-256",
        "MILVAION_KAFKA_SASL_USERNAME": "user",
        "MILVAION_KAFKA_SASL_PASSWORD": "secret",
        "MILVAION_KAFKA_TOPIC_PREFIX": "stage.",
        "MILVAION_KAFKA_AUTO_CREATE_TOPICS": "false",
        "MILVAION_KAFKA_PARTITIONS": "4",
        "MILVAION_KAFKA_REPLICATION_FACTOR": "3",
        "MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION": "2",
        "MILVAION_REDIS_URL": "false",
    }.items():
        monkeypatch.setenv(key, value)

    options = WorkerOptions.from_env()
    k = options.kafka_resolved

    assert options.worker_id == "env-worker" and options.transport == "kafka" and k is not None
    assert k.brokers == ["fallback:9092"] and k.sasl_mechanism == "SCRAM-SHA-256" and k.sasl_username == "user" and k.sasl_password == "secret"
    assert k.topic_prefix == "stage." and not k.auto_create_topics and k.security_protocol == "SASL_PLAINTEXT"
    assert k.num_partitions == 4 and k.replication_factor == 3 and k.max_in_flight_per_partition == 2

    monkeypatch.setenv("MILVAION_KAFKA_BOOTSTRAP_SERVERS", "primary:9092")
    monkeypatch.setenv("MILVAION_KAFKA_NUM_PARTITIONS", "9")
    monkeypatch.delenv("MILVAION_KAFKA_PARTITIONS")
    k = kafka_options_from_env()
    assert k.brokers == "primary:9092" and k.num_partitions == 9

    # A kafka argument replaces the environment's Kafka settings; a transport argument wins too.
    options = WorkerOptions.from_env(kafka=KafkaOptions(brokers="arg:9092"))
    assert options.kafka_resolved is not None and options.kafka_resolved.brokers == ["arg:9092"]
    assert WorkerOptions.from_env(transport="rabbitmq").kafka_resolved is None

    monkeypatch.setenv("MILVAION_KAFKA_PARTITIONS", "many")
    with pytest.raises(ValueError, match="MILVAION_KAFKA_PARTITIONS"):
        WorkerOptions.from_env()


# Processor: the hooks the Kafka transport uses.


def kafka_delivery(h: Harness, **job: Any) -> tuple[Delivery, list[Any]]:
    settled: list[Any] = []
    body = {"id": JOB_ID, "displayName": "Test", "jobNameInWorker": "TestJob", "jobData": None, **job}

    async def ack() -> None:
        settled.append("ack")

    async def nack(requeue: bool) -> None:
        settled.append("requeue" if requeue else "nack")

    async def reject(reason: str) -> None:
        settled.append(("reject", reason))

    async def schedule_retry(request: RetryRequest) -> None:
        settled.append(request)

    return Delivery(body=json.dumps(body).encode(), headers={"OccurrenceId": OCCURRENCE_ID}, correlation_id=None, redelivered=False,
                    ack=ack, nack=nack, reject=reject, schedule_retry=schedule_retry), settled


async def test_failed_job_is_handed_to_the_broker_for_retry() -> None:
    def handler(ctx: Any) -> None:
        raise RuntimeError("boom")

    h = Harness({"TestJob": handler}, max_retries=3, base_retry_delay=5)
    delivery, settled = kafka_delivery(h)
    before = time.time()

    await h.processor.handle(delivery, h.consumer)

    request = settled[0]
    assert isinstance(request, RetryRequest) and settled[1] == "ack"
    assert request.job_id == JOB_ID and request.occurrence_id == OCCURRENCE_ID and request.retry_count == 1 and request.max_retries == 3
    assert before + 5 <= request.not_before <= time.time() + 5
    assert h.sleeps == [], "the instance slot is free during the backoff"
    assert h.pub.statuses() == [1]
    assert all(m["record_key"] == OCCURRENCE_ID for m in h.pub.to("job_status_updates_queue"))


async def test_retry_that_cannot_be_scheduled_is_dead_lettered() -> None:
    def handler(ctx: Any) -> None:
        raise RuntimeError("boom")

    h = Harness({"TestJob": handler}, max_retries=1)
    delivery, settled = kafka_delivery(h)

    async def failing(request: RetryRequest) -> None:
        raise ConnectionError("broker down")

    delivery.schedule_retry = failing
    await h.processor.handle(delivery, h.consumer)

    assert settled[0][0] == "reject" and "broker down" in settled[0][1]


async def test_invalid_job_is_rejected_with_a_reason() -> None:
    h = Harness({"TestJob": lambda ctx: None})
    delivery, settled = kafka_delivery(h)
    delivery.body = b"not json"

    await h.processor.handle(delivery, h.consumer)

    assert settled == [("reject", "the message is not a valid job")]


async def test_dead_letter_is_keyed_by_occurrence() -> None:
    def handler(ctx: Any) -> None:
        raise RuntimeError("boom")

    h = Harness({"TestJob": handler}, max_retries=0)
    delivery, settled = kafka_delivery(h)

    await h.processor.handle(delivery, h.consumer)

    dead = h.pub.dead_lettered()
    assert settled == ["ack"] and len(dead) == 1 and dead[0]["record_key"] == OCCURRENCE_ID


async def test_held_retry_sends_running_and_heartbeats_until_due() -> None:
    h = Harness({"TestJob": lambda ctx: None})
    h.options.job_heartbeat_interval = 0.02
    h.processor.sleep = asyncio.sleep
    body = json.dumps({"id": JOB_ID, "jobNameInWorker": "TestJob"}).encode()

    await h.processor.hold_heartbeats(body, {"OccurrenceId": OCCURRENCE_ID, "x-retry-count": "1"}, time.time() + 0.1, asyncio.Event())

    statuses = h.pub.statuses()
    assert len(statuses) >= 3 and set(statuses) == {1}

    stop = asyncio.Event()
    stop.set()
    count = len(h.pub.messages)
    await h.processor.hold_heartbeats(body, {"OccurrenceId": OCCURRENCE_ID}, time.time() + 10, stop)
    assert len(h.pub.messages) == count, "a released hold sends nothing"


# Transport: dispatching, pausing, commits and rebalancing with a fake client.


class FakeConsumer:
    def __init__(self) -> None:
        self.paused: set[TopicPartition] = set()
        self.commits: list[dict[TopicPartition, int]] = []
        self.seeks: list[tuple[TopicPartition, int]] = []

    def pause(self, *tps: TopicPartition) -> None:
        self.paused.update(tps)

    def resume(self, *tps: TopicPartition) -> None:
        self.paused.difference_update(tps)

    async def commit(self, offsets: dict[TopicPartition, int]) -> None:
        self.commits.append(dict(offsets))

    def seek(self, tp: TopicPartition, offset: int) -> None:
        self.seeks.append((tp, offset))


class FakeProducer:
    def __init__(self) -> None:
        self.sent: list[dict[str, Any]] = []

    async def send_and_wait(self, topic: str, value: bytes, key: bytes | None = None, headers: list[tuple[str, bytes]] | None = None) -> None:
        self.sent.append({"topic": topic, "value": value, "key": key, "headers": dict(headers or [])})


class BlockingProcessor:
    """Holds every job until released, and records what ran."""

    def __init__(self) -> None:
        self.started: list[Any] = []
        self.release = asyncio.Event()
        self.held: list[tuple[bytes, dict[str, Any], float]] = []

    async def handle(self, delivery: Delivery, consumer: Any) -> None:
        self.started.append(delivery)
        await self.release.wait()
        await delivery.ack()

    async def hold_heartbeats(self, body: bytes, headers: dict[str, Any], due: float, stop: asyncio.Event) -> None:
        self.held.append((body, headers, due))


def make_transport(max_parallel: int = 4, in_flight: int | None = None) -> tuple[KafkaTransport, FakeConsumer, FakeProducer, BlockingProcessor]:
    worker = Worker(WorkerOptions(worker_id="Py Worker", transport="kafka", max_parallel_jobs=max_parallel, redis_url=None,
                                  instance_id="py-worker-1", logger=logging.getLogger("milvaion-test"),
                                  kafka=KafkaOptions(max_in_flight_per_partition=in_flight)))
    transport = KafkaTransport(worker)
    consumer, producer, processor = FakeConsumer(), FakeProducer(), BlockingProcessor()
    transport._consumer = consumer  # type: ignore[assignment]
    transport._producer = producer  # type: ignore[assignment]
    worker._processor = processor  # type: ignore[assignment]
    return transport, consumer, producer, processor


def record(topic: str, partition: int, offset: int, headers: dict[str, str] | None = None, value: bytes = b"{}", key: bytes = b"job") -> Any:
    return SimpleNamespace(topic=topic, partition=partition, offset=offset, key=key, value=value,
                           headers=tuple((k, v.encode()) for k, v in (headers or {}).items()))


async def settle() -> None:
    for _ in range(5):
        await asyncio.sleep(0)


async def test_dispatch_respects_partition_and_instance_limits() -> None:
    transport, consumer, _, processor = make_transport(max_parallel=3, in_flight=2)
    jobs = transport.topics.jobs
    p0, p1 = TopicPartition(jobs, 0), TopicPartition(jobs, 1)
    transport._on_assigned({p0, p1})

    transport._accept({p0: [record(jobs, 0, o) for o in range(5)], p1: [record(jobs, 1, o) for o in range(2)]})
    await settle()

    assert len(processor.started) == 3, "the instance limit applies across partitions"
    assert transport._parts[p0].in_flight <= 2 and transport._parts[p1].in_flight <= 2
    assert p0 in consumer.paused, "a partition with records waiting is paused"

    processor.release.set()

    for _ in range(20):
        await settle()

    assert len(processor.started) == 7
    assert not consumer.paused, "partitions resume once their backlog is gone"

    await transport._commit()
    assert consumer.commits == [{p0: 5, p1: 2}]

    await transport._commit()
    assert len(consumer.commits) == 1, "nothing moved, nothing committed"


async def test_retry_record_is_held_until_due() -> None:
    transport, consumer, _, processor = make_transport()
    retry = TopicPartition(transport.topics.retry, 0)
    transport._on_assigned({retry})
    due_ms = int(time.time() * 1000) + 150
    body = json.dumps({"id": JOB_ID, "jobNameInWorker": "TestJob"}).encode()

    transport._accept({retry: [record(retry.topic, 0, 0, {"OccurrenceId": OCCURRENCE_ID, "x-not-before": str(due_ms)}, body)]})
    await settle()

    assert processor.started == [] and len(processor.held) == 1 and retry in consumer.paused
    assert transport._parts[retry].tracker.commit_position() is None, "a held record is not finished"

    await asyncio.sleep(0.25)
    await settle()

    assert len(processor.started) == 1 and time.time() * 1000 >= due_ms
    processor.release.set()


async def test_rebalance_commits_and_keeps_partitions_that_come_back() -> None:
    transport, consumer, _, processor = make_transport()
    jobs = transport.topics.jobs
    p0, p1 = TopicPartition(jobs, 0), TopicPartition(jobs, 1)
    transport._on_assigned({p0, p1})
    transport._accept({p0: [record(jobs, 0, 0), record(jobs, 0, 1)], p1: [record(jobs, 1, 7)]})
    await settle()

    first = processor.started[0]
    await first.ack()  # offset 0 of p0 finished; offset 1 still runs

    await transport._on_revoked({p0, p1})
    assert consumer.commits[-1] == {p0: 1}

    # p0 comes straight back, p1 goes to another instance.
    transport._on_assigned({p0})
    assert (p0, 2) in consumer.seeks, "records already taken are not read again"
    assert p1 not in transport._parts

    for delivery in processor.started[1:]:
        await delivery.ack()

    await transport._commit()
    assert consumer.commits[-1] == {p0: 2}, "the revoked partition's offset is left to its new owner"
    processor.release.set()


async def test_publish_maps_destinations_to_topics() -> None:
    transport, _, producer, _ = make_transport()
    t = transport.topics

    await transport.publish("", "job_status_updates_queue", b"{}", key=OCCURRENCE_ID)
    await transport.publish("", "worker_logs_queue", b"{}", key=OCCURRENCE_ID)
    await transport.publish("", "worker_heartbeat_queue", b"{}", key="py-worker-1")
    await transport.publish("", "worker_registration_queue", b"{}", key="py-worker-1")
    await transport.publish("dlx_scheduled_jobs", "failed_jobs", b"{}", correlation_id=OCCURRENCE_ID,
                            headers={"CorrelationId": OCCURRENCE_ID, "x-retry-count": 2, "MaxRetries": 2})

    assert [s["topic"] for s in producer.sent] == [t.status, t.logs, t.heartbeats, t.registrations, t.dlq]
    assert t.jobs == "milvaion.jobs.py_worker"
    assert producer.sent[0]["key"] == OCCURRENCE_ID.encode() and producer.sent[2]["key"] == b"py-worker-1"

    dead = producer.sent[4]
    assert dead["key"] == OCCURRENCE_ID.encode()
    assert dead["headers"] == {"OccurrenceId": OCCURRENCE_ID.encode(), "CorrelationId": OCCURRENCE_ID.encode(), "x-retry-count": b"2", "MaxRetries": b"2"}

    with pytest.raises(ValueError):
        await transport.publish("jobs.topic", "x", b"{}")


async def test_poison_record_and_retry_and_requeue_produce_the_right_records() -> None:
    transport, _, producer, processor = make_transport()
    jobs = transport.topics.jobs
    p0 = TopicPartition(jobs, 0)
    transport._on_assigned({p0})
    transport._accept({p0: [record(jobs, 0, 0, {"JobType": "TestJob"}, b"not json", b"poison"),
                            record(jobs, 0, 1, {"OccurrenceId": OCCURRENCE_ID, "JobType": "TestJob"}, b"{}", JOB_ID.encode()),
                            record(jobs, 0, 2, {"OccurrenceId": OCCURRENCE_ID}, b"{}", JOB_ID.encode())]})
    await settle()
    ps = transport._parts[p0]
    items = processor.started

    await items[0].reject("the message is not a valid job")
    await items[1].schedule_retry(RetryRequest(JOB_ID, OCCURRENCE_ID, 1, 3, 1794612341.949))
    await items[1].ack()
    await items[2].nack(True)

    poison, retry, requeued = producer.sent
    assert poison["topic"] == transport.topics.dlq and poison["value"] == b"not json" and poison["key"] == b"poison"
    assert poison["headers"]["x-original-topic"] == jobs.encode() and poison["headers"]["JobType"] == b"TestJob"
    assert poison["headers"]["x-dlq-reason"] == b"the message is not a valid job"

    assert retry["topic"] == transport.topics.retry and retry["key"] == JOB_ID.encode()
    assert retry["headers"]["x-retry-count"] == b"1" and retry["headers"]["MaxRetries"] == b"3"
    assert retry["headers"]["x-not-before"] == b"1794612341949" and retry["headers"]["JobType"] == b"TestJob"
    assert retry["headers"]["CorrelationId"] == OCCURRENCE_ID.encode()

    assert requeued["topic"] == jobs and requeued["key"] == JOB_ID.encode() and requeued["headers"]["OccurrenceId"] == OCCURRENCE_ID.encode()
    assert ps.tracker.commit_position() == 3
    processor.release.set()
