# frozen_string_literal: true

Gem::Specification.new do |spec|
  spec.name = "milvaion-worker"
  # Kept equal to Milvaion::Worker::VERSION by a spec; the publish workflow checks it against the tag.
  spec.version = "1.1.0"
  spec.authors = ["Milvasoft"]
  spec.summary = "Ruby worker SDK for the Milvaion distributed job scheduler."
  spec.description = "Run Milvaion jobs in Ruby: registration, heartbeats, live logs, retries with " \
                     "exponential backoff, dead lettering, timeouts, cancellation and graceful shutdown " \
                     "over RabbitMQ (or Apache Kafka with the rdkafka gem) and Redis."
  spec.homepage = "https://github.com/Milvasoft/milvaion"
  spec.license = "Apache-2.0"
  spec.required_ruby_version = ">= 3.1"

  spec.metadata = {
    "homepage_uri" => spec.homepage,
    "source_code_uri" => "https://github.com/Milvasoft/milvaion/tree/master/sdks/ruby/milvaion-worker",
    "documentation_uri" => "https://github.com/Milvasoft/milvaion/blob/master/sdks/ruby/milvaion-worker/README.md",
    "bug_tracker_uri" => "https://github.com/Milvasoft/milvaion/issues"
  }

  spec.files = Dir["lib/**/*.rb", "README.md", "milvaion-worker.gemspec"]
  spec.require_paths = ["lib"]

  spec.add_dependency "bunny", ">= 2.22", "< 3"
  spec.add_dependency "logger", ">= 1.4"
  spec.add_dependency "redis", ">= 4.8", "< 6"
end
