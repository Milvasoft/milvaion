data "milvaion_worker" "reporter" {
  worker_id = "reporter-worker"
}

resource "milvaion_job" "report" {
  name            = "nightly-report"
  worker_id       = data.milvaion_worker.reporter.worker_id
  job_name        = "ReportJob"
  cron_expression = "0 0 2 * * *"

  lifecycle {
    precondition {
      condition     = contains(data.milvaion_worker.reporter.job_names, "ReportJob")
      error_message = "reporter-worker does not offer ReportJob."
    }
  }
}
