# frozen_string_literal: true

require "json"
require "redis"

module Milvaion
  # Receives the cancellation requests the API publishes on Redis, on a thread of its own.
  # If the connection drops, it reconnects and subscribes again.
  class CancellationListener
    # A subscription that sees no message for this long is renewed, which also detects a dead connection.
    IDLE_RESUBSCRIBE_SECONDS = 60
    RECONNECT_DELAY_SECONDS = 2

    def initialize(url, channel, registry, logger, redis_factory: nil)
      @url = url
      @channel = channel
      @registry = registry
      @logger = logger
      @redis_factory = redis_factory || ->(redis_url) { Redis.new(url: redis_url, connect_timeout: 5, reconnect_attempts: 0) }
      @mutex = Mutex.new
      @client = nil
      @thread = nil
      @stopped = false
    end

    # Checks that Redis is reachable, then starts listening. Raises if it is not.
    def start(timeout)
      deadline = monotonic + timeout

      begin
        probe = @redis_factory.call(@url)
        probe.ping
        probe.close
      rescue StandardError => e
        raise ConnectionError, "Could not connect to Redis within #{timeout}s: #{e.message}" if monotonic + RECONNECT_DELAY_SECONDS > deadline

        @logger.warn("Could not connect to Redis: #{e.message}")
        Kernel.sleep(RECONNECT_DELAY_SECONDS)
        retry
      end

      ready = Queue.new

      @thread = Thread.new do
        Thread.current.name = "milvaion-cancel"
        listen(ready)
      end

      # Wait for the first subscription so a cancellation sent right after start is not missed.
      Thread.new { Kernel.sleep(timeout) && ready << :timeout }
      @logger.warn("Subscribing to #{@channel} is taking long; continuing.") if ready.pop == :timeout

      @logger.info("Listening for cancellations on #{@channel}")
      self
    end

    def stop
      @mutex.synchronize do
        @stopped = true
        client = @client
        @client = nil

        begin
          client&.close
        rescue StandardError
          nil
        end
      end

      @thread&.kill
      @thread&.join(2)
      @thread = nil
    end

    # Handles one message from the channel. Public for tests.
    def on_message(payload)
      request = JSON.parse(payload.to_s)
      return unless request.is_a?(Hash)

      occurrence_id = request["CorrelationId"] || request["OccurrenceId"]
      reason = request["Reason"]

      return unless occurrence_id && @registry.cancel(occurrence_id, reason)

      @logger.info("Cancelling occurrence #{occurrence_id}: #{reason.to_s.empty? ? 'no reason given' : reason}")
    rescue JSON::ParserError
      @logger.debug("Ignored a cancellation message that is not JSON.")
    end

    private

    def listen(ready)
      failed = false

      until stopped?
        begin
          client = @redis_factory.call(@url)
          @mutex.synchronize { @client = client }
          break if stopped?

          client.subscribe_with_timeout(IDLE_RESUBSCRIBE_SECONDS, @channel) do |on|
            on.subscribe do
              ready << :ok
              @logger.info("Reconnected to Redis.") if failed
              failed = false
            end

            on.message { |_channel, payload| on_message(payload) }
          end
        rescue Redis::TimeoutError
          # No message for a while: renew the subscription.
        rescue StandardError => e
          break if stopped?

          @logger.warn("Redis cancellation listener error: #{e.message}. Reconnecting...") unless failed
          failed = true
          Kernel.sleep(RECONNECT_DELAY_SECONDS)
        ensure
          begin
            client&.close
          rescue StandardError
            nil
          end
        end
      end
    end

    def stopped?
      @mutex.synchronize { @stopped }
    end

    def monotonic
      Process.clock_gettime(Process::CLOCK_MONOTONIC)
    end
  end
end
