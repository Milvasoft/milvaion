# frozen_string_literal: true

require "etc"
require "socket"

module Milvaion
  # Process resource usage for heartbeats. Inside a Linux container the cgroup figures are used,
  # matching what `docker stats` reports, as the .NET SDK does.
  class ResourceSampler
    def initialize
      @last_cpu = nil
      @last_sample = 0.0
      @cores = Etc.nprocessors
    end

    def memory_bytes
      cgroup_memory || proc_rss || ps_rss || 0
    end

    # CPU used since the previous call, summed across cores (one busy core = 100).
    def cpu_usage_percent
      cpu = cgroup_cpu_seconds || Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
      now = Process.clock_gettime(Process::CLOCK_MONOTONIC)

      if @last_cpu.nil?
        @last_cpu = cpu
        @last_sample = now
        return 0.0
      end

      elapsed = now - @last_sample
      used = cpu - @last_cpu
      @last_cpu = cpu
      @last_sample = now

      return 0.0 if elapsed <= 0

      (used / elapsed * 100).round(2).clamp(0.0, 100.0 * @cores)
    end

    # The address used for outbound traffic, or "Unknown".
    def self.local_ip_address
      UDPSocket.open do |socket|
        socket.connect("10.255.255.255", 1) # no packet is sent
        socket.addr.last
      end
    rescue StandardError
      "Unknown"
    end

    private

    def read_int(path)
      Integer(File.read(path).strip, 10)
    rescue StandardError
      nil
    end

    def read_stat(path, key)
      File.foreach(path) do |line|
        name, value = line.split
        return Integer(value, 10) if name == key && value
      end

      nil
    rescue StandardError
      nil
    end

    def cgroup_memory
      current = read_int("/sys/fs/cgroup/memory.current")
      return [0, current - (read_stat("/sys/fs/cgroup/memory.stat", "inactive_file") || 0)].max if current

      usage = read_int("/sys/fs/cgroup/memory/memory.usage_in_bytes")
      return [0, usage - (read_stat("/sys/fs/cgroup/memory/memory.stat", "total_inactive_file") || 0)].max if usage

      nil
    end

    def cgroup_cpu_seconds
      usage = read_stat("/sys/fs/cgroup/cpu.stat", "usage_usec")
      return usage / 1_000_000.0 if usage

      ["/sys/fs/cgroup/cpu/cpuacct.usage", "/sys/fs/cgroup/cpuacct/cpuacct.usage"].each do |path|
        nanos = read_int(path)
        return nanos / 1_000_000_000.0 if nanos
      end

      nil
    end

    def proc_rss
      pages = Integer(File.read("/proc/self/statm").split[1], 10)
      pages * page_size
    rescue StandardError
      nil
    end

    def page_size
      Etc.sysconf(Etc::SC_PAGESIZE)
    rescue StandardError
      4096
    end

    # macOS and other systems without /proc.
    def ps_rss
      return nil if Gem.win_platform?

      kilobytes = Integer(IO.popen(["ps", "-o", "rss=", "-p", Process.pid.to_s], err: File::NULL, &:read).strip, 10)
      kilobytes * 1024
    rescue StandardError
      nil
    end
  end
end
