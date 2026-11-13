# frozen_string_literal: true

# Sample Milvaion worker.
#
#   MILVAION_WORKER_ID=rb-sample-worker \
#   MILVAION_RABBITMQ_URL=amqp://guest:guest@localhost:5672 \
#   MILVAION_REDIS_URL=redis://localhost:6379 \
#   bundle exec ruby examples/basic_worker.rb

require "milvaion-worker"

worker = Milvaion::Worker.new(Milvaion::WorkerOptions.from_env(
  worker_id: ENV.fetch("MILVAION_WORKER_ID", "rb-sample-worker"),
  heartbeat_interval: 5
))

# Block-based job.
worker.job(
  "GreetJob",
  execution_timeout: 60,
  data_schema: {
    type: "object",
    properties: {
      name: { type: "string", description: "Who to greet" },
      delayMs: { type: "integer", description: "How long the job takes" }
    },
    required: ["name"]
  }
) do |ctx|
  data = ctx.data || {}
  raise Milvaion::PermanentJobError, 'Job data needs a "name".' if data["name"].to_s.empty?

  ctx.log("Greeting #{data['name']}", { "attempt" => ctx.retry_count + 1 })

  # Wakes up and raises Milvaion::JobAborted if the job is cancelled or times out.
  ctx.sleep(data.fetch("delayMs", 1000) / 1000.0)

  { greeting: "Hello, #{data['name']}!" }
end

# Class-based job. Each execution gets a new instance.
class FlakyJob < Milvaion::Job
  job_options max_retries: 2, base_retry_delay: 2

  def perform(ctx)
    # Fails on the first attempt to show retries in the dashboard.
    raise "Simulated transient failure" if ctx.retry_count.zero?

    ctx.log("Succeeded on retry")
    nil
  end
end

worker.job(FlakyJob)

worker.run # blocks; SIGINT/SIGTERM shut down gracefully
