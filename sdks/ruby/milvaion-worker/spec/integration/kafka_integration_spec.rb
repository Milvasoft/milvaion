# frozen_string_literal: true

require "securerandom"

# Runs jobs end to end over a real Kafka broker. Skipped unless MILVAION_KAFKA_BROKERS is set:
#
#   docker run -d --name kafka -p 9092:9092 apache/kafka:3.9.0
#   MILVAION_KAFKA_BROKERS=localhost:9092 bundle exec rspec spec/integration/kafka_integration_spec.rb
#
# Every example uses a topic prefix of its own, so runs do not see each other's records.
RSpec.describe Milvaion::KafkaTransport, kafka: true do
  s = Milvaion::Status

  # Reads what the worker writes to the API's topics and its retry topic.
  class KafkaCollector
    Entry = Struct.new(:topic, :key, :headers, :raw, :body, keyword_init: true)

    def initialize(brokers, topics)
      @consumer = Rdkafka::Config.new(
        "bootstrap.servers": brokers,
        "group.id": "rb-collector-#{SecureRandom.hex(4)}",
        "auto.offset.reset": "earliest",
        "enable.auto.commit": false,
        "topic.metadata.refresh.interval.ms": 1000
      ).consumer
      @consumer.subscribe(*topics)
      @mutex = Mutex.new
      @entries = []
      @seen = {}
      @stop = false

      @thread = Thread.new do
        until @stop
          message = begin
            @consumer.poll(100)
          rescue Rdkafka::RdkafkaError
            nil
          end
          next unless message

          raw = message.payload.to_s.dup.force_encoding(Encoding::UTF_8)
          body = begin
            JSON.parse(raw)
          rescue JSON::ParserError
            nil
          end

          entry = Entry.new(topic: message.topic, key: message.key&.dup&.force_encoding(Encoding::UTF_8),
                            headers: Milvaion::Kafka.decode_headers(message.headers).transform_values { |v| v&.force_encoding(Encoding::UTF_8) },
                            raw: raw, body: body)
          # A rebalance of the collector's own group can deliver a record twice.
          @mutex.synchronize do
            @entries << entry unless @seen[[message.topic, message.partition, message.offset]]
            @seen[[message.topic, message.partition, message.offset]] = true
          end
        end
      end
    end

    def of(topic)
      @mutex.synchronize { @entries.select { |e| e.topic == topic } }
    end

    def close
      @stop = true
      @thread.join
      @consumer.close
    end
  end

  # One worker on its own topic prefix, with a producer that plays the API.
  class KafkaHarness
    attr_reader :worker, :topics, :collector, :producer

    def initialize(brokers, worker_id, handlers, job_options: {}, **worker_options)
      @brokers = brokers
      prefix = "rbit#{SecureRandom.hex(4)}."
      @worker = Milvaion::Worker.new(worker_id: worker_id, transport: "kafka", redis_url: nil, heartbeat_interval: 1, max_parallel_jobs: 4,
                                     startup_timeout: 60, logger: test_logger,
                                     kafka: { brokers: brokers, topic_prefix: prefix, num_partitions: 3, replication_factor: 1 }, **worker_options)
      handlers.each { |name, handler| @worker.job(name, handler, **job_options) }
      @topics = Milvaion::Kafka.topics(prefix, worker_id)
      @worker.start

      @producer = Rdkafka::Config.new("bootstrap.servers": brokers).producer
      @collector = KafkaCollector.new(brokers, [topics.status, topics.logs, topics.heartbeats, topics.registrations, topics.dlq, topics.retry])
    end

    # Publishes a job the way the API does and returns [job id, occurrence id].
    def dispatch(job_name = "TestJob", extra = {})
      job_id = SecureRandom.uuid
      occurrence_id = SecureRandom.uuid
      body = { "id" => job_id, "displayName" => job_name, "jobNameInWorker" => job_name, "jobData" => nil, "executeAt" => Milvaion.utc_now }.merge(extra)
      headers = { "OccurrenceId" => occurrence_id, "CorrelationId" => occurrence_id, "JobType" => job_name, "WorkerId" => worker.worker_id,
                  "ExecuteAt" => Milvaion.utc_now, "Priority" => "5" }

      produce(topics.jobs, JSON.generate(body), job_id, headers)
      [job_id, occurrence_id]
    end

    def produce(topic, payload, key, headers)
      handle = producer.produce(topic: topic, payload: payload, key: key, headers: headers)
      Milvaion::KafkaTransport.wait_for(handle, 10)
    end

    def statuses(occurrence_id)
      collector.of(topics.status).select { |e| e.key == occurrence_id }.map { |e| e.body["status"] }
    end

    # [committed, end] of the worker group on a topic, summed over its partitions.
    def committed_vs_end(topic)
      consumer = Rdkafka::Config.new("bootstrap.servers": @brokers, "group.id": topics.group).consumer
      list = Rdkafka::Consumer::TopicPartitionList.new
      list.add_topic(topic, 3)
      committed = consumer.committed(list, 5_000).to_h[topic].sum { |p| [p.offset.to_i, 0].max }
      ends = (0...3).sum { |partition| consumer.query_watermark_offsets(topic, partition, 5_000).last }
      [committed, ends]
    ensure
      consumer&.close
    end

    def dispose
      worker.stop
      collector.close
      producer.close
    end
  end

  before(:all) do
    require "rdkafka"
    Rdkafka::Config.logger = test_logger
  end

  def start(handlers, **options)
    KafkaHarness.new(ENV.fetch("MILVAION_KAFKA_BROKERS"), "Rb-Kafka Worker", handlers, **options).tap { |h| (@harnesses ||= []) << h }
  end

  def eventually(timeout = 15)
    deadline = Process.clock_gettime(Process::CLOCK_MONOTONIC) + timeout

    begin
      yield
    rescue RSpec::Expectations::ExpectationNotMetError
      raise if Process.clock_gettime(Process::CLOCK_MONOTONIC) > deadline

      sleep 0.2
      retry
    end
  end

  after do
    (@harnesses || []).each do |h|
      h.dispose
    rescue StandardError
      nil
    end
  end

  it "creates its topics, registers, runs a job and reports Running then Completed" do
    h = start({ "TestJob" => lambda { |ctx|
      ctx.log("hello from kafka")
      { ok: true }
    } })
    job_id, occurrence_id = h.dispatch

    wait_until(30) { h.statuses(occurrence_id).include?(s::COMPLETED) }
    expect(h.statuses(occurrence_id)).to eq([s::RUNNING, s::COMPLETED])
    expect(h.topics.jobs).to end_with("milvaion.jobs.rb-kafka_worker")

    final = h.collector.of(h.topics.status).find { |e| e.key == occurrence_id && e.body["status"] == s::COMPLETED }
    expect(final.body).to include("jobId" => job_id, "workerId" => "Rb-Kafka Worker", "result" => '{"ok":true}', "instanceId" => h.worker.instance_id)

    registration = wait_until(10) { h.collector.of(h.topics.registrations).first }
    expect(registration.key).to eq(h.worker.instance_id)
    expect(registration.body["jobTypes"]).to eq(["TestJob"])

    log = wait_until(10) { h.collector.of(h.topics.logs).find { |e| e.raw.include?("hello from kafka") } }
    expect(log.key).to eq(occurrence_id)

    heartbeat = wait_until(10) { h.collector.of(h.topics.heartbeats).find { |e| e.key == h.worker.instance_id } }
    expect(heartbeat.body).to include("workerId" => "Rb-Kafka Worker", "isStopping" => false)

    # The record is committed once its outcome is reported (commits run every 500 ms).
    eventually { expect(h.committed_vs_end(h.topics.jobs)).to eq([1, 1]) }
  end

  it "retries through the retry topic, then dead-letters with MaxRetries and a Failed status" do
    attempts = []
    times = []
    h = start({ "TestJob" => lambda { |ctx|
      attempts << ctx.retry_count
      times << (Time.now.to_r * 1000)
      raise "always broken"
    } }, job_options: { max_retries: 2, base_retry_delay: 1 }, job_heartbeat_interval: 0.5)
    started = Time.now.to_r * 1000
    job_id, occurrence_id = h.dispatch

    wait_until(60) { h.collector.of(h.topics.dlq).any? { |e| e.key == occurrence_id } }
    wait_until(10) { h.statuses(occurrence_id).include?(s::FAILED) }

    expect(attempts).to eq([0, 1, 2])

    retries = h.collector.of(h.topics.retry).select { |e| e.key == job_id }
    expect(retries.map { |e| e.headers["x-retry-count"] }).to eq(%w[1 2])
    expect(retries.map { |e| e.headers.values_at("OccurrenceId", "CorrelationId", "MaxRetries") }).to all(eq([occurrence_id, occurrence_id, "2"]))
    expect(retries.map { |e| e.headers["x-not-before"] }).to all(match(/\A\d+\z/))
    expect(retries.first.headers["JobType"]).to eq("TestJob")

    due = retries.map { |e| Integer(e.headers["x-not-before"]) }
    expect(due[0]).to be >= started + 1000
    expect(times[1]).to be >= due[0]
    expect(times[2]).to be >= due[1]
    expect(due[1] - times[1]).to be >= 1900

    dlq = h.collector.of(h.topics.dlq).find { |e| e.key == occurrence_id }
    expect(dlq.headers).to include("MaxRetries" => "2", "x-retry-count" => "2", "OccurrenceId" => occurrence_id, "CorrelationId" => occurrence_id)
    expect(dlq.body).to include("id" => job_id, "status" => s::FAILED)
    expect(dlq.body["exception"]).to start_with("Max retries (2) exceeded")

    statuses = h.statuses(occurrence_id)
    expect(statuses.last).to eq(s::FAILED)
    expect(statuses[0...-1]).to all(eq(s::RUNNING))
    # Job heartbeats while the retries wait.
    expect(statuses.count(s::RUNNING)).to be > 3

    eventually do
      expect(h.committed_vs_end(h.topics.jobs)).to eq([1, 1])
      expect(h.committed_vs_end(h.topics.retry)).to eq([2, 2])
    end
  end

  it "dead-letters a permanent failure with MaxRetries 0" do
    h = start({ "TestJob" => ->(_ctx) { raise Milvaion::PermanentJobError, "bad input" } })
    _, occurrence_id = h.dispatch

    dlq = wait_until(30) { h.collector.of(h.topics.dlq).find { |e| e.key == occurrence_id } }
    expect(dlq.headers).to include("MaxRetries" => "0", "x-retry-count" => "0")
    expect(h.collector.of(h.topics.retry)).to be_empty
  end

  it "forwards a record that is not a job to the dead letter topic unchanged" do
    h = start({ "TestJob" => ->(_ctx) { "ok" } })
    h.produce(h.topics.jobs, "not json", "poison", { "JobType" => "TestJob" })

    dlq = wait_until(30) { h.collector.of(h.topics.dlq).first }
    expect(dlq.raw).to eq("not json")
    expect(dlq.key).to eq("poison")
    expect(dlq.headers).to include("x-original-topic" => h.topics.jobs, "JobType" => "TestJob")
    expect(dlq.headers["x-dlq-reason"]).not_to be_empty

    eventually { expect(h.committed_vs_end(h.topics.jobs)).to eq([1, 1]) }
  end

  it "runs jobs of one partition in parallel up to the per-partition limit" do
    running = 0
    peak = 0
    lock = Mutex.new
    h = start({ "TestJob" => lambda { |_ctx|
      lock.synchronize { peak = [peak, running += 1].max }
      sleep 0.5
      lock.synchronize { running -= 1 }
    } }, max_parallel_jobs: 4)

    # Same key, so the same partition. The instance allows 4, the partition max_parallel_jobs too.
    ids = Array.new(6) { SecureRandom.uuid }
    ids.each do |occurrence_id|
      h.produce(h.topics.jobs, JSON.generate({ "id" => "same-job", "jobNameInWorker" => "TestJob" }), "same-job",
                { "OccurrenceId" => occurrence_id })
    end

    ids.each { |occurrence_id| wait_until(30) { h.statuses(occurrence_id).include?(s::COMPLETED) } }
    expect(peak).to eq(4)
    eventually { expect(h.committed_vs_end(h.topics.jobs)).to eq([6, 6]) }
  end

  it "writes a job interrupted by shutdown back to its topic and commits the original" do
    h = start({ "TestJob" => ->(ctx) { loop { ctx.sleep(0.05) } } }, shutdown_timeout: 0.3)
    _, occurrence_id = h.dispatch
    wait_until(30) { h.statuses(occurrence_id).include?(s::RUNNING) }

    h.worker.stop

    expect(h.worker.state).to eq(:stopped)
    expect(h.statuses(occurrence_id)).to eq([s::RUNNING])
    expect(h.committed_vs_end(h.topics.jobs)).to eq([1, 2])

    farewell = wait_until(10) { h.collector.of(h.topics.heartbeats).find { |e| e.body["isStopping"] } }
    expect(farewell.body["currentJobs"]).to eq(0)
  end
end
