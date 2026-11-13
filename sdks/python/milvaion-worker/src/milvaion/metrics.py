from __future__ import annotations

import os
import socket
import time
from pathlib import Path


class ResourceSampler:
    """Process resource usage for heartbeats. Inside a Linux container the cgroup figures are used,
    matching what `docker stats` reports, as the .NET SDK does."""

    def __init__(self) -> None:
        self._last_cpu: float | None = None
        self._last_sample = 0.0
        self._cores = os.cpu_count() or 1

    def memory_bytes(self) -> int:
        for read in (_cgroup_memory, _proc_rss):
            value = read()

            if value is not None:
                return value

        return 0

    def cpu_usage_percent(self) -> float:
        """CPU used since the previous call, summed across cores (one busy core = 100)."""
        cpu = _cgroup_cpu_seconds()

        if cpu is None:
            cpu = time.process_time()

        now = time.monotonic()

        if self._last_cpu is None:
            self._last_cpu, self._last_sample = cpu, now
            return 0.0

        elapsed = now - self._last_sample
        used = cpu - self._last_cpu
        self._last_cpu, self._last_sample = cpu, now

        if elapsed <= 0:
            return 0.0

        return min(max(round(used / elapsed * 100, 2), 0.0), 100.0 * self._cores)


def _read_int(path: str) -> int | None:
    try:
        return int(Path(path).read_text().strip())
    except (OSError, ValueError):
        return None


def _read_stat(path: str, key: str) -> int | None:
    try:
        for line in Path(path).read_text().splitlines():
            parts = line.split()

            if len(parts) >= 2 and parts[0] == key:
                return int(parts[1])
    except (OSError, ValueError):
        pass

    return None


def _cgroup_memory() -> int | None:
    current = _read_int("/sys/fs/cgroup/memory.current")

    if current is not None:
        return max(0, current - (_read_stat("/sys/fs/cgroup/memory.stat", "inactive_file") or 0))

    usage = _read_int("/sys/fs/cgroup/memory/memory.usage_in_bytes")

    if usage is not None:
        return max(0, usage - (_read_stat("/sys/fs/cgroup/memory/memory.stat", "total_inactive_file") or 0))

    return None


def _cgroup_cpu_seconds() -> float | None:
    usage = _read_stat("/sys/fs/cgroup/cpu.stat", "usage_usec")

    if usage is not None:
        return usage / 1_000_000

    for path in ("/sys/fs/cgroup/cpu/cpuacct.usage", "/sys/fs/cgroup/cpuacct/cpuacct.usage"):
        nanos = _read_int(path)

        if nanos is not None:
            return nanos / 1_000_000_000

    return None


def _proc_rss() -> int | None:
    try:
        pages = int(Path("/proc/self/statm").read_text().split()[1])
        return pages * os.sysconf("SC_PAGE_SIZE")
    except (OSError, ValueError, IndexError, AttributeError):
        pass

    if os.name == "nt":
        return _windows_working_set()

    return None


def _windows_working_set() -> int | None:
    try:
        import ctypes
        from ctypes import wintypes

        class Counters(ctypes.Structure):
            _fields_ = [
                ("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
            ]

        counters = Counters()
        counters.cb = ctypes.sizeof(Counters)
        process = ctypes.windll.kernel32.GetCurrentProcess()

        if ctypes.windll.psapi.GetProcessMemoryInfo(process, ctypes.byref(counters), counters.cb):
            return int(counters.WorkingSetSize)
    except Exception:  # noqa: BLE001
        pass

    return None


def local_ip_address() -> str:
    """The address used for outbound traffic, or "Unknown"."""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.connect(("10.255.255.255", 1))  # no packet is sent
            return probe.getsockname()[0]
    except OSError:
        return "Unknown"
