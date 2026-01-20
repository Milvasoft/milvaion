import { useState, useEffect } from 'react'
import api from '../../services/api'
import Icon from '../../components/Icon'
import './DatabaseStatistics.css'

function DatabaseStatistics() {
  const [dbStats, setDbStats] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)

  useEffect(() => {
    loadDatabaseStats()
  }, [])

  const loadDatabaseStats = async () => {
    try {
      setLoading(true)
      const response = await api.get('/admin/database-statistics')
      const stats = response?.data || response
      setDbStats(stats)
    } catch (err) {
      console.error('Failed to load database statistics:', err)
      setError('Failed to load database statistics')
    } finally {
      setLoading(false)
    }
  }

  const formatBytes = (bytes) => {
    if (!bytes) return '0 B'
    const suffixes = ['B', 'KB', 'MB', 'GB', 'TB']
    const k = 1024
    const i = Math.floor(Math.log(bytes) / Math.log(k))
    return `${(bytes / Math.pow(k, i)).toFixed(2)} ${suffixes[i]}`
  }

  const getStatusBadgeClass = (status) => {
    switch (status) {
      case 2: return 'badge-success' // Success
      case 3: return 'badge-error'   // Failed
      case 4: return 'badge-warning' // Cancelled
      case 1: return 'badge-info'    // Running
      default: return 'badge-secondary' // Pending
    }
  }

  const getStatusLabel = (status) => {
    const statusMap = {
      0: 'Pending',
      1: 'Running',
      2: 'Success',
      3: 'Failed',
      4: 'Cancelled'
    }
    return statusMap[status] || 'Unknown'
  }

  // Process occurrence growth data (moved outside JSX)
  const processOccurrenceGrowth = () => {
    if (!dbStats?.occurrenceGrowth || dbStats.occurrenceGrowth.length === 0) {
      return []
    }

    // Group by day
    const groupedByDay = dbStats.occurrenceGrowth.slice(0, 7).reduce((acc, item) => {
      const dayKey = new Date(item.day).toLocaleDateString()
      if (!acc[dayKey]) {
        acc[dayKey] = { day: dayKey, total: 0, byStatus: {} }
      }
      acc[dayKey].total += item.count
      acc[dayKey].byStatus[item.status] = item.count
      return acc
    }, {})

    // Convert to array and sort
    return Object.values(groupedByDay).sort((a, b) => {
      return new Date(b.day) - new Date(a.day)
    })
  }

  if (loading) {
    return (
      <div className="dashboard-card db-stats-card">
        <div className="card-header">
          <h3>
            <Icon name="storage" size={20} />
            Database Statistics
          </h3>
        </div>
        <div className="card-content">
          <div className="loading-spinner">Loading...</div>
        </div>
      </div>
    )
  }

  if (error) {
    return (
      <div className="dashboard-card db-stats-card">
        <div className="card-header">
          <h3>
            <Icon name="storage" size={20} />
            Database Statistics
          </h3>
        </div>
        <div className="card-content">
          <div className="error-message">{error}</div>
        </div>
      </div>
    )
  }

  const occurrenceGrowthData = processOccurrenceGrowth()

  return (
    <div className="dashboard-card db-stats-card">
      <div className="card-header">
        <h3>
          <Icon name="storage" size={20} />
          Database Statistics
        </h3>
        <button className="refresh-btn-small" onClick={loadDatabaseStats} title="Refresh">
          <Icon name="refresh" size={18} />
        </button>
      </div>
      {/* Total Database Size */}
      <div className="db-stat-summary">
        <div className="stat-item-large">
          <Icon name="database" size={32} className="stat-icon-large" />
          <div>
            <div className="stat-value-large">{dbStats?.totalDatabaseSize || 'N/A'}</div>
            <div className="stat-label">Total Database Size</div>
          </div>
        </div>
      </div>

      <div className="card-content">

        {/* Top Tables */}
        <div className="db-section">
          <h4 className="section-title">
            <Icon name="table_chart" size={18} />
            Top Tables by Size
          </h4>
          <div className="table-list">
            {dbStats?.tableSizes?.slice(0, 5).map((table, index) => (
              <div key={index} className="table-item">
                <div className="table-info">
                  <span className="table-name">{table.tableName}</span>
                  <span className="table-size">{table.size}</span>
                </div>
                <div className="progress-bar">
                  <div
                    className="progress-fill"
                    style={{ width: `${table.percentage}%` }}
                    title={`${table.percentage.toFixed(1)}% of total`}
                  />
                </div>
              </div>
            ))}
          </div>
        </div>

        {/* Occurrence Growth (Last 7 Days) */}
        <div className="db-section">
          <h4 className="section-title">
            <Icon name="trending_up" size={18} />
            Recent Occurrence Activity (7 Days)
          </h4>
          <div className="occurrence-growth">
            {occurrenceGrowthData.length > 0 ? (
              occurrenceGrowthData.map((dayData, index) => (
                <div key={index} className="day-stat">
                  <div className="day-header">
                    <span className="day-label">{dayData.day}</span>
                    <span className="day-total">{dayData.total.toLocaleString()} occurrences</span>
                  </div>
                  <div className="status-badges">
                    {Object.entries(dayData.byStatus).map(([status, count]) => (
                      <span key={status} className={`badge ${getStatusBadgeClass(parseInt(status))}`}>
                        {getStatusLabel(parseInt(status))}: {count}
                      </span>
                    ))}
                  </div>
                </div>
              ))
            ) : (
              <div className="no-data">No recent data available</div>
            )}
          </div>
        </div>

        {/* Large Occurrences Warning */}
        {dbStats?.largeOccurrences && dbStats.largeOccurrences.length > 0 && (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="warning" size={18} />
              Largest Occurrences
            </h4>
            <div className="large-occurrences">
              {dbStats.largeOccurrences.slice(0, 3).map((occ, index) => (
                <div key={index} className="large-occ-item">
                  <div className="occ-header">
                    <span className="occ-job-name">{occ.jobName}</span>
                    <span className={`badge ${getStatusBadgeClass(occ.status)}`}>
                      {getStatusLabel(occ.status)}
                    </span>
                  </div>
                  <div className="occ-sizes">
                    <span>Logs: {formatBytes(occ.logsSize)}</span>
                    <span>Exception: {formatBytes(occ.exceptionSize)}</span>
                    <span className="total-size">Total: {formatBytes(occ.totalSize)}</span>
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

export default DatabaseStatistics
