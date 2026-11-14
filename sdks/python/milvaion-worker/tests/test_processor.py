from __future__ import annotations

import asyncio
import json
import logging
import threading
from typing import Any

import pytest

from milvaion import JobOptions, PermanentJobError, Status, WorkerOptions
from milvaion.errors import AbortReason
from milvaion.log_buffer import LogBuffer
from milvaion.options import resolve_job
from milvaion.processor import CancellationRegistry, Delivery, Processor

OCCURRENCE_ID = "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00"
JOB_ID = "0193a6c2-0000-7c41-9a3b-2f6e8d4c1b99"


class FakePublisher:
    def __init__(self) -> None:
        self.messages: list[dict[str, Any]] = []
        self.fail_next = 0

    async def publish(self, exchange: str, routing_key: str, body: bytes, *, persistent: bool = True,
                      correlation_id: str | None = None, headers: dict[str, Any] | None = None, key: str | None = None) -> None:
        if self.fail_next > 0:
            self.fail_next -= 1
            raise ConnectionError("publish failed")

        self.messages.append({"exchange": exchange, "key": routing_key, "body": json.loads(body), "raw": body,
                              "correlation_id": correlation_id, "headers": headers, "record_key": key})

    def to(self, queue: str) -> list[dict[str, Any]]:
        return [m for m in self.messages if m["exchange"] == "" and m["key"] == queue]

    def statuses(self) -> list[int]:
        return [m["body"]["status"] for m in self.to("job_status_updates_queue")]

    def last_status(self) -> dict[str, Any]:
        return self.to("job_status_updates_queue")[-1]["body"]

    def logs(self) -> list[dict[str, Any]]:
        return [entry["log"] for m in self.to("worker_logs_queue") for entry in m["body"]["logs"]]

    def dead_lettered(self) -> list[dict[str, Any]]:
        return [m for m in self.messages if m["exchange"] == "dlx_scheduled_jobs"]


class FakeDelivery:
    def __init__(self, job: dict[str, Any] | None = None, headers: dict[str, Any] | None = None, redelivered: bool = False) -> None:
        body = {"id": JOB_ID, "displayName": "Test", "jobNameInWorker": "TestJob", "jobData": None, **(job or {})}
        self.settled: list[str] = []
        self.delivery = Delivery(
            body=json.dumps(body).encode(),
            headers={"OccurrenceId": OCCURRENCE_ID.encode(), **(headers or {})},
            correlation_id=None,
            redelivered=redelivered,
            ack=self._ack,
            nack=self._nack,
        )

    async def _ack(self) -> None:
        self.settled.append("ack")

    async def _nack(self, requeue: bool) -> None:
        self.settled.append("requeue" if requeue else "nack")


class Harness:
    def __init__(self, handlers: dict[str, Any], **job_options: Any) -> None:
        self.options = WorkerOptions(worker_id="py-worker", redis_url=None, job_heartbeat_interval=0, instance_id="py-worker-abcd1234",
                                     logger=logging.getLogger("milvaion-test"))
        self.pub = FakePublisher()

        async def send(batch: dict[str, Any]) -> None:
            await self.pub.publish("", "worker_logs_queue", json.dumps(batch).encode())

        self.logs = LogBuffer(send, self.options.logger, flush_interval=0.005)  # type: ignore[arg-type]
        self.cancellations = CancellationRegistry()
        self.jobs = {name.lower(): resolve_job(name, handler, JobOptions(**job_options), self.options) for name, handler in handlers.items()}
        self.consumer = next(iter(self.jobs.values()))
        self.processor = Processor(self.options, self.jobs, self.pub, self.logs, self.cancellations)
        self.sleeps: list[float] = []

        async def fake_sleep(seconds: float) -> None:
            self.sleeps.append(seconds)

        self.processor.sleep = fake_sleep

    async def handle(self, delivery: FakeDelivery) -> None:
        await self.processor.handle(delivery.delivery, self.consumer)


S = Status


async def test_completes_and_reports_result() -> None:
    async def handler(ctx):
        ctx.log("working", {"to": ctx.data["to"]})
        return {"sent": True}

    h = Harness({"TestJob": handler})
    d = FakeDelivery({"jobData": json.dumps({"to": "a@b.c"})})
    await h.handle(d)

    assert d.settled == ["ack"]
    assert h.pub.statuses() == [S.RUNNING, S.COMPLETED]

    final = h.pub.last_status()
    assert (final["occurrenceId"], final["jobId"], final["workerId"], final["instanceId"]) == (OCCURRENCE_ID, JOB_ID, "py-worker", "py-worker-abcd1234")
    assert final["result"] == '{"sent": true}'
    assert final["startTime"] and final["endTime"] and isinstance(final["durationMs"], int)
    assert any(entry["message"] == "working" and entry["level"] == "Information" and entry["category"] == "UserCode" and entry["data"] == {"to": "a@b.c"}
               for entry in h.pub.logs())


async def test_sync_handler_runs_in_a_thread() -> None:
    main_thread = threading.get_ident()
    seen: list[int] = []

    def handler(ctx):
        seen.append(threading.get_ident())
        ctx.log("from a thread")
        return "done"

    h = Harness({"TestJob": handler})
    await h.handle(FakeDelivery())

    assert seen and seen[0] != main_thread
    assert h.pub.last_status()["result"] == "done"
    assert any(entry["message"] == "from a thread" for entry in h.pub.logs())


async def test_default_result_message() -> None:
    h = Harness({"TestJob": lambda ctx: None})
    await h.handle(FakeDelivery())

    assert h.pub.last_status()["result"] == "Job TestJob completed successfully"


async def test_retries_with_backoff() -> None:
    async def handler(ctx):
        raise RuntimeError("boom")

    h = Harness({"TestJob": handler}, max_retries=3, base_retry_delay=5)
    d = FakeDelivery(headers={"x-retry-count": 1})
    await h.handle(d)

    assert d.settled == ["ack"]
    assert h.sleeps == [10]
    assert h.pub.statuses() == [S.RUNNING]

    [retry] = h.pub.to(h.consumer.queue)
    assert retry["raw"] == d.delivery.body
    assert retry["correlation_id"] == OCCURRENCE_ID
    assert retry["headers"] == {"CorrelationId": OCCURRENCE_ID, "x-retry-count": 2, "MaxRetries": 3}
    assert any(entry["category"] == "Retry" and "attempt 2/3" in entry["message"] for entry in h.pub.logs())
    assert h.pub.dead_lettered() == []


async def test_dead_letters_after_retries() -> None:
    async def handler(ctx):
        raise RuntimeError("still broken")

    h = Harness({"TestJob": handler}, max_retries=3)
    d = FakeDelivery(headers={"x-retry-count": 3})
    await h.handle(d)

    assert d.settled == ["ack"]
    assert h.pub.statuses() == [S.RUNNING, S.FAILED]
    assert h.pub.last_status()["exception"].startswith("Max retries (3) exceeded. Original exception: Type: RuntimeError\nMessage: still broken")

    [dlq] = h.pub.dead_lettered()
    assert dlq["key"] == "failed_jobs"
    assert dlq["body"]["id"] == JOB_ID and dlq["body"]["status"] == S.FAILED
    assert dlq["headers"] == {"CorrelationId": OCCURRENCE_ID, "x-retry-count": 3, "MaxRetries": 3}


async def test_permanent_failure_skips_retries() -> None:
    async def handler(ctx):
        raise PermanentJobError("bad input")

    h = Harness({"TestJob": handler})
    d = FakeDelivery()
    await h.handle(d)

    assert d.settled == ["ack"]
    assert h.sleeps == []
    assert h.pub.statuses() == [S.RUNNING, S.FAILED]

    [dlq] = h.pub.dead_lettered()
    assert dlq["body"]["exception"].startswith("[PERMANENT FAILURE] Type: milvaion.errors.PermanentJobError")
    assert dlq["headers"]["MaxRetries"] == 0


async def test_invalid_job_data_is_permanent() -> None:
    h = Harness({"TestJob": lambda ctx: ctx.data})
    await h.handle(FakeDelivery({"jobData": "{not json"}))

    assert "Job data is not valid JSON" in h.pub.dead_lettered()[0]["body"]["exception"]


async def test_times_out_async_handler() -> None:
    cancelled = asyncio.Event()

    async def handler(ctx):
        try:
            await asyncio.sleep(60)
        except asyncio.CancelledError:
            cancelled.set()
            raise

    h = Harness({"TestJob": handler})
    d = FakeDelivery({"executionTimeoutSeconds": 0.05})
    await h.handle(d)
    await asyncio.wait_for(cancelled.wait(), 1)

    assert d.settled == ["ack"]
    assert h.pub.statuses() == [S.RUNNING, S.TIMED_OUT]
    assert "exceeded maximum timeout of 0.05 seconds" in h.pub.last_status()["exception"]


async def test_times_out_sync_handler_that_ignores_abort() -> None:
    release = threading.Event()
    h = Harness({"TestJob": lambda ctx: release.wait(5)})
    d = FakeDelivery({"executionTimeoutSeconds": 0.05})
    await h.handle(d)
    release.set()

    assert d.settled == ["ack"]
    assert h.pub.statuses() == [S.RUNNING, S.TIMED_OUT]


async def test_cancels_running_job() -> None:
    started = asyncio.Event()

    async def handler(ctx):
        started.set()
        await asyncio.sleep(60)

    h = Harness({"TestJob": handler})
    d = FakeDelivery()
    running = asyncio.create_task(h.handle(d))

    await started.wait()
    assert h.cancellations.cancel(OCCURRENCE_ID.upper(), "Cancelled by user")
    await running

    assert d.settled == ["ack"]
    assert h.pub.statuses() == [S.RUNNING, S.CANCELLED]


async def test_sync_handler_sees_abort() -> None:
    started = threading.Event()
    observed: list[bool] = []

    def handler(ctx):
        started.set()

        while not ctx.is_aborted:
            threading.Event().wait(0.01)

        observed.append(ctx.abort_reason is AbortReason.CANCELLED)

    h = Harness({"TestJob": handler})
    running = asyncio.create_task(h.handle(FakeDelivery()))

    await asyncio.to_thread(started.wait, 2)
    h.cancellations.cancel(OCCURRENCE_ID)
    await running

    for _ in range(100):
        if observed:
            break
        await asyncio.sleep(0.01)

    assert observed == [True]


async def test_shutdown_returns_job_to_queue() -> None:
    started = asyncio.Event()

    async def handler(ctx):
        started.set()
        await asyncio.sleep(60)

    h = Harness({"TestJob": handler})
    d = FakeDelivery()
    running = asyncio.create_task(h.handle(d))

    await started.wait()
    h.cancellations.abort_all(AbortReason.SHUTDOWN, "stopping")
    await running

    assert d.settled == ["requeue"]
    assert h.pub.statuses() == [S.RUNNING]


async def test_shutdown_during_retry_delay_requeues() -> None:
    async def handler(ctx):
        raise RuntimeError("boom")

    h = Harness({"TestJob": handler})
    d = FakeDelivery()
    h.processor.begin_shutdown()
    await h.handle(d)

    assert d.settled == ["requeue"]
    assert h.pub.to(h.consumer.queue) == []


async def test_unknown_job_is_dead_lettered() -> None:
    h = Harness({"TestJob": lambda ctx: "ok"})
    d = FakeDelivery({"jobNameInWorker": "OtherJob"})
    await h.handle(d)

    assert d.settled == ["ack"]
    assert h.pub.statuses() == [S.RUNNING, S.FAILED]
    assert h.pub.dead_lettered()[0]["body"]["exception"].startswith("[PERMANENT FAILURE] No IJob implementation registered for type: OtherJob")


async def test_job_names_are_case_insensitive() -> None:
    h = Harness({"TestJob": lambda ctx: "done"})
    await h.handle(FakeDelivery({"jobNameInWorker": "testjob"}))

    assert h.pub.last_status()["result"] == "done"


async def test_rejects_body_that_is_not_a_job() -> None:
    h = Harness({"TestJob": lambda ctx: "ok"})
    d = FakeDelivery()
    d.delivery.body = b"not json"
    await h.handle(d)

    assert d.settled == ["nack"]
    assert h.pub.messages == []


async def test_reports_redelivery() -> None:
    h = Harness({"TestJob": lambda ctx: "ok"})
    await h.handle(FakeDelivery(redelivered=True))

    assert h.pub.statuses() == [S.RUNNING, S.RUNNING, S.COMPLETED]
    assert any(entry["category"] == "Redelivery" for entry in h.pub.logs())


async def test_retries_refused_final_status() -> None:
    h = Harness({"TestJob": lambda ctx: "ok"})
    h.pub.fail_next = 1
    await h.handle(FakeDelivery())

    assert h.pub.statuses() == [S.RUNNING, S.COMPLETED]


async def test_wait_for_idle_tracks_running_jobs() -> None:
    release = asyncio.Event()
    started = asyncio.Event()

    async def handler(ctx):
        started.set()
        await release.wait()

    h = Harness({"TestJob": handler})
    running = asyncio.create_task(h.handle(FakeDelivery()))

    await started.wait()
    assert h.processor.running == 1
    assert await h.processor.wait_for_idle(0.01) is False

    release.set()
    await running

    assert h.processor.running == 0
    assert await h.processor.wait_for_idle(0.01) is True


@pytest.fixture(autouse=True)
def _quiet_logs(caplog: pytest.LogCaptureFixture) -> None:
    caplog.set_level(logging.CRITICAL, logger="milvaion-test")
