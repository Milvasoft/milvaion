# frozen_string_literal: true

module Milvaion
  # Kafka wire contract shared with the Milvaion API and the other SDKs.
  # See docs/githubdocs/KAFKA-TRANSPORT.md in the repository. Nothing in this file needs the
  # rdkafka gem; the transport itself (kafka_transport.rb) loads it only when it is used.
  module Kafka
    HEADER_OCCURRENCE_ID = "OccurrenceId"
    HEADER_CORRELATION_ID = "CorrelationId"
    HEADER_RETRY_COUNT = "x-retry-count"
    HEADER_MAX_RETRIES = "MaxRetries"
    HEADER_NOT_BEFORE = "x-not-before"
    HEADER_ORIGINAL_TOPIC = "x-original-topic"
    HEADER_DLQ_REASON = "x-dlq-reason"

    TOPIC_PREFIX_PATTERN = /\A[A-Za-z0-9._-]*\z/.freeze
    SASL_MECHANISMS = { "plain" => "PLAIN", "scramsha256" => "SCRAM-SHA-256", "scramsha512" => "SCRAM-SHA-512" }.freeze

    # Topic and consumer group names of one worker.
    Topics = Struct.new(:jobs, :retry, :status, :logs, :heartbeats, :registrations, :dlq, :group, keyword_init: true)

    # The worker id as it appears in topic and group names: lower case, with every character
    # outside a-z 0-9 . _ - replaced by "_".
    def self.worker_segment(worker_id)
      worker_id.to_s.each_char.map do |char|
        lower = char.downcase
        lower.match?(/\A[a-z0-9._-]\z/) ? lower : "_"
      end.join
    end

    def self.topics(prefix, worker_id)
      worker = worker_segment(worker_id)

      Topics.new(
        jobs: "#{prefix}milvaion.jobs.#{worker}",
        retry: "#{prefix}milvaion.jobs.#{worker}.retry",
        status: "#{prefix}milvaion.status",
        logs: "#{prefix}milvaion.logs",
        heartbeats: "#{prefix}milvaion.heartbeats",
        registrations: "#{prefix}milvaion.registrations",
        dlq: "#{prefix}milvaion.dlq",
        group: "#{prefix}milvaion.worker.#{worker}"
      )
    end

    # Record headers as the processor reads them: string keys and string values, the last of
    # repeated headers winning (rdkafka returns an Array for a repeated header).
    def self.decode_headers(headers)
      (headers || {}).to_h do |key, value|
        value = value.last if value.is_a?(Array)
        [key.to_s, value.nil? ? nil : value.to_s]
      end
    end

    # Headers as UTF-8 strings, numbers as decimal strings, in a stable order.
    def self.encode_headers(headers)
      (headers || {}).sort_by { |key, _| key.to_s }.to_h do |key, value|
        [key.to_s, header_value(value)]
      end
    end

    def self.header_value(value)
      case value
      when nil then ""
      when String then value.dup.force_encoding(Encoding::UTF_8)
      else value.to_s
      end
    end

    # x-not-before as a Time, or nil when the header is missing or invalid.
    def self.not_before(headers)
      raw = (headers || {})[HEADER_NOT_BEFORE].to_s.strip
      return nil unless raw.match?(/\A-?\d+\z/)

      Time.at(Integer(raw, 10) / 1000r)
    end

    def self.unix_ms(time)
      (time.to_r * 1000).floor
    end

    # Kafka settings with every default applied, validated. Built by WorkerOptions.
    class Settings
      KEYS = %i[brokers tls tls_ca_file tls_cert_file tls_key_file sasl_mechanism sasl_username sasl_password
                topic_prefix auto_create_topics num_partitions replication_factor max_in_flight_per_partition].freeze

      attr_reader(*KEYS)

      def initialize(options, max_parallel_jobs)
        options = (options || {}).to_h.transform_keys(&:to_sym)
        unknown = options.keys - KEYS
        raise ArgumentError, "Unknown Kafka option(s): #{unknown.join(', ')}" unless unknown.empty?

        @brokers = Array(options[:brokers]).flat_map { |entry| entry.to_s.split(",") }.map(&:strip).reject(&:empty?)
        @brokers = ["localhost:9092"] if @brokers.empty?

        @topic_prefix = options[:topic_prefix].to_s
        unless TOPIC_PREFIX_PATTERN.match?(@topic_prefix)
          raise ArgumentError, "Kafka topic_prefix may only contain a-z A-Z 0-9 . _ -, got #{@topic_prefix.inspect}"
        end

        @auto_create_topics = options.fetch(:auto_create_topics, true) != false
        @num_partitions = integer(options[:num_partitions], 12, "num_partitions")
        raise ArgumentError, "Kafka num_partitions must be positive, got #{@num_partitions}" unless @num_partitions.between?(1, 1 << 20)

        @replication_factor = integer(options[:replication_factor], -1, "replication_factor")
        unless @replication_factor == -1 || @replication_factor.between?(1, 32_767)
          raise ArgumentError, "Kafka replication_factor must be -1 or positive, got #{@replication_factor}"
        end

        @max_in_flight_per_partition = integer(options[:max_in_flight_per_partition], max_parallel_jobs, "max_in_flight_per_partition")
        unless @max_in_flight_per_partition.positive?
          raise ArgumentError, "Kafka max_in_flight_per_partition must be positive, got #{@max_in_flight_per_partition}"
        end

        resolve_tls(options)
        resolve_sasl(options)
      end

      def tls?
        @tls
      end

      def sasl?
        !@sasl_mechanism.nil?
      end

      # librdkafka settings shared by the producer, the admin client and the consumer.
      def client_config(client_id)
        config = {
          "bootstrap.servers": brokers.join(","),
          "client.id": client_id,
          "security.protocol": security_protocol
        }

        config[:"ssl.ca.location"] = tls_ca_file if tls_ca_file
        config[:"ssl.certificate.location"] = tls_cert_file if tls_cert_file
        config[:"ssl.key.location"] = tls_key_file if tls_key_file

        if sasl?
          config[:"sasl.mechanisms"] = sasl_mechanism
          config[:"sasl.username"] = sasl_username
          config[:"sasl.password"] = sasl_password.to_s
        end

        config
      end

      def security_protocol
        if sasl?
          tls? ? "sasl_ssl" : "sasl_plaintext"
        else
          tls? ? "ssl" : "plaintext"
        end
      end

      private

      def integer(value, default, name)
        return default if value.nil? || value.to_s.strip.empty? || value == 0

        Integer(value)
      rescue ArgumentError, TypeError
        raise ArgumentError, "Kafka #{name} must be an integer, got #{value.inspect}"
      end

      def resolve_tls(options)
        @tls_ca_file = presence(options[:tls_ca_file])
        @tls_cert_file = presence(options[:tls_cert_file])
        @tls_key_file = presence(options[:tls_key_file])
        @tls = (options[:tls] ? true : false) || !(@tls_ca_file || @tls_cert_file || @tls_key_file).nil?

        if @tls_cert_file.nil? != @tls_key_file.nil?
          raise ArgumentError, "Kafka tls_cert_file and tls_key_file must be set together"
        end

        { tls_ca_file: @tls_ca_file, tls_cert_file: @tls_cert_file, tls_key_file: @tls_key_file }.each do |name, path|
          raise ArgumentError, "Kafka #{name} #{path.inspect} cannot be read" if path && !File.readable?(path)
        end
      end

      def resolve_sasl(options)
        raw = options[:sasl_mechanism].to_s.strip
        @sasl_username = presence(options[:sasl_username])
        @sasl_password = options[:sasl_password]
        @sasl_mechanism = nil
        return if raw.empty?

        @sasl_mechanism = SASL_MECHANISMS[raw.downcase.delete("-_")]

        if @sasl_mechanism.nil?
          raise ArgumentError, %(Kafka sasl_mechanism must be "plain", "scram-sha-256" or "scram-sha-512", got #{raw.inspect})
        end

        raise ArgumentError, "Kafka sasl_username is required with sasl_mechanism" if @sasl_username.nil?
      end

      def presence(value)
        value = value.to_s.strip
        value.empty? ? nil : value
      end
    end

    ENV_STRINGS = {
      "MILVAION_KAFKA_TLS_CA_FILE" => :tls_ca_file,
      "MILVAION_KAFKA_TLS_CERT_FILE" => :tls_cert_file,
      "MILVAION_KAFKA_TLS_KEY_FILE" => :tls_key_file,
      "MILVAION_KAFKA_SASL_MECHANISM" => :sasl_mechanism,
      "MILVAION_KAFKA_SASL_USERNAME" => :sasl_username,
      "MILVAION_KAFKA_SASL_PASSWORD" => :sasl_password,
      "MILVAION_KAFKA_TOPIC_PREFIX" => :topic_prefix
    }.freeze

    ENV_INTEGERS = {
      "MILVAION_KAFKA_NUM_PARTITIONS" => :num_partitions,
      "MILVAION_KAFKA_PARTITIONS" => :num_partitions,
      "MILVAION_KAFKA_REPLICATION_FACTOR" => :replication_factor,
      "MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION" => :max_in_flight_per_partition
    }.freeze

    # The Kafka options found in MILVAION_KAFKA_* variables, as a Hash for WorkerOptions' kafka:.
    def self.options_from_env(env)
      read = ->(key) { env[key].to_s.strip }
      values = {}

      brokers = read.call("MILVAION_KAFKA_BOOTSTRAP_SERVERS")
      brokers = read.call("KAFKA_BOOTSTRAP_SERVERS") if brokers.empty?
      values[:brokers] = brokers unless brokers.empty?

      values[:tls] = true if read.call("MILVAION_KAFKA_TLS").casecmp?("true")

      ENV_STRINGS.each do |key, name|
        value = read.call(key)
        values[name] = value unless value.empty?
      end

      values[:auto_create_topics] = false if read.call("MILVAION_KAFKA_AUTO_CREATE_TOPICS").casecmp?("false")

      ENV_INTEGERS.each do |key, name|
        raw = read.call(key)
        next if raw.empty? || values.key?(name)

        begin
          values[name] = Integer(raw, 10)
        rescue ArgumentError
          raise ArgumentError, "#{key} must be a number, got #{raw.inspect}"
        end
      end

      values
    end

    # Follows the records of one partition that this instance has taken, in offset order, and knows
    # how far the partition may be committed: up to the first record that is not finished, because
    # a Kafka commit covers every record before it.
    class OffsetTracker
      def initialize
        @pending = [] # [offset, done] pairs in offset order
        @last = -1
        @position = -1
      end

      # Registers a record taken from the partition. A repeated or older offset returns false.
      def add(offset)
        return false if offset <= @last

        @last = offset
        @pending << [offset, false]
        true
      end

      # Marks a record finished. Returns false for an offset the tracker does not hold.
      def done(offset)
        index = @pending.bsearch_index { |(o, _)| o >= offset }
        return false if index.nil? || @pending[index][0] != offset

        @pending[index][1] = true

        while (head = @pending.first) && head[1]
          @position = head[0] + 1
          @pending.shift
        end

        true
      end

      # The offset to commit, one past the end of the contiguous finished range, or nil until the
      # first record has finished.
      def commit_position
        @position.negative? ? nil : @position
      end

      # Records taken that are not ready to commit yet.
      def outstanding
        @pending.size
      end
    end
  end
end
