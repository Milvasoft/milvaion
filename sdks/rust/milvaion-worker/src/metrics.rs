//! Process resource usage for heartbeats. Inside a Linux container the cgroup figures are used,
//! matching what `docker stats` reports, as the .NET SDK does. Elsewhere `/proc` is read; on
//! systems without either, both figures are 0.

use std::time::Instant;

#[derive(Default)]
pub(crate) struct ResourceSampler {
    last: Option<(f64, Instant)>, // CPU microseconds, sample time
}

impl ResourceSampler {
    pub fn memory_bytes(&self) -> i64 {
        cgroup_memory_bytes().or_else(proc_rss_bytes).unwrap_or(0)
    }

    /// CPU used since the previous call, summed across cores (one busy core = 100).
    pub fn cpu_usage_percent(&mut self) -> f64 {
        let Some(cpu) = cgroup_cpu_micros().or_else(proc_cpu_micros) else {
            return 0.0;
        };

        let now = Instant::now();

        let Some((last_cpu, last_time)) = self.last.replace((cpu, now)) else {
            return 0.0;
        };

        let elapsed = now.duration_since(last_time).as_micros() as f64;

        if elapsed <= 0.0 {
            return 0.0;
        }

        let percent = ((cpu - last_cpu) / elapsed * 100.0 * 100.0).round() / 100.0;

        percent.clamp(0.0, 100.0 * crate::options::cpu_count() as f64)
    }
}

fn read_int(path: &str) -> Option<i64> {
    std::fs::read_to_string(path).ok()?.trim().parse().ok()
}

fn read_stat(path: &str, key: &str) -> Option<i64> {
    std::fs::read_to_string(path).ok()?.lines().find_map(|line| {
        let mut fields = line.split_whitespace();
        (fields.next()? == key).then(|| fields.next()?.parse().ok()).flatten()
    })
}

fn cgroup_memory_bytes() -> Option<i64> {
    if let Some(v) = read_int("/sys/fs/cgroup/memory.current") {
        let inactive = read_stat("/sys/fs/cgroup/memory.stat", "inactive_file").unwrap_or(0);
        return Some((v - inactive).max(0));
    }

    let v = read_int("/sys/fs/cgroup/memory/memory.usage_in_bytes")?;
    let inactive = read_stat("/sys/fs/cgroup/memory/memory.stat", "total_inactive_file").unwrap_or(0);
    Some((v - inactive).max(0))
}

fn cgroup_cpu_micros() -> Option<f64> {
    if let Some(v) = read_stat("/sys/fs/cgroup/cpu.stat", "usage_usec") {
        return Some(v as f64);
    }

    ["/sys/fs/cgroup/cpu/cpuacct.usage", "/sys/fs/cgroup/cpuacct/cpuacct.usage"]
        .iter()
        .find_map(|path| read_int(path))
        .map(|nanos| nanos as f64 / 1000.0)
}

/// Resident set size from /proc (Linux), assuming 4 KiB pages.
fn proc_rss_bytes() -> Option<i64> {
    let statm = std::fs::read_to_string("/proc/self/statm").ok()?;
    let pages: i64 = statm.split_whitespace().nth(1)?.parse().ok()?;
    Some(pages * 4096)
}

/// User + system CPU time from /proc (Linux). Clock ticks are assumed to be 100 Hz.
fn proc_cpu_micros() -> Option<f64> {
    let stat = std::fs::read_to_string("/proc/self/stat").ok()?;
    // The command name can contain spaces; fields after it start past the last ')'.
    let fields: Vec<&str> = stat[stat.rfind(')')? + 1..].split_whitespace().collect();
    let utime: f64 = fields.get(11)?.parse().ok()?;
    let stime: f64 = fields.get(12)?.parse().ok()?;
    Some((utime + stime) * 10_000.0)
}

/// The local address used to reach `host:port`, or "Unknown". Connecting a UDP socket sends nothing.
pub(crate) fn local_ip_address(target: Option<(String, u16)>) -> String {
    let Some((host, port)) = target else {
        return "Unknown".to_string();
    };

    std::net::UdpSocket::bind("0.0.0.0:0")
        .and_then(|socket| {
            socket.connect((host.as_str(), port))?;
            socket.local_addr()
        })
        .map(|addr| addr.ip().to_string())
        .unwrap_or_else(|_| "Unknown".to_string())
}

pub(crate) fn host_name() -> String {
    ["HOSTNAME", "COMPUTERNAME"]
        .iter()
        .find_map(|key| std::env::var(key).ok().filter(|v| !v.trim().is_empty()))
        .or_else(|| std::fs::read_to_string("/etc/hostname").ok().map(|h| h.trim().to_string()))
        .filter(|h| !h.is_empty())
        .unwrap_or_else(|| "unknown".to_string())
}
