# frozen_string_literal: true

require "json"

module Milvaion
  # One message taken from a job queue or topic. ack is called with no arguments, nack with the
  # requeue flag: nack(true) returns the message so it runs again, nack(false) dead-letters it.
  #
  # reject:         optional; dead-letters a message that cannot be processed, with the reason.
  #                 Without it nack(false) is used.
  # schedule_retry: optional; hands a failed job to the broker for a delayed retry instead of
  #                 waiting in process (Kafka's retry topic). Called with a RetryRequest; returns once
  #                 the broker has accepted the retry and raises otherwise.
  Delivery = Struct.new(:body, :headers, :correlation_id, :redelivered, :ack, :nack, :reject, :schedule_retry, keyword_init: true)

  # The next attempt of a failed job. retry_count is that of the next attempt; not_before a Time.
  RetryRequest = Struct.new(:job_id, :occurrence_id, :retry_count, :max_retries, :not_before, keyword_init: true)

  # Abort signals of the jobs running on this instance, keyed by occurrence id.
  class CancellationRegistry
    def initialize
      @mutex = Mutex.new
      @running = {}
    end

    def register(occurrence_id, abort)
      @mutex.synchronize { @running[occurrence_id.downcase] = abort }
    end

    def unregister(occurrence_id)
      @mutex.synchronize { @running.delete(occurrence_id.downcase) }
    end

    # Returns false when the job is not running on this instance.
    def cancel(occurrence_id, reason = nil)
      abort = @mutex.synchronize { @running[occurrence_id.to_s.downcase] }
      return false if abort.nil?

      abort.trigger(AbortReason::CANCELLED, reason.to_s.empty? ? "Job was cancelled." : "Job was cancelled: #{reason}")
      true
    end

    def abort_all(reason, message)
      @mutex.synchronize { @running.values.dup }.each { |abort| abort.trigger(reason, message) }
    end
  end

  # Runs job deliveries and reports their progress to the API, following the worker protocol.
  # #handle runs synchronously on the calling thread (a worker pool thread); the handler itself runs
  # on a thread of its own so that a timeout or cancellation can settle the occurrence right away.
  class Processor
    GUID = /\A\h{8}-?\h{4}-?\h{4}-?\h{4}-?\h{12}\z/.freeze
    STATUS_RETRY_DELAYS = [0.5, 2, 5].freeze

    Outcome = Struct.new(:result, :error, :abort, :abort_message, keyword_init: true)

    attr_reader :options, :jobs, :publisher, :logs, :cancellations, :logger
    # Called whenever the number of running jobs changes.
    attr_accessor :on_count_changed
    # Sleeps between attempts to publish a status. Replaceable in tests.
    attr_accessor :status_sleep
    # Waits out a retry delay; returns true if the worker started stopping meanwhile. Replaceable in tests.
    attr_accessor :retry_wait

    def initialize(options, jobs, publisher, logs, cancellations)
      @options = options
      @jobs = jobs
      @publisher = publisher
      @logs = logs
      @cancellations = cancellations
      @logger = options.logger
      @shutdown = AbortSignal.new
      @count_mutex = Mutex.new
      @idle = ConditionVariable.new
      @running = 0
      @status_sleep = ->(seconds) { Kernel.sleep(seconds) }
      @retry_wait = ->(seconds) { @shutdown.wait(seconds) }
    end

    def running
      @count_mutex.synchronize { @running }
    end

    def stopping?
      @shutdown.aborted?
    end

    # Makes jobs waiting out a retry delay give their message back right away.
    def begin_shutdown
      @shutdown.trigger(AbortReason::SHUTDOWN, "Worker is shutting down.")
    end

    # True once no job is running, false after timeout seconds.
    def wait_for_idle(timeout)
      deadline = monotonic + timeout.to_f

      @count_mutex.synchronize do
        while @running.positive?
          remaining = deadline - monotonic
          return false if remaining <= 0

          @idle.wait(@count_mutex, remaining)
        end
      end

      true
    end

    def handle(delivery, consumer)
      change_count(1)

      begin
        process(delivery, consumer)
      rescue StandardError => e
        logger.error("Unexpected error while processing a job delivery: #{e.class}: #{e.message}")
        reject(delivery, "unexpected error while processing the job: #{e.class}: #{e.message}")
      ensure
        change_count(-1)
      end
    end

    # The parsed job, or nil when the body is not a scheduled job.
    def self.parse_job(body)
      job = JSON.parse(body.to_s)
      return nil unless job.is_a?(Hash) && job["id"].is_a?(String) && job["jobNameInWorker"].is_a?(String)

      job
    rescue JSON::ParserError, EncodingError
      nil
    end

    # A header value as a string. RabbitMQ delivers strings as binary.
    def self.header_string(value)
      return nil unless value.is_a?(String)

      value.dup.force_encoding(Encoding::UTF_8).scrub
    end

    # Occurrence id from the OccurrenceId header, the CorrelationId header or the correlation id property.
    def self.parse_occurrence_id(headers, correlation_id)
      headers ||= {}

      [header_string(headers["OccurrenceId"]), header_string(headers["CorrelationId"]), correlation_id].each do |candidate|
        value = candidate.to_s.strip
        return value if GUID.match?(value)
      end

      nil
    end

    def self.parse_retry_count(headers)
      value = (headers || {})["x-retry-count"]

      case value
      when Integer
        [0, value].max
      when String
        if value.strip.match?(/\A\d+\z/)
          value.strip.to_i
        elsif value.bytesize >= 4
          # The .NET SDK reads a 4-byte value as a little-endian int.
          [0, value.byteslice(0, 4).unpack1("l<")].max
        else
          0
        end
      else
        0
      end
    end

    def self.serialize_result(value, job_name)
      return "Job #{job_name} completed successfully" if value.nil?
      return value.dup.force_encoding(Encoding::UTF_8).scrub if value.is_a?(String)

      JSON.generate(value)
    rescue StandardError
      value.to_s
    end

    def self.seconds(value)
      float = value.to_f
      float == float.to_i ? float.to_i.to_s : float.to_s
    end

    private

    def monotonic
      Process.clock_gettime(Process::CLOCK_MONOTONIC)
    end

    def change_count(delta)
      @count_mutex.synchronize do
        @running += delta
        @idle.broadcast if @running.zero?
      end

      on_count_changed&.call
    end

    def process(delivery, consumer)
      job = Processor.parse_job(delivery.body)

      if job.nil?
        logger.error("Received a message on #{consumer.queue} that is not a valid job. Sending it to the dead letter queue.")
        reject(delivery, "the message is not a valid job")
        return
      end

      occurrence_id = Processor.parse_occurrence_id(delivery.headers, delivery.correlation_id)

      if occurrence_id.nil?
        logger.error("Job #{job['id']} arrived without an occurrence id. Sending it to the dead letter queue.")
        reject(delivery, "the message has no occurrence id")
        return
      end

      run = JobRun.new(self, job, occurrence_id, Processor.parse_retry_count(delivery.headers))

      if delivery.redelivered
        logger.warn("Job #{job['id']} (occurrence #{occurrence_id}) was redelivered; the previous attempt may have crashed.")
        run.status(Status::RUNNING, start: Time.now.utc)
        run.log("Warning", "⚠️ Job redelivered - previous execution may have crashed or timed out. Re-executing from the beginning.",
                { "Reason" => "RabbitMQ redelivery detected", "WorkerId" => options.instance_id }, "Redelivery")
      end

      abort = AbortSignal.new
      cancellations.register(occurrence_id, abort)

      begin
        execute(delivery, consumer, run, abort)
      ensure
        cancellations.unregister(occurrence_id)
      end
    end

    def execute(delivery, consumer, run, abort)
      job = run.job
      start = Time.now.utc

      run.status(Status::RUNNING, start: start)
      run.flush_logs

      registered = jobs[job["jobNameInWorker"].downcase]

      if registered.nil?
        exception = "No IJob implementation registered for type: #{job['jobNameInWorker']}"
        logger.error(exception)
        run.status(Status::FAILED, end_time: Time.now.utc, exception: exception)
        dead_letter(delivery, run, "[PERMANENT FAILURE] #{exception}", run.retry_count, 0)
        return
      end

      job_timeout = job["executionTimeoutSeconds"]
      timeout = job_timeout.nil? ? registered.execution_timeout.to_f : job_timeout.to_f

      outcome = run_handler(registered, run, abort, timeout)
      timing = { start: start, end: Time.now.utc }
      timing[:duration_ms] = ((timing[:end] - start) * 1000).to_i

      if outcome.abort
        finish_aborted(delivery, run, outcome, timing, timeout)
      elsif outcome.error
        finish_failed(delivery, consumer, registered, run, outcome.error, timing)
      else
        run.flush_logs
        run.status(Status::COMPLETED, start: start, end_time: timing[:end], duration_ms: timing[:duration_ms], result: outcome.result)
        settle { delivery.ack.call }
      end
    end

    def run_handler(registered, run, abort, timeout)
      mutex = Mutex.new
      finished = ConditionVariable.new
      box = nil

      abort.on_abort { mutex.synchronize { finished.broadcast } }

      context = JobContext.new(
        occurrence_id: run.occurrence_id,
        job: run.job,
        retry_count: run.retry_count,
        instance_id: options.instance_id,
        abort: abort,
        sink: ->(log) { logs.add(run.occurrence_id, options.instance_id, log) }
      )

      thread = Thread.new do
        Thread.current.name = "milvaion-job"
        Thread.current.report_on_exception = false

        outcome =
          begin
            [:ok, invoke(registered.handler, context)]
          rescue Exception => e # rubocop:disable Lint/RescueException -- every failure of user code is reported
            [:error, e]
          end

        mutex.synchronize do
          box = outcome
          finished.broadcast
        end
      end

      wait_for_handler(run, abort, timeout, mutex, finished) { box }

      if box && !abort.aborted?
        kind, value = box
        return Outcome.new(result: Processor.serialize_result(value, run.job["jobNameInWorker"])) if kind == :ok
        return Outcome.new(abort: value.reason, abort_message: value.message) if value.is_a?(JobAborted)

        return Outcome.new(error: value)
      end

      # Ruby cannot stop a thread safely from outside. The occurrence settles right away; the handler
      # thread keeps running until it notices the abort (ctx.aborted?, ctx.sleep) or returns, unless
      # the job opted into interrupt_on_abort.
      if registered.interrupt_on_abort && thread.alive?
        thread.raise(JobAborted.new(abort.reason, abort.message))
      end

      Outcome.new(abort: abort.reason || AbortReason::CANCELLED, abort_message: abort.message)
    end

    def invoke(handler, context)
      if handler.is_a?(Class)
        handler.new.perform(context)
      else
        handler.call(context)
      end
    end

    # Waits until the handler finishes or the job is aborted, firing the timeout and sending job heartbeats meanwhile.
    def wait_for_handler(run, abort, timeout, mutex, finished, &result)
      now = monotonic
      deadline = timeout.positive? ? now + timeout : nil
      interval = options.job_heartbeat_interval.to_f
      next_heartbeat = interval.positive? ? now + interval : nil

      loop do
        mutex.synchronize do
          while result.call.nil? && !abort.aborted?
            wake = [deadline, next_heartbeat].compact.min

            if wake.nil?
              finished.wait(mutex)
            else
              remaining = wake - monotonic
              break if remaining <= 0

              finished.wait(mutex, remaining)
            end
          end
        end

        return if !mutex.synchronize { result.call }.nil? || abort.aborted?

        now = monotonic

        if deadline && now >= deadline
          abort.trigger(AbortReason::TIMED_OUT, "Job execution exceeded timeout limit of #{Processor.seconds(timeout)} seconds")
          return
        end

        if next_heartbeat && now >= next_heartbeat
          run.status(Status::RUNNING, reliable: false)
          next_heartbeat = monotonic + interval
        end
      end
    end

    def finish_aborted(delivery, run, outcome, timing, timeout)
      if outcome.abort == AbortReason::SHUTDOWN
        # Nobody cancelled this job; it goes back to the queue and runs again.
        logger.warn("Job #{run.job['id']} (occurrence #{run.occurrence_id}) was interrupted by worker shutdown. Returning it to the queue.")
        run.flush_logs
        settle { delivery.nack.call(true) }
        return
      end

      if outcome.abort == AbortReason::TIMED_OUT
        run.log("Error", "Job execution exceeded timeout limit of #{Processor.seconds(timeout)} seconds", nil, "UserDefined")
        run.flush_logs
        run.status(Status::TIMED_OUT, start: timing[:start], end_time: timing[:end], duration_ms: timing[:duration_ms],
                                      exception: "Job execution exceeded maximum timeout of #{Processor.seconds(timeout)} seconds (#{timing[:duration_ms]}ms elapsed)")
      else
        run.log("Warning", "Job execution was cancelled", { "Reason" => outcome.abort_message })
        run.flush_logs
        run.status(Status::CANCELLED, start: timing[:start], end_time: timing[:end], duration_ms: timing[:duration_ms],
                                      result: "Job cancelled by user or system")
      end

      settle { delivery.ack.call }
    end

    def finish_failed(delivery, consumer, registered, run, error, timing)
      exception = Milvaion.format_error(error)

      if error.is_a?(PermanentJobError)
        run.log_error("Permanent job failure (will not retry): #{error.message}", error)
        run.log("Error", "❌ Permanent failure - job will not be retried. Moving to Dead Letter Queue (DLQ).",
                { "FailureType" => "Permanent", "FinalStatus" => "MovedToDLQ" }, "Retry")
        run.flush_logs
        run.status(Status::FAILED, start: timing[:start], end_time: timing[:end], duration_ms: timing[:duration_ms], exception: exception)
        dead_letter(delivery, run, "[PERMANENT FAILURE] #{exception}", run.retry_count, 0)
        return
      end

      run.log_error("Job execution failed: #{error.message}", error)

      if run.retry_count < registered.max_retries
        delay = registered.base_retry_delay * (2**run.retry_count)

        run.log("Warning", "⚠️ Job failed (attempt #{run.retry_count + 1}/#{registered.max_retries}). Retrying in #{Processor.seconds(delay)}s...", {
                  "RetryAttempt" => run.retry_count + 1,
                  "MaxRetries" => registered.max_retries,
                  "DelaySeconds" => delay,
                  "NextRetryTime" => Milvaion.iso(Time.now + delay)
                }, "Retry")
        run.flush_logs
        retry_later(delivery, consumer, run, delay, registered.max_retries)
        return
      end

      final = "Max retries (#{registered.max_retries}) exceeded. Original exception: #{exception}"

      run.log("Error", "❌ Job failed after #{registered.max_retries} retries. Moving to Dead Letter Queue (DLQ).",
              { "TotalRetries" => registered.max_retries, "FinalStatus" => "MovedToDLQ" }, "Retry")
      run.flush_logs
      run.status(Status::FAILED, start: timing[:start], end_time: Time.now.utc, duration_ms: timing[:duration_ms], exception: final)
      dead_letter(delivery, run, final, registered.max_retries, registered.max_retries)
    end

    # Waits out the backoff, republishes the job with a higher retry count and acks the original.
    # The original stays unacked meanwhile, so a crash during the wait gets it redelivered. A broker
    # that schedules retries itself (Kafka) gets the retry right away instead.
    def retry_later(delivery, consumer, run, delay, max_retries)
      return schedule_retry(delivery, run, delay, max_retries) if delivery.schedule_retry

      interval = options.job_heartbeat_interval.to_f
      remaining = delay.to_f

      while remaining.positive?
        step = interval.positive? ? [remaining, interval].min : remaining

        if stopping? || retry_wait.call(step)
          logger.warn("Worker is stopping during the retry delay of job #{run.job['id']}. Returning it to the queue.")
          settle { delivery.nack.call(true) }
          return
        end

        remaining -= step
        run.status(Status::RUNNING, reliable: false) if remaining.positive? && interval.positive?
      end

      begin
        publisher.publish("", consumer.queue, delivery.body, persistent: true, correlation_id: run.occurrence_id,
                                                             headers: { "CorrelationId" => run.occurrence_id, "x-retry-count" => run.retry_count + 1, "MaxRetries" => max_retries })
      rescue StandardError => e
        logger.error("Failed to republish job #{run.job['id']} for retry. Dead-lettering the message: #{e.message}")
        reject(delivery, "the job could not be republished for retry: #{e.message}")
        return
      end

      settle { delivery.ack.call }
    end

    # Hands the next attempt to the broker, settles the original and keeps the occurrence's job
    # heartbeat going until the attempt is due, since the occurrence stays Running.
    def schedule_retry(delivery, run, delay, max_retries)
      due = Time.now + delay

      begin
        delivery.schedule_retry.call(RetryRequest.new(job_id: run.job["id"], occurrence_id: run.occurrence_id, retry_count: run.retry_count + 1,
                                                      max_retries: max_retries, not_before: due))
      rescue StandardError => e
        logger.error("Failed to schedule job #{run.job['id']} for retry. Dead-lettering the message: #{e.message}")
        reject(delivery, "the job could not be scheduled for retry: #{e.message}")
        return
      end

      settle { delivery.ack.call }

      Thread.new do
        Thread.current.name = "milvaion-retry-heartbeat"
        heartbeat_while_waiting(run, due)
      end
    end

    public

    # Sends Running statuses every job_heartbeat_interval for an occurrence that waits for its next
    # attempt, until due, until stop (an AbortSignal) is triggered or the worker stops.
    def heartbeat_while_waiting(run, due, stop = nil)
      interval = options.job_heartbeat_interval.to_f
      return unless interval.positive?

      loop do
        remaining = due - Time.now
        return if remaining <= 0 || stopping?

        step = [remaining, interval].min
        return if stop ? stop.wait(step) : @shutdown.wait(step)

        run.status(Status::RUNNING, reliable: false) if due > Time.now && !stopping?
      end
    rescue StandardError => e
      logger.debug("Job heartbeat while waiting for a retry failed: #{e.message}")
    end

    private

    # Dead-letters a message that cannot be processed.
    def reject(delivery, reason)
      if delivery.reject
        settle { delivery.reject.call(reason) }
      else
        settle { delivery.nack.call(false) }
      end
    end

    def dead_letter(delivery, run, exception, retry_count, max_retries)
      job = run.job
      body = JSON.generate({
                             "id" => job["id"],
                             "displayName" => job["displayName"],
                             "jobNameInWorker" => job["jobNameInWorker"],
                             "jobData" => job["jobData"],
                             "executeAt" => job["executeAt"],
                             "status" => Status::FAILED,
                             "exception" => exception
                           })

      begin
        publisher.publish(Protocol::EXCHANGE_DEAD_LETTER, Protocol::DEAD_LETTER_ROUTING_KEY, body, persistent: true, correlation_id: run.occurrence_id,
                                                                                                  key: run.occurrence_id, headers: { "CorrelationId" => run.occurrence_id, "x-retry-count" => retry_count, "MaxRetries" => max_retries })
      rescue StandardError => e
        # The queue dead-letters a rejected message itself, just without the exception details.
        logger.error("Failed to publish job #{job['id']} to the dead letter queue. Rejecting the message instead: #{e.message}")
        reject(delivery, "the dead letter message could not be published: #{e.message}")
        return
      end

      settle { delivery.ack.call }
    end

    def settle
      yield
    rescue StandardError => e
      # An ack or nack on a channel that has closed: the broker redelivers the message.
      logger.warn("Could not settle a message; the broker will redeliver it: #{e.message}")
    end
  end

  # State and reporting helpers of one execution.
  class JobRun
    attr_reader :job, :occurrence_id, :retry_count

    def initialize(processor, job, occurrence_id, retry_count)
      @p = processor
      @job = job
      @occurrence_id = occurrence_id
      @retry_count = retry_count
    end

    def log(level, message, data = nil, category = "UserCode")
      @p.logs.add(occurrence_id, @p.options.instance_id, {
                    "timestamp" => Milvaion.utc_now, "level" => level, "message" => message, "data" => data,
                    "category" => category, "exceptionType" => nil
                  })
    end

    def log_error(message, error)
      type = Milvaion.error_type_name(error)

      @p.logs.add(occurrence_id, @p.options.instance_id, {
                    "timestamp" => Milvaion.utc_now,
                    "level" => "Error",
                    "message" => message,
                    "data" => { "ExceptionType" => type, "StackTrace" => Milvaion.format_error(error), "InnerException" => error.cause&.message },
                    "category" => "UserDefined",
                    "exceptionType" => type
                  })
    end

    def flush_logs
      @p.logs.flush
    rescue StandardError => e
      @p.logger.warn("Failed to flush job logs; they stay buffered and will be retried: #{e.message}")
    end

    # Publishes a status update. Final statuses are retried, because losing one leaves the
    # occurrence running until the zombie detector gives up on it.
    def status(status, start: nil, end_time: nil, duration_ms: nil, result: nil, exception: nil, reliable: true)
      body = JSON.generate({
                             "occurrenceId" => occurrence_id,
                             "jobId" => job["id"],
                             "workerId" => @p.options.worker_id,
                             "instanceId" => @p.options.instance_id,
                             "status" => status,
                             "startTime" => Milvaion.iso(start),
                             "endTime" => Milvaion.iso(end_time),
                             "durationMs" => duration_ms,
                             "result" => result,
                             "exception" => exception,
                             "messageTimestamp" => Milvaion.utc_now
                           })

      delays = reliable ? Processor::STATUS_RETRY_DELAYS : []
      attempt = 0

      begin
        @p.publisher.publish("", Protocol::QUEUE_STATUS_UPDATES, body, persistent: true, key: occurrence_id)
      rescue StandardError => e
        if attempt >= delays.size
          @p.logger.warn("Failed to publish status #{Status.name_of(status)} for occurrence #{occurrence_id}: #{e.message}")
          return
        end

        @p.status_sleep.call(delays[attempt])
        attempt += 1
        retry
      end
    end
  end
end
