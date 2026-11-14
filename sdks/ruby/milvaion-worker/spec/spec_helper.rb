# frozen_string_literal: true

require "json"
require "logger"
require "milvaion-worker"

OCCURRENCE_ID = "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00"
JOB_ID = "0193a6c2-0000-7c41-9a3b-2f6e8d4c1b99"

def test_logger
  Logger.new(ENV["MILVAION_TEST_LOG"] ? $stdout : File::NULL)
end

# Records what the processor publishes. Can be told to fail the next publishes.
class FakePublisher
  attr_reader :messages
  attr_accessor :fail_next

  def initialize
    @messages = []
    @fail_next = 0
    @mutex = Mutex.new
  end

  def publish(exchange, routing_key, body, persistent: true, correlation_id: nil, headers: nil, key: nil)
    @mutex.synchronize do
      if @fail_next.positive?
        @fail_next -= 1
        raise Milvaion::ConnectionError, "publish failed"
      end

      @messages << { exchange: exchange, key: routing_key, body: JSON.parse(body), raw: body, persistent: persistent,
                     correlation_id: correlation_id, headers: headers, record_key: key }
    end
  end

  def to(queue)
    @mutex.synchronize { @messages.select { |m| m[:exchange] == "" && m[:key] == queue } }
  end

  def statuses
    to("job_status_updates_queue").map { |m| m[:body]["status"] }
  end

  def last_status
    to("job_status_updates_queue").last[:body]
  end

  def logs
    to("worker_logs_queue").flat_map { |m| m[:body]["logs"].map { |entry| entry["log"] } }
  end

  def dead_lettered
    @mutex.synchronize { @messages.select { |m| m[:exchange] == "dlx_scheduled_jobs" } }
  end
end

class FakeDelivery
  attr_reader :settled, :delivery

  def initialize(job: {}, headers: {}, redelivered: false)
    body = { "id" => JOB_ID, "displayName" => "Test", "jobNameInWorker" => "TestJob", "jobData" => nil }.merge(job)
    @settled = []
    @delivery = Milvaion::Delivery.new(
      body: JSON.generate(body),
      headers: { "OccurrenceId" => OCCURRENCE_ID.b }.merge(headers),
      correlation_id: nil,
      redelivered: redelivered,
      ack: -> { @settled << "ack" },
      nack: ->(requeue) { @settled << (requeue ? "requeue" : "nack") }
    )
  end
end

# A processor wired to a fake publisher, with retry delays recorded instead of waited out.
class Harness
  attr_reader :options, :pub, :logs, :cancellations, :jobs, :consumer, :processor, :sleeps

  def initialize(handlers, worker_options: {}, **job_options)
    @options = Milvaion::WorkerOptions.new(worker_id: "rb-worker", redis_url: nil, job_heartbeat_interval: 0,
                                           instance_id: "rb-worker-abcd1234", logger: test_logger, **worker_options)
    @pub = FakePublisher.new
    @logs = Milvaion::LogBuffer.new(->(batch) { @pub.publish("", "worker_logs_queue", JSON.generate(batch)) }, @options.logger, flush_interval: 0.005)
    @cancellations = Milvaion::CancellationRegistry.new
    @jobs = handlers.to_h do |name, handler|
      [name.downcase, Milvaion.resolve_job(name, handler, Milvaion::JobOptions.new(**job_options), @options)]
    end
    @consumer = @jobs.values.first
    @processor = Milvaion::Processor.new(@options, @jobs, @pub, @logs, @cancellations)
    @sleeps = []
    @processor.retry_wait = lambda { |seconds|
      @sleeps << seconds
      @processor.stopping?
    }
    @processor.status_sleep = ->(_seconds) {}
  end

  def handle(fake)
    @processor.handle(fake.delivery, @consumer)
  end

  # Runs handle on a thread of its own and returns it.
  def handle_async(fake)
    Thread.new { handle(fake) }
  end
end

# Polls until the block returns truthy, failing after timeout seconds.
def wait_until(timeout = 5)
  deadline = Process.clock_gettime(Process::CLOCK_MONOTONIC) + timeout

  loop do
    value = yield
    return value if value
    raise "Condition not met within #{timeout}s" if Process.clock_gettime(Process::CLOCK_MONOTONIC) > deadline

    sleep 0.01
  end
end

RSpec.configure do |config|
  config.expect_with(:rspec) { |c| c.syntax = :expect }
  config.disable_monkey_patching!
  config.order = :random
  config.filter_run_excluding(integration: true) unless ENV["MILVAION_TEST_RABBITMQ_URL"]
  config.filter_run_excluding(kafka: true) if ENV["MILVAION_KAFKA_BROKERS"].to_s.strip.empty?
end
