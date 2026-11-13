# frozen_string_literal: true

module Milvaion
  # A fixed-size pool of threads that run job deliveries. Items wait in a FIFO queue until a thread is free.
  class ThreadPool
    attr_reader :size

    def initialize(size, logger, name: "milvaion-pool")
      @size = size
      @logger = logger
      @name = name
      @mutex = Mutex.new
      @available = ConditionVariable.new
      @items = []
      @closed = false
      @threads = []
    end

    def start
      @mutex.synchronize do
        next unless @threads.empty?

        @threads = Array.new(size) do |index|
          Thread.new do
            Thread.current.name = "#{@name}-#{index}"
            work
          end
        end
      end

      self
    end

    # Queues an item. Returns false once the pool is closed; the caller then handles the item itself.
    def post(item, &block)
      @mutex.synchronize do
        return false if @closed

        @items << [item, block]
        @available.signal
      end

      true
    end

    # Stops taking items and returns the ones that have not started yet.
    def close
      @mutex.synchronize do
        @closed = true
        @available.broadcast
        taken = @items.map(&:first)
        @items.clear
        taken
      end
    end

    def join(timeout)
      deadline = Process.clock_gettime(Process::CLOCK_MONOTONIC) + timeout

      @threads.each do |thread|
        remaining = deadline - Process.clock_gettime(Process::CLOCK_MONOTONIC)
        thread.join([remaining, 0].max)
      end
    end

    private

    def work
      loop do
        item, block = @mutex.synchronize do
          @available.wait(@mutex) while @items.empty? && !@closed
          return if @items.empty?

          @items.shift
        end

        begin
          block.call(item)
        rescue Exception => e # rubocop:disable Lint/RescueException -- a pool thread must survive any item
          @logger.error("Unexpected error in the job pool: #{e.class}: #{e.message}")
        end
      end
    end
  end
end
