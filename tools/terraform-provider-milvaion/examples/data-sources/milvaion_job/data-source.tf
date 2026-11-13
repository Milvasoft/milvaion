# Look a job up by its display name (it must be unique) or by id.
data "milvaion_job" "cleanup" {
  name = "cleanup-temp-files"
}

output "cleanup_schedule" {
  value = data.milvaion_job.cleanup.cron_expression
}
