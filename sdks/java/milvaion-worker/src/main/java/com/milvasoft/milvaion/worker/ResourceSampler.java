package com.milvasoft.milvaion.worker;

import java.lang.management.ManagementFactory;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.file.Files;
import java.nio.file.Path;

/**
 * Process resource usage for heartbeats. Inside a Linux container the cgroup figures are used,
 * matching what `docker stats` reports, as the .NET SDK does.
 */
final class ResourceSampler {
    private final int cores = Runtime.getRuntime().availableProcessors();
    private double lastCpuNanos = -1;
    private long lastSampleNanos;

    synchronized long memoryBytes() {
        Long value = cgroupMemory();

        if (value == null) value = procRss();

        if (value != null) return value;

        var memory = ManagementFactory.getMemoryMXBean();

        return memory.getHeapMemoryUsage().getCommitted() + memory.getNonHeapMemoryUsage().getCommitted();
    }

    /** CPU used since the previous call, summed across cores (one busy core = 100). */
    synchronized double cpuUsagePercent() {
        Double cpu = cgroupCpuNanos();

        if (cpu == null) cpu = processCpuNanos();

        if (cpu == null) return 0;

        long now = System.nanoTime();

        if (lastCpuNanos < 0) {
            lastCpuNanos = cpu;
            lastSampleNanos = now;
            return 0;
        }

        double elapsed = now - lastSampleNanos;
        double used = cpu - lastCpuNanos;
        lastCpuNanos = cpu;
        lastSampleNanos = now;

        if (elapsed <= 0) return 0;

        double percent = Math.round(used / elapsed * 100 * 100) / 100.0;

        return Math.min(Math.max(percent, 0), 100.0 * cores);
    }

    private static Double processCpuNanos() {
        if (ManagementFactory.getOperatingSystemMXBean() instanceof com.sun.management.OperatingSystemMXBean os) {
            long nanos = os.getProcessCpuTime();
            return nanos < 0 ? null : (double) nanos;
        }

        return null;
    }

    private static Long cgroupMemory() {
        Long current = readLong("/sys/fs/cgroup/memory.current");

        if (current != null) return Math.max(0, current - orZero(readStat("/sys/fs/cgroup/memory.stat", "inactive_file")));

        Long usage = readLong("/sys/fs/cgroup/memory/memory.usage_in_bytes");

        if (usage != null) return Math.max(0, usage - orZero(readStat("/sys/fs/cgroup/memory/memory.stat", "total_inactive_file")));

        return null;
    }

    private static Double cgroupCpuNanos() {
        Long micros = readStat("/sys/fs/cgroup/cpu.stat", "usage_usec");

        if (micros != null) return micros * 1000.0;

        for (String path : new String[]{"/sys/fs/cgroup/cpu/cpuacct.usage", "/sys/fs/cgroup/cpuacct/cpuacct.usage"}) {
            Long nanos = readLong(path);

            if (nanos != null) return nanos.doubleValue();
        }

        return null;
    }

    private static Long procRss() {
        try {
            String[] fields = Files.readString(Path.of("/proc/self/statm")).trim().split("\\s+");
            return Long.parseLong(fields[1]) * 4096;
        } catch (Exception e) {
            return null;
        }
    }

    private static Long readLong(String path) {
        try {
            return Long.parseLong(Files.readString(Path.of(path)).trim());
        } catch (Exception e) {
            return null;
        }
    }

    private static Long readStat(String path, String key) {
        try {
            for (String line : Files.readAllLines(Path.of(path))) {
                String[] parts = line.trim().split("\\s+");

                if (parts.length >= 2 && parts[0].equals(key)) return Long.parseLong(parts[1]);
            }
        } catch (Exception e) {
            // Not available
        }

        return null;
    }

    private static long orZero(Long value) {
        return value == null ? 0 : value;
    }

    /** The address used for outbound traffic, or "Unknown". */
    static String localIpAddress() {
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.connect(InetAddress.getByName("10.255.255.255"), 1); // no packet is sent
            String address = socket.getLocalAddress().getHostAddress();
            return address.equals("0.0.0.0") ? InetAddress.getLocalHost().getHostAddress() : address;
        } catch (Exception e) {
            return "Unknown";
        }
    }
}
