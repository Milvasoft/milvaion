import { useState, useEffect, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import metricReportService from '../../services/metricReportService'
import Icon from '../../components/Icon'
import { formatDateTime } from '../../utils/dateUtils'
import '../../components/Modal.css'
import './ReportDashboard.css'
import { SkeletonGrid } from '../../components/Skeleton'

const METRIC_TYPES = [
  {
    type: 'FailureRateTrend',
    name: 'Failure Rate Trend',
    icon: 'trending_down',
    description: 'Error rate changes over time',
    color: '#ef4444'
  },
  {
    type: 'PercentileDurations',
    name: 'P50 / P95 / P99 Durations',
    icon: 'show_chart',
    description: 'Percentile-based duration distribution',
    color: '#3b82f6'
  },
  {
    type: 'TopSlowJobs',
    name: 'Top Slow Jobs',
    icon: 'hourglass_empty',
    description: 'Average duration by job name',
    color: '#f59e0b'
  },
  {
    type: 'WorkerThroughput',
    name: 'Worker Throughput',
    icon: 'speed',
    description: 'Job count processed by each worker',
    color: '#10b981'
  },
  {
    type: 'WorkerUtilizationTrend',
    name: 'Worker Utilization Trend',
    icon: 'timeline',
    description: 'Capacity vs actual utilization rate',
    color: '#06b6d4'
  },
  {
    type: 'CronScheduleVsActual',
    name: 'Cron Schedule vs Actual',
    icon: 'schedule',
    description: 'Scheduled vs actual execution times',
    color: '#ec4899'
  },
  {
    type: 'JobHealthScore',
    name: 'Job Health Score',
    icon: 'favorite',
    description: 'Success rate for each job',
    color: '#8b5cf6'
  },
  {
    type: 'WorkflowSuccessRate',
    name: 'Workflow Success Rate',
    icon: 'account_tree',
    description: 'Success and failure rates for each workflow',
    color: '#6366f1'
  },
  {
    type: 'WorkflowStepBottleneck',
    name: 'Workflow Step Bottleneck',
    icon: 'troubleshoot',
    description: 'Step-level performance analysis per workflow',
    color: '#0ea5e9'
  },
  {
    type: 'WorkflowDurationTrend',
    name: 'Workflow Duration Trend',
    icon: 'timeline',
    description: 'Workflow execution duration over time',
    color: '#14b8a6'
  }
]

function ReportDashboard() {
  const navigate = useNavigate()
  const [latestReports, setLatestReports] = useState({})
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [refreshing, setRefreshing] = useState(false)
  const [cleanupOpen, setCleanupOpen] = useState(false)
  const [retentionDays, setRetentionDays] = useState(30)
  const [cleanupLoading, setCleanupLoading] = useState(false)
  const [cleanupResult, setCleanupResult] = useState(null)

  const fetchLatestReports = useCallback(async () => {
    try {
      setError(null)
      const reports = {}

      await Promise.all(
        METRIC_TYPES.map(async (metric) => {
          try {
            const response = await metricReportService.getLatestReportByType(metric.type)
            if (response?.isSuccess && response.data) {
              reports[metric.type] = response.data
            }
          } catch (err) {
            console.debug(`No report found for ${metric.type}`)
          }
        })
      )

      setLatestReports(reports)
    } catch (err) {
      setError(err.message || 'Failed to fetch reports')
    } finally {
      setLoading(false)
      setRefreshing(false)
    }
  }, [])

  useEffect(() => {
    fetchLatestReports()
  }, [fetchLatestReports])

  const handleRefresh = () => {
    setRefreshing(true)
    fetchLatestReports()
  }

  const handleViewReport = (metricType) => {
    navigate(`/reports/${metricType.toLowerCase()}`)
  }

  const handleCleanup = async () => {
    if (retentionDays < 1) return
    try {
      setCleanupLoading(true)
      setCleanupResult(null)
      const response = await metricReportService.deleteOldReports(retentionDays)
      const deletedCount = response?.data ?? 0
      setCleanupResult({ success: true, count: deletedCount })
      fetchLatestReports()
    } catch (err) {
      setCleanupResult({ success: false, message: err.message || 'Failed to delete old reports' })
    } finally {
      setCleanupLoading(false)
    }
  }

  if (loading) {
    return (
      <div className="page page-container">
        <div className="page-header">
          <h1><Icon name="analytics" size={28} /> Reports</h1>
        </div>
        <SkeletonGrid cards={9} lines={3} />
      </div>
    )
  }

  return (
    <div className="page-container report-dashboard">
      <div className="page-header">
        <div>
          <h1><Icon name="analytics" size={28} /> Reports</h1>
          <p className="page-description">Metric and performance reports</p>
        </div>
        <div className="page-header-actions">
          <button
            className="dtl-btn dtl-btn--secondary"
            onClick={() => { setCleanupResult(null); setCleanupOpen(true) }}
          >
            <Icon name="delete_sweep" size={20} />
            Cleanup
          </button>
          <button
            className="dtl-btn dtl-btn--secondary"
            onClick={handleRefresh}
            disabled={refreshing}
          >
            <Icon name={refreshing ? 'sync' : 'refresh'} size={20} className={refreshing ? 'spinning' : ''} />
            Refresh
          </button>
        </div>
      </div>

      {error && (
        <div className="alert alert-error">
          <Icon name="error" size={20} />
          {error}
        </div>
      )}

      <div className="report-grid">
        {METRIC_TYPES.map((metric) => {
          const report = latestReports[metric.type]
          const hasData = !!report

          return (
            <div
              key={metric.type}
              className={`report-card ${!hasData ? 'no-data' : ''}`}
              onClick={() => hasData && handleViewReport(metric.type)}
              style={{ cursor: hasData ? 'pointer' : 'default' }}
            >
              <div className="report-card-header">
                <div className="report-icon" style={{ backgroundColor: `${metric.color}20`, color: metric.color }}>
                  <Icon name={metric.icon} size={32} />
                </div>
                <div className="report-info">
                  <h3>{metric.name}</h3>
                  <p>{metric.description}</p>
                </div>
              </div>

              {hasData ? (
                <div className="report-card-body">
                  <div className="report-meta">
                    <div className="meta-item">
                      <Icon name="schedule" size={16} />
                      <span>{formatDateTime(report.generatedAt)}</span>
                    </div>
                    <div className="meta-item">
                      <Icon name="date_range" size={16} />
                      <span>
                        {formatDateTime(report.periodStartTime)} - {formatDateTime(report.periodEndTime)}
                      </span>
                    </div>
                  </div>
                  <div className="report-action">
                    <button className="btn btn-sm btn-primary">
                      View Details
                      <Icon name="arrow_forward" size={16} />
                    </button>
                  </div>
                </div>
              ) : (
                <div className="report-card-empty">
                  <Icon name="inbox" size={48} />
                  <p>No data available</p>
                  <small>Report not yet generated</small>
                </div>
              )}
            </div>
          )
        })}
      </div>

      {cleanupOpen && (
        <div className="modal-overlay" onClick={(e) => { if (e.target === e.currentTarget) setCleanupOpen(false) }}>
          <div className="modal-content modal-warning">
            <div className="modal-header">
              <div className="modal-icon">
                <Icon name="delete_sweep" size={32} />
              </div>
              <h3 className="modal-title">Cleanup Old Reports</h3>
              <button className="modal-close-btn" onClick={() => setCleanupOpen(false)}>
                <Icon name="close" size={20} />
              </button>
            </div>
            <div className="modal-body">
              <p className="modal-message">
                Delete metric reports older than the specified number of days.
              </p>
              <div className="cleanup-input-group">
                <label htmlFor="retentionDays">Retention Days</label>
                <input
                  id="retentionDays"
                  type="number"
                  min="1"
                  value={retentionDays}
                  onChange={(e) => setRetentionDays(Number(e.target.value))}
                  className="cleanup-input"
                />
              </div>
              {cleanupResult && (
                <div className={`cleanup-result ${cleanupResult.success ? 'success' : 'error'}`}>
                  <Icon name={cleanupResult.success ? 'check_circle' : 'error'} size={16} />
                  {cleanupResult.success
                    ? `${cleanupResult.count} report(s) deleted successfully.`
                    : cleanupResult.message}
                </div>
              )}
            </div>
            <div className="modal-footer">
              <button className="modal-btn modal-btn-cancel" onClick={() => setCleanupOpen(false)}>
                Cancel
              </button>
              <button
                className="modal-btn modal-btn-confirm modal-btn-warning"
                onClick={handleCleanup}
                disabled={cleanupLoading || retentionDays < 1}
              >
                {cleanupLoading ? 'Deleting...' : 'Delete'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

export default ReportDashboard
