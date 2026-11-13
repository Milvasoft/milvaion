# frozen_string_literal: true

module Milvaion
  # Batches occurrence logs and sends them from a background thread every flush_interval seconds,
  # or as soon as batch_size entries are waiting. Safe to add to from any thread.
  #
  # A batch that fails to send is kept and sent first next time, so logs keep their order.
  class LogBuffer
    attr_reader :batch_size, :flush_interval, :max_buffered

    # sender is called with one batch hash ({"logs", "batchTimestamp", "count"}) and raises if it cannot send it.
    def initialize(sender, logger, batch_size: 50, flush_interval: 0.5, max_buffered: 10_000)
      @sender = sender
      @logger = logger
      @batch_size = batch_size
      @flush_interval = flush_interval
      @max_buffered = max_buffered
      @mutex = Mutex.new
      @changed = ConditionVariable.new
      @flush_mutex = Mutex.new
      @buffer = []
      @unsent = nil
      @dropped = 0
      @closed = false
      @thread = nil
    end

    def start
      @thread ||= Thread.new do
        Thread.current.name = "milvaion-logs"
        run
      end

      self
    end

    def add(occurrence_id, instance_id, log)
      @mutex.synchronize do
        if @buffer.size >= max_buffered
          @buffer.shift
          @dropped += 1
          @logger.warn("Log buffer is full; dropped #{@dropped} oldest log(s) so far.") if @dropped % 1000 == 1
        end

        @buffer << { "occurrenceId" => occurrence_id, "workerId" => instance_id, "log" => log, "messageTimestamp" => Milvaion.utc_now }
        @changed.broadcast
      end
    end

    def pending
      @mutex.synchronize { @buffer.size + (@unsent&.size || 0) }
    end

    # Sends everything buffered so far. Raises if a batch cannot be sent; it stays buffered.
    def flush
      @flush_mutex.synchronize do
        per_round = batch_size * 2
        rounds = ((pending + per_round - 1) / per_round) + 1

        rounds.times do
          batch = @mutex.synchronize do
            if @unsent.nil?
              taken = @buffer.shift(per_round)
              @unsent = taken unless taken.empty?
            end

            @unsent
          end

          return if batch.nil?

          @sender.call({ "logs" => batch, "batchTimestamp" => Milvaion.utc_now, "count" => batch.size })
          @mutex.synchronize { @unsent = nil }
        end
      end
    end

    def close
      @mutex.synchronize do
        @closed = true
        @changed.broadcast
      end

      @thread&.join(5)
      @thread = nil
    end

    private

    def run
      loop do
        @mutex.synchronize do
          @changed.wait(@mutex) while !@closed && @buffer.empty? && @unsent.nil?
          # Give more entries a moment to arrive, unless a full batch is already waiting.
          deadline = monotonic + flush_interval

          while !@closed && @buffer.size < batch_size
            remaining = deadline - monotonic
            break if remaining <= 0

            @changed.wait(@mutex, remaining)
          end
        end

        break if @mutex.synchronize { @closed }

        begin
          flush
        rescue StandardError => e
          @logger.debug("Failed to send logs; will retry: #{e.message}")
          Kernel.sleep(flush_interval)
        end
      end
    end

    def monotonic
      Process.clock_gettime(Process::CLOCK_MONOTONIC)
    end
  end
end
