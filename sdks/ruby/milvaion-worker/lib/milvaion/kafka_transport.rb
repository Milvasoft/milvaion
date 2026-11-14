# frozen_string_literal: true

require "json"

module Milvaion
  # Runs a worker on Kafka (docs/githubdocs/KAFKA-TRANSPORT.md). One consumer group member reads the
  # worker's job and retry topics; records are buffered per partition, dispatched up to
  # max_parallel_jobs (and max_in_flight_per_partition per partition) and committed in order once
  # their outcome has been reported. It is also the publisher the processor reports through.
  #
  # Uses the rdkafka gem, which is not a dependency of milvaion-worker: it is loaded only when the
  # transport is "kafka".
  class KafkaTransport
    COMMIT_INTERVAL = 0.5
    POLL_TIMEOUT_MS = 100
    PRODUCE_TIMEOUT = 30
    RELIABLE_PRODUCE_DELAYS = [0, 1, 2, 5].freeze

    # Raised when a record is produced while the transport is not connected.
    class NotConnectedError < ConnectionError; end

    # What this instance holds of one assigned partition.
    PartitionState = Struct.new(:topic, :partition, :retry, :tracker, :backlog, :in_flight, :want_paused, :paused, :held, :committed,
                                keyword_init: true) do
      def key
        [topic, partition]
      end
    end

    # A record taken from the consumer, with its headers decoded.
    Record = Struct.new(:message, :headers)
    # The head of a retry partition that is not due yet.
    Held = Struct.new(:offset, :stop)
    # What the processor knows of the "queue" a record came from.
    Source = Struct.new(:queue)

    # Calls back into the transport from rdkafka's rebalance callback (on the polling thread).
    class RebalanceListener
      def initialize(transport)
        @transport = transport
      end

      # Older rdkafka versions pass the consumer first.
      def on_partitions_assigned(*args)
        @transport.on_assigned(args.last)
      end

      def on_partitions_revoked(*args)
        @transport.on_revoked(args.last)
      end
    end

    def self.load_rdkafka!
      return if defined?(::Rdkafka::Config)

      require "rdkafka"
    rescue LoadError => e
      raise Error, "The Kafka transport needs the rdkafka gem. Add `gem \"rdkafka\"` to your Gemfile (#{e.message})."
    end

    # Waits for an rdkafka handle (delivery, admin operation). rdkafka renamed the timeout from
    # seconds to milliseconds; both are supported.
    def self.wait_for(handle, seconds)
      if handle.method(:wait).parameters.any? { |_, name| name == :max_wait_timeout_ms }
        handle.wait(max_wait_timeout_ms: (seconds * 1000).to_i)
      else
        handle.wait(max_wait_timeout: seconds)
      end
    end

    attr_reader :topics

    # register publishes this instance's registration (and a first heartbeat); it raises on failure.
    def initialize(options, register)
      self.class.load_rdkafka!

      @options = options
      @settings = options.kafka
      @register = register
      @logger = options.logger
      @topics = Kafka.topics(@settings.topic_prefix, options.worker_id)

      @mutex = Mutex.new
      @assigned = {}
      @parts = {}
      @running = 0
      @cursor = 0
      @stopping = false
      @poll_stop = false
      @heartbeat_failed = false
      @heartbeat_mutex = Mutex.new
      @producer = nil
      @consumer = nil
    end

    # Connects, creates the topics, registers and starts consuming. Gives up after startup_timeout.
    def start(processor, pool)
      @processor = processor
      @pool = pool
      deadline = monotonic + @options.startup_timeout.to_f

      # Idempotent, so the client's own retries do not write a record twice.
      @producer = Rdkafka::Config.new(client_config.merge("message.timeout.ms": PRODUCE_TIMEOUT * 1000, "enable.idempotence": true)).producer

      create_topics(deadline) if @settings.auto_create_topics
      with_startup_retry(deadline, "register with the API") { @register.call }

      config = Rdkafka::Config.new(client_config.merge(
                                     "group.id": topics.group,
                                     "enable.auto.commit": false,
                                     "enable.auto.offset.store": false,
                                     "auto.offset.reset": "earliest",
                                     "partition.assignment.strategy": "cooperative-sticky"
                                   ))
      config.consumer_rebalance_listener = RebalanceListener.new(self)
      @consumer = config.consumer
      @consumer.subscribe(topics.jobs, topics.retry)

      @poll_thread = Thread.new do
        Thread.current.name = "milvaion-kafka-poll"
        poll_loop
      end

      @logger.info("Consuming #{topics.jobs} and #{topics.retry} in group #{topics.group} (max parallel jobs #{@options.max_parallel_jobs}, " \
                   "max in flight per partition #{@settings.max_in_flight_per_partition}).")
      self
    end

    # Maps the processor's RabbitMQ-named destinations to the Kafka topics and waits for the broker's
    # acknowledgement.
    def publish(exchange, routing_key, body, persistent: true, correlation_id: nil, headers: nil, key: nil) # rubocop:disable Lint/UnusedMethodArgument
      topic = topic_for(exchange, routing_key)
      record_headers = (headers || {}).dup

      if correlation_id
        record_headers[Kafka::HEADER_OCCURRENCE_ID] = correlation_id
        record_headers[Kafka::HEADER_CORRELATION_ID] = correlation_id
      end

      record_key = key || correlation_id
      heartbeat = exchange.empty? && routing_key == Protocol::QUEUE_WORKER_HEARTBEAT

      begin
        produce(topic: topic, payload: body, key: record_key, headers: Kafka.encode_headers(record_headers))
      rescue StandardError => e
        after_heartbeat(e) if heartbeat
        raise
      end

      after_heartbeat(nil) if heartbeat
    end

    # The processor publishes through the transport; there is nothing to detach.
    def detach; end

    # Stops taking records. Running jobs carry on and can still report their outcome; finished
    # records keep being committed.
    def stop_consuming
      @mutex.synchronize do
        @stopping = true
        @parts.each_value { |ps| release_hold(ps) }
      end
    end

    # Runs once running jobs have finished or have been written back: commits and leaves the group.
    def drained
      stop_polling
      consumer = @consumer
      return if consumer.nil?

      commit
      close_consumer
    end

    def close
      stop_polling
      close_consumer

      producer = @producer
      @producer = nil
      return if producer.nil?

      begin
        producer.flush(5_000)
      rescue StandardError
        nil
      end

      begin
        producer.close
      rescue StandardError => e
        @logger.debug("Closing the Kafka producer failed: #{e.message}")
      end
    end

    # Rebalance callbacks, called on the polling thread.

    def on_assigned(list)
      pairs = partition_pairs(list)
      stopping = @mutex.synchronize do
        pairs.each { |pair| @assigned[pair] = true }
        @stopping
      end

      return if pairs.empty?

      # A partition that was paused when this instance had it before must flow again.
      stopping ? @consumer.pause(partition_list(pairs)) : @consumer.resume(partition_list(pairs))
      @logger.info("Kafka partitions assigned: #{format_pairs(pairs)}")
    rescue StandardError => e
      @logger.warn("Handling assigned Kafka partitions failed: #{e.message}")
    end

    # Commits what has finished on the revoked partitions and forgets them. Jobs still running on
    # them finish and report, but their offsets are left to the new owner.
    def on_revoked(list)
      pairs = partition_pairs(list)
      commit(pairs)

      @mutex.synchronize do
        pairs.each do |pair|
          @assigned.delete(pair)
          ps = @parts.delete(pair)
          release_hold(ps) if ps
        end
      end

      @logger.info("Kafka partitions revoked: #{format_pairs(pairs)}") unless pairs.empty?
    rescue StandardError => e
      @logger.warn("Handling revoked Kafka partitions failed: #{e.message}")
    end

    # Starts buffered records while slots are free. Safe to call from any thread.
    def dispatch
      @mutex.synchronize { dispatch_locked }
    end

    private

    def client_config
      @settings.client_config(@options.instance_id)
    end

    def monotonic
      Process.clock_gettime(Process::CLOCK_MONOTONIC)
    end

    def topic_for(exchange, routing_key)
      return topics.dlq if exchange == Protocol::EXCHANGE_DEAD_LETTER

      if exchange.empty?
        case routing_key
        when Protocol::QUEUE_STATUS_UPDATES then return topics.status
        when Protocol::QUEUE_WORKER_LOGS then return topics.logs
        when Protocol::QUEUE_WORKER_HEARTBEAT then return topics.heartbeats
        when Protocol::QUEUE_WORKER_REGISTRATION then return topics.registrations
        end
      end

      raise Error, "No Kafka topic for exchange #{exchange.inspect} and routing key #{routing_key.inspect}"
    end

    def with_startup_retry(deadline, what)
      yield
    rescue StandardError => e
      raise ConnectionError, "Could not #{what} on Kafka (#{@settings.brokers.join(',')}) within #{@options.startup_timeout}s: #{e.message}" if monotonic + 1 > deadline

      @logger.warn("Could not #{what} on Kafka: #{e.message}")
      Kernel.sleep(1)
      retry
    end

    # Creates the worker's job and retry topics and the shared topics it writes to. Topics that
    # exist already are left as they are.
    def create_topics(deadline)
      admin = Rdkafka::Config.new(client_config).admin

      begin
        with_startup_retry(deadline, "create the topics") do
          names = [topics.jobs, topics.retry, topics.status, topics.logs, topics.heartbeats, topics.registrations, topics.dlq]
          handles = names.map { |name| [name, admin.create_topic(name, @settings.num_partitions, @settings.replication_factor)] }

          handles.each do |name, handle|
            wait_for(handle, [[deadline - monotonic, PRODUCE_TIMEOUT].min, 1].max)
          rescue Rdkafka::RdkafkaError => e
            raise unless e.code == :topic_already_exists

            @logger.debug("Kafka topic #{name} exists.")
          end
        end
      ensure
        admin.close
      end
    end

    def wait_for(handle, seconds)
      self.class.wait_for(handle, seconds)
    end

    def produce(topic:, payload:, key:, headers:, timeout: PRODUCE_TIMEOUT)
      producer = @producer
      raise NotConnectedError, "Not connected to Kafka." if producer.nil?

      handle = producer.produce(topic: topic, payload: payload, key: key, headers: headers.nil? || headers.empty? ? nil : headers)
      wait_for(handle, timeout)
    rescue Rdkafka::ClosedProducerError
      raise NotConnectedError, "Not connected to Kafka."
    end

    # Retries a produce whose failure would otherwise leave the record's offset stuck.
    def produce_reliably(**record)
      last = nil

      RELIABLE_PRODUCE_DELAYS.each do |delay|
        Kernel.sleep(delay) if delay.positive?

        begin
          return produce(**record)
        rescue NotConnectedError
          raise
        rescue StandardError => e
          last = e
        end
      end

      raise last
    end

    # Registers again once heartbeats get through after failing, the Kafka counterpart of
    # registering after a RabbitMQ reconnect: the API may have dropped the instance meanwhile.
    def after_heartbeat(error)
      if error
        @heartbeat_mutex.synchronize { @heartbeat_failed = true } unless error.is_a?(NotConnectedError)
        return
      end

      failed = @heartbeat_mutex.synchronize do
        was = @heartbeat_failed
        @heartbeat_failed = false
        was
      end

      return unless failed

      Thread.new do
        @register.call
      rescue StandardError => e
        @logger.warn("Could not register again after Kafka recovered: #{e.message}")
        @heartbeat_mutex.synchronize { @heartbeat_failed = true }
      end
    end

    def poll_loop
      last_commit = monotonic
      paused_for_stop = false

      until @poll_stop
        begin
          message = @consumer.poll(POLL_TIMEOUT_MS)
          accept(message) if message
        rescue Rdkafka::RdkafkaError => e
          @logger.warn("Kafka poll failed: #{e.message}")
          Kernel.sleep(1) unless @poll_stop
        end

        begin
          if @mutex.synchronize { @stopping }
            unless paused_for_stop
              paused_for_stop = true
              pause_everything
            end
          else
            dispatch
            apply_pauses
          end

          if monotonic - last_commit >= COMMIT_INTERVAL
            commit
            last_commit = monotonic
          end
        rescue StandardError => e
          @logger.error("Kafka consumer loop error: #{e.class}: #{e.message}")
          Kernel.sleep(0.1)
        end
      end
    end

    def stop_polling
      @poll_stop = true
      thread = @poll_thread
      @poll_thread = nil
      thread&.join unless thread == Thread.current
    end

    def close_consumer
      consumer = @consumer
      return if consumer.nil?

      begin
        consumer.close
      rescue StandardError => e
        @logger.debug("Closing the Kafka consumer failed: #{e.message}")
      ensure
        @mutex.synchronize { @consumer = nil }
      end
    end

    # Buffers a polled record in its partition.
    def accept(message)
      @mutex.synchronize do
        return if @stopping

        pair = [message.topic, message.partition]
        # A record of a partition revoked since; its new owner reads it.
        return unless @assigned[pair]

        ps = @parts[pair] ||= PartitionState.new(topic: message.topic, partition: message.partition, retry: message.topic == topics.retry,
                                                 tracker: Kafka::OffsetTracker.new, backlog: [], in_flight: 0, want_paused: false,
                                                 paused: false, held: nil, committed: -1)

        ps.backlog << Record.new(message, Kafka.decode_headers(message.headers)) if ps.tracker.add(message.offset)
      end
    end

    # Starts buffered records while instance and partition slots are free and holds retry records
    # that are not due. A partition with records waiting here is to be paused, so the consumer keeps
    # polling (and stays in the group) without buffering more.
    def dispatch_locked
      return if @stopping || @consumer.nil?

      states = @parts.values.sort_by(&:key)
      return if states.empty?

      now = Time.now
      start = @cursor % states.size
      @cursor += 1

      states.size.times do |i|
        ps = states[(start + i) % states.size]

        while @running < @options.max_parallel_jobs && ps.in_flight < @settings.max_in_flight_per_partition && !ps.backlog.empty?
          record = ps.backlog.first

          if ps.retry
            due = Kafka.not_before(record.headers)

            if due && due > now
              hold(ps, record, due)
              break
            end
          end

          release_hold(ps)
          ps.backlog.shift
          ps.in_flight += 1
          @running += 1

          next if post(ps, record)

          # The pool is closed: the worker is stopping and the record is left to the next owner.
          ps.in_flight -= 1
          @running -= 1
          break
        end

        ps.want_paused = !ps.backlog.empty?
      end
    end

    def post(ps, record)
      delivery = build_delivery(ps, record)
      source = Source.new(record.message.topic)

      @pool.post(delivery) do |item|
        @processor.handle(item, source)
      ensure
        release(ps)
      end
    end

    def build_delivery(ps, record)
      message = record.message

      Delivery.new(
        body: message.payload.to_s,
        headers: record.headers,
        correlation_id: nil,
        redelivered: false,
        ack: -> { mark_done(ps, message.offset) },
        nack: ->(requeue) { requeue ? write_back(ps, record) : dead_letter_raw(ps, record, "rejected by the worker") },
        reject: ->(reason) { dead_letter_raw(ps, record, reason) },
        schedule_retry: ->(request) { produce_retry(record, request) }
      )
    end

    # Frees the instance and partition slot of a finished record.
    def release(ps)
      @mutex.synchronize do
        ps.in_flight -= 1
        @running -= 1
        dispatch_locked
      end
    end

    # Lets the record's offset be committed. A partition revoked meanwhile is ignored: its new owner
    # runs the record again.
    def mark_done(ps, offset)
      @mutex.synchronize do
        ps.tracker.done(offset) if @parts[ps.key].equal?(ps)
      end
    end

    # Keeps the head of a retry partition until it is due, sending the occurrence's job heartbeats
    # meanwhile. The polling loop dispatches it once it is due.
    def hold(ps, record, due)
      return if ps.held && ps.held.offset == record.message.offset

      release_hold(ps)
      stop = AbortSignal.new
      ps.held = Held.new(record.message.offset, stop)

      job = Processor.parse_job(record.message.payload)
      occurrence_id = Processor.parse_occurrence_id(record.headers, nil)
      return if job.nil? || occurrence_id.nil?

      run = JobRun.new(@processor, job, occurrence_id, Processor.parse_retry_count(record.headers))

      Thread.new do
        Thread.current.name = "milvaion-kafka-hold"

        unless stop.aborted?
          run.status(Status::RUNNING, reliable: false)
          @processor.heartbeat_while_waiting(run, due, stop)
        end
      end
    end

    def release_hold(ps)
      held = ps.held
      return if held.nil?

      ps.held = nil
      held.stop.trigger(AbortReason::SHUTDOWN, "released")
    end

    # Writes an interrupted job back to its own topic, the counterpart of a RabbitMQ requeue.
    def write_back(ps, record)
      message = record.message
      produce_reliably(topic: message.topic, payload: message.payload, key: message.key, headers: Kafka.encode_headers(record.headers))
      mark_done(ps, message.offset)
    end

    # Forwards a record the worker cannot process to the dead letter topic unchanged.
    def dead_letter_raw(ps, record, reason)
      message = record.message
      headers = record.headers.merge(Kafka::HEADER_ORIGINAL_TOPIC => message.topic, Kafka::HEADER_DLQ_REASON => reason.to_s)
      key = Processor.parse_occurrence_id(record.headers, nil) || message.key

      produce_reliably(topic: topics.dlq, payload: message.payload, key: key, headers: Kafka.encode_headers(headers))
      mark_done(ps, message.offset)
    end

    # Writes the next attempt to the retry topic, due at request.not_before.
    def produce_retry(record, request)
      headers = record.headers.merge(
        Kafka::HEADER_OCCURRENCE_ID => request.occurrence_id,
        Kafka::HEADER_CORRELATION_ID => request.occurrence_id,
        Kafka::HEADER_RETRY_COUNT => request.retry_count,
        Kafka::HEADER_MAX_RETRIES => request.max_retries,
        Kafka::HEADER_NOT_BEFORE => Kafka.unix_ms(request.not_before)
      )
      key = request.job_id.to_s.empty? ? record.message.key : request.job_id

      produce(topic: topics.retry, payload: record.message.payload, key: key, headers: Kafka.encode_headers(headers))
    end

    # Applies the pause state dispatch decided. On the polling thread only.
    def apply_pauses
      pause = []
      resume = []

      @mutex.synchronize do
        @parts.each_value do |ps|
          next if ps.want_paused == ps.paused

          ps.paused = ps.want_paused
          (ps.paused ? pause : resume) << ps.key
        end
      end

      @consumer.pause(partition_list(pause)) unless pause.empty?
      @consumer.resume(partition_list(resume)) unless resume.empty?
    rescue Rdkafka::RdkafkaError => e
      @logger.debug("Pausing or resuming Kafka partitions failed: #{e.message}")
    end

    def pause_everything
      pairs = @mutex.synchronize { @assigned.keys }
      @consumer.pause(partition_list(pairs)) unless pairs.empty?
    rescue Rdkafka::RdkafkaError => e
      @logger.debug("Pausing the Kafka partitions failed: #{e.message}")
    end

    # Commits the finished position of every partition (or only of the given [topic, partition]
    # pairs) that moved.
    def commit(only = nil)
      offsets = @mutex.synchronize do
        @parts.each_value.filter_map do |ps|
          next if only && !only.include?(ps.key)

          position = ps.tracker.commit_position
          [ps, position] if position && position > ps.committed
        end
      end

      consumer = @consumer
      return if offsets.empty? || consumer.nil?

      list = Rdkafka::Consumer::TopicPartitionList.new
      offsets.group_by { |ps, _| ps.topic }.each do |topic, entries|
        list.add_topic_and_partitions_with_offsets(topic, entries.to_h { |ps, position| [ps.partition, position] })
      end

      consumer.commit(list, false)

      @mutex.synchronize do
        offsets.each { |ps, position| ps.committed = [ps.committed, position].max }
      end
    rescue Rdkafka::RdkafkaError, Rdkafka::ClosedConsumerError => e
      @logger.warn("Kafka offset commit failed; will retry: #{e.message}")
    end

    def partition_pairs(list)
      list.to_h.flat_map do |topic, partitions|
        Array(partitions).map { |p| [topic.to_s, p.respond_to?(:partition) ? p.partition : p] }
      end
    end

    def partition_list(pairs)
      list = Rdkafka::Consumer::TopicPartitionList.new
      pairs.group_by(&:first).each { |topic, entries| list.add_topic(topic, entries.map(&:last)) }
      list
    end

    def format_pairs(pairs)
      pairs.map { |topic, partition| "#{topic}[#{partition}]" }.join(", ")
    end
  end
end
