# frozen_string_literal: true

require "bunny"
require "redis"
require "securerandom"

# Runs jobs end to end against a real RabbitMQ and Redis. Skipped unless MILVAION_TEST_RABBITMQ_URL is set:
#
#   docker run -d --name rabbit -p 5672:5672 rabbitmq:4
#   docker run -d --name redis -p 6379:6379 redis:7
#   MILVAION_TEST_RABBITMQ_URL=amqp://guest:guest@localhost:5672 MILVAION_TEST_REDIS_URL=redis://localhost:6379 bundle exec rspec
RSpec.describe Milvaion::Worker, integration: true do
  s = Milvaion::Status

  # Collects what the worker sends to the API.
  class Observer
    QUEUES = %w[job_status_updates_queue worker_logs_queue worker_registration_queue worker_heartbeat_queue failed_jobs_queue].freeze

    attr_reader :channel

    def initialize(url)
      @connection = Bunny.new(url, logger: test_logger).tap(&:start)
      @channel = @connection.create_channel
      @mutex = Mutex.new
      @messages = Hash.new { |hash, key| hash[key] = [] }

      @channel.topic("jobs.topic", durable: true)
      dlx = @channel.direct("dlx_scheduled_jobs", durable: true)

      QUEUES.each do |name|
        queue = @channel.queue(name, durable: true)
        queue.bind(dlx, routing_key: "failed_jobs") if name == "failed_jobs_queue"
        queue.subscribe(manual_ack: false) do |_info, properties, body|
          @mutex.synchronize { @messages[name] << { body: JSON.parse(body), properties: properties } }
        end
      end
    end

    def messages(queue)
      @mutex.synchronize { @messages[queue].dup }
    end

    def statuses(occurrence_id)
      messages("job_status_updates_queue").map { |m| m[:body] }.select { |b| b["occurrenceId"] == occurrence_id }
    end

    def logs(occurrence_id)
      messages("worker_logs_queue").flat_map { |m| m[:body]["logs"] }.select { |e| e["occurrenceId"] == occurrence_id }.map { |e| e["log"] }
    end

    def dispatch(routing_key, job_name, data: nil, timeout: nil)
      occurrence_id = SecureRandom.uuid
      body = { "id" => SecureRandom.uuid, "displayName" => job_name, "jobNameInWorker" => job_name, "jobData" => data && JSON.generate(data),
               "executeAt" => Milvaion.utc_now, "executionTimeoutSeconds" => timeout }
      @channel.exchange("jobs.topic", type: :topic, durable: true, no_declare: true)
              .publish(JSON.generate(body), routing_key: routing_key, persistent: true, correlation_id: occurrence_id,
                                            headers: { "OccurrenceId" => occurrence_id })
      occurrence_id
    end

    def close
      @connection.close
    end
  end

  rabbitmq_url = ENV.fetch("MILVAION_TEST_RABBITMQ_URL", nil)
  redis_url = ENV.fetch("MILVAION_TEST_REDIS_URL", "redis://localhost:6379")
  worker_id = "rb-it-#{SecureRandom.hex(3)}"

  def final_status(observer, occurrence_id, timeout = 20)
    wait_until(timeout) { observer.statuses(occurrence_id).find { |b| b["status"] != Milvaion::Status::RUNNING } }
  end

  before(:all) do
    @observer = Observer.new(rabbitmq_url)
    @worker = described_class.new(worker_id: worker_id, rabbitmq_url: rabbitmq_url, redis_url: redis_url, heartbeat_interval: 1,
                                  max_parallel_jobs: 4, logger: test_logger)

    @worker.job("EchoJob", data_schema: { "type" => "object" }) do |ctx|
      ctx.log("echoing", ctx.data)
      { "echo" => ctx.data, "attempt" => ctx.retry_count }
    end

    @worker.job("SlowJob") do |ctx|
      ctx.log("started")
      loop { ctx.sleep(0.05) }
    end

    @worker.job("FailJob", max_retries: 1, base_retry_delay: 0.1) { |_ctx| raise "always fails" }

    @worker.start
  end

  after(:all) do
    @worker&.stop
    @observer&.close
  end

  it "registers with metadata and sends heartbeats" do
    registration = wait_until(10) { @observer.messages("worker_registration_queue").map { |m| m[:body] }.find { |b| b["workerId"] == worker_id } }
    metadata = JSON.parse(registration["metadata"])

    expect(metadata.values_at("Sdk", "SdkVersion")).to eq(["ruby", Milvaion::Worker::VERSION])
    expect(registration["routingPatterns"]).to include("EchoJob" => "#{worker_id}.echo.*")
    expect(registration["jobDataDefinitions"]).to eq({ "EchoJob" => '{"type":"object"}' })

    heartbeat = wait_until(10) { @observer.messages("worker_heartbeat_queue").map { |m| m[:body] }.find { |b| b["instanceId"] == @worker.instance_id } }
    expect(heartbeat).to include("workerId" => worker_id, "isStopping" => false)
    expect(heartbeat["memoryBytes"]).to be > 0
  end

  it "runs a job end to end" do
    occurrence_id = @observer.dispatch("#{worker_id}.echo.job", "EchoJob", data: { "name" => "Ada" })
    final = final_status(@observer, occurrence_id)

    expect(final["status"]).to eq(s::COMPLETED)
    expect(JSON.parse(final["result"])).to eq({ "echo" => { "name" => "Ada" }, "attempt" => 0 })
    expect(@observer.statuses(occurrence_id).first["status"]).to eq(s::RUNNING)

    log = wait_until { @observer.logs(occurrence_id).find { |l| l["message"] == "echoing" } }
    expect(log).to include("level" => "Information", "data" => { "name" => "Ada" })
  end

  it "cancels a running job through Redis" do
    occurrence_id = @observer.dispatch("#{worker_id}.slow.job", "SlowJob")
    wait_until(10) { @observer.logs(occurrence_id).any? { |l| l["message"] == "started" } }

    redis = Redis.new(url: redis_url)
    redis.publish("Milvaion:JobScheduler:cancellation_channel", JSON.generate({ "CorrelationId" => occurrence_id, "Reason" => "Cancelled by test" }))
    redis.close

    expect(final_status(@observer, occurrence_id)["status"]).to eq(s::CANCELLED)
  end

  it "times out a job" do
    occurrence_id = @observer.dispatch("#{worker_id}.slow.job", "SlowJob", timeout: 0.3)
    final = final_status(@observer, occurrence_id)

    expect(final["status"]).to eq(s::TIMED_OUT)
    expect(final["exception"]).to start_with("Job execution exceeded maximum timeout of 0.3 seconds")
  end

  it "retries and then dead letters a failing job" do
    occurrence_id = @observer.dispatch("#{worker_id}.fail.job", "FailJob")
    final = final_status(@observer, occurrence_id)

    expect(final["status"]).to eq(s::FAILED)
    expect(final["exception"]).to start_with("Max retries (1) exceeded.")

    dead = wait_until { @observer.messages("failed_jobs_queue").find { |m| m[:properties].correlation_id == occurrence_id } }
    expect(dead[:body]).to include("jobNameInWorker" => "FailJob", "status" => s::FAILED)
    expect(dead[:properties].headers).to include("x-retry-count" => 1, "MaxRetries" => 1)
    expect(@observer.logs(occurrence_id)).to include(a_hash_including("category" => "Retry", "message" => a_string_including("attempt 1/1")))
  end

  it "returns a running job to the queue on shutdown" do
    other_id = "#{worker_id}-stop"
    worker = described_class.new(worker_id: other_id, rabbitmq_url: rabbitmq_url, redis_url: nil, shutdown_timeout: 0.3, logger: test_logger)
    worker.job("SlowJob") { |ctx| loop { ctx.sleep(0.05) } }
    worker.start

    occurrence_id = @observer.dispatch("#{other_id}.slow.job", "SlowJob")
    wait_until(10) { @observer.statuses(occurrence_id).any? }
    worker.stop

    expect(worker.state).to eq(:stopped)
    expect(@observer.statuses(occurrence_id).map { |b| b["status"] }).to eq([s::RUNNING])

    queue = @observer.channel.queue("scheduled_jobs_queue.#{other_id}.slow.wildcard", passive: true)
    expect(wait_until { queue.message_count == 1 && queue.message_count }).to eq(1)

    farewell = wait_until { @observer.messages("worker_heartbeat_queue").map { |m| m[:body] }.find { |b| b["instanceId"] == worker.instance_id && b["isStopping"] } }
    expect(farewell["currentJobs"]).to eq(0)

    @observer.channel.queue_delete("scheduled_jobs_queue.#{other_id}.slow.wildcard")
  end
end
