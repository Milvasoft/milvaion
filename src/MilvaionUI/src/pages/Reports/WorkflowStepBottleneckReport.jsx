import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer } from 'recharts'
import metricReportService from '../../services/metricReportService'
import ReportEmptyData, { isReportEmpty } from '../../components/ReportEmptyData'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { formatDateTime, formatDateShort } from '../../utils/dateUtils'
import './ReportDetail.css'
import { SkeletonChart } from '../../components/Skeleton'

const STEP_COLORS = ['#6366f1', '#ef4444', '#f59e0b', '#10b981', '#3b82f6', '#8b5cf6', '#ec4899', '#14b8a6']

function WorkflowStepBottleneckReport() {
  const navigate = useNavigate()
  const [reports, setReports] = useState([])
  const [selectedReport, setSelectedReport] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [deleting, setDeleting] = useState(false)
  const { modalProps, showConfirm } = useModal()
  const [selectedWorkflow, setSelectedWorkflow] = useState(null)

  useEffect(() => {
    fetchReports()
  }, [])

  const fetchReports = async () => {
    try {
      setLoading(true)
      setError(null)
      const response = await metricReportService.getReports('WorkflowStepBottleneck')
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

  const renderContent = () => {
    if (!selectedReport) return null

    const data = JSON.parse(selectedReport.data)

    if (isReportEmpty('WorkflowStepBottleneck', data)) return <ReportEmptyData metricType="WorkflowStepBottleneck" report={selectedReport} />
    const workflows = data.Workflows

    if (workflows.length === 0) {
      return (
        <div className="empty-state">
          <Icon name="inbox" size={48} />
          <h3>No workflow step data</h3>
        </div>
      )
    }

    const activeWorkflow = selectedWorkflow
      ? workflows.find(w => w.WorkflowId === selectedWorkflow)
      : workflows[0]

    const chartData = activeWorkflow?.Steps.map(step => ({
      stepName: step.StepName.length > 25 ? step.StepName.substring(0, 25) + '...' : step.StepName,
      fullStepName: step.StepName,
      avgDuration: Number((step.AvgDurationMs / 1000).toFixed(2)),
      maxDuration: Number((step.MaxDurationMs / 1000).toFixed(2)),
      executions: step.ExecutionCount,
      failures: step.FailureCount,
      skipped: step.SkippedCount,
      retries: step.RetryCount,
    })) || []

    const totalSteps = activeWorkflow?.Steps.length || 0
    const bottleneck = chartData.length > 0 ? chartData[0] : null
    const totalFailures = chartData.reduce((sum, s) => sum + s.failures, 0)
    const totalRetries = chartData.reduce((sum, s) => sum + s.retries, 0)

    return (
      <div className="chart-container">
        {workflows.length > 1 && (
          <div className="report-selector" style={{ background: 'transparent', border: 'none', padding: 0 }}>
            <label><Icon name="account_tree" size={16} /> Workflow:</label>
            <select
              value={activeWorkflow?.WorkflowId || ''}
              onChange={(e) => setSelectedWorkflow(e.target.value)}
            >
              {workflows.map((wf) => (
                <option key={wf.WorkflowId} value={wf.WorkflowId}>{wf.WorkflowName}</option>
              ))}
            </select>
          </div>
        )}

        <div className="chart-stats">
          <div className="stat-card">
            <div className="stat-label">Bottleneck Step</div>
            <div className="stat-value" style={{ color: '#ef4444', fontSize: '1.25rem' }}>
              {bottleneck?.fullStepName || 'N/A'}
            </div>
            <div className="stat-detail">{bottleneck?.avgDuration}s avg</div>
          </div>
          <div className="stat-card">
            <div className="stat-label">Total Steps</div>
            <div className="stat-value" style={{ color: '#3b82f6' }}>{totalSteps}</div>
          </div>
          <div className="stat-card">
            <div className="stat-label">Total Failures</div>
            <div className="stat-value" style={{ color: '#ef4444' }}>{totalFailures}</div>
          </div>
          <div className="stat-card">
            <div className="stat-label">Total Retries</div>
            <div className="stat-value" style={{ color: '#f59e0b' }}>{totalRetries}</div>
          </div>
        </div>

        <ResponsiveContainer width="100%" height={Math.max(400, totalSteps * 60)}>
          <BarChart data={chartData} layout="vertical" margin={{ top: 20, right: 30, left: 180, bottom: 20 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border-color)" />
            <XAxis
              type="number"
              stroke="var(--text-secondary)"
              label={{ value: 'Duration (seconds)', position: 'insideBottom', offset: -10 }}
            />
            <YAxis
              type="category"
              dataKey="stepName"
              stroke="var(--text-secondary)"
              width={170}
              style={{ fontSize: '0.75rem' }}
            />
            <Tooltip
              contentStyle={{
                backgroundColor: 'var(--surface-bg)',
                border: '1px solid var(--border-color)',
                borderRadius: '8px'
              }}
              formatter={(value, name) => [`${value}s`, name]}
              labelFormatter={(label) => {
                const item = chartData.find(d => d.stepName === label)
                return item ? `${item.fullStepName} (${item.executions} runs, ${item.failures} fails, ${item.retries} retries)` : label
              }}
            />
            <Legend />
            <Bar dataKey="avgDuration" fill="#6366f1" name="Avg Duration (s)" />
            <Bar dataKey="maxDuration" fill="#ef444480" name="Max Duration (s)" />
          </BarChart>
        </ResponsiveContainer>
      </div>
    )
  }

  if (loading) {
    return (
      <div className="page-container">
        <div className="page-header">
          <h1><Icon name="troubleshoot" size={28} /> Workflow Step Bottleneck</h1>
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
          <h1><Icon name="troubleshoot" size={28} /> Workflow Step Bottleneck</h1>
          <p className="page-description">Step-level performance analysis per workflow</p>
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

export default WorkflowStepBottleneckReport
