from __future__ import annotations

import json
import logging
import re

import pytest

from milvaion import JobOptions, Worker, WorkerOptions, generate_routing_pattern, queue_name_for_pattern
from milvaion.errors import format_error
from milvaion.log_buffer import LogBuffer
from milvaion.options import resolve_job
from milvaion.processor import parse_occurrence_id, parse_retry_count

GUID = "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00"


def test_routing_matches_dotnet_sdk() -> None:
    assert generate_routing_pattern("SendEmailJob", "Email-Worker-01") == "email-worker-01.sendemail.*"
    assert generate_routing_pattern("NonParallel", "w") == "w.nonparallel.*"
    assert queue_name_for_pattern("email-worker-01.sendemail.*") == "scheduled_jobs_queue.email-worker-01.sendemail.wildcard"
    assert queue_name_for_pattern("a.#") == "scheduled_jobs_queue.a.all"
    assert re.fullmatch(r"py-worker-[0-9a-f]{8}", WorkerOptions(worker_id="py-worker").instance_id or "")


@pytest.mark.parametrize(("headers", "correlation", "expected"), [
    ({"OccurrenceId": GUID.encode()}, None, GUID),
    ({"CorrelationId": GUID}, None, GUID),
    ({}, GUID, GUID),
    ({"OccurrenceId": "not-a-guid"}, GUID, GUID),
    (None, None, None),
])
def test_parse_occurrence_id(headers, correlation, expected) -> None:
    assert parse_occurrence_id(headers, correlation) == expected


@pytest.mark.parametrize(("value", "expected"), [(2, 2), (bytes([4, 0, 0, 0]), 4), ("5", 5), (None, 0), (True, 0)])
def test_parse_retry_count(value, expected) -> None:
    assert parse_retry_count({"x-retry-count": value}) == expected


def test_format_error_includes_cause_chain() -> None:
    try:
        try:
            raise TypeError("inner")
        except TypeError as inner:
            raise ValueError("outer") from inner
    except ValueError as error:
        text = format_error(error)

    assert text.startswith("Type: ValueError\nMessage: outer\nStackTrace: ")
    assert "Inner Exception:\nType: TypeError\nMessage: inner" in text


async def test_log_buffer_keeps_order_after_failed_send() -> None:
    sent: list[str] = []
    fail = [True]

    async def send(batch):
        if fail[0]:
            fail[0] = False
            raise ConnectionError("broker down")

        sent.extend(entry["log"]["message"] for entry in batch["logs"])

    buffer = LogBuffer(send, logging.getLogger("t"), batch_size=1000, flush_interval=3600)
    buffer.add("o", "i", {"message": "1"})
    buffer.add("o", "i", {"message": "2"})

    with pytest.raises(ConnectionError):
        await buffer.flush()

    buffer.add("o", "i", {"message": "3"})
    await buffer.flush()
    buffer.close()

    assert sent == ["1", "2", "3"]


async def test_log_buffer_drops_oldest_beyond_limit() -> None:
    sent: list[str] = []

    async def send(batch):
        sent.extend(entry["log"]["message"] for entry in batch["logs"])

    buffer = LogBuffer(send, logging.getLogger("t"), batch_size=1000, flush_interval=3600, max_buffered=2)

    for message in ("1", "2", "3"):
        buffer.add("o", "i", {"message": message})

    await buffer.flush()
    buffer.close()

    assert sent == ["2", "3"]


def test_options_defaults() -> None:
    options = WorkerOptions(worker_id=" report-worker ")

    assert options.worker_id == "report-worker"
    assert options.cancellation_channel == "Milvaion:JobScheduler:cancellation_channel"
    assert options.execution_timeout == 3600

    job = resolve_job("SendEmailJob", lambda ctx: None, JobOptions(), options)

    assert (job.routing_pattern, job.consumer_id, job.max_retries, job.base_retry_delay) == ("report-worker.sendemail.*", "sendemail-consumer", 3, 5)

    with pytest.raises(ValueError):
        WorkerOptions(worker_id="")

    with pytest.raises(ValueError):
        WorkerOptions(worker_id="w", queue_type="lazy")


def test_options_from_env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MILVAION_WORKER_ID", "env-worker")
    monkeypatch.setenv("MILVAION_RABBITMQ_QUEUE_TYPE", "Quorum")
    monkeypatch.setenv("MILVAION_REDIS_URL", "redis://redis:6379")
    monkeypatch.setenv("MILVAION_REDIS_KEY_PREFIX", "X:")
    monkeypatch.setenv("MILVAION_MAX_PARALLEL_JOBS", "4")

    options = WorkerOptions.from_env()

    assert (options.worker_id, options.queue_type, options.max_parallel_jobs, options.cancellation_channel) == ("env-worker", "quorum", 4, "X:cancellation_channel")
    assert WorkerOptions.from_env(worker_id="override").worker_id == "override"

    monkeypatch.setenv("MILVAION_REDIS_URL", "false")
    assert WorkerOptions.from_env().redis_url is None


def test_duplicate_job_names_are_rejected() -> None:
    worker = Worker(worker_id="w", redis_url=None)
    worker.add_job("TestJob", lambda ctx: None)

    with pytest.raises(ValueError):
        worker.add_job("testjob", lambda ctx: None)


def test_registration_metadata_is_pascal_case() -> None:
    worker = Worker(worker_id="w", redis_url=None)

    @worker.job("GreetJob", data_schema={"type": "object"})
    async def greet(ctx):
        return None

    registration = worker._build_registration()
    metadata = json.loads(registration["metadata"])

    assert metadata["JobConfigs"][0]["JobType"] == "GreetJob" and metadata["JobConfigs"][0]["ConsumerId"] == "greet-consumer"
    assert registration["jobDataDefinitions"] == {"GreetJob": '{"type": "object"}'}
    assert registration["routingPatterns"] == {"GreetJob": "w.greet.*"}
