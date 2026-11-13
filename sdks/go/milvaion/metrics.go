package milvaion

import (
	"math"
	"net"
	"os"
	"runtime"
	"strconv"
	"strings"
	"time"
)

// resourceSampler measures process resource usage for heartbeats. Inside a Linux container the
// cgroup figures are used, matching what `docker stats` reports, as the .NET SDK does.
type resourceSampler struct {
	lastCPU    float64 // microseconds
	lastSample time.Time
}

func (s *resourceSampler) memoryBytes() int64 {
	if v, ok := cgroupMemoryBytes(); ok {
		return v
	}

	if v, ok := procRSSBytes(); ok {
		return v
	}

	var stats runtime.MemStats
	runtime.ReadMemStats(&stats)

	return int64(stats.Sys)
}

// cpuUsagePercent is the CPU used since the previous call, summed across cores (one busy core = 100).
// Returns 0 where CPU time cannot be read.
func (s *resourceSampler) cpuUsagePercent() float64 {
	cpu, ok := cgroupCPUMicros()

	if !ok {
		if cpu, ok = procCPUMicros(); !ok {
			return 0
		}
	}

	now := time.Now()

	if s.lastSample.IsZero() {
		s.lastCPU, s.lastSample = cpu, now
		return 0
	}

	elapsed := float64(now.Sub(s.lastSample).Microseconds())
	used := cpu - s.lastCPU
	s.lastCPU, s.lastSample = cpu, now

	if elapsed <= 0 {
		return 0
	}

	percent := math.Round(used/elapsed*100*100) / 100

	return math.Min(math.Max(percent, 0), float64(100*runtime.NumCPU()))
}

func cgroupMemoryBytes() (int64, bool) {
	if v, ok := readInt("/sys/fs/cgroup/memory.current"); ok {
		inactive, _ := readStat("/sys/fs/cgroup/memory.stat", "inactive_file")
		return max(0, v-inactive), true
	}

	if v, ok := readInt("/sys/fs/cgroup/memory/memory.usage_in_bytes"); ok {
		inactive, _ := readStat("/sys/fs/cgroup/memory/memory.stat", "total_inactive_file")
		return max(0, v-inactive), true
	}

	return 0, false
}

func cgroupCPUMicros() (float64, bool) {
	if v, ok := readStat("/sys/fs/cgroup/cpu.stat", "usage_usec"); ok {
		return float64(v), true
	}

	for _, path := range []string{"/sys/fs/cgroup/cpu/cpuacct.usage", "/sys/fs/cgroup/cpuacct/cpuacct.usage"} {
		if v, ok := readInt(path); ok {
			return float64(v) / 1000, true
		}
	}

	return 0, false
}

// procRSSBytes reads the resident set size from /proc (Linux).
func procRSSBytes() (int64, bool) {
	data, err := os.ReadFile("/proc/self/statm")

	if err != nil {
		return 0, false
	}

	fields := strings.Fields(string(data))

	if len(fields) < 2 {
		return 0, false
	}

	pages, err := strconv.ParseInt(fields[1], 10, 64)

	if err != nil {
		return 0, false
	}

	return pages * int64(os.Getpagesize()), true
}

// procCPUMicros reads user + system CPU time from /proc (Linux). Clock ticks are assumed to be 100 Hz.
func procCPUMicros() (float64, bool) {
	data, err := os.ReadFile("/proc/self/stat")

	if err != nil {
		return 0, false
	}

	// The command name can contain spaces; fields after it start past the last ')'.
	text := string(data)
	fields := strings.Fields(text[strings.LastIndexByte(text, ')')+1:])

	if len(fields) < 13 {
		return 0, false
	}

	utime, err1 := strconv.ParseFloat(fields[11], 64)
	stime, err2 := strconv.ParseFloat(fields[12], 64)

	if err1 != nil || err2 != nil {
		return 0, false
	}

	return (utime + stime) * 10_000, true
}

func readInt(path string) (int64, bool) {
	data, err := os.ReadFile(path)

	if err != nil {
		return 0, false
	}

	v, err := strconv.ParseInt(strings.TrimSpace(string(data)), 10, 64)

	return v, err == nil
}

func readStat(path, key string) (int64, bool) {
	data, err := os.ReadFile(path)

	if err != nil {
		return 0, false
	}

	for _, line := range strings.Split(string(data), "\n") {
		fields := strings.Fields(line)

		if len(fields) >= 2 && fields[0] == key {
			v, err := strconv.ParseInt(fields[1], 10, 64)
			return v, err == nil
		}
	}

	return 0, false
}

// localIPAddress is the first external IPv4 address, or "Unknown".
func localIPAddress() string {
	addresses, err := net.InterfaceAddrs()

	if err != nil {
		return "Unknown"
	}

	for _, address := range addresses {
		if ipNet, ok := address.(*net.IPNet); ok && !ipNet.IP.IsLoopback() && ipNet.IP.To4() != nil {
			return ipNet.IP.String()
		}
	}

	return "Unknown"
}
