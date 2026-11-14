# frozen_string_literal: true

RSpec.describe Milvaion::Processor do
  s = Milvaion::Status

  it "completes and reports the result" do
    h = Harness.new({ "TestJob" => lambda { |ctx|
      ctx.log("working", { "to" => ctx.data["to"] })
      { sent: true }
    } })
    d = FakeDelivery.new(job: { "jobData" => JSON.generate({ to: "a@b.c" }) })
    h.handle(d)

    expect(d.settled).to eq(["ack"])
    expect(h.pub.statuses).to eq([s::RUNNING, s::COMPLETED])

    final = h.pub.last_status
    expect(final.values_at("occurrenceId", "jobId", "workerId", "instanceId")).to eq([OCCURRENCE_ID, JOB_ID, "rb-worker", "rb-worker-abcd1234"])
    expect(final["result"]).to eq('{"sent":true}')
    expect(final["startTime"]).to match(/\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z\z/)
    expect(final["endTime"]).not_to be_nil
    expect(final["durationMs"]).to be_a(Integer)
    expect(h.pub.logs).to include(a_hash_including("message" => "working", "level" => "Information", "category" => "UserCode", "data" => { "to" => "a@b.c" }))
  end

  it "runs the handler on a thread of its own" do
    seen = []
    h = Harness.new({ "TestJob" => lambda { |_ctx|
      seen << Thread.current
      "done"
    } })
    h.handle(FakeDelivery.new)

    expect(seen.first).not_to eq(Thread.current)
    expect(h.pub.last_status["result"]).to eq("done")
  end

  it "reports a default result when the handler returns nil" do
    h = Harness.new({ "TestJob" => ->(_ctx) {} })
    h.handle(FakeDelivery.new)

    expect(h.pub.last_status["result"]).to eq("Job TestJob completed successfully")
  end

  it "runs class-based jobs with a new instance per execution" do
    klass = Class.new(Milvaion::Job) do
      def perform(ctx)
        "instance #{object_id} retry #{ctx.retry_count}"
      end
    end

    h = Harness.new({ "TestJob" => klass })
    h.handle(FakeDelivery.new)
    h.handle(FakeDelivery.new)

    results = h.pub.to("job_status_updates_queue").map { |m| m[:body]["result"] }.compact
    expect(results.size).to eq(2)
    expect(results.uniq.size).to eq(2)
  end

  it "retries with exponential backoff" do
    h = Harness.new({ "TestJob" => ->(_ctx) { raise "boom" } }, max_retries: 3, base_retry_delay: 5)
    d = FakeDelivery.new(headers: { "x-retry-count" => 1 })
    h.handle(d)

    expect(d.settled).to eq(["ack"])
    expect(h.sleeps).to eq([10.0])
    expect(h.pub.statuses).to eq([s::RUNNING])

    retries = h.pub.to(h.consumer.queue)
    expect(retries.size).to eq(1)
    expect(retries.first[:raw]).to eq(d.delivery.body)
    expect(retries.first[:correlation_id]).to eq(OCCURRENCE_ID)
    expect(retries.first[:headers]).to eq({ "CorrelationId" => OCCURRENCE_ID, "x-retry-count" => 2, "MaxRetries" => 3 })
    expect(h.pub.logs).to include(a_hash_including("category" => "Retry", "message" => a_string_including("attempt 2/3")))
    expect(h.pub.dead_lettered).to be_empty
  end

  it "sends job heartbeats during the retry delay" do
    h = Harness.new({ "TestJob" => ->(_ctx) { raise "boom" } }, worker_options: { job_heartbeat_interval: 2 }, base_retry_delay: 5)
    h.handle(FakeDelivery.new)

    expect(h.sleeps).to eq([2.0, 2.0, 1.0])
    expect(h.pub.statuses).to eq([s::RUNNING, s::RUNNING, s::RUNNING])
  end

  it "dead letters after the last retry" do
    h = Harness.new({ "TestJob" => ->(_ctx) { raise "still broken" } }, max_retries: 3)
    d = FakeDelivery.new(headers: { "x-retry-count" => 3 })
    h.handle(d)

    expect(d.settled).to eq(["ack"])
    expect(h.pub.statuses).to eq([s::RUNNING, s::FAILED])
    expect(h.pub.last_status["exception"]).to start_with("Max retries (3) exceeded. Original exception: Type: RuntimeError\nMessage: still broken")

    dlq = h.pub.dead_lettered
    expect(dlq.size).to eq(1)
    expect(dlq.first[:key]).to eq("failed_jobs")
    expect(dlq.first[:body].values_at("id", "status")).to eq([JOB_ID, s::FAILED])
    expect(dlq.first[:headers]).to eq({ "CorrelationId" => OCCURRENCE_ID, "x-retry-count" => 3, "MaxRetries" => 3 })
  end

  it "does not retry a permanent failure" do
    h = Harness.new({ "TestJob" => ->(_ctx) { raise Milvaion::PermanentJobError, "bad input" } })
    d = FakeDelivery.new
    h.handle(d)

    expect(d.settled).to eq(["ack"])
    expect(h.sleeps).to be_empty
    expect(h.pub.statuses).to eq([s::RUNNING, s::FAILED])

    dlq = h.pub.dead_lettered.first
    expect(dlq[:body]["exception"]).to start_with("[PERMANENT FAILURE] Type: Milvaion::PermanentJobError")
    expect(dlq[:headers]["MaxRetries"]).to eq(0)
  end

  it "ends a retry-after failure as Failed with retryAfter, without retrying or dead-lettering" do
    at = Time.utc(2026, 11, 14, 11, 0, 0)
    h = Harness.new({ "TestJob" => ->(_ctx) { raise Milvaion::RetryAfterError.new(at, "quota exhausted") } }, max_retries: 3)
    d = FakeDelivery.new
    h.handle(d)

    expect(d.settled).to eq(["ack"])
    expect(h.sleeps).to be_empty
    expect(h.pub.to(h.consumer.queue)).to be_empty
    expect(h.pub.dead_lettered).to be_empty
    expect(h.pub.statuses).to eq([s::RUNNING, s::FAILED])

    final = h.pub.last_status
    expect(final["retryAfter"]).to eq("2026-11-14T11:00:00.000Z")
    expect(final["exception"]).to include("2026-11-14T11:00:00.000Z")
    expect(final["exception"]).to include("Type: Milvaion::RetryAfterError
Message: quota exhausted")
    expect(final["endTime"]).not_to be_nil
    expect(final["durationMs"]).to be_a(Integer)
    expect(h.pub.logs).to include(a_hash_including("level" => "Warning", "category" => "Retry",
                                                   "message" => a_string_starting_with("⏰ Job asked to be retried at 2026-11-14T11:00:00.000Z"),
                                                   "data" => { "RetryAfter" => "2026-11-14T11:00:00.000Z" }))
  end

  it "does not send retryAfter in other statuses" do
    h = Harness.new({ "TestJob" => ->(_ctx) { "ok" } })
    h.handle(FakeDelivery.new)

    expect(h.pub.to("job_status_updates_queue").map { |m| m[:body] }).to all(satisfy { |body| !body.key?("retryAfter") })
  end

  it "only honors a top-level retry-after error" do
    h = Harness.new({ "TestJob" => lambda { |_ctx|
      begin
        raise Milvaion::RetryAfterError, Time.now + 60
      rescue Milvaion::RetryAfterError
        raise "wrapped"
      end
    } }, max_retries: 0)
    h.handle(FakeDelivery.new)

    expect(h.pub.last_status).not_to have_key("retryAfter")
    expect(h.pub.dead_lettered.size).to eq(1)
  end

  it "treats job data that is not JSON as a permanent failure" do
    h = Harness.new({ "TestJob" => ->(ctx) { ctx.data } })
    h.handle(FakeDelivery.new(job: { "jobData" => "{not json" }))

    expect(h.pub.dead_lettered.first[:body]["exception"]).to include("Job data is not valid JSON.")
  end

  it "times out a handler that checks the context" do
    h = Harness.new({ "TestJob" => ->(ctx) { ctx.sleep(10) } })
    d = FakeDelivery.new(job: { "executionTimeoutSeconds" => 0.05 })
    h.handle(d)

    expect(d.settled).to eq(["ack"])
    expect(h.pub.statuses).to eq([s::RUNNING, s::TIMED_OUT])
    expect(h.pub.last_status["exception"]).to start_with("Job execution exceeded maximum timeout of 0.05 seconds")
  end

  it "settles a timed out handler that ignores the context right away" do
    release = Queue.new
    h = Harness.new({ "TestJob" => ->(_ctx) { release.pop } }, execution_timeout: 0.05)
    started = Process.clock_gettime(Process::CLOCK_MONOTONIC)
    h.handle(FakeDelivery.new)

    expect(Process.clock_gettime(Process::CLOCK_MONOTONIC) - started).to be < 2
    expect(h.pub.statuses.last).to eq(s::TIMED_OUT)
  ensure
    release << :go
  end

  it "interrupts the handler thread when the job opts in" do
    finished = Queue.new
    h = Harness.new({ "TestJob" => lambda { |_ctx|
      begin
        Kernel.sleep(10)
      ensure
        finished << $!.class
      end
    } }, execution_timeout: 0.05, interrupt_on_abort: true)
    h.handle(FakeDelivery.new)

    expect(h.pub.statuses.last).to eq(s::TIMED_OUT)
    expect(finished.pop).to eq(Milvaion::JobAborted)
  end

  it "cancels a running job" do
    h = Harness.new({ "TestJob" => lambda { |ctx|
      loop { ctx.sleep(0.01) }
    } })
    d = FakeDelivery.new
    thread = h.handle_async(d)

    wait_until { h.cancellations.cancel(OCCURRENCE_ID.upcase, "Cancelled by user") }
    thread.join(5)

    expect(d.settled).to eq(["ack"])
    expect(h.pub.statuses).to eq([s::RUNNING, s::CANCELLED])
    expect(h.pub.last_status["result"]).to eq("Job cancelled by user or system")
    expect(h.pub.logs).to include(a_hash_including("message" => "Job execution was cancelled", "data" => { "Reason" => "Job was cancelled: Cancelled by user" }))
  end

  it "lets the handler see the abort" do
    seen = Queue.new
    h = Harness.new({ "TestJob" => lambda { |ctx|
      Kernel.sleep(0.01) until ctx.aborted?
      seen << ctx.abort_reason
    } })
    d = FakeDelivery.new
    thread = h.handle_async(d)

    wait_until { h.cancellations.cancel(OCCURRENCE_ID) }
    thread.join(5)

    expect(seen.pop).to eq(Milvaion::AbortReason::CANCELLED)
  end

  it "returns a job interrupted by shutdown to the queue" do
    h = Harness.new({ "TestJob" => ->(ctx) { loop { ctx.sleep(0.01) } } })
    d = FakeDelivery.new
    thread = h.handle_async(d)

    wait_until { h.processor.running == 1 && h.pub.statuses.any? }
    h.cancellations.abort_all(Milvaion::AbortReason::SHUTDOWN, "Worker is shutting down.")
    thread.join(5)

    expect(d.settled).to eq(["requeue"])
    expect(h.pub.statuses).to eq([s::RUNNING])
  end

  it "returns the job to the queue when the worker stops during the retry delay" do
    h = Harness.new({ "TestJob" => ->(_ctx) { raise "boom" } })
    h.processor.begin_shutdown
    d = FakeDelivery.new
    h.handle(d)

    expect(d.settled).to eq(["requeue"])
    expect(h.pub.to(h.consumer.queue)).to be_empty
  end

  it "dead letters a job with no handler" do
    h = Harness.new({ "TestJob" => ->(_ctx) {} })
    d = FakeDelivery.new(job: { "jobNameInWorker" => "OtherJob" })
    h.handle(d)

    expect(d.settled).to eq(["ack"])
    expect(h.pub.statuses).to eq([s::RUNNING, s::FAILED])
    expect(h.pub.dead_lettered.first[:body]["exception"]).to eq("[PERMANENT FAILURE] No IJob implementation registered for type: OtherJob")
  end

  it "matches job names case-insensitively" do
    h = Harness.new({ "TestJob" => ->(_ctx) { "ok" } })
    h.handle(FakeDelivery.new(job: { "jobNameInWorker" => "testjob" }))

    expect(h.pub.last_status["result"]).to eq("ok")
  end

  it "rejects a body that is not a job" do
    h = Harness.new({ "TestJob" => ->(_ctx) {} })
    fake = FakeDelivery.new
    fake.delivery.body = "not json"
    h.handle(fake)

    expect(fake.settled).to eq(["nack"])
    expect(h.pub.messages).to be_empty
  end

  it "reports a redelivery" do
    h = Harness.new({ "TestJob" => ->(_ctx) {} })
    h.handle(FakeDelivery.new(redelivered: true))

    expect(h.pub.statuses).to eq([s::RUNNING, s::RUNNING, s::COMPLETED])
    expect(h.pub.logs).to include(a_hash_including("category" => "Redelivery", "level" => "Warning"))
  end

  it "retries a final status the broker refused" do
    h = Harness.new({ "TestJob" => ->(_ctx) { h.pub.fail_next = 2 } })
    h.handle(FakeDelivery.new)

    expect(h.pub.statuses).to eq([s::RUNNING, s::COMPLETED])
  end

  it "tracks running jobs for wait_for_idle" do
    release = Queue.new
    h = Harness.new({ "TestJob" => ->(_ctx) { release.pop } })
    thread = h.handle_async(FakeDelivery.new)

    wait_until { h.processor.running == 1 }
    expect(h.processor.wait_for_idle(0.05)).to be(false)

    release << :go
    thread.join(5)
    expect(h.processor.wait_for_idle(1)).to be(true)
  end

  it "sends job heartbeats while a job runs" do
    h = Harness.new({ "TestJob" => ->(ctx) { ctx.sleep(0.35) } }, worker_options: { job_heartbeat_interval: 0.1 })
    h.handle(FakeDelivery.new)

    running = h.pub.statuses.count(s::RUNNING)
    expect(running).to be >= 3
    expect(h.pub.statuses.last).to eq(s::COMPLETED)
  end
end
