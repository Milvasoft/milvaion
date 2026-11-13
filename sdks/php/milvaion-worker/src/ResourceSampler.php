<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker;

/**
 * Process resource usage for heartbeats. Inside a Linux container the cgroup figures are used,
 * matching what `docker stats` reports, as the .NET SDK does.
 *
 * @internal
 */
final class ResourceSampler
{
    private ?float $lastCpu = null;
    private float $lastSample = 0;

    public function memoryBytes(): int
    {
        $current = self::readInt('/sys/fs/cgroup/memory.current');

        if ($current !== null) {
            return max(0, $current - (self::readStat('/sys/fs/cgroup/memory.stat', 'inactive_file') ?? 0));
        }

        $statm = @file_get_contents('/proc/self/statm');

        if ($statm !== false) {
            $fields = preg_split('/\s+/', trim($statm));

            if (isset($fields[1])) {
                return (int) $fields[1] * 4096;
            }
        }

        return memory_get_usage(true);
    }

    /** CPU used since the previous call, summed across cores (one busy core = 100). */
    public function cpuUsagePercent(): float
    {
        $usage = self::readStat('/sys/fs/cgroup/cpu.stat', 'usage_usec');
        $cpu = $usage !== null ? $usage / 1_000_000 : self::processCpuSeconds();
        $now = microtime(true);

        if ($this->lastCpu === null) {
            [$this->lastCpu, $this->lastSample] = [$cpu, $now];

            return 0.0;
        }

        $elapsed = $now - $this->lastSample;
        $used = $cpu - $this->lastCpu;
        [$this->lastCpu, $this->lastSample] = [$cpu, $now];

        return $elapsed <= 0 ? 0.0 : max(0.0, round($used / $elapsed * 100, 2));
    }

    private static function processCpuSeconds(): float
    {
        $usage = getrusage();

        return $usage['ru_utime.tv_sec'] + $usage['ru_utime.tv_usec'] / 1e6 + $usage['ru_stime.tv_sec'] + $usage['ru_stime.tv_usec'] / 1e6;
    }

    private static function readInt(string $path): ?int
    {
        $value = @file_get_contents($path);

        return $value !== false && is_numeric(trim($value)) ? (int) trim($value) : null;
    }

    private static function readStat(string $path, string $key): ?int
    {
        $content = @file_get_contents($path);

        if ($content === false) {
            return null;
        }

        foreach (explode("\n", $content) as $line) {
            $parts = preg_split('/\s+/', trim($line));

            if (count($parts) >= 2 && $parts[0] === $key) {
                return (int) $parts[1];
            }
        }

        return null;
    }

    /** The address used for outbound traffic, or "Unknown". */
    public static function localIpAddress(): string
    {
        $socket = @stream_socket_client('udp://10.255.255.255:1', $errno, $error, 1);

        if ($socket === false) {
            return 'Unknown';
        }

        $name = stream_socket_get_name($socket, false);
        fclose($socket);

        return $name === false ? 'Unknown' : (string) preg_replace('/:\d+$/', '', $name);
    }
}
