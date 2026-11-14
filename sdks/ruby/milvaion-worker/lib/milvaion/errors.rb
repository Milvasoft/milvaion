# frozen_string_literal: true

module Milvaion
  # Base class of the errors this SDK raises.
  class Error < StandardError; end

  # A failure that retrying cannot fix, such as invalid job data.
  # A job that raises it is not retried and goes straight to the dead letter queue.
  class PermanentJobError < Error; end

  # Asks Milvaion to run the job again at a given time instead of retrying now, for example when an
  # API quota resets at 11:00 UTC. The worker does not retry it and does not dead-letter it: the
  # occurrence ends as Failed with retryAfter set. Milvaion reruns workflow steps at that time; a
  # standalone job ends there as Failed.
  class RetryAfterError < Error
    # The requested time, in UTC.
    attr_reader :retry_at

    # retry_at: a Time (or anything responding to to_time, such as a DateTime). It is converted to UTC.
    def initialize(retry_at, message = nil)
      time = retry_at.is_a?(Time) ? retry_at : retry_at&.to_time
      raise ArgumentError, "retry_at must be a Time" unless time.is_a?(Time)

      @retry_at = time.getutc
      super(message || "Job asked to be retried at #{Milvaion.iso(@retry_at)}.")
    end
  end

  # RabbitMQ or Redis could not be reached, or a message was not confirmed by the broker.
  class ConnectionError < Error; end

  # Why a running job was aborted.
  module AbortReason
    # Cancelled from the dashboard.
    CANCELLED = :cancelled
    # Ran past its execution timeout.
    TIMED_OUT = :timed_out
    # The worker is shutting down; the job goes back to the queue.
    SHUTDOWN = :shutdown
  end

  # Raised by JobContext#raise_if_aborted and JobContext#sleep once the job has been aborted.
  class JobAborted < Error
    attr_reader :reason

    def initialize(reason, message = nil)
      super(message || "Job was aborted (#{reason}).")
      @reason = reason
    end
  end

  # Renders an exception the way the .NET SDK does, so the dashboard shows both alike.
  def self.format_error(error)
    formatted = +"Type: #{error_type_name(error)}\nMessage: #{error.message}\nStackTrace: #{(error.backtrace || []).join("\n")}\n"
    formatted << "\nInner Exception:\n" << format_error(error.cause) if error.cause

    formatted
  end

  def self.error_type_name(error)
    error.class.name || error.class.to_s
  end
end
