import Icon from './Icon'
import { formatDateShort } from '../utils/dateUtils'

/**
 * Per-report explanation of what each report measures and why it can be legitimately empty.
 *
 * A report row is only produced for records that match the report's criteria within the analysis window.
 * Some reports also require a minimum number of executions before a job/workflow is included, so in a
 * low-traffic environment they can be empty even though jobs ran. Stating the criteria here turns a blank
 * chart into an understandable "nothing qualified" message.
 */
export const REPORT_CRITERIA = {
  FailureRateTrend: {
    emptyReason: 'No job executions started within the selected period, so there is no hourly failure rate to plot.',
    criteria: 'Groups completed and failed job occurrences by hour. Only occurrences whose start time falls inside the analysis window are counted.'
  },
  PercentileDurations: {
    emptyReason: 'No job ran often enough in this period to compute duration percentiles.',
    criteria: 'Only jobs with at least 10 completed executions (with a measured duration) in the period are included, so infrequent jobs are omitted.'
  },
  TopSlowJobs: {
    emptyReason: 'No job completed with a measured duration in this period.',
    criteria: 'Ranks jobs by average duration. Only completed occurrences that recorded a duration are counted; queued or still-running executions are excluded.'
  },
  WorkerThroughput: {
    emptyReason: 'No executions were attributed to a worker in this period.',
    criteria: 'Groups executions by the worker that processed them. Only occurrences with a WorkerId are counted.'
  },
  WorkerUtilizationTrend: {
    emptyReason: 'No completed, worker-attributed executions in this period.',
    criteria: 'Computes hourly worker utilisation from executions that have both a WorkerId and a measured duration.'
  },
  CronScheduleVsActual: {
    emptyReason: 'No scheduled executions in this period to compare against their planned time.',
    criteria: 'Compares each occurrence’s scheduled fire time with when it actually started. Only occurrences with both timestamps are included.'
  },
  JobHealthScore: {
    emptyReason: 'No job ran often enough in this period to score its reliability.',
    criteria: 'Only jobs with at least 5 executions in the period are scored, so infrequent jobs are omitted.'
  },
  WorkflowSuccessRate: {
    emptyReason: 'No workflow runs finished in this period.',
    criteria: 'Aggregates workflow runs that completed, failed, were partial or cancelled within the period. Runs still in progress are excluded.'
  },
  WorkflowStepBottleneck: {
    emptyReason: 'No workflow step executions in this period.',
    criteria: 'Breaks down average and max step durations per workflow. Requires workflow step occurrences in the period.'
  },
  WorkflowDurationTrend: {
    emptyReason: 'No workflow runs in this period to trend.',
    criteria: 'Plots average workflow run duration over time. Requires workflow runs with a measured duration in the period.'
  }
}

/**
 * Returns true when a parsed report payload has no rows to render, across every report shape
 * (arrays like DataPoints/Jobs/Workers/Workflows, or the object-keyed Jobs map used by percentiles).
 */
export function isReportEmpty(metricType, data) {
  if (!data) return true

  for (const key of ['DataPoints', 'Jobs', 'Workers', 'Workflows']) {
    const value = data[key]
    if (value == null) continue
    if (Array.isArray(value)) return value.length === 0
    if (typeof value === 'object') return Object.keys(value).length === 0
  }

  return false
}

/**
 * Informative empty state shown when a report exists but matched no records. Explains the report's
 * qualifying criteria so an empty result is understood rather than mistaken for a bug.
 */
function ReportEmptyData({ metricType, report }) {
  const info = REPORT_CRITERIA[metricType] || {}

  return (
    <div className="empty-state report-empty-data">
      <Icon name="inbox" size={64} />
      <h3>No data for this period</h3>
      {report && (
        <p className="report-empty-period">
          {formatDateShort(report.periodStartTime)} &ndash; {formatDateShort(report.periodEndTime)}
        </p>
      )}
      <p>{info.emptyReason || 'No records matched this report’s criteria for the selected period.'}</p>
      {info.criteria && (
        <div className="report-criteria">
          <div className="report-criteria-label">
            <Icon name="info" size={16} /> What this report includes
          </div>
          <p>{info.criteria}</p>
        </div>
      )}
    </div>
  )
}

export default ReportEmptyData
