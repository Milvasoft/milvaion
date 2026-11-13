from __future__ import annotations

import asyncio
import contextlib
import inspect
import json
import logging
import re
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from typing import Any, Protocol

from .context import JobContext, _Abort, utc_now
from .errors import AbortReason, JobAborted, PermanentJobError, _type_name, format_error
from .log_buffer import LogBuffer
from .options import ResolvedJob, WorkerOptions
from .protocol import DEAD_LETTER_ROUTING_KEY, EXCHANGE_DEAD_LETTER, QUEUE_STATUS_UPDATES, Status

_GUID = re.compile(r"^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$", re.IGNORECASE)


class Publisher(Protocol):
    """Publishing side of the broker, with publisher confirms."""

    async def publish(self, exchange: str, routing_key: str, body: bytes, *, persistent: bool = True,
                      correlation_id: str | None = None, headers: dict[str, Any] | None = None) -> None: ...


@dataclass
class Delivery:
    """One message taken from a job queue."""

    body: bytes
    headers: dict[str, Any]
    correlation_id: str | None
    redelivered: bool
    ack: Callable[[], Awaitable[None]]
    nack: Callable[[bool], Awaitable[None]]


class CancellationRegistry:
    """Abort handles of the jobs running on this instance, keyed by occurrence id."""

    def __init__(self) -> None:
        self._running: dict[str, _Abort] = {}

    def register(self, occurrence_id: str, abort: _Abort) -> None:
        self._running[occurrence_id.lower()] = abort

    def unregister(self, occurrence_id: str) -> None:
        self._running.pop(occurrence_id.lower(), None)

    def cancel(self, occurrence_id: str, reason: str | None = None) -> bool:
        """Returns False when the job is not running on this instance."""
        abort = self._running.get(occurrence_id.lower())

        if abort is None:
            return False

        abort.trigger(AbortReason.CANCELLED, f"Job was cancelled: {reason}" if reason else "Job was cancelled.")

        return True

    def abort_all(self, reason: AbortReason, message: str) -> None:
        for abort in list(self._running.values()):
            abort.trigger(reason, message)


@dataclass
class _Outcome:
    result: str | None = None
    error: BaseException | None = None
    abort: AbortReason | None = None
    abort_message: str = ""


@dataclass
class _Timing:
    start: datetime
    end: datetime | None = None
    extra: dict[str, Any] = field(default_factory=dict)

    @property
    def duration_ms(self) -> int:
        return int(((self.end or datetime.now(timezone.utc)) - self.start).total_seconds() * 1000)


class Processor:
    """Runs job deliveries and reports their progress to the API, following the worker protocol."""

    def __init__(self, options: WorkerOptions, jobs: dict[str, ResolvedJob], publisher: Publisher,
                 logs: LogBuffer, cancellations: CancellationRegistry) -> None:
        self.options = options
        self.jobs = jobs
        self.publisher = publisher
        self.logs = logs
        self.cancellations = cancellations
        self.logger: logging.Logger = options.logger  # type: ignore[assignment]
        self.on_count_changed: Callable[[], None] | None = None
        self.sleep: Callable[[float], Awaitable[None]] = asyncio.sleep
        self._shutdown = asyncio.Event()
        self._running = 0
        self._idle = asyncio.Event()
        self._idle.set()

    @property
    def running(self) -> int:
        return self._running

    def begin_shutdown(self) -> None:
        """Makes jobs waiting out a retry delay give their message back right away."""
        self._shutdown.set()

    async def wait_for_idle(self, timeout: float) -> bool:
        """True once no job is running, False after ``timeout`` seconds."""
        try:
            await asyncio.wait_for(self._idle.wait(), timeout)
            return True
        except asyncio.TimeoutError:
            return False

    async def handle(self, delivery: Delivery, consumer: ResolvedJob) -> None:
        self._running += 1
        self._idle.clear()
        self._count_changed()

        try:
            await self._process(delivery, consumer)
        except Exception:  # noqa: BLE001
            self.logger.exception("Unexpected error while processing a job delivery.")
            await self._settle(delivery.nack(False))
        finally:
            self._running -= 1

            if self._running == 0:
                self._idle.set()

            self._count_changed()

    def _count_changed(self) -> None:
        if self.on_count_changed is not None:
            self.on_count_changed()

    async def _process(self, delivery: Delivery, consumer: ResolvedJob) -> None:
        job = parse_job(delivery.body)

        if job is None:
            self.logger.error("Received a message on %s that is not a valid job. Sending it to the dead letter queue.", consumer.queue)
            await self._settle(delivery.nack(False))
            return

        occurrence_id = parse_occurrence_id(delivery.headers, delivery.correlation_id)

        if occurrence_id is None:
            self.logger.error("Job %s arrived without an occurrence id. Sending it to the dead letter queue.", job["id"])
            await self._settle(delivery.nack(False))
            return

        run = _JobRun(self, job, occurrence_id, parse_retry_count(delivery.headers))

        if delivery.redelivered:
            self.logger.warning("Job %s (occurrence %s) was redelivered; the previous attempt may have crashed.", job["id"], occurrence_id)
            await run.status(Status.RUNNING, start=datetime.now(timezone.utc))
            run.log("Warning", "⚠️ Job redelivered - previous execution may have crashed or timed out. Re-executing from the beginning.",
                    {"Reason": "RabbitMQ redelivery detected", "WorkerId": self.options.instance_id}, "Redelivery")

        abort = _Abort()
        self.cancellations.register(occurrence_id, abort)

        try:
            await self._execute(delivery, consumer, run, abort)
        finally:
            self.cancellations.unregister(occurrence_id)

    async def _execute(self, delivery: Delivery, consumer: ResolvedJob, run: _JobRun, abort: _Abort) -> None:
        job = run.job
        timing = _Timing(start=datetime.now(timezone.utc))

        await run.status(Status.RUNNING, start=timing.start)
        await run.flush_logs()

        registered = self.jobs.get(job["jobNameInWorker"].lower())

        if registered is None:
            exception = f"No IJob implementation registered for type: {job['jobNameInWorker']}"
            self.logger.error(exception)
            await run.status(Status.FAILED, end=datetime.now(timezone.utc), exception=exception)
            await self._dead_letter(delivery, run, f"[PERMANENT FAILURE] {exception}", run.retry_count, 0)
            return

        job_timeout = job.get("executionTimeoutSeconds")
        timeout = float(job_timeout) if job_timeout is not None else registered.execution_timeout

        outcome = await self._run_handler(registered, run, abort, timeout)
        timing.end = datetime.now(timezone.utc)

        if outcome.abort is not None:
            await self._finish_aborted(delivery, run, outcome, timing, timeout)
        elif outcome.error is not None:
            await self._finish_failed(delivery, consumer, registered, run, outcome.error, timing)
        else:
            await run.flush_logs()
            await run.status(Status.COMPLETED, start=timing.start, end=timing.end, duration_ms=timing.duration_ms, result=outcome.result)
            await self._settle(delivery.ack())

    async def _run_handler(self, registered: ResolvedJob, run: _JobRun, abort: _Abort, timeout: float) -> _Outcome:
        loop = asyncio.get_running_loop()
        aborted = asyncio.Event()
        abort.on_abort(lambda: loop.call_soon_threadsafe(aborted.set))

        timer = loop.call_later(timeout, abort.trigger, AbortReason.TIMED_OUT,
                                f"Job execution exceeded timeout limit of {_seconds(timeout)} seconds") if timeout and timeout > 0 else None

        heartbeat = loop.create_task(self._job_heartbeats(run)) if self.options.job_heartbeat_interval > 0 else None

        context = JobContext(
            occurrence_id=run.occurrence_id,
            job=run.job,
            retry_count=run.retry_count,
            instance_id=self.options.instance_id or "",
            abort=abort,
            sink=lambda log: self.logs.add(run.occurrence_id, self.options.instance_id or "", log),
        )

        handler = registered.handler

        # Async handlers are cancelled when the job is aborted; synchronous ones run in a thread that
        # cannot be stopped, so the job settles right away and the thread finishes in the background.
        if inspect.iscoroutinefunction(handler):
            task = loop.create_task(handler(context))
        else:
            task = loop.create_task(asyncio.to_thread(handler, context))

        abort_waiter = loop.create_task(aborted.wait())

        try:
            done, _ = await asyncio.wait({task, abort_waiter}, return_when=asyncio.FIRST_COMPLETED)

            if task in done and abort.reason is None:
                error = task.exception()

                if error is None:
                    return _Outcome(result=serialize_result(task.result(), run.job["jobNameInWorker"]))

                if isinstance(error, JobAborted):
                    return _Outcome(abort=error.reason, abort_message=str(error))

                return _Outcome(error=error)

            task.cancel()

            with contextlib.suppress(BaseException):
                await asyncio.wait({task}, timeout=0)

            return _Outcome(abort=abort.reason or AbortReason.CANCELLED, abort_message=abort.message)
        finally:
            if timer is not None:
                timer.cancel()

            abort_waiter.cancel()

            if heartbeat is not None:
                heartbeat.cancel()

            if task.done() and not task.cancelled():
                task.exception()  # mark retrieved

    async def _job_heartbeats(self, run: _JobRun) -> None:
        while True:
            await asyncio.sleep(self.options.job_heartbeat_interval)
            await run.status(Status.RUNNING, retry=False)

    async def _finish_aborted(self, delivery: Delivery, run: _JobRun, outcome: _Outcome, timing: _Timing, timeout: float) -> None:
        if outcome.abort is AbortReason.SHUTDOWN:
            # Nobody cancelled this job; it goes back to the queue and runs again.
            self.logger.warning("Job %s (occurrence %s) was interrupted by worker shutdown. Returning it to the queue.", run.job["id"], run.occurrence_id)
            await run.flush_logs()
            await self._settle(delivery.nack(True))
            return

        if outcome.abort is AbortReason.TIMED_OUT:
            run.log("Error", f"Job execution exceeded timeout limit of {_seconds(timeout)} seconds", None, "UserDefined")
            await run.flush_logs()
            await run.status(Status.TIMED_OUT, start=timing.start, end=timing.end, duration_ms=timing.duration_ms,
                             exception=f"Job execution exceeded maximum timeout of {_seconds(timeout)} seconds ({timing.duration_ms}ms elapsed)")
        else:
            run.log("Warning", "Job execution was cancelled", {"Reason": outcome.abort_message})
            await run.flush_logs()
            await run.status(Status.CANCELLED, start=timing.start, end=timing.end, duration_ms=timing.duration_ms,
                             result="Job cancelled by user or system")

        await self._settle(delivery.ack())

    async def _finish_failed(self, delivery: Delivery, consumer: ResolvedJob, registered: ResolvedJob, run: _JobRun,
                             error: BaseException, timing: _Timing) -> None:
        exception = format_error(error)

        if isinstance(error, PermanentJobError):
            run.log_error(f"Permanent job failure (will not retry): {error}", error)
            run.log("Error", "❌ Permanent failure - job will not be retried. Moving to Dead Letter Queue (DLQ).",
                    {"FailureType": "Permanent", "FinalStatus": "MovedToDLQ"}, "Retry")
            await run.flush_logs()
            await run.status(Status.FAILED, start=timing.start, end=timing.end, duration_ms=timing.duration_ms, exception=exception)
            await self._dead_letter(delivery, run, f"[PERMANENT FAILURE] {exception}", run.retry_count, 0)
            return

        run.log_error(f"Job execution failed: {error}", error)

        if run.retry_count < registered.max_retries:
            delay = registered.base_retry_delay * (2 ** run.retry_count)

            run.log("Warning", f"⚠️ Job failed (attempt {run.retry_count + 1}/{registered.max_retries}). Retrying in {_seconds(delay)}s...", {
                "RetryAttempt": run.retry_count + 1,
                "MaxRetries": registered.max_retries,
                "DelaySeconds": delay,
                "NextRetryTime": (datetime.now(timezone.utc) + timedelta(seconds=delay)).isoformat().replace("+00:00", "Z"),
            }, "Retry")
            await run.flush_logs()
            await self._retry(delivery, consumer, run, delay, registered.max_retries)
            return

        final = f"Max retries ({registered.max_retries}) exceeded. Original exception: {exception}"

        run.log("Error", f"❌ Job failed after {registered.max_retries} retries. Moving to Dead Letter Queue (DLQ).",
                {"TotalRetries": registered.max_retries, "FinalStatus": "MovedToDLQ"}, "Retry")
        await run.flush_logs()
        await run.status(Status.FAILED, start=timing.start, end=datetime.now(timezone.utc), duration_ms=timing.duration_ms, exception=final)
        await self._dead_letter(delivery, run, final, registered.max_retries, registered.max_retries)

    async def _retry(self, delivery: Delivery, consumer: ResolvedJob, run: _JobRun, delay: float, max_retries: int) -> None:
        """Waits out the backoff, republishes the job with a higher retry count and acks the original.

        The original stays unacked meanwhile, so a crash during the wait gets it redelivered."""
        interval = self.options.job_heartbeat_interval
        remaining = delay

        while remaining > 0:
            step = min(remaining, interval) if interval > 0 else remaining

            if await self._wait_unless_stopping(step):
                self.logger.warning("Worker is stopping during the retry delay of job %s. Returning it to the queue.", run.job["id"])
                await self._settle(delivery.nack(True))
                return

            remaining -= step

            if remaining > 0 and interval > 0:
                await run.status(Status.RUNNING, retry=False)

        try:
            await self.publisher.publish("", consumer.queue, delivery.body, persistent=True, correlation_id=run.occurrence_id,
                                         headers={"CorrelationId": run.occurrence_id, "x-retry-count": run.retry_count + 1, "MaxRetries": max_retries})
        except Exception as error:  # noqa: BLE001
            self.logger.error("Failed to republish job %s for retry. Dead-lettering the message: %s", run.job["id"], error)
            await self._settle(delivery.nack(False))
            return

        await self._settle(delivery.ack())

    async def _wait_unless_stopping(self, seconds: float) -> bool:
        """Sleeps; returns True if the worker started stopping meanwhile."""
        if self._shutdown.is_set():
            return True

        sleeper = asyncio.ensure_future(self.sleep(seconds))
        stopping = asyncio.ensure_future(self._shutdown.wait())

        try:
            await asyncio.wait({sleeper, stopping}, return_when=asyncio.FIRST_COMPLETED)
        finally:
            sleeper.cancel()
            stopping.cancel()

        return self._shutdown.is_set()

    async def _dead_letter(self, delivery: Delivery, run: _JobRun, exception: str, retry_count: int, max_retries: int) -> None:
        job = run.job
        body = json.dumps({
            "id": job["id"],
            "displayName": job.get("displayName"),
            "jobNameInWorker": job["jobNameInWorker"],
            "jobData": job.get("jobData"),
            "executeAt": job.get("executeAt"),
            "status": int(Status.FAILED),
            "exception": exception,
        }).encode()

        try:
            await self.publisher.publish(EXCHANGE_DEAD_LETTER, DEAD_LETTER_ROUTING_KEY, body, persistent=True, correlation_id=run.occurrence_id,
                                         headers={"CorrelationId": run.occurrence_id, "x-retry-count": retry_count, "MaxRetries": max_retries})
        except Exception as error:  # noqa: BLE001
            # The queue dead-letters a rejected message itself, just without the exception details.
            self.logger.error("Failed to publish job %s to the dead letter queue. Rejecting the message instead: %s", job["id"], error)
            await self._settle(delivery.nack(False))
            return

        await self._settle(delivery.ack())

    async def _settle(self, action: Awaitable[None]) -> None:
        try:
            await action
        except Exception as error:  # noqa: BLE001
            # An ack or nack on a channel that has closed: the broker redelivers the message.
            self.logger.warning("Could not settle a message; the broker will redeliver it: %s", error)


class _JobRun:
    """State and reporting helpers of one execution."""

    def __init__(self, processor: Processor, job: dict[str, Any], occurrence_id: str, retry_count: int) -> None:
        self.p = processor
        self.job = job
        self.occurrence_id = occurrence_id
        self.retry_count = retry_count

    def log(self, level: str, message: str, data: dict[str, Any] | None = None, category: str = "UserCode") -> None:
        self.p.logs.add(self.occurrence_id, self.p.options.instance_id or "", {
            "timestamp": utc_now(), "level": level, "message": message, "data": data, "category": category, "exceptionType": None,
        })

    def log_error(self, message: str, error: BaseException) -> None:
        inner = error.__cause__ or error.__context__
        self.p.logs.add(self.occurrence_id, self.p.options.instance_id or "", {
            "timestamp": utc_now(),
            "level": "Error",
            "message": message,
            "data": {"ExceptionType": _type_name(error), "StackTrace": format_error(error), "InnerException": str(inner) if inner else None},
            "category": "UserDefined",
            "exceptionType": _type_name(error),
        })

    async def flush_logs(self) -> None:
        try:
            await asyncio.wait_for(self.p.logs.flush(), 10)
        except Exception as error:  # noqa: BLE001
            self.p.logger.warning("Failed to flush job logs; they stay buffered and will be retried: %s", error)

    async def status(self, status: Status, *, start: datetime | None = None, end: datetime | None = None, duration_ms: int | None = None,
                     result: str | None = None, exception: str | None = None, retry: bool = True) -> None:
        """Publishes a status update. Final statuses are retried, because losing one leaves the
        occurrence running until the zombie detector gives up on it."""
        body = json.dumps({
            "occurrenceId": self.occurrence_id,
            "jobId": self.job["id"],
            "workerId": self.p.options.worker_id,
            "instanceId": self.p.options.instance_id,
            "status": int(status),
            "startTime": _iso(start),
            "endTime": _iso(end),
            "durationMs": duration_ms,
            "result": result,
            "exception": exception,
            "messageTimestamp": utc_now(),
        }).encode()

        delays = [0.5, 2, 5] if retry else []

        for attempt in range(len(delays) + 1):
            try:
                await self.p.publisher.publish("", QUEUE_STATUS_UPDATES, body, persistent=True)
                return
            except Exception as error:  # noqa: BLE001
                if attempt >= len(delays):
                    self.p.logger.warning("Failed to publish status %s for occurrence %s: %s", status.name, self.occurrence_id, error)
                    return

                await asyncio.sleep(delays[attempt])


def parse_job(body: bytes) -> dict[str, Any] | None:
    try:
        job = json.loads(body)
    except (ValueError, UnicodeDecodeError):
        return None

    if not isinstance(job, dict) or not isinstance(job.get("id"), str) or not isinstance(job.get("jobNameInWorker"), str):
        return None

    return job


def header_string(value: Any) -> str | None:
    if isinstance(value, str):
        return value

    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode("utf-8", errors="replace")

    return None


def parse_occurrence_id(headers: dict[str, Any] | None, correlation_id: str | None) -> str | None:
    """Occurrence id from the OccurrenceId header, the CorrelationId header or the correlation id property."""
    headers = headers or {}

    for candidate in (header_string(headers.get("OccurrenceId")), header_string(headers.get("CorrelationId")), correlation_id):
        if candidate and _GUID.match(candidate.strip()):
            return candidate.strip()

    return None


def parse_retry_count(headers: dict[str, Any] | None) -> int:
    value = (headers or {}).get("x-retry-count")

    if isinstance(value, bool):
        return 0

    if isinstance(value, int):
        return max(0, value)

    if isinstance(value, (bytes, bytearray)) and len(value) >= 4:
        # The .NET SDK reads a 4-byte value as a little-endian int.
        return max(0, int.from_bytes(bytes(value[:4]), "little", signed=True))

    if isinstance(value, str) and value.strip().isdigit():
        return int(value)

    return 0


def serialize_result(value: Any, job_name: str) -> str:
    if value is None:
        return f"Job {job_name} completed successfully"

    if isinstance(value, str):
        return value

    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode("utf-8", errors="replace")

    try:
        return json.dumps(value, default=str)
    except (TypeError, ValueError):
        return str(value)


def _iso(value: datetime | None) -> str | None:
    return value.isoformat().replace("+00:00", "Z") if value else None


def _seconds(value: float) -> str:
    return str(int(value)) if float(value).is_integer() else str(value)

