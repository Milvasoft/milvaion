# frozen_string_literal: true

module Milvaion
  # Base class for class-based jobs. Each execution gets a new instance.
  #
  #   class SendEmailJob < Milvaion::Job
  #     job_options max_retries: 5, data_schema: { type: "object" }
  #
  #     def perform(ctx)
  #       ctx.log("Sending to #{ctx.data['to']}")
  #       { sent: true }
  #     end
  #   end
  #
  #   worker.job(SendEmailJob)
  class Job
    class << self
      # The name the dashboard dispatches the job by. Defaults to the class name without its namespace.
      def job_name(name = nil)
        @job_name = name.to_s unless name.nil?
        @job_name || self.name.to_s.split("::").last
      end

      # Job options (see JobOptions). Merged with those of the parent class.
      def job_options(**options)
        @job_options = own_job_options.merge(options) unless options.empty?
        inherited = superclass.respond_to?(:job_options) ? superclass.job_options : {}
        inherited.merge(own_job_options)
      end

      private

      def own_job_options
        @job_options || {}
      end
    end

    # Runs the job. The return value is stored as the occurrence result.
    def perform(_ctx)
      raise NotImplementedError, "#{self.class} must implement perform(ctx)"
    end
  end
end
