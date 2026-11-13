import { readFileSync } from 'node:fs';
import os from 'node:os';

/**
 * Process resource usage for heartbeats. Inside a Linux container the cgroup figures are used,
 * matching what `docker stats` reports, as the .NET SDK does.
 */
export class ResourceSampler {
  private lastCpuMicros = -1;
  private lastSampleNs = 0n;
  private readonly cores = os.availableParallelism();

  memoryBytes(): number {
    return readCgroupMemoryBytes() ?? process.memoryUsage.rss();
  }

  /** CPU used since the previous call, summed across cores (one busy core = 100). */
  cpuUsagePercent(): number {
    const cpuMicros = readCgroupCpuMicros() ?? processCpuMicros();
    const now = process.hrtime.bigint();

    if (this.lastCpuMicros < 0) {
      this.lastCpuMicros = cpuMicros;
      this.lastSampleNs = now;

      return 0;
    }

    const elapsedMicros = Number(now - this.lastSampleNs) / 1000;
    const usedMicros = cpuMicros - this.lastCpuMicros;

    this.lastCpuMicros = cpuMicros;
    this.lastSampleNs = now;

    if (elapsedMicros <= 0)
      return 0;

    const percent = (usedMicros / elapsedMicros) * 100;

    return Math.min(Math.max(Math.round(percent * 100) / 100, 0), 100 * this.cores);
  }
}

function processCpuMicros(): number {
  const usage = process.cpuUsage();

  return usage.user + usage.system;
}

function readCgroupMemoryBytes(): number | undefined {
  if (process.platform !== 'linux')
    return undefined;

  const v2 = readNumber('/sys/fs/cgroup/memory.current');

  if (v2 !== undefined)
    return Math.max(0, v2 - (readStat('/sys/fs/cgroup/memory.stat', 'inactive_file') ?? 0));

  const v1 = readNumber('/sys/fs/cgroup/memory/memory.usage_in_bytes');

  if (v1 !== undefined)
    return Math.max(0, v1 - (readStat('/sys/fs/cgroup/memory/memory.stat', 'total_inactive_file') ?? 0));

  return undefined;
}

function readCgroupCpuMicros(): number | undefined {
  if (process.platform !== 'linux')
    return undefined;

  const v2 = readStat('/sys/fs/cgroup/cpu.stat', 'usage_usec');

  if (v2 !== undefined)
    return v2;

  const v1Nanos = readNumber('/sys/fs/cgroup/cpu/cpuacct.usage') ?? readNumber('/sys/fs/cgroup/cpuacct/cpuacct.usage');

  return v1Nanos === undefined ? undefined : v1Nanos / 1000;
}

function readNumber(path: string): number | undefined {
  try {
    const value = Number(readFileSync(path, 'utf8').trim());

    return Number.isFinite(value) ? value : undefined;
  } catch {
    return undefined;
  }
}

function readStat(path: string, key: string): number | undefined {
  try {
    for (const line of readFileSync(path, 'utf8').split('\n')) {
      const [name, value] = line.trim().split(/\s+/);

      if (name === key && value !== undefined) {
        const parsed = Number(value);

        return Number.isFinite(parsed) ? parsed : undefined;
      }
    }
  } catch {
    // Not available
  }

  return undefined;
}

/** First external IPv4 address, or `Unknown`. */
export function localIpAddress(): string {
  for (const addresses of Object.values(os.networkInterfaces())) {
    for (const address of addresses ?? []) {
      if (address.family === 'IPv4' && !address.internal)
        return address.address;
    }
  }

  return 'Unknown';
}
