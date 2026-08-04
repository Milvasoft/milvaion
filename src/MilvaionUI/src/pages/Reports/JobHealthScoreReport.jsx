import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import metricReportService from '../../services/metricReportService'
import ReportEmptyData, { isReportEmpty } from '../../components/ReportEmptyData'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { formatDateTime, formatDateShort } from '../../utils/dateUtils'
import './ReportDetail.css'
import { SkeletonChart } from '../../components/Skeleton'

function JobHealthScoreReport() {
  const navigate = useNavigate()
  const [reports, setReports] = useState([])
  const [selectedReport, setSelectedReport] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [deleting, setDeleting] = useState(false)
  const { modalProps, showConfirm } = useModal()
  const [searchQuery, setSearchQuery] = useState('')
  const [minSuccessRate, setMinSuccessRate] = useState(0)
  const [activeFilter, setActiveFilter] = useState(null) // 'all' | 'healthy' | 'warning' | 'critical'

  useEffect(() => {
    fetchReports()
  }, [])

  const fetchReports = async () => {
    try {
      setLoading(true)
      setError(null)
      const response = await metricReportService.getReports('JobHealthScore')
      const reportList = response?.data?.data || response?.data || []

      if (reportList.length > 0) {
        setReports(reportList)
        setSelectedReport(reportList[0])
      }
    } catch (err) {
      setError(err.message || 'Failed to fetch reports')
    } finally {
      setLoading(false)
    }
  }

  const handleDelete = async () => {
    if (!selectedReport) return
    const confirmed = await showConfirm('Are you sure you want to delete this metric report?', 'Delete Report', 'Delete', 'Cancel')
    if (!confirmed) return
    try {
      setDeleting(true)
      await metricReportService.deleteReport(selectedReport.id)
      const remaining = reports.filter(r => r.id !== selectedReport.id)
      setReports(remaining)
      setSelectedReport(remaining.length > 0 ? remaining[0] : null)
    } catch (err) {
      setError(err.message || 'Failed to delete report')
    } finally {
      setDeleting(false)
    }
  }

  const getHealthColor = (successRate) => {
    if (successRate >= 95) return '#10b981'
    if (successRate >= 80) return '#f59e0b'
    return '#ef4444'
  }

  const getHealthLabel = (successRate) => {
    if (successRate >= 95) return 'Excellent'
    if (successRate >= 80) return 'Good'
    if (successRate >= 60) return 'Fair'
    return 'Poor'
  }

  const renderHealthScores = () => {
    if (!selectedReport) return null

    const data = JSON.parse(selectedReport.data)

    if (isReportEmpty('JobHealthScore', data)) return <ReportEmptyData metricType="JobHealthScore" report={selectedReport} />

    const totalCount = data.Jobs.length
    const healthyCount = data.Jobs.filter(j => j.SuccessRate >= 95).length
    const warningCount = data.Jobs.filter(j => j.SuccessRate >= 80 && j.SuccessRate < 95).length
    const criticalCount = data.Jobs.filter(j => j.SuccessRate < 80).length

    const filteredJobs = data.Jobs.filter(job => {
      if (searchQuery && !job.JobName.toLowerCase().includes(searchQuery.toLowerCase())) return false
      if (job.SuccessRate < minSuccessRate) return false
      if (activeFilter === 'healthy' && job.SuccessRate < 95) return false
      if (activeFilter === 'warning' && (job.SuccessRate < 80 || job.SuccessRate >= 95)) return false
      if (activeFilter === 'critical' && job.SuccessRate >= 80) return false
      return true
    })

    const filterCards = [
      { key: 'all', label: 'Total Jobs', count: totalCount, color: '#3b82f6' },
      { key: 'healthy', label: 'Healthy Jobs (>95%)', count: healthyCount, color: '#10b981' },
      { key: 'warning', label: 'Warning Jobs (80-95%)', count: warningCount, color: '#f59e0b' },
      { key: 'critical', label: 'Critical Jobs (<80%)', count: criticalCount, color: '#ef4444' },
    ]

    return (
      <div className="health-scores-container">
        <div className="health-summary">
          {filterCards.map(card => (
            <div
              key={card.key}
              className={`stat-card stat-card-clickable ${activeFilter === card.key || (card.key === 'all' && activeFilter === null) ? 'stat-card-active' : ''}`}
              style={activeFilter === card.key || (card.key === 'all' && activeFilter === null)
                ? { borderColor: card.color, boxShadow: `0 0 0 1px ${card.color}` }
                : { cursor: 'pointer' }}
              onClick={() => setActiveFilter(card.key === 'all' ? null : (activeFilter === card.key ? null : card.key))}
            >
              <div className="stat-label">{card.label}</div>
              <div className="stat-value" style={{ color: card.color }}>
                {card.count}
              </div>
            </div>
          ))}
        </div>

        <div className="health-filters">
          <div className="health-search">
            <Icon name="search" size={18} />
            <input
              type="text"
              placeholder="Search jobs..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
            />
            {searchQuery && (
              <button className="search-clear" onClick={() => setSearchQuery('')}>
                <Icon name="close" size={16} />
              </button>
            )}
          </div>
          <div className="health-slider">
            <label>Min Success Rate: <strong>{minSuccessRate}%</strong></label>
            <input
              type="range"
              min="0"
              max="100"
              value={minSuccessRate}
              onChange={(e) => setMinSuccessRate(Number(e.target.value))}
            />
          </div>
        </div>

        {filteredJobs.length === 0 ? (
          <div className="empty-state">
            <Icon name="filter_list_off" size={48} />
            <h3>No jobs match the filters</h3>
            <p>Try adjusting the search, slider, or card filter</p>
          </div>
        ) : (
          <div className="health-cards-grid">
            {filteredJobs.map((job) => {
              const healthColor = getHealthColor(job.SuccessRate)
              const healthLabel = getHealthLabel(job.SuccessRate)

            return (
              <div key={job.JobName} className="health-card">
                <div className="health-card-header">
                  <h4 title={job.JobName}>
                    {job.JobName.length > 40 ? job.JobName.substring(0, 40) + '...' : job.JobName}
                  </h4>
                  <span
                    className="health-badge"
                    style={{
                      backgroundColor: `${healthColor}20`,
                      color: healthColor,
                      border: `1px solid ${healthColor}`
                    }}
                  >
                    {healthLabel}
                  </span>
                </div>

                <div className="health-gauge">
                  <svg viewBox="0 0 200 120" className="gauge-svg">
                    <defs>
                      <linearGradient id={`gauge-gradient-${job.jobName}`} x1="0%" y1="0%" x2="100%" y2="0%">
                        <stop offset="0%" stopColor="#ef4444" />
                        <stop offset="50%" stopColor="#f59e0b" />
                        <stop offset="100%" stopColor="#10b981" />
                      </linearGradient>
                    </defs>

                    <path
                      d="M 20 100 A 80 80 0 0 1 180 100"
                      fill="none"
                      stroke="var(--border-color)"
                      strokeWidth="12"
                      strokeLinecap="round"
                    />

                    <path
                      d="M 20 100 A 80 80 0 0 1 180 100"
                      fill="none"
                      stroke={`url(#gauge-gradient-${job.JobName})`}
                      strokeWidth="12"
                      strokeLinecap="round"
                      strokeDasharray={`${(job.SuccessRate / 100) * 251.2} 251.2`}
                    />

                    <text
                      x="100"
                      y="80"
                      textAnchor="middle"
                      fontSize="32"
                      fontWeight="bold"
                      fill={healthColor}
                    >
                      {job.SuccessRate.toFixed(1)}%
                    </text>
                  </svg>
                </div>

                <div className="health-stats">
                  <div className="health-stat">
                    <Icon name="check_circle" size={16} />
                    <span className="stat-label">Success:</span>
                    <span className="stat-value">{job.SuccessCount}</span>
                  </div>
                  <div className="health-stat">
                    <Icon name="cancel" size={16} />
                    <span className="stat-label">Failed:</span>
                    <span className="stat-value">{job.FailureCount}</span>
                  </div>
                  <div className="health-stat">
                    <Icon name="pending_actions" size={16} />
                    <span className="stat-label">Total:</span>
                    <span className="stat-value">{job.TotalOccurrences}</span>
                  </div>
                </div>
              </div>
            )
          })}
          </div>
        )}
      </div>
    )
  }

  if (loading) {
    return (
      <div className="page-container">
        <div className="page-header">
          <h1><Icon name="favorite" size={28} /> Job Health Score</h1>
        </div>
        <SkeletonChart bars={14} height={300} />
      </div>
    )
  }

  return (
    <div className="page-container report-detail">
      <div className="page-header">
        <div>
          <button className="btn-back" onClick={() => navigate('/reports')}>
            <Icon name="arrow_back" size={20} />
          </button>
          <h1><Icon name="favorite" size={28} /> Job Health Score</h1>
          <p className="page-description">Success rate for last N executions per job</p>
          {selectedReport?.period && <span className="report-period-badge">{selectedReport.period}</span>}
        </div>
        <div className="page-header-actions">
          {reports.length > 1 && (
            <select
              className="report-select"
              value={selectedReport?.id || ''}
              onChange={(e) => {
                const report = reports.find(r => r.id === e.target.value)
                setSelectedReport(report)
              }}
            >
              {reports.map((report) => (
                <option key={report.id} value={report.id}>
                  {report.period ? report.period + ' • ' : ''}{formatDateTime(report.generatedAt)} ({formatDateShort(report.periodStartTime)} - {formatDateShort(report.periodEndTime)})
                </option>
              ))}
            </select>
          )}
          {selectedReport && (
            <button
              className="btn-delete-report"
              onClick={handleDelete}
              disabled={deleting}
            >
              <Icon name="delete" size={20} />
              {deleting ? 'Deleting...' : 'Delete'}
            </button>
          )}
          <button
            className="btn btn-secondary"
            onClick={fetchReports}
            disabled={loading}
          >
            <Icon name="refresh" size={20} />
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

      {reports.length === 0 ? (
        <div className="empty-state">
          <Icon name="inbox" size={64} />
          <h3>No reports available</h3>
          <p>Reports will appear here once generated by ReporterWorker</p>
        </div>
      ) : (
        <>
          {renderHealthScores()}
        </>
      )}

      <Modal {...modalProps} />
    </div>
  )
}

export default JobHealthScoreReport
