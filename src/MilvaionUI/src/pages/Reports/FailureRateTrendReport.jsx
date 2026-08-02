import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer, ReferenceLine } from 'recharts'
import metricReportService from '../../services/metricReportService'
import ReportEmptyData, { isReportEmpty } from '../../components/ReportEmptyData'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { formatDateTime, formatDateShort } from '../../utils/dateUtils'
import './ReportDetail.css'
import { SkeletonChart } from '../../components/Skeleton'

function FailureRateTrendReport() {
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
      const response = await metricReportService.getReports('FailureRateTrend')
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

    if (isReportEmpty('FailureRateTrend', data)) return <ReportEmptyData metricType="FailureRateTrend" report={selectedReport} />
    const chartData = data.DataPoints.map(point => ({
      time: new Date(point.Timestamp).toLocaleTimeString('tr-TR', {
        hour: '2-digit',
        minute: '2-digit',
        day: '2-digit',
        month: 'short'
      }),
      failureRate: Number(point.Value.toFixed(2)),
      timestamp: point.Timestamp
    }))

    const maxRate = Math.max(...chartData.map(d => d.failureRate))
    const avgRate = chartData.reduce((sum, d) => sum + d.failureRate, 0) / chartData.length

    return (
      <div className="chart-container">
        <div className="chart-stats">
          <div className="stat-card">
            <div className="stat-label">Average Failure Rate</div>
            <div className="stat-value" style={{ color: '#f59e0b' }}>
              {avgRate.toFixed(2)}%
            </div>
          </div>
          <div className="stat-card">
            <div className="stat-label">Peak Failure Rate</div>
            <div className="stat-value" style={{ color: '#ef4444' }}>
              {maxRate.toFixed(2)}%
            </div>
          </div>
          <div className="stat-card">
            <div className="stat-label">Threshold</div>
            <div className="stat-value" style={{ color: '#6b7280' }}>
              {data.ThresholdPercentage}%
            </div>
          </div>
          <div className="stat-card">
            <div className="stat-label">Data Points</div>
            <div className="stat-value" style={{ color: '#3b82f6' }}>
              {chartData.length}
            </div>
          </div>
        </div>

        <ResponsiveContainer width="100%" height={400}>
          <LineChart data={chartData} margin={{ top: 20, right: 30, left: 20, bottom: 20 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border-color)" />
            <XAxis
              dataKey="time"
              stroke="var(--text-secondary)"
              style={{ fontSize: '0.75rem' }}
              angle={-45}
              textAnchor="end"
              height={80}
            />
            <YAxis
              stroke="var(--text-secondary)"
              style={{ fontSize: '0.875rem' }}
              label={{ value: 'Failure Rate (%)', angle: -90, position: 'insideLeft' }}
            />
            <Tooltip
              contentStyle={{
                backgroundColor: 'var(--surface-bg)',
                border: '1px solid var(--border-color)',
                borderRadius: '8px'
              }}
              formatter={(value) => [`${value}%`, 'Failure Rate']}
            />
            <Legend />
            <ReferenceLine
              y={data.ThresholdPercentage}
              stroke="#ef4444"
              strokeDasharray="5 5"
              label="Threshold"
            />
            <Line
              type="monotone"
              dataKey="failureRate"
              stroke="#ef4444"
              strokeWidth={2}
              dot={{ r: 4 }}
              activeDot={{ r: 6 }}
              name="Failure Rate (%)"
            />
          </LineChart>
        </ResponsiveContainer>
      </div>
    )
  }

  if (loading) {
    return (
      <div className="page-container">
        <div className="page-header">
          <h1><Icon name="trending_down" size={28} /> Failure Rate Trend</h1>
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
          <h1><Icon name="trending_down" size={28} /> Failure Rate Trend</h1>
          <p className="page-description">Error rate changes over time</p>
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
          {renderChart()}
        </>
      )}

      <Modal {...modalProps} />
    </div>
  )
}

export default FailureRateTrendReport
