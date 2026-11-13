from __future__ import annotations

import asyncio
import logging
import threading
from collections.abc import Awaitable, Callable
from typing import Any

from .context import utc_now

BatchSender = Callable[[dict[str, Any]], Awaitable[None]]


class LogBuffer:
    """Batches occurrence logs. Safe to add to from handler threads.

    A batch that fails to send is kept and sent first next time, so logs keep their order."""

    def __init__(self, send: BatchSender, logger: logging.Logger, *, batch_size: int = 50,
                 flush_interval: float = 0.5, max_buffered: int = 10_000) -> None:
        self._send = send
        self._logger = logger
        self.batch_size = batch_size
        self.flush_interval = flush_interval
        self.max_buffered = max_buffered
        self._loop = asyncio.get_running_loop()
        self._lock = threading.Lock()
        self._buffer: list[dict[str, Any]] = []
        self._unsent: list[dict[str, Any]] | None = None
        self._flush_lock = asyncio.Lock()
        self._timer: asyncio.TimerHandle | None = None
        self._tasks: set[asyncio.Task[Any]] = set()
        self._dropped = 0
        self._closed = False

    def add(self, occurrence_id: str, instance_id: str, log: dict[str, Any]) -> None:
        with self._lock:
            if len(self._buffer) >= self.max_buffered:
                self._buffer.pop(0)
                self._dropped += 1

                if self._dropped % 1000 == 1:
                    self._logger.warning("Log buffer is full; dropped %d oldest log(s) so far.", self._dropped)

            self._buffer.append({"occurrenceId": occurrence_id, "workerId": instance_id, "log": log, "messageTimestamp": utc_now()})
            full = len(self._buffer) >= self.batch_size

        self._loop.call_soon_threadsafe(self._after_add, full)

    def _after_add(self, full: bool) -> None:
        if full:
            self._spawn_flush()
        elif self._timer is None and not self._closed:
            self._timer = self._loop.call_later(self.flush_interval, self._on_timer)

    def _on_timer(self) -> None:
        self._timer = None
        self._spawn_flush(retry=True)

    def _spawn_flush(self, retry: bool = False) -> None:
        async def run() -> None:
            try:
                await self.flush()
            except Exception as error:  # noqa: BLE001 - retried by the timer
                self._logger.debug("Failed to send logs; will retry: %s", error)

                if retry and self._timer is None and not self._closed:
                    self._timer = self._loop.call_later(self.flush_interval, self._on_timer)

        task = self._loop.create_task(run())
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)

    @property
    def pending(self) -> int:
        with self._lock:
            return len(self._buffer) + len(self._unsent or [])

    async def flush(self) -> None:
        """Sends everything buffered so far. Raises if a batch cannot be sent; it stays buffered."""
        async with self._flush_lock:
            per_round = self.batch_size * 2
            rounds = -(-self.pending // per_round) + 1

            for _ in range(rounds):
                batch = self._unsent

                if batch is None:
                    with self._lock:
                        batch, self._buffer = self._buffer[:per_round], self._buffer[per_round:]

                if not batch:
                    return

                self._unsent = batch
                await self._send({"logs": batch, "batchTimestamp": utc_now(), "count": len(batch)})
                self._unsent = None

    def close(self) -> None:
        self._closed = True

        if self._timer is not None:
            self._timer.cancel()
            self._timer = None
