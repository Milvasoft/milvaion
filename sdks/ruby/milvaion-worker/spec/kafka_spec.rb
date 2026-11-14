# frozen_string_literal: true

require "rbconfig"
require "tempfile"

# Unit specs of the Kafka transport's contract. None of them needs a broker or the rdkafka gem.
RSpec.describe Milvaion::Kafka do
  describe ".worker_segment" do
    it "lower-cases the worker id and replaces characters outside a-z 0-9 . _ -" do
      expect(described_class.worker_segment("Report-Worker")).to eq("report-worker")
      expect(described_class.worker_segment("mail.worker_2")).to eq("mail.worker_2")
      expect(described_class.worker_segment("Mail Worker/EU:1")).to eq("mail_worker_eu_1")
      expect(described_class.worker_segment("işçi")).to eq("i__i")
    end
  end

  describe ".topics" do
    it "builds every topic and the consumer group with the prefix" do
      topics = described_class.topics("prod.", "Mail Worker")

      expect(topics.to_h).to eq(
        jobs: "prod.milvaion.jobs.mail_worker",
        retry: "prod.milvaion.jobs.mail_worker.retry",
        status: "prod.milvaion.status",
        logs: "prod.milvaion.logs",
        heartbeats: "prod.milvaion.heartbeats",
        registrations: "prod.milvaion.registrations",
        dlq: "prod.milvaion.dlq",
        group: "prod.milvaion.worker.mail_worker"
      )
      expect(described_class.topics("", "w").jobs).to eq("milvaion.jobs.w")
    end
  end

  describe "headers" do
    it "decodes header values to strings, keeping the last of repeated headers" do
      decoded = described_class.decode_headers({ "OccurrenceId" => "a".b, :MaxRetries => "3", "x-retry-count" => %w[1 2] })

      expect(decoded).to eq({ "OccurrenceId" => "a", "MaxRetries" => "3", "x-retry-count" => "2" })
      expect(described_class.decode_headers(nil)).to eq({})
    end

    it "encodes numbers as decimal strings in a stable order" do
      encoded = described_class.encode_headers({ "x-retry-count" => 2, "MaxRetries" => 3, "CorrelationId" => "c", "Empty" => nil })

      expect(encoded).to eq({ "CorrelationId" => "c", "Empty" => "", "MaxRetries" => "3", "x-retry-count" => "2" })
      expect(encoded.keys).to eq(%w[CorrelationId Empty MaxRetries x-retry-count])
    end

    it "reads the occurrence id from OccurrenceId, then CorrelationId" do
      other = "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b01"

      expect(Milvaion::Processor.parse_occurrence_id({ "OccurrenceId" => OCCURRENCE_ID, "CorrelationId" => other }, nil)).to eq(OCCURRENCE_ID)
      expect(Milvaion::Processor.parse_occurrence_id({ "CorrelationId" => other }, nil)).to eq(other)
      expect(Milvaion::Processor.parse_occurrence_id({ "OccurrenceId" => "nope" }, nil)).to be_nil
    end

    it "reads the retry count from a decimal string, absent or invalid meaning 0" do
      expect(Milvaion::Processor.parse_retry_count({ "x-retry-count" => "2" })).to eq(2)
      expect(Milvaion::Processor.parse_retry_count({})).to eq(0)
      expect(Milvaion::Processor.parse_retry_count({ "x-retry-count" => "x" })).to eq(0)
    end

    it "reads x-not-before as Unix milliseconds" do
      expect(described_class.not_before({ "x-not-before" => "1700000000123" })).to eq(Time.at(1_700_000_000.123r))
      expect(described_class.not_before({ "x-not-before" => "soon" })).to be_nil
      expect(described_class.not_before({})).to be_nil
      expect(described_class.unix_ms(Time.at(1_700_000_000.123r))).to eq(1_700_000_000_123)
    end
  end

  describe Milvaion::Kafka::OffsetTracker do
    it "commits only the end of the contiguous finished range" do
      tracker = described_class.new
      (10..14).each { |offset| expect(tracker.add(offset)).to be(true) }

      expect(tracker.commit_position).to be_nil
      tracker.done(12)
      tracker.done(13)
      expect(tracker.commit_position).to be_nil
      tracker.done(10)
      expect(tracker.commit_position).to eq(11)
      tracker.done(11)
      expect(tracker.commit_position).to eq(14)
      expect(tracker.outstanding).to eq(1)
      tracker.done(14)
      expect(tracker.commit_position).to eq(15)
      expect(tracker.outstanding).to eq(0)
    end

    it "rejects repeated and older offsets and unknown ones" do
      tracker = described_class.new
      tracker.add(5)

      expect(tracker.add(5)).to be(false)
      expect(tracker.add(3)).to be(false)
      expect(tracker.done(4)).to be(false)
      expect(tracker.add(7)).to be(true)
      expect(tracker.done(7)).to be(true)
      expect(tracker.commit_position).to be_nil
      expect(tracker.done(5)).to be(true)
      expect(tracker.commit_position).to eq(8)
    end

    it "stays correct over many records finishing out of order" do
      tracker = described_class.new
      offsets = (0...500).to_a
      offsets.each { |offset| tracker.add(offset) }

      finished = []
      offsets.shuffle(random: Random.new(42)).each do |offset|
        tracker.done(offset)
        finished << offset
        contiguous = (0..500).find { |o| !finished.include?(o) }
        expect(tracker.commit_position).to eq(contiguous.zero? ? nil : contiguous)
      end
    end
  end

  describe "options" do
    def options(**kwargs)
      Milvaion::WorkerOptions.new(worker_id: "w", logger: test_logger, **kwargs)
    end

    it "defaults to RabbitMQ" do
      expect(options.transport).to eq("rabbitmq")
      expect(options.kafka?).to be(false)
      expect(options.kafka).to be_nil
    end

    it "applies the Kafka defaults" do
      k = options(transport: "Kafka", max_parallel_jobs: 6).kafka

      expect(k.brokers).to eq(["localhost:9092"])
      expect(k.topic_prefix).to eq("")
      expect(k.auto_create_topics).to be(true)
      expect([k.num_partitions, k.replication_factor, k.max_in_flight_per_partition]).to eq([12, -1, 6])
      expect(k.tls?).to be(false)
      expect(k.sasl?).to be(false)
      expect(k.client_config("i")).to eq({ "bootstrap.servers": "localhost:9092", "client.id": "i", "security.protocol": "plaintext" })
    end

    it "accepts every Kafka setting" do
      k = options(transport: "kafka", kafka: {
                    brokers: ["a:9092, b:9092", "c:9092"], tls: true, sasl_mechanism: "SCRAM-SHA-512", sasl_username: "u", sasl_password: "p",
                    topic_prefix: "prod.", auto_create_topics: false, num_partitions: 3, replication_factor: 2, max_in_flight_per_partition: 1
                  }).kafka

      expect(k.brokers).to eq(%w[a:9092 b:9092 c:9092])
      expect([k.auto_create_topics, k.num_partitions, k.replication_factor, k.max_in_flight_per_partition]).to eq([false, 3, 2, 1])
      expect(k.client_config("i")).to include("security.protocol": "sasl_ssl", "sasl.mechanisms": "SCRAM-SHA-512", "sasl.username": "u",
                                              "sasl.password": "p", "bootstrap.servers": "a:9092,b:9092,c:9092")
    end

    it "turns TLS on when a certificate file is set" do
      Tempfile.create("ca") do |file|
        k = options(transport: "kafka", kafka: { tls_ca_file: file.path, sasl_mechanism: "plain", sasl_username: "u" }).kafka

        expect(k.tls?).to be(true)
        expect(k.client_config("i")).to include("security.protocol": "sasl_ssl", "ssl.ca.location": file.path, "sasl.mechanisms": "PLAIN")
      end
    end

    it "validates the Kafka options" do
      [
        { transport: "nats" },
        { transport: "kafka", kafka: { topic_prefix: "bad prefix" } },
        { transport: "kafka", kafka: { sasl_mechanism: "gssapi", sasl_username: "u" } },
        { transport: "kafka", kafka: { sasl_mechanism: "plain" } },
        { transport: "kafka", kafka: { replication_factor: -2 } },
        { transport: "kafka", kafka: { num_partitions: -1 } },
        { transport: "kafka", kafka: { max_in_flight_per_partition: -1 } },
        { transport: "kafka", kafka: { tls_cert_file: "only-cert.pem" } },
        { transport: "kafka", kafka: { tls_ca_file: "does-not-exist.pem" } },
        { transport: "kafka", kafka: { partitions: 3 } }
      ].each do |invalid|
        expect { options(**invalid) }.to raise_error(ArgumentError), "expected an error for #{invalid}"
      end
    end

    it "reads the Kafka environment" do
      env = {
        "MILVAION_WORKER_ID" => "w",
        "MILVAION_TRANSPORT" => "kafka",
        "KAFKA_BOOTSTRAP_SERVERS" => "k1:9092,k2:9092",
        "MILVAION_KAFKA_TOPIC_PREFIX" => "stage.",
        "MILVAION_KAFKA_SASL_MECHANISM" => "plain",
        "MILVAION_KAFKA_SASL_USERNAME" => "user",
        "MILVAION_KAFKA_SASL_PASSWORD" => "secret",
        "MILVAION_KAFKA_AUTO_CREATE_TOPICS" => "false",
        "MILVAION_KAFKA_PARTITIONS" => "6",
        "MILVAION_KAFKA_REPLICATION_FACTOR" => "3",
        "MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION" => "2"
      }

      o = Milvaion::WorkerOptions.from_env(env, logger: test_logger)
      k = o.kafka

      expect(o.transport).to eq("kafka")
      expect(k.brokers).to eq(%w[k1:9092 k2:9092])
      expect([k.topic_prefix, k.sasl_mechanism, k.sasl_username, k.sasl_password]).to eq(["stage.", "PLAIN", "user", "secret"])
      expect([k.auto_create_topics, k.num_partitions, k.replication_factor, k.max_in_flight_per_partition]).to eq([false, 6, 3, 2])

      env["MILVAION_KAFKA_BOOTSTRAP_SERVERS"] = "preferred:9092"
      env["MILVAION_KAFKA_NUM_PARTITIONS"] = "9"
      env["MILVAION_KAFKA_TLS"] = "true"
      o = Milvaion::WorkerOptions.from_env(env, logger: test_logger, kafka: { topic_prefix: "override." })

      expect(o.kafka.brokers).to eq(["preferred:9092"])
      expect(o.kafka.num_partitions).to eq(9)
      expect(o.kafka.topic_prefix).to eq("override.")
      expect(o.kafka.client_config("i")[:"security.protocol"]).to eq("sasl_ssl")

      expect { Milvaion::WorkerOptions.from_env(env.merge("MILVAION_KAFKA_PARTITIONS" => "x", "MILVAION_KAFKA_NUM_PARTITIONS" => "")) }
        .to raise_error(ArgumentError, /MILVAION_KAFKA_PARTITIONS/)
    end
  end

  it "does not load rdkafka unless the transport is kafka" do
    lib = File.expand_path("../lib", __dir__)
    script = 'require "milvaion-worker"; Milvaion::Worker.new(worker_id: "w"); exit(defined?(Rdkafka) ? 1 : 0)'

    expect(system(RbConfig.ruby, "-I", lib, "-e", script)).to be(true)
  end

  it "explains how to add rdkafka when it is missing" do
    hide_const("Rdkafka")
    allow(Milvaion::KafkaTransport).to receive(:require).with("rdkafka").and_raise(LoadError, "cannot load such file -- rdkafka")

    worker = Milvaion::Worker.new(worker_id: "w", transport: "kafka", logger: test_logger)
    worker.job("TestJob") { |_ctx| nil }

    expect { worker.start }.to raise_error(Milvaion::Error, /gem "rdkafka"/)
    expect(worker.state).to eq(:idle)
  end
end

# The Kafka transport plugs into the processor through Delivery#schedule_retry and Delivery#reject.
RSpec.describe Milvaion::Processor, "with broker-scheduled retries" do
  s = Milvaion::Status

  it "hands a retry to the broker right away with its due time, then settles the original" do
    h = Harness.new({ "TestJob" => ->(_ctx) { raise "boom" } }, max_retries: 3, base_retry_delay: 5)
    d = FakeDelivery.new(headers: { "x-retry-count" => "1" })
    requests = []
    d.delivery.schedule_retry = ->(request) { requests << request }

    before = Time.now
    h.handle(d)

    expect(d.settled).to eq(["ack"])
    expect(h.pub.statuses).to eq([s::RUNNING])
    expect(h.sleeps).to be_empty
    expect(requests.size).to eq(1)

    request = requests.first
    expect(request.to_h.slice(:job_id, :occurrence_id, :retry_count, :max_retries)).to eq(job_id: JOB_ID, occurrence_id: OCCURRENCE_ID, retry_count: 2, max_retries: 3)
    expect(request.not_before - before).to be_between(10, 11)
    expect(h.pub.to(h.consumer.queue)).to be_empty
    expect(h.pub.dead_lettered).to be_empty
    expect(h.pub.logs).to include(a_hash_including("category" => "Retry", "message" => a_string_including("attempt 2/3")))
  end

  it "keeps sending job heartbeats until a scheduled retry is due" do
    h = Harness.new({ "TestJob" => ->(_ctx) { raise "boom" } }, worker_options: { job_heartbeat_interval: 0.05 }, base_retry_delay: 0.3)
    d = FakeDelivery.new
    d.delivery.schedule_retry = ->(_request) {}

    h.handle(d)
    expect(d.settled).to eq(["ack"])

    wait_until(2) { h.pub.statuses.count(s::RUNNING) >= 3 }
    sleep 0.4
    count = h.pub.statuses.size
    sleep 0.2
    expect(h.pub.statuses.size).to eq(count)
  end

  it "settles a retry-after failure without scheduling a retry or dead-lettering" do
    at = Time.now + 3600
    h = Harness.new({ "TestJob" => ->(_ctx) { raise Milvaion::RetryAfterError, at } }, max_retries: 3)
    d = FakeDelivery.new
    requests = []
    rejected = []
    d.delivery.schedule_retry = ->(request) { requests << request }
    d.delivery.reject = ->(reason) { rejected << reason }

    h.handle(d)

    expect(d.settled).to eq(["ack"])
    expect(requests).to be_empty
    expect(rejected).to be_empty
    expect(h.pub.dead_lettered).to be_empty
    expect(h.pub.statuses).to eq([s::RUNNING, s::FAILED])
    expect(h.pub.last_status["retryAfter"]).to eq(Milvaion.iso(at.getutc))
    expect(h.pub.to("job_status_updates_queue").map { |m| m[:record_key] }.uniq).to eq([OCCURRENCE_ID])
  end

  it "dead-letters through reject when the retry cannot be scheduled" do
    h = Harness.new({ "TestJob" => ->(_ctx) { raise "boom" } })
    d = FakeDelivery.new
    reasons = []
    d.delivery.schedule_retry = ->(_request) { raise Milvaion::ConnectionError, "broker down" }
    d.delivery.reject = ->(reason) { reasons << reason }

    h.handle(d)

    expect(d.settled).to be_empty
    expect(reasons.size).to eq(1)
    expect(reasons.first).to include("broker down")
  end

  it "passes a reason when it rejects a record that is not a job" do
    h = Harness.new({ "TestJob" => ->(_ctx) { "ok" } })
    d = FakeDelivery.new
    d.delivery.body = "not json"
    reasons = []
    d.delivery.reject = ->(reason) { reasons << reason }

    h.handle(d)

    expect(d.settled).to be_empty
    expect(reasons).to eq(["the message is not a valid job"])
  end

  it "keys statuses and dead letters by occurrence id" do
    h = Harness.new({ "TestJob" => ->(_ctx) { raise Milvaion::PermanentJobError, "bad" } })
    h.handle(FakeDelivery.new)

    expect(h.pub.to("job_status_updates_queue").map { |m| m[:record_key] }.uniq).to eq([OCCURRENCE_ID])
    expect(h.pub.dead_lettered.map { |m| m[:record_key] }).to eq([OCCURRENCE_ID])
    expect(h.pub.dead_lettered.first[:headers]).to include("MaxRetries" => 0)
  end
end
