# frozen_string_literal: true

RSpec.describe "Milvaion units" do
  describe "version" do
    it "matches the gemspec" do
      spec = Gem::Specification.load(File.expand_path("../milvaion-worker.gemspec", __dir__))

      expect(spec.version.to_s).to eq(Milvaion::Worker::VERSION)
    end
  end

  describe "routing" do
    it "matches the .NET SDK" do
      expect(Milvaion.generate_routing_pattern("SendEmailJob", "Mail-Worker")).to eq("mail-worker.sendemail.*")
      expect(Milvaion.generate_routing_pattern("Cleanup", "w")).to eq("w.cleanup.*")
      expect(Milvaion.generate_routing_pattern("NightlyJOB", "w")).to eq("w.nightly.*")
      expect(Milvaion.queue_name_for_pattern("mail-worker.sendemail.*")).to eq("scheduled_jobs_queue.mail-worker.sendemail.wildcard")
      expect(Milvaion.queue_name_for_pattern("a.#")).to eq("scheduled_jobs_queue.a.all")
    end
  end

  describe "occurrence id" do
    [
      [{ "OccurrenceId" => OCCURRENCE_ID.b }, nil, OCCURRENCE_ID],
      [{ "OccurrenceId" => "not-a-guid", "CorrelationId" => OCCURRENCE_ID }, nil, OCCURRENCE_ID],
      [{}, OCCURRENCE_ID, OCCURRENCE_ID],
      [{ "CorrelationId" => 5 }, "nope", nil],
      [nil, nil, nil]
    ].each do |headers, correlation_id, expected|
      it "reads #{expected.inspect} from #{headers.inspect} / #{correlation_id.inspect}" do
        expect(Milvaion::Processor.parse_occurrence_id(headers, correlation_id)).to eq(expected)
      end
    end
  end

  describe "retry count" do
    [[nil, 0], [2, 2], [-1, 0], ["3", 3], [[4, 0, 0, 0].pack("C*"), 4], ["x", 0], [true, 0]].each do |value, expected|
      it "reads #{expected} from #{value.inspect}" do
        expect(Milvaion::Processor.parse_retry_count({ "x-retry-count" => value })).to eq(expected)
      end
    end
  end

  describe "errors" do
    it "formats the cause chain like the .NET SDK" do
      error =
        begin
          begin
            raise ArgumentError, "inner"
          rescue ArgumentError
            raise Milvaion::PermanentJobError, "outer"
          end
        rescue Milvaion::PermanentJobError => e
          e
        end

      formatted = Milvaion.format_error(error)

      expect(formatted).to start_with("Type: Milvaion::PermanentJobError\nMessage: outer\nStackTrace: ")
      expect(formatted).to include("\nInner Exception:\nType: ArgumentError\nMessage: inner")
    end
  end

  describe Milvaion::LogBuffer do
    it "keeps the order after a failed send" do
      sent = []
      fail_next = true
      buffer = described_class.new(lambda { |batch|
        if fail_next
          fail_next = false
          raise "down"
        end

        sent.concat(batch["logs"].map { |entry| entry["log"]["message"] })
      }, test_logger, batch_size: 2)

      buffer.add(OCCURRENCE_ID, "i", { "message" => "1" })
      expect { buffer.flush }.to raise_error(RuntimeError, "down")

      buffer.add(OCCURRENCE_ID, "i", { "message" => "2" })
      buffer.add(OCCURRENCE_ID, "i", { "message" => "3" })
      buffer.flush

      expect(sent).to eq(%w[1 2 3])
      expect(buffer.pending).to eq(0)
    end

    it "drops the oldest entries beyond the limit" do
      sent = []
      buffer = described_class.new(->(batch) { sent.concat(batch["logs"].map { |entry| entry["log"]["message"] }) }, test_logger, max_buffered: 2)

      %w[1 2 3].each { |message| buffer.add(OCCURRENCE_ID, "i", { "message" => message }) }
      buffer.flush

      expect(sent).to eq(%w[2 3])
    end

    it "sends batches from its background thread" do
      batches = Queue.new
      buffer = described_class.new(->(batch) { batches << batch }, test_logger, flush_interval: 0.02).start

      buffer.add(OCCURRENCE_ID, "i", { "message" => "hello" })
      batch = batches.pop

      expect(batch["count"]).to eq(1)
      expect(batch["logs"].first).to include("occurrenceId" => OCCURRENCE_ID, "workerId" => "i")
      expect(batch["batchTimestamp"]).to end_with("Z")
    ensure
      buffer&.close
    end
  end

  describe Milvaion::WorkerOptions do
    it "applies defaults" do
      options = described_class.new(worker_id: " report-worker ")

      expect(options.worker_id).to eq("report-worker")
      expect(options.cancellation_channel).to eq("Milvaion:JobScheduler:cancellation_channel")
      expect(options.execution_timeout).to eq(3600)
      expect(options.instance_id).to match(/\Areport-worker-\h{8}\z/)

      job = Milvaion.resolve_job("SendEmailJob", ->(_ctx) {}, Milvaion::JobOptions.new, options)

      expect([job.routing_pattern, job.consumer_id, job.max_retries, job.base_retry_delay]).to eq(["report-worker.sendemail.*", "sendemail-consumer", 3, 5.0])
      expect(job.max_parallel_jobs).to eq([10, options.max_parallel_jobs].min)
    end

    it "rejects invalid values" do
      expect { described_class.new(worker_id: "") }.to raise_error(ArgumentError)
      expect { described_class.new(worker_id: "w", queue_type: "lazy") }.to raise_error(ArgumentError)
      expect { described_class.new(worker_id: "w", max_parallel_jobs: 0) }.to raise_error(ArgumentError)
      expect { described_class.new(worker_id: "w", unknown: 1) }.to raise_error(ArgumentError, /unknown/)
      expect { Milvaion::JobOptions.new(retries: 1) }.to raise_error(ArgumentError, /retries/)
    end

    it "reads MILVAION_* environment variables" do
      env = {
        "MILVAION_WORKER_ID" => "env-worker",
        "MILVAION_RABBITMQ_URL" => "amqp://rabbit:5672",
        "MILVAION_RABBITMQ_QUEUE_TYPE" => "Quorum",
        "MILVAION_REDIS_URL" => "redis://redis:6379",
        "MILVAION_REDIS_KEY_PREFIX" => "X:",
        "MILVAION_MAX_PARALLEL_JOBS" => "4",
        "MILVAION_EXECUTION_TIMEOUT_SECONDS" => "120",
        "MILVAION_HEARTBEAT_INTERVAL_SECONDS" => "5"
      }

      options = described_class.from_env(env)

      expect([options.worker_id, options.rabbitmq_url, options.queue_type, options.redis_url]).to eq(["env-worker", "amqp://rabbit:5672", "quorum", "redis://redis:6379"])
      expect([options.max_parallel_jobs, options.cancellation_channel, options.execution_timeout, options.heartbeat_interval]).to eq([4, "X:cancellation_channel", 120.0, 5.0])
      expect(described_class.from_env(env, worker_id: "override").worker_id).to eq("override")
      expect(described_class.from_env(env.merge("MILVAION_REDIS_URL" => "false")).redis_url).to be_nil
      expect { described_class.from_env(env.merge("MILVAION_MAX_PARALLEL_JOBS" => "many")) }.to raise_error(ArgumentError, /MILVAION_MAX_PARALLEL_JOBS/)
    end
  end

  describe Milvaion::Worker do
    let(:worker) { described_class.new(worker_id: "w", redis_url: nil, logger: test_logger) }

    it "rejects duplicate job names" do
      worker.job("TestJob") { |_ctx| nil }

      expect { worker.job("testjob") { |_ctx| nil } }.to raise_error(ArgumentError, /already registered/)
    end

    it "needs a handler" do
      expect { worker.job("TestJob") }.to raise_error(ArgumentError)
    end

    it "registers class-based jobs with their name and options" do
      stub_const("Reports::BuildReportJob", Class.new(Milvaion::Job) do
        job_options max_retries: 7, data_schema: { "type" => "object" }

        def perform(_ctx) = nil
      end)

      worker.job(Reports::BuildReportJob, max_parallel_jobs: 2)
      registration = worker.build_registration
      config = JSON.parse(registration["metadata"])["JobConfigs"].first

      expect(registration["jobTypes"]).to eq(["BuildReportJob"])
      expect(config).to include("JobType" => "BuildReportJob", "ConsumerId" => "buildreport-consumer", "MaxParallelJobs" => 2)
      expect(registration["jobDataDefinitions"]).to eq({ "BuildReportJob" => '{"type":"object"}' })
    end

    it "lets a subclass inherit and override job options" do
      base = Class.new(Milvaion::Job) { job_options max_retries: 1, base_retry_delay: 2 }
      child = Class.new(base) do
        job_name "ChildJob"
        job_options max_retries: 4
      end

      expect(child.job_options).to eq({ max_retries: 4, base_retry_delay: 2 })
      expect(child.job_name).to eq("ChildJob")
    end

    it "builds a registration with PascalCase metadata" do
      worker.job("GreetJob", data_schema: { "type" => "object" }, result_schema: '{"type":"string"}') { |_ctx| nil }
      registration = worker.build_registration
      metadata = JSON.parse(registration["metadata"])

      expect(metadata.values_at("Sdk", "SdkVersion")).to eq(["ruby", Milvaion::Worker::VERSION])
      expect(metadata["RuntimeVersion"]).to eq("Ruby #{RUBY_VERSION}")
      expect(metadata.keys).to all(match(/\A[A-Z]/))
      expect(metadata["JobConfigs"].first).to include("JobType" => "GreetJob", "ConsumerId" => "greet-consumer", "ExecutionTimeoutSeconds" => 3600)
      expect(registration["jobDataDefinitions"]).to eq({ "GreetJob" => '{"type":"object"}' })
      expect(registration["jobResultDefinitions"]).to eq({ "GreetJob" => '{"type":"string"}' })
      expect(registration["routingPatterns"]).to eq({ "GreetJob" => "w.greet.*" })
      expect(registration["displayName"]).to eq("w (#{worker.instance_id})")
    end

    it "refuses to start without jobs" do
      expect { worker.start }.to raise_error(Milvaion::Error, /at least one job/)
    end
  end

  describe Milvaion::CancellationListener do
    it "cancels a job running here and ignores the rest" do
      registry = Milvaion::CancellationRegistry.new
      abort = Milvaion::AbortSignal.new
      registry.register(OCCURRENCE_ID, abort)
      listener = described_class.new("redis://unused", "c", registry, test_logger)

      listener.on_message("not json")
      listener.on_message(JSON.generate({ "CorrelationId" => "0193a6c2-5d1e-7c41-9a3b-000000000000" }))
      expect(abort.aborted?).to be(false)

      listener.on_message(JSON.generate({ "CorrelationId" => OCCURRENCE_ID.upcase, "Reason" => "Stop" }))
      expect([abort.reason, abort.message]).to eq([Milvaion::AbortReason::CANCELLED, "Job was cancelled: Stop"])
    end
  end

  describe Milvaion::JobContext do
    it "sleeps until aborted" do
      abort = Milvaion::AbortSignal.new
      ctx = Milvaion::JobContext.new(occurrence_id: OCCURRENCE_ID, job: { "id" => JOB_ID }, retry_count: 0, instance_id: "i", abort: abort, sink: ->(_log) {})

      Thread.new do
        sleep 0.05
        abort.trigger(Milvaion::AbortReason::TIMED_OUT, "too slow")
      end

      started = Process.clock_gettime(Process::CLOCK_MONOTONIC)
      expect { ctx.sleep(10) }.to raise_error(Milvaion::JobAborted) { |e| expect(e.reason).to eq(:timed_out) }
      expect(Process.clock_gettime(Process::CLOCK_MONOTONIC) - started).to be < 2
    end
  end

  describe Milvaion::ThreadPool do
    it "runs items in parallel up to its size and hands back the ones not started" do
      pool = described_class.new(2, test_logger).start
      release = Queue.new
      started = Queue.new

      4.times { |i| pool.post(i) { |item| started << item && release.pop } }
      2.times { started.pop }

      expect(pool.close.sort).to eq([2, 3])
      expect(pool.post(5) { nil }).to be(false)

      2.times { release << :go }
      pool.join(2)
    end
  end
end
