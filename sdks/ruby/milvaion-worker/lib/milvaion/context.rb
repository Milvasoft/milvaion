# frozen_string_literal: true

require "json"

module Milvaion
  # Abort state of one execution. Safe to read and wait on from any thread.
  class AbortSignal
    attr_reader :reason, :message

    def initialize
      @mutex = Mutex.new
      @changed = ConditionVariable.new
      @reason = nil
      @message = ""
      @listeners = []
    end

    def aborted?
      !@reason.nil?
    end

    # Sets the reason once; later calls return false.
    def trigger(reason, message)
      listeners = @mutex.synchronize do
        return false unless @reason.nil?

        @reason = reason
        @message = message
        @changed.broadcast
        @listeners.dup
      end

      listeners.each(&:call)
      true
    end

    # Calls the block once when the signal is triggered, right away if it already was.
    def on_abort(&listener)
      @mutex.synchronize do
        if @reason.nil?
          @listeners << listener
          return
        end
      end

      listener.call
    end

    # Sleeps up to seconds, waking early on abort. Returns true if aborted.
    def wait(seconds)
      deadline = monotonic + seconds.to_f

      @mutex.synchronize do
        while @reason.nil?
          remaining = deadline - monotonic
          break if remaining <= 0

          @changed.wait(@mutex, remaining)
        end
      end

      aborted?
    end

    private

    def monotonic
      Process.clock_gettime(Process::CLOCK_MONOTONIC)
    end
  end

  # What a job handler receives.
  class JobContext
    # Id of this execution. Stable across retries and redeliveries, so it works as an idempotency key.
    attr_reader :occurrence_id
    # 0 on the first attempt, then 1, 2, ...
    attr_reader :retry_count
    # Instance id of this worker process.
    attr_reader :instance_id
    # The scheduled job as the API sent it, a Hash with string keys.
    attr_reader :job

    def initialize(occurrence_id:, job:, retry_count:, instance_id:, abort:, sink:)
      @occurrence_id = occurrence_id
      @job = job
      @retry_count = retry_count
      @instance_id = instance_id
      @abort = abort
      @sink = sink
      @data_mutex = Mutex.new
      @parsed = false
      @data = nil
    end

    def job_id
      job["id"]
    end

    def job_name
      job["jobNameInWorker"]
    end

    # Job data as the raw JSON string, or nil.
    def raw_data
      job["jobData"]
    end

    # Parsed job data (string keys), or nil when the job has none. Invalid JSON fails the job permanently.
    def data
      @data_mutex.synchronize do
        unless @parsed
          raw = raw_data.to_s.strip

          begin
            @data = raw.empty? ? nil : JSON.parse(raw)
          rescue JSON::ParserError
            raise PermanentJobError, "Job data is not valid JSON."
          end

          @parsed = true
        end
      end

      @data
    end

    # True once the job has been cancelled, has timed out or the worker is shutting down.
    # Ruby threads cannot be stopped safely from outside, so check this in long loops.
    def aborted?
      @abort.aborted?
    end

    # :cancelled, :timed_out, :shutdown or nil.
    def abort_reason
      @abort.reason
    end

    # Raises JobAborted if the job has been aborted.
    def raise_if_aborted
      raise JobAborted.new(@abort.reason, @abort.message) if @abort.aborted?
    end

    # Sleeps up to seconds, and raises JobAborted as soon as the job is aborted. Use it instead of Kernel#sleep.
    def sleep(seconds)
      @abort.wait(seconds)
      raise_if_aborted
    end

    # Writes an Information entry to the occurrence log.
    def log(message, data = nil)
      write("Information", message, data)
    end
    alias info log

    def debug(message, data = nil)
      write("Debug", message, data)
    end

    def warn(message, data = nil)
      write("Warning", message, data)
    end

    # Writes an Error entry, with the details of error when given.
    def error(message, error = nil, data = nil)
      details = (data || {}).dup
      exception_type = nil

      if error
        exception_type = Milvaion.error_type_name(error)
        details["ExceptionType"] = exception_type
        details["StackTrace"] = Milvaion.format_error(error)
        details["InnerException"] = error.cause.message if error.cause
      end

      write("Error", message, details, "UserDefined", exception_type)
    end

    private

    def write(level, message, data, category = "UserCode", exception_type = nil)
      @sink.call({
        "timestamp" => Milvaion.utc_now,
        "level" => level,
        "message" => message.to_s,
        "data" => data,
        "category" => category,
        "exceptionType" => exception_type
      })
    end
  end
end
