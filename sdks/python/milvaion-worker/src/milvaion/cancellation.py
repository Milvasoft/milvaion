from __future__ import annotations

import asyncio
import contextlib
import json
import logging
from typing import Any

from redis import asyncio as aioredis

from .processor import CancellationRegistry


class CancellationListener:
    """Receives the cancellation requests the API publishes on Redis."""

    def __init__(self, url: str, channel: str, registry: CancellationRegistry, logger: logging.Logger) -> None:
        self._url = url
        self._channel = channel
        self._registry = registry
        self._logger = logger
        self._client: Any = None
        self._task: asyncio.Task[None] | None = None

    async def start(self) -> None:
        self._client = aioredis.from_url(self._url, decode_responses=True, health_check_interval=30)
        await self._client.ping()

        pubsub = self._client.pubsub(ignore_subscribe_messages=True)
        await pubsub.subscribe(self._channel)

        self._task = asyncio.get_running_loop().create_task(self._listen(pubsub))
        self._logger.info("Listening for cancellations on %s", self._channel)

    async def _listen(self, pubsub: Any) -> None:
        while True:
            try:
                async for message in pubsub.listen():
                    if message.get("type") == "message":
                        self._on_message(message.get("data"))
            except asyncio.CancelledError:
                with contextlib.suppress(Exception):
                    await pubsub.aclose()
                raise
            except Exception as error:  # noqa: BLE001 - reconnect and resubscribe
                self._logger.warning("Redis cancellation listener error: %s. Reconnecting...", error)
                await asyncio.sleep(2)

                with contextlib.suppress(Exception):
                    await pubsub.subscribe(self._channel)

    def _on_message(self, payload: Any) -> None:
        try:
            request = json.loads(payload)
        except (TypeError, ValueError):
            self._logger.debug("Ignored a cancellation message that is not JSON.")
            return

        if not isinstance(request, dict):
            return

        occurrence_id = request.get("CorrelationId") or request.get("OccurrenceId")

        if occurrence_id and self._registry.cancel(occurrence_id, request.get("Reason")):
            self._logger.info("Cancelling occurrence %s: %s", occurrence_id, request.get("Reason") or "no reason given")

    async def stop(self) -> None:
        if self._task is not None:
            self._task.cancel()

            with contextlib.suppress(BaseException):
                await self._task

        if self._client is not None:
            with contextlib.suppress(Exception):
                await self._client.aclose()
