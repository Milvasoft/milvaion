import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { ScatterChart, Scatter, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, ReferenceLine, ZAxis } from 'recharts'
import metricReportService from '../../services/metricReportService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { formatDateTime, formatDateShort } from '../../utils/dateUtils'
import './ReportDetail.css'
import { SkeletonChart } from '../../components/Skeleton'

function CronScheduleVsActualReport() {
  const navigate = useNavigate()
  const [reports, setReports] = useState([])
  const [selectedReport, setSelectedReport] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [deleting, setDeleting] = useState(false)
  const { modalProps, showConfirm } = useModal()
  const [currentPage, setCurrentPage] = useState(1)
  const pageSize = 15

  useEffect(() => {
    fetchReports()
  }, [])

  const fetchReports = async () => {
    try {
      setLoading(true)
      setError(null)
      const response = await metricReportService.getReports('CronScheduleVsActual')
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

    const chartData = data.Jobs.map((job, index) => ({
      jobName: job.JobName,
      deviationSeconds: job.DeviationSeconds,
      scheduledTime: new Date(job.ScheduledTime).getTime(),
      actualTime: new Date(job.ActualTime).getTime(),
      index: index
    }))

    const avgDeviation = chartData.reduce((sum, d) => sum + Math.abs(d.deviationSeconds), 0) / chartData.length
    const maxDeviation = Math.max(...chartData.map(d => Math.abs(d.deviationSeconds)))
    const onTimeJobs = chartData.filter(d => Math.abs(d.deviationSeconds) < 10).length

    return (
      <div className="chart-container">
        <div className="chart-stats">
          <div className="stat-card">
            <div className="stat-label">Average Deviation</div>
            <div className="stat-value" style={{ color: '#f59e0b' }}>
              {avgDeviation.toFixed(1)}s
            </div>
          </div>
          <div className="stat-card">
            <div className="stat-label">Max Deviation</div>
            <div className="stat-value" style={{ color: '#ef4444' }}>
              {maxDeviation.toFixed(1)}s
            </div>
          </div>
          <div className="stat-card">
            <div className="stat-label">On-Time Jobs (&lt;10sec)</div>
            <div className="stat-value" style={{ color: '#10b981' }}>
              {onTimeJobs} / {chartData.length}
            </div>
          </div>
        </div>

        <div className="schedule-table">
          <table>
            <thead>
              <tr>
                <th>Job Name</th>
                <th>Scheduled Time</th>
                <th>Actual Time</th>
                <th>Deviation</th>
              </tr>
            </thead>
            <tbody>
              {data.Jobs.slice((currentPage - 1) * pageSize, currentPage * pageSize).map((job, i) => {
                const deviation = job.DeviationSeconds
                const deviationColor = Math.abs(deviation) < 10 ? '#10b981' :
                                      Math.abs(deviation) < 60 ? '#f59e0b' : '#ef4444'

                return (
                  <tr
                    key={job.OccurrenceId || `${job.JobName}-${i}`}
                    className="schedule-row-clickable"
                    onClick={() => job.OccurrenceId && navigate(`/occurrences/${job.OccurrenceId}`)}
                  >
                    <td className="job-name-cell">
                      {job.JobId ? (
                        <a href={`/jobs/${job.JobId}`} onClick={(e) => { e.stopPropagation(); e.preventDefault(); navigate(`/jobs/${job.JobId}`) }} className="table-link">
                          {job.JobName}
                        </a>
                      ) : job.JobName}
                    </td>
                    <td>{formatDateTime(job.ScheduledTime)}</td>
                    <td>{formatDateTime(job.ActualTime)}</td>
                    <td>
                      <span
                        className="deviation-badge"
                        style={{
                          backgroundColor: `${deviationColor}20`,
                          color: deviationColor,
                          border: `1px solid ${deviationColor}`
                        }}
                      >
                        {deviation > 0 ? '+' : ''}{deviation.toFixed(1)}s
                      </span>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>

          {data.Jobs.length > pageSize && (
            <div className="table-pagination">
              <button
                className="btn btn-sm btn-secondary"
                disabled={currentPage === 1}
                onClick={() => setCurrentPage(p => p - 1)}
              >
                <Icon name="chevron_left" size={16} />
              </button>
              <span className="pagination-info">
                {(currentPage - 1) * pageSize + 1}–{Math.min(currentPage * pageSize, data.Jobs.length)} / {data.Jobs.length}
              </span>
              <button
                className="btn btn-sm btn-secondary"
                disabled={currentPage * pageSize >= data.Jobs.length}
                onClick={() => setCurrentPage(p => p + 1)}
              >
                <Icon name="chevron_right" size={16} />
              </button>
            </div>
          )}
        </div>

        <ResponsiveContainer width="100%" height={300}>
          <ScatterChart margin={{ top: 20, right: 30, left: 20, bottom: 20 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border-color)" />
            <XAxis
              type="number"
              dataKey="index"
              name="Job Index"
              stroke="var(--text-secondary)"
              label={{ value: 'Job Index', position: 'insideBottom', offset: -10 }}
            />
            <YAxis
              type="number"
              dataKey="deviationSeconds"
              name="Deviation (s)"
              stroke="var(--text-secondary)"
              label={{ value: 'Deviation (seconds)', angle: -90, position: 'insideLeft' }}
            />
            <ZAxis range={[100, 100]} />
            <Tooltip
              contentStyle={{
                backgroundColor: 'var(--surface-bg)',
                border: '1px solid var(--border-color)',
                borderRadius: '8px'
              }}
              formatter={(value) => [`${Number(value).toFixed(2)}s`, 'Deviation']}
              labelFormatter={(label) => {
                const item = chartData.find(d => d.index === label)
                return item?.jobName || `Job ${label}`
              }}
            />
            <ReferenceLine y={0} stroke="#6b7280" strokeDasharray="3 3" />
            <Scatter
              data={chartData}
              fill="#3b82f6"
              shape={(props) => {
                const { cx, cy, payload } = props
                const color = Math.abs(payload.deviationSeconds) < 10 ? '#10b981' :
                             Math.abs(payload.deviationSeconds) < 60 ? '#f59e0b' : '#ef4444'
                return <circle cx={cx} cy={cy} r={6} fill={color} />
              }}
            />
          </ScatterChart>
        </ResponsiveContainer>
      </div>
    )
  }

  if (loading) {
    return (
      <div className="page-container">
        <div className="page-header">
          <h1><Icon name="schedule" size={28} /> Cron Schedule vs Actual</h1>
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
          <h1><Icon name="schedule" size={28} /> Cron Schedule vs Actual</h1>
          <p className="page-description">Scheduled vs actual execution time deviation</p>
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

export default CronScheduleVsActualReport
