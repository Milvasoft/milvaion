import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer } from 'recharts'
import metricReportService from '../../services/metricReportService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { formatDateTime, formatDateShort } from '../../utils/dateUtils'
import './ReportDetail.css'
import { SkeletonChart } from '../../components/Skeleton'

const WORKER_COLORS = [
  '#3b82f6', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6',
  '#ec4899', '#14b8a6', '#f97316', '#06b6d4', '#84cc16'
]

function WorkerUtilizationTrendReport() {
  const navigate = useNavigate()
  const [reports, setReports] = useState([])
  const [selectedReport, setSelectedReport] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [deleting, setDeleting] = useState(false)
  const { modalProps, showConfirm } = useModal()

  useEffect(() => {
    fetchReports()
  }, [])

  const fetchReports = async () => {
    try {
      setLoading(true)
      setError(null)
      const response = await metricReportService.getReports('WorkerUtilizationTrend')
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

  const renderChart = () => {
    if (!selectedReport) return null

    const data = JSON.parse(selectedReport.data)

    const chartData = data.DataPoints.map(point => ({
      time: new Date(point.Timestamp).toLocaleTimeString('tr-TR', {
        hour: '2-digit',
        minute: '2-digit',
        day: '2-digit',
        month: 'short'
      }),
      timestamp: point.Timestamp,
      ...point.WorkerUtilization
    }))

    const workerIds = data.DataPoints.length > 0
      ? Object.keys(data.DataPoints[0].WorkerUtilization)
      : []

    return (
      <div className="chart-container">
        <div className="chart-stats">
          <div className="stat-card">
            <div className="stat-label">Workers Tracked</div>
            <div className="stat-value" style={{ color: '#3b82f6' }}>
              {workerIds.length}
            </div>
          </div>
          <div className="stat-card">
            <div className="stat-label">Time Points</div>
            <div className="stat-value" style={{ color: '#10b981' }}>
              {chartData.length}
            </div>
          </div>
        </div>

        <ResponsiveContainer width="100%" height={400}>
          <LineChart data={chartData} margin={{ top: 20, right: 30, left: 20, bottom: 60 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border-color)" />
            <XAxis
              dataKey="time"
              stroke="var(--text-secondary)"
              style={{ fontSize: '0.75rem' }}
              angle={-45}
              textAnchor="end"
              height={100}
            />
            <YAxis
              stroke="var(--text-secondary)"
              style={{ fontSize: '0.875rem' }}
              label={{ value: 'Utilization (%)', angle: -90, position: 'insideLeft' }}
              domain={[0, 100]}
            />
            <Tooltip
              contentStyle={{
                backgroundColor: 'var(--surface-bg)',
                border: '1px solid var(--border-color)',
                borderRadius: '8px'
              }}
              formatter={(value) => [`${Number(value).toFixed(2)}%`, 'Utilization']}
            />
            <Legend />
            {workerIds.map((workerId, index) => (
              <Line
                key={workerId}
                type="monotone"
                dataKey={workerId}
                stroke={WORKER_COLORS[index % WORKER_COLORS.length]}
                strokeWidth={2}
                dot={{ r: 3 }}
                activeDot={{ r: 5 }}
                name={workerId}
              />
            ))}
          </LineChart>
        </ResponsiveContainer>
      </div>
    )
  }

  if (loading) {
    return (
      <div className="page-container">
        <div className="page-header">
          <h1><Icon name="timeline" size={28} /> Worker Utilization Trend</h1>
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
          <h1><Icon name="timeline" size={28} /> Worker Utilization Trend</h1>
          <p className="page-description">Capacity vs actual utilization rate (time series)</p>
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
                  {formatDateTime(report.generatedAt)} ({formatDateShort(report.periodStartTime)} - {formatDateShort(report.periodEndTime)})
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
          {renderChart()}
        </>
      )}

      <Modal {...modalProps} />
    </div>
  )
}

export default WorkerUtilizationTrendReport
