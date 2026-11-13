from __future__ import annotations

import json
import threading
from collections.abc import Callable
from datetime import datetime, timezone
from typing import Any

from .errors import AbortReason, JobAborted, PermanentJobError, _type_name, format_error


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


class _Abort:
    """Abort state of one execution. Readable from handler threads."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self.reason: AbortReason | None = None
        self.message = ""
        self._listeners: list[Callable[[], None]] = []

    def trigger(self, reason: AbortReason, message: str) -> bool:
        with self._lock:
            if self.reason is not None:
                return False

            self.reason, self.message = reason, message
            listeners = list(self._listeners)

        for listener in listeners:
            listener()

        return True

    def on_abort(self, listener: Callable[[], None]) -> None:
        with self._lock:
            if self.reason is None:
                self._listeners.append(listener)
                return

        listener()


class JobContext:
    """What a job handler receives."""

    def __init__(
        self,
        *,
        occurrence_id: str,
        job: dict[str, Any],
        retry_count: int,
        instance_id: str,
        abort: _Abort,
        sink: Callable[[dict[str, Any]], None],
    ) -> None:
        self.occurrence_id = occurrence_id
        """Id of this execution. Stable across retries and redeliveries, so it works as an idempotency key."""
        self.retry_count = retry_count
        """0 on the first attempt, then 1, 2, ..."""
        self.instance_id = instance_id
        """Instance id of this worker process."""
        self.job = job
        """The scheduled job as the API sent it."""
        self._abort = abort
        self._sink = sink
        self._data: Any = None
        self._parsed = False

    @property
    def job_id(self) -> str:
        return self.job["id"]

    @property
    def job_name(self) -> str:
        return self.job["jobNameInWorker"]

    @property
    def raw_data(self) -> str | None:
        """Job data as the raw JSON string, or None."""
        return self.job.get("jobData")

    @property
    def data(self) -> Any:
        """Parsed job data, or None when the job has none. Invalid JSON fails the job permanently."""
        if not self._parsed:
            raw = (self.raw_data or "").strip()

            try:
                self._data = json.loads(raw) if raw else None
            except json.JSONDecodeError as error:
                raise PermanentJobError("Job data is not valid JSON.") from error

            self._parsed = True

        return self._data

    @property
    def is_aborted(self) -> bool:
        """True once the job has been cancelled, has timed out or the worker is shutting down.

        Async handlers are also cancelled (they get ``asyncio.CancelledError``); synchronous handlers
        run in a thread and should check this in long loops."""
        return self._abort.reason is not None

    @property
    def abort_reason(self) -> AbortReason | None:
        return self._abort.reason

    def raise_if_aborted(self) -> None:
        """Raises JobAborted if the job has been aborted."""
        if self._abort.reason is not None:
            raise JobAborted(self._abort.reason, self._abort.message)

    def log(self, message: str, data: dict[str, Any] | None = None) -> None:
        """Writes an Information entry to the occurrence log."""
        self._write("Information", message, data)

    def debug(self, message: str, data: dict[str, Any] | None = None) -> None:
        self._write("Debug", message, data)

    def warn(self, message: str, data: dict[str, Any] | None = None) -> None:
        self._write("Warning", message, data)

    def error(self, message: str, error: BaseException | None = None, data: dict[str, Any] | None = None) -> None:
        """Writes an Error entry, with the details of ``error`` when given."""
        details = dict(data or {})
        exception_type = None

        if error is not None:
            exception_type = _type_name(error)
            details["ExceptionType"] = exception_type
            details["StackTrace"] = format_error(error)
            inner = error.__cause__ or error.__context__

            if inner is not None:
                details["InnerException"] = str(inner)

        self._write("Error", message, details, "UserDefined", exception_type)

    def _write(self, level: str, message: str, data: dict[str, Any] | None, category: str = "UserCode", exception_type: str | None = None) -> None:
        self._sink({
            "timestamp": utc_now(),
            "level": level,
            "message": message,
            "data": data,
            "category": category,
            "exceptionType": exception_type,
        })
