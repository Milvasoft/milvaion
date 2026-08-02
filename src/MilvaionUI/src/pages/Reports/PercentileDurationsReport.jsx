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

function PercentileDurationsReport() {
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
      const response = await metricReportService.getReports('PercentileDurations')
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

    if (isReportEmpty('PercentileDurations', data)) return <ReportEmptyData metricType="PercentileDurations" report={selectedReport} />
    const chartData = Object.entries(data.Jobs).map(([jobName, percentiles]) => ({
      jobName: jobName.length > 30 ? jobName.substring(0, 30) + '...' : jobName,
      fullJobName: jobName,
      P50: Number((percentiles.P50 / 1000).toFixed(2)),
      P95: Number((percentiles.P95 / 1000).toFixed(2)),
      P99: Number((percentiles.P99 / 1000).toFixed(2))
    }))

    const totalJobs = chartData.length

    return (
      <div className="chart-container">
        <div className="chart-stats">
          <div className="stat-card">
            <div className="stat-label">Total Jobs Analyzed</div>
            <div className="stat-value" style={{ color: '#3b82f6' }}>
              {totalJobs}
            </div>
          </div>
        </div>

        <ResponsiveContainer width="100%" height={Math.max(400, totalJobs * 60)}>
          <BarChart
            data={chartData}
            layout="vertical"
            margin={{ top: 20, right: 30, left: 180, bottom: 20 }}
          >
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border-color)" />
            <XAxis
              type="number"
              stroke="var(--text-secondary)"
              label={{ value: 'Duration (seconds)', position: 'insideBottom', offset: -10 }}
            />
            <YAxis
              type="category"
              dataKey="jobName"
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
                const item = chartData.find(d => d.jobName === label)
                return item?.fullJobName || label
              }}
            />
            <Legend />
            <Bar dataKey="P50" fill="#10b981" name="P50" />
            <Bar dataKey="P95" fill="#f59e0b" name="P95" />
            <Bar dataKey="P99" fill="#ef4444" name="P99" />
          </BarChart>
        </ResponsiveContainer>
      </div>
    )
  }

  if (loading) {
    return (
      <div className="page-container">
        <div className="page-header">
          <h1><Icon name="show_chart" size={28} /> P50 / P95 / P99 Durations</h1>
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
          <h1><Icon name="show_chart" size={28} /> P50 / P95 / P99 Durations</h1>
          <p className="page-description">Percentile-based duration distribution</p>
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

export default PercentileDurationsReport
