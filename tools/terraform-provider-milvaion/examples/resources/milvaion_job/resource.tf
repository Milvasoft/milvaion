# A recurring job.
resource "milvaion_job" "invoice_report" {
  name            = "nightly-invoice-report"
  description     = "Sends yesterday's invoices to finance"
  tags            = ["billing", "reports"]
  worker_id       = "reporter-worker"
  job_name        = "InvoiceReportJob"
  cron_expression = "0 0 2 * * *" # six fields, seconds first

  job_data = jsonencode({
    recipients = ["finance@example.com"]
    format     = "pdf"
  })

  concurrent_execution_policy = "Skip"
  execution_timeout_seconds   = 900

  auto_disable = {
    threshold              = 5
    failure_window_minutes = 60
  }
}

# A one-time job.
resource "milvaion_job" "migration" {
  name       = "one-off-migration"
  worker_id  = "sample-worker"
  job_name   = "MigrateJob"
  execute_at = "2030-12-01T22:00:00Z"
}
