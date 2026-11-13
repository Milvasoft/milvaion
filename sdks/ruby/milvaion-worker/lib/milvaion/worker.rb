# frozen_string_literal: true

require "json"
require "rbconfig"
require "socket"
require "time"

require_relative "worker/version"
require_relative "protocol"
require_relative "errors"
require_relative "options"
require_relative "context"
require_relative "log_buffer"
require_relative "processor"
require_relative "thread_pool"
require_relative "metrics"
require_relative "job"
require_relative "cancellation_listener"

module Milvaion
  # A Milvaion worker. Register jobs with #job, then call #run (blocking) or #start / #stop.
  #
  #   worker = Milvaion::Worker.new(worker_id: "report-worker")
  #   worker.job("SendEmailJob") { |ctx| ctx.log("hi"); { sent: true } }
  #   worker.run
  class Worker
    attr_reader :options

    def initialize(options = nil, **kwargs)
      @options = options || WorkerOptions.new(**kwargs)
      @jobs = {}
      @cancellations = CancellationRegistry.new
      @sampler = ResourceSampler.new
      @publisher = AmqpPublisher.new
      @consumers = []
      @state = :idle
      @state_mutex = Mutex.new
      @heartbeat_mutex = Mutex.new
      @heartbeat_changed = ConditionVariable.new
      @heartbeat_requested = false
      @heartbeat_stopped = false
      @last_immediate = 0.0
      @stop_thread = nil
    end

    def logger
      options.logger
    end

    def worker_id
      options.worker_id
    end

    def instance_id
      options.instance_id
    end

    def state
      @state_mutex.synchronize { @state }
    end

    def running_jobs
      @processor ? @processor.running : 0
    end

    # Registers a job. Either a name and a block (or any object that responds to call):
    #
    #   worker.job("SendEmailJob", max_retries: 5) { |ctx| ... }
    #
    # or a Milvaion::Job subclass, optionally with options that override its job_options:
    #
    #   worker.job(SendEmailJob)
    #
    # Names are unique within a worker and matched case-insensitively.
    def job(name_or_class, handler = nil, **options, &block)
      if name_or_class.is_a?(Class)
        klass = name_or_class
        raise ArgumentError, "#{klass} must define perform(ctx)" unless klass.method_defined?(:perform)

        name = klass.respond_to?(:job_name) ? klass.job_name : klass.name.to_s.split("::").last
        defaults = klass.respond_to?(:job_options) ? klass.job_options : {}

        return add_job(name, klass, JobOptions.new(**defaults, **options))
      end

      callable = handler || block
      raise ArgumentError, "Pass a block or a handler that responds to call." unless callable.respond_to?(:call)

      add_job(name_or_class, callable, JobOptions.new(**options))
    end

    # Registers a handler (anything that responds to call(ctx), or a Job class) under the name the
    # dashboard dispatches the job by (JobNameInWorker).
    def add_job(name, handler, job_options = JobOptions.new)
      raise Error, "Jobs must be added before the worker starts." unless state == :idle

      name = name.to_s.strip
      raise ArgumentError, "Job name is required." if name.empty?

      if @jobs.key?(name.downcase)
        raise ArgumentError, "Job #{name.inspect} is already registered. Job names must be unique within a worker (case-insensitive)."
      end

      @jobs[name.downcase] = Milvaion.resolve_job(name, handler, job_options, options)
      self
    end

    # Starts the worker and blocks until SIGINT or SIGTERM, then shuts down gracefully.
    def run
      reader, writer = IO.pipe
      previous = {}

      %w[INT TERM].each do |signal|
        previous[signal] = Signal.trap(signal) do
          writer.write_nonblock(".")
        rescue StandardError
          nil
        end
      end

      begin
        start
        reader.read(1)
        logger.info("Shutdown signal received.")
      ensure
        stop
        previous.each { |signal, handler| Signal.trap(signal, handler || "DEFAULT") }
        reader.close
        writer.close
      end
    end

    # Connects, registers this instance and starts consuming. Raises if RabbitMQ or Redis cannot be
    # reached within startup_timeout.
    def start
      @state_mutex.synchronize do
        raise Error, "Worker cannot be started from state #{@state}." unless @state == :idle
        raise Error, "Add at least one job before starting the worker." if @jobs.empty?

        @state = :starting
      end

      logger.info("Starting worker #{worker_id} (instance #{instance_id}) with #{@jobs.size} job type(s).")

      @logs = LogBuffer.new(->(batch) { send_logs(batch) }, logger).start
      @processor = Processor.new(options, @jobs, @publisher, @logs, @cancellations)
      @processor.on_count_changed = -> { request_immediate_heartbeat }
      @pool = ThreadPool.new(options.max_parallel_jobs, logger).start

      begin
        if options.redis_url
          @listener = CancellationListener.new(options.redis_url, options.cancellation_channel, @cancellations, logger)
          @listener.start(options.startup_timeout)
        else
          logger.warn("Redis is disabled: jobs cannot be cancelled from the dashboard.")
        end

        connect
      rescue Exception # rubocop:disable Lint/RescueException -- clean up on any failure, then re-raise
        set_state(:stopped)
        close
        raise
      end

      @heartbeat_thread = Thread.new do
        Thread.current.name = "milvaion-heartbeat"
        heartbeat_loop
      end

      set_state(:running)
      self
    end

    # Stops taking new jobs, lets running ones finish for up to shutdown_timeout, returns the rest to
    # the queue and disconnects. Safe to call more than once.
    def stop
      thread = @state_mutex.synchronize do
        return if %i[idle stopped].include?(@state)

        @stop_thread ||= Thread.new { shutdown }
      end

      thread.join
      nil
    end

    # The registration message. Public for tests.
    def build_registration
      jobs = @jobs.values
      schema = ->(value) { value.is_a?(String) ? value : JSON.generate(value) }

      metadata = {
        "IsExternal" => false,
        "ExternalScheduler" => nil,
        "ProcessorCount" => Etc.nprocessors,
        "OSVersion" => os_version,
        "RuntimeVersion" => "Ruby #{RUBY_VERSION}",
        "HeartbeatInterval" => options.heartbeat_interval.to_i,
        "Sdk" => "ruby",
        "SdkVersion" => VERSION,
        "JobConfigs" => jobs.map do |j|
          { "JobType" => j.name, "ConsumerId" => j.consumer_id, "MaxParallelJobs" => j.max_parallel_jobs, "ExecutionTimeoutSeconds" => j.execution_timeout.to_i }
        end
      }

      {
        "workerId" => worker_id,
        "instanceId" => instance_id,
        "displayName" => "#{worker_id} (#{instance_id})",
        "hostName" => Socket.gethostname,
        "ipAddress" => ResourceSampler.local_ip_address,
        "routingPatterns" => jobs.to_h { |j| [j.name, j.routing_pattern] },
        "jobDataDefinitions" => jobs.reject { |j| j.data_schema.nil? }.to_h { |j| [j.name, schema.call(j.data_schema)] },
        "jobResultDefinitions" => jobs.reject { |j| j.result_schema.nil? }.to_h { |j| [j.name, schema.call(j.result_schema)] },
        "jobTypes" => jobs.map(&:name),
        "maxParallelJobs" => options.max_parallel_jobs,
        "version" => options.version,
        # The API reads this case-sensitively, so its properties are PascalCase.
        "metadata" => JSON.generate(metadata)
      }
    end

    private

    def set_state(value)
      @state_mutex.synchronize { @state = value }
    end

    def monotonic
      Process.clock_gettime(Process::CLOCK_MONOTONIC)
    end

    def os_version
      uname = Etc.uname
      "#{uname[:sysname]} #{uname[:release]}"
    rescue StandardError
      RbConfig::CONFIG["host_os"]
    end

    def connect
      require "bunny"

      deadline = monotonic + options.startup_timeout

      begin
        connection = Bunny.new(
          options.rabbitmq_url,
          heartbeat: 60,
          connection_timeout: 10,
          automatically_recover: true,
          network_recovery_interval: 2,
          recovery_attempts: nil,
          recover_from_connection_close: true,
          connection_name: instance_id,
          logger: logger
        )
        connection.start
      rescue StandardError => e
        raise ConnectionError, "Could not connect to RabbitMQ within #{options.startup_timeout}s: #{e.message}" if monotonic + 2 > deadline

        logger.warn("Could not connect to RabbitMQ: #{e.message}")
        Kernel.sleep(2)
        retry
      end

      @connection = connection
      # Bunny restores channels, queues, bindings and consumers itself; the API also needs a fresh registration.
      connection.after_recovery_completed { Thread.new { on_reconnect } }

      setup(connection)
      logger.info("Connected to RabbitMQ.")
    end

    def setup(connection)
      queue_args = options.queue_type == "quorum" ? { "x-queue-type" => "quorum" } : {}
      channel = connection.create_channel
      channel.confirm_select

      [Protocol::QUEUE_STATUS_UPDATES, Protocol::QUEUE_WORKER_LOGS, Protocol::QUEUE_WORKER_HEARTBEAT, Protocol::QUEUE_WORKER_REGISTRATION].each do |queue|
        channel.queue(queue, durable: true, arguments: queue_args)
      end

      channel.topic(Protocol::EXCHANGE_JOBS, durable: true)
      dead_letter = channel.direct(Protocol::EXCHANGE_DEAD_LETTER, durable: true)
      channel.queue(Protocol::QUEUE_FAILED_OCCURRENCES, durable: true, arguments: queue_args)
             .bind(dead_letter, routing_key: Protocol::DEAD_LETTER_ROUTING_KEY)

      # Before consuming, so the first delivery can already report its status.
      @publisher.attach(channel, { Protocol::EXCHANGE_DEAD_LETTER => dead_letter })

      @jobs.each_value do |job|
        consumer_channel = connection.create_channel(nil, 1)
        consumer_channel.prefetch(job.max_parallel_jobs)
        settle_lock = Mutex.new

        arguments = { "x-dead-letter-exchange" => Protocol::EXCHANGE_DEAD_LETTER, "x-dead-letter-routing-key" => Protocol::DEAD_LETTER_ROUTING_KEY }.merge(queue_args)
        queue = consumer_channel.queue(job.queue, durable: true, arguments: arguments)
        queue.bind(Protocol::EXCHANGE_JOBS, routing_key: job.routing_pattern)

        consumer = queue.subscribe(manual_ack: true, block: false, consumer_tag: "#{instance_id}.#{job.consumer_id}") do |info, properties, body|
          on_delivery(job, consumer_channel, settle_lock, info, properties, body)
        end

        @consumers << consumer
        logger.info("Consuming #{job.name} from #{job.queue} (pattern #{job.routing_pattern}, prefetch #{job.max_parallel_jobs}).")
      end

      register
    end

    def on_delivery(job, channel, settle_lock, info, properties, body)
      tag = info.delivery_tag

      delivery = Delivery.new(
        body: body,
        headers: properties.headers || {},
        correlation_id: properties.correlation_id,
        redelivered: info.redelivered? ? true : false,
        ack: -> { settle_lock.synchronize { channel.ack(tag) } },
        nack: ->(requeue) { settle_lock.synchronize { channel.nack(tag, false, requeue) } }
      )

      return if @pool.post(delivery) { |item| @processor.handle(item, job) }

      # The worker is stopping: give the message back.
      begin
        delivery.nack.call(true)
      rescue StandardError => e
        logger.debug("Could not return a message to the queue: #{e.message}")
      end
    end

    def on_reconnect
      logger.info("Reconnected to RabbitMQ.")
      register
    rescue StandardError => e
      logger.warn("Re-registering after a reconnect failed: #{e.message}")
    end

    def register
      @publisher.publish("", Protocol::QUEUE_WORKER_REGISTRATION, JSON.generate(build_registration), persistent: true)
      send_heartbeat(false)
      logger.info("Registered #{worker_id} (instance #{instance_id}).")
    end

    def heartbeat_loop
      interval = options.heartbeat_interval.to_f
      next_regular = monotonic + interval

      loop do
        @heartbeat_mutex.synchronize do
          loop do
            return if @heartbeat_stopped

            now = monotonic

            if @heartbeat_requested && now >= @last_immediate + 1
              @heartbeat_requested = false
              @last_immediate = now
              break
            end

            if now >= next_regular
              next_regular = now + interval
              break
            end

            wake = [next_regular, (@last_immediate + 1 if @heartbeat_requested)].compact.min
            @heartbeat_changed.wait(@heartbeat_mutex, wake - now)
          end
        end

        send_heartbeat(false)
      end
    end

    # Reports a changed job count at most once a second; the last change is always sent.
    def request_immediate_heartbeat
      return unless %i[running stopping].include?(state)

      @heartbeat_mutex.synchronize do
        @heartbeat_requested = true
        @heartbeat_changed.signal
      end
    end

    def send_heartbeat(stopping)
      body = JSON.generate({
                             "workerId" => worker_id,
                             "instanceId" => instance_id,
                             "currentJobs" => stopping ? 0 : running_jobs,
                             "memoryBytes" => @sampler.memory_bytes,
                             "cpuUsagePercent" => @sampler.cpu_usage_percent,
                             "timestamp" => Milvaion.utc_now,
                             "isStopping" => stopping
                           })

      @publisher.publish("", Protocol::QUEUE_WORKER_HEARTBEAT, body, persistent: false)
    rescue StandardError => e
      logger.debug("Heartbeat failed; will retry on the next interval: #{e.message}")
    end

    def send_logs(batch)
      @publisher.publish("", Protocol::QUEUE_WORKER_LOGS, JSON.generate(batch), persistent: true)
    end

    def shutdown
      set_state(:stopping)
      logger.info("Stopping worker instance #{instance_id}...")

      @consumers.each do |consumer|
        consumer.cancel
      rescue StandardError
        nil
      end
      @consumers.clear

      # Deliveries waiting for a pool thread have not started: give them straight back.
      @pool.close.each do |delivery|
        delivery.nack.call(true)
      rescue StandardError
        nil
      end

      @processor.begin_shutdown

      unless @processor.wait_for_idle(options.shutdown_timeout)
        logger.warn("#{@processor.running} job(s) still running after #{options.shutdown_timeout}s. Returning them to the queue.")
        @cancellations.abort_all(AbortReason::SHUTDOWN, "Worker is shutting down.")
        @processor.wait_for_idle(10)
      end

      @heartbeat_mutex.synchronize do
        @heartbeat_stopped = true
        @heartbeat_changed.broadcast
      end
      @heartbeat_thread&.join(5)

      send_heartbeat(true)

      begin
        @logs.flush
      rescue StandardError => e
        logger.warn("Some job logs could not be sent before shutdown: #{e.message}")
      end

      close
      set_state(:stopped)
      logger.info("Worker instance #{instance_id} stopped.")
    end

    def close
      @listener&.stop
      @logs&.close
      @publisher.detach
      @pool&.close
      @pool&.join(5)

      begin
        @connection&.close
      rescue StandardError
        nil
      end
    end
  end

  # Publishes on the confirm channel and waits for the broker's confirmation. Thread-safe.
  class AmqpPublisher
    def initialize
      @mutex = Mutex.new
      @channel = nil
      @exchanges = {}
    end

    def attach(channel, exchanges)
      @mutex.synchronize do
        @channel = channel
        @exchanges = exchanges
      end
    end

    def detach
      @mutex.synchronize do
        @channel = nil
        @exchanges = {}
      end
    end

    def publish(exchange, routing_key, body, persistent: true, correlation_id: nil, headers: nil)
      @mutex.synchronize do
        channel = @channel
        raise ConnectionError, "Not connected to RabbitMQ." if channel.nil? || !channel.open?

        target = exchange.empty? ? channel.default_exchange : @exchanges.fetch(exchange) { channel.exchange(exchange, no_declare: true) }
        properties = { routing_key: routing_key, persistent: persistent, content_type: "application/json" }
        properties[:correlation_id] = correlation_id if correlation_id
        properties[:headers] = headers if headers

        target.publish(body, **properties)
        raise ConnectionError, "RabbitMQ did not confirm the message to #{routing_key}." unless channel.wait_for_confirms
      end
    end
  end
end
