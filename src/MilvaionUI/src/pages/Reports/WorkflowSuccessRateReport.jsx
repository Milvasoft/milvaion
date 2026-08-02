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

function WorkflowSuccessRateReport() {
  const navigate = useNavigate()
  const [reports, setReports] = useState([])
  const [selectedReport, setSelectedReport] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [deleting, setDeleting] = useState(false)
  const { modalProps, showConfirm } = useModal()
  const [searchQuery, setSearchQuery] = useState('')
  const [activeFilter, setActiveFilter] = useState(null)

  useEffect(() => {
    fetchReports()
  }, [])

  const fetchReports = async () => {
    try {
      setLoading(true)
      setError(null)
      const response = await metricReportService.getReports('WorkflowSuccessRate')
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

  const getHealthColor = (rate) => {
    if (rate >= 95) return '#10b981'
    if (rate >= 80) return '#f59e0b'
    return '#ef4444'
  }

  const getHealthLabel = (rate) => {
    if (rate >= 95) return 'Healthy'
    if (rate >= 80) return 'Warning'
    return 'Critical'
  }

  const renderContent = () => {
    if (!selectedReport) return null

    const data = JSON.parse(selectedReport.data)

    if (isReportEmpty('WorkflowSuccessRate', data)) return <ReportEmptyData metricType="WorkflowSuccessRate" report={selectedReport} />
    const workflows = data.Workflows

    const totalCount = workflows.length
    const healthyCount = workflows.filter(w => ((w.CompletedCount + w.PartialCount) * 100 / w.TotalRuns) >= 95).length
    const warningCount = workflows.filter(w => { const r = (w.CompletedCount + w.PartialCount) * 100 / w.TotalRuns; return r >= 80 && r < 95 }).length
    const criticalCount = workflows.filter(w => ((w.CompletedCount + w.PartialCount) * 100 / w.TotalRuns) < 80).length

    const filteredWorkflows = workflows.filter(w => {
      if (searchQuery && !w.WorkflowName.toLowerCase().includes(searchQuery.toLowerCase())) return false
      const rate = w.TotalRuns > 0 ? (w.CompletedCount + w.PartialCount) * 100 / w.TotalRuns : 0
      if (activeFilter === 'healthy' && rate < 95) return false
      if (activeFilter === 'warning' && (rate < 80 || rate >= 95)) return false
      if (activeFilter === 'critical' && rate >= 80) return false
      return true
    })

    const filterCards = [
      { key: 'all', label: 'Total Workflows', count: totalCount, color: '#3b82f6' },
      { key: 'healthy', label: 'Healthy (>95%)', count: healthyCount, color: '#10b981' },
      { key: 'warning', label: 'Warning (80-95%)', count: warningCount, color: '#f59e0b' },
      { key: 'critical', label: 'Critical (<80%)', count: criticalCount, color: '#ef4444' },
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
                : {}}
              onClick={() => setActiveFilter(card.key === 'all' ? null : (activeFilter === card.key ? null : card.key))}
            >
              <div className="stat-label">{card.label}</div>
              <div className="stat-value" style={{ color: card.color }}>{card.count}</div>
            </div>
          ))}
        </div>

        <div className="health-filters">
          <div className="health-search">
            <Icon name="search" size={18} />
            <input
              type="text"
              placeholder="Search workflows..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
            />
            {searchQuery && (
              <button className="search-clear" onClick={() => setSearchQuery('')}>
                <Icon name="close" size={16} />
              </button>
            )}
          </div>
        </div>

        {filteredWorkflows.length === 0 ? (
          <div className="empty-state">
            <Icon name="filter_list_off" size={48} />
            <h3>No workflows match the filters</h3>
          </div>
        ) : (
          <div className="health-cards-grid">
            {filteredWorkflows.map((wf) => {
              const rate = wf.TotalRuns > 0 ? (wf.CompletedCount + wf.PartialCount) * 100 / wf.TotalRuns : 0
              const healthColor = getHealthColor(rate)

              return (
                <div
                  key={wf.WorkflowId}
                  className="health-card schedule-row-clickable"
                  onClick={() => navigate(`/workflows/${wf.WorkflowId}`)}
                >
                  <div className="health-card-header">
                    <h4 title={wf.WorkflowName}>{wf.WorkflowName}</h4>
                    <span
                      className="health-badge"
                      style={{
                        backgroundColor: `${healthColor}20`,
                        color: healthColor,
                        border: `1px solid ${healthColor}`
                      }}
                    >
                      {getHealthLabel(rate)}
                    </span>
                  </div>

                  <div className="health-gauge">
                    <svg viewBox="0 0 200 120" className="gauge-svg">
                      <defs>
                        <linearGradient id={`wf-gauge-${wf.WorkflowId}`} x1="0%" y1="0%" x2="100%" y2="0%">
                          <stop offset="0%" stopColor="#ef4444" />
                          <stop offset="50%" stopColor="#f59e0b" />
                          <stop offset="100%" stopColor="#10b981" />
                        </linearGradient>
                      </defs>
                      <path d="M 20 100 A 80 80 0 0 1 180 100" fill="none" stroke="var(--border-color)" strokeWidth="12" strokeLinecap="round" />
                      <path d="M 20 100 A 80 80 0 0 1 180 100" fill="none" stroke={`url(#wf-gauge-${wf.WorkflowId})`} strokeWidth="12" strokeLinecap="round" strokeDasharray={`${(rate / 100) * 251.2} 251.2`} />
                      <text x="100" y="80" textAnchor="middle" fontSize="32" fontWeight="bold" fill={healthColor}>
                        {rate.toFixed(1)}%
                      </text>
                    </svg>
                  </div>

                  <div className="health-stats">
                    <div className="health-stat">
                      <Icon name="check_circle" size={16} />
                      <span className="stat-label">Completed:</span>
                      <span className="stat-value">{wf.CompletedCount}</span>
                    </div>
                    <div className="health-stat">
                      <Icon name="warning" size={16} />
                      <span className="stat-label">Partial:</span>
                      <span className="stat-value">{wf.PartialCount}</span>
                    </div>
                    <div className="health-stat">
                      <Icon name="cancel" size={16} />
                      <span className="stat-label">Failed:</span>
                      <span className="stat-value">{wf.FailedCount}</span>
                    </div>
                    <div className="health-stat">
                      <Icon name="timer" size={16} />
                      <span className="stat-label">Avg Duration:</span>
                      <span className="stat-value">{(wf.AvgDurationMs / 1000).toFixed(1)}s</span>
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
          <h1><Icon name="account_tree" size={28} /> Workflow Success Rate</h1>
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
          <h1><Icon name="account_tree" size={28} /> Workflow Success Rate</h1>
          <p className="page-description">Success and failure rates for each workflow</p>
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
          <button className="btn btn-secondary" onClick={fetchReports} disabled={loading}>
            <Icon name="refresh" size={20} /> Refresh
          </button>
        </div>
      </div>

      {error && (
        <div className="alert alert-error"><Icon name="error" size={20} /> {error}</div>
      )}

      {reports.length === 0 ? (
        <div className="empty-state">
          <Icon name="inbox" size={64} />
          <h3>No reports available</h3>
          <p>Reports will appear here once generated by ReporterWorker</p>
        </div>
      ) : renderContent()}

      <Modal {...modalProps} />
    </div>
  )
}

export default WorkflowSuccessRateReport
