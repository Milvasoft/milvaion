# frozen_string_literal: true

module Milvaion
  # Base class of the errors this SDK raises.
  class Error < StandardError; end

  # A failure that retrying cannot fix, such as invalid job data.
  # A job that raises it is not retried and goes straight to the dead letter queue.
  class PermanentJobError < Error; end

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
