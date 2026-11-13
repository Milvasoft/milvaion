# frozen_string_literal: true

require "etc"
require "logger"
require "securerandom"

module Milvaion
  # Worker configuration. Only worker_id is required.
  class WorkerOptions
    DEFAULTS = {
      rabbitmq_url: "amqp://guest:guest@localhost:5672/",
      queue_type: "classic",
      redis_url: "redis://localhost:6379",
      redis_key_prefix: Protocol::DEFAULT_KEY_PREFIX,
      max_parallel_jobs: nil,
      execution_timeout: 3600,
      heartbeat_interval: 30,
      job_heartbeat_interval: 60,
      shutdown_timeout: 30,
      startup_timeout: 30,
      version: "1.0.0",
      instance_id: nil,
      logger: nil
    }.freeze

    # worker_id:              identifies the worker application. Use the same value on every replica.
    # rabbitmq_url:           AMQP URL of the broker the API uses.
    # queue_type:             "classic" or "quorum". Must match the API's MilvaionConfig:RabbitMQ:QueueType.
    # redis_url:              Redis only carries cancellation requests. nil disables them.
    # redis_key_prefix:       must match the API's MilvaionConfig:Redis:KeyPrefix.
    # max_parallel_jobs:      size of the job thread pool, reported as this instance's capacity.
    # execution_timeout:      default job timeout in seconds. nil or 0 disables it.
    # heartbeat_interval:     seconds between instance heartbeats.
    # job_heartbeat_interval: seconds between Running updates of a long job, so it is not flagged as a zombie. 0 disables it.
    # shutdown_timeout:       how long stop lets running jobs finish before returning them to the queue.
    # startup_timeout:        how long start waits for RabbitMQ and Redis.
    # version:                shown in the dashboard.
    # instance_id:            overrides the generated "{worker_id}-{8 hex}" id. Must be unique per process.
    # logger:                 a Logger. Defaults to one that writes to $stdout at INFO.
    attr_reader :worker_id, :rabbitmq_url, :queue_type, :redis_url, :redis_key_prefix, :max_parallel_jobs,
                :execution_timeout, :heartbeat_interval, :job_heartbeat_interval, :shutdown_timeout,
                :startup_timeout, :version, :instance_id, :logger

    def initialize(worker_id:, **options)
      unknown = options.keys - DEFAULTS.keys
      raise ArgumentError, "Unknown worker option(s): #{unknown.join(', ')}" unless unknown.empty?

      values = DEFAULTS.merge(options)

      @worker_id = worker_id.to_s.strip
      raise ArgumentError, "worker_id is required" if @worker_id.empty?

      @rabbitmq_url = values[:rabbitmq_url]
      @queue_type = (values[:queue_type] || "classic").to_s.strip.downcase
      raise ArgumentError, %(queue_type must be "classic" or "quorum", got #{values[:queue_type].inspect}) unless %w[classic quorum].include?(@queue_type)

      @redis_url = values[:redis_url]
      @redis_key_prefix = values[:redis_key_prefix]
      @max_parallel_jobs = Integer(values[:max_parallel_jobs] || Etc.nprocessors * 2)
      raise ArgumentError, "max_parallel_jobs must be positive, got #{@max_parallel_jobs}" unless @max_parallel_jobs.positive?

      @execution_timeout = values[:execution_timeout]
      @heartbeat_interval = values[:heartbeat_interval]
      @job_heartbeat_interval = values[:job_heartbeat_interval]
      @shutdown_timeout = values[:shutdown_timeout]
      @startup_timeout = values[:startup_timeout]
      @version = values[:version]
      @instance_id = values[:instance_id].to_s.empty? ? "#{@worker_id}-#{SecureRandom.hex(4)}" : values[:instance_id]
      @logger = values[:logger] || Logger.new($stdout, progname: "milvaion", level: Logger::INFO)
    end

    def cancellation_channel
      "#{redis_key_prefix}cancellation_channel"
    end

    ENV_MAPPING = {
      "MILVAION_WORKER_ID" => [:worker_id, :string],
      "MILVAION_RABBITMQ_URL" => [:rabbitmq_url, :string],
      "MILVAION_RABBITMQ_QUEUE_TYPE" => [:queue_type, :string],
      "MILVAION_REDIS_KEY_PREFIX" => [:redis_key_prefix, :string],
      "MILVAION_MAX_PARALLEL_JOBS" => [:max_parallel_jobs, :integer],
      "MILVAION_EXECUTION_TIMEOUT_SECONDS" => [:execution_timeout, :float],
      "MILVAION_HEARTBEAT_INTERVAL_SECONDS" => [:heartbeat_interval, :float]
    }.freeze

    # Reads MILVAION_WORKER_ID, MILVAION_RABBITMQ_URL, MILVAION_RABBITMQ_QUEUE_TYPE, MILVAION_REDIS_URL
    # ("false" disables Redis), MILVAION_REDIS_KEY_PREFIX, MILVAION_MAX_PARALLEL_JOBS,
    # MILVAION_EXECUTION_TIMEOUT_SECONDS and MILVAION_HEARTBEAT_INTERVAL_SECONDS.
    # Keyword arguments win over the environment.
    def self.from_env(env = ENV, **overrides)
      values = {}

      ENV_MAPPING.each do |key, (name, type)|
        raw = env[key].to_s.strip
        next if raw.empty?

        values[name] =
          case type
          when :integer then Integer(raw, 10)
          when :float then Float(raw)
          else raw
          end
      rescue ArgumentError
        raise ArgumentError, "#{key} must be a number, got #{raw.inspect}"
      end

      redis_url = env["MILVAION_REDIS_URL"].to_s.strip

      if redis_url.casecmp?("false")
        values[:redis_url] = nil
      elsif !redis_url.empty?
        values[:redis_url] = redis_url
      end

      values.merge!(overrides)
      values[:worker_id] ||= ""

      new(**values)
    end
  end

  # Configuration of one job type. Defaults apply to options left out.
  #
  # routing_pattern:   default "{worker_id}.{name without Job}.*".
  # consumer_id:       default "{name without Job}-consumer".
  # max_parallel_jobs: jobs of this type that may run at once on this instance (prefetch). Default: min(10, worker max).
  # execution_timeout: seconds. A timeout set on the job in the dashboard wins. 0 disables it. Default: the worker default.
  # max_retries:       retries after the first failure before the job goes to the dead letter queue. Default 3.
  # base_retry_delay:  retry n waits base_retry_delay * 2^(n-1) seconds. Default 5.
  # data_schema:       JSON Schema of the job data, as a Hash or a JSON string. The dashboard builds the job data form from it.
  # result_schema:     JSON Schema of the result.
  # interrupt_on_abort: raise JobAborted inside the handler thread on cancel or timeout (Thread#raise, like
  #                    the Timeout module, with the same risks). Default false: the handler must check the context.
  class JobOptions
    KEYS = %i[routing_pattern consumer_id max_parallel_jobs execution_timeout max_retries base_retry_delay data_schema result_schema interrupt_on_abort].freeze

    attr_reader(*KEYS)

    def initialize(**options)
      unknown = options.keys - KEYS
      raise ArgumentError, "Unknown job option(s): #{unknown.join(', ')}" unless unknown.empty?

      @routing_pattern = options[:routing_pattern]
      @consumer_id = options[:consumer_id]
      @max_parallel_jobs = options[:max_parallel_jobs]
      @execution_timeout = options[:execution_timeout]
      @max_retries = options.fetch(:max_retries, 3)
      @base_retry_delay = options.fetch(:base_retry_delay, 5)
      @data_schema = options[:data_schema]
      @result_schema = options[:result_schema]
      @interrupt_on_abort = options.fetch(:interrupt_on_abort, false)
    end

    def to_h
      KEYS.to_h { |key| [key, public_send(key)] }
    end
  end

  # A job type with all defaults applied.
  ResolvedJob = Struct.new(:name, :handler, :routing_pattern, :queue, :consumer_id, :max_parallel_jobs,
                           :execution_timeout, :max_retries, :base_retry_delay, :data_schema, :result_schema, :interrupt_on_abort,
                           keyword_init: true)

  # The routing pattern a job gets when it does not set one, identical to the .NET SDK:
  # "SendEmailJob" in worker "Mail-Worker" becomes "mail-worker.sendemail.*".
  def self.generate_routing_pattern(job_name, worker_id)
    "#{worker_id.downcase}.#{trim_job_suffix(job_name).downcase}.*"
  end

  # The queue a job type is consumed from.
  def self.queue_name_for_pattern(pattern)
    "#{Protocol::QUEUE_JOBS}.#{pattern.gsub('*', 'wildcard').gsub('#', 'all')}"
  end

  def self.trim_job_suffix(name)
    name.sub(/job\z/i, "")
  end

  def self.resolve_job(name, handler, options, worker)
    max_parallel = Integer(options.max_parallel_jobs || [10, worker.max_parallel_jobs].min)

    unless max_parallel.between?(1, 65_535)
      raise ArgumentError, "max_parallel_jobs of job #{name.inspect} must be between 1 and 65535, got #{max_parallel}"
    end

    pattern = options.routing_pattern.to_s.strip
    pattern = generate_routing_pattern(name, worker.worker_id) if pattern.empty?
    timeout = options.execution_timeout.nil? ? worker.execution_timeout : options.execution_timeout

    ResolvedJob.new(
      name: name,
      handler: handler,
      routing_pattern: pattern,
      queue: queue_name_for_pattern(pattern),
      consumer_id: options.consumer_id || "#{trim_job_suffix(name).downcase}-consumer",
      max_parallel_jobs: max_parallel,
      execution_timeout: timeout || 0,
      max_retries: [0, Integer(options.max_retries)].max,
      base_retry_delay: [0.0, Float(options.base_retry_delay)].max,
      data_schema: options.data_schema,
      result_schema: options.result_schema,
      interrupt_on_abort: options.interrupt_on_abort ? true : false
    )
  end
end
