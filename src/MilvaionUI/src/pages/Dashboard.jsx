import { useState, useEffect, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import dashboardService from '../services/dashboardService'
import Icon from '../components/Icon'
import AutoRefreshIndicator from '../components/AutoRefreshIndicator'
import './Dashboard.css'

function Dashboard() {
  const navigate = useNavigate()
  const [stats, setStats] = useState({
    totalExecutions: 0,
    queuedJobs: 0,
    completedJobs: 0,
    failedJobs: 0,
    cancelledJobs: 0,
    timedOutJobs: 0,
    runningJobs: 0,
    averageDuration: null,
    successRate: 0,
    totalWorkers: 0,
    totalWorkerInstances: 0,
    workerCurrentJobs: 0,
    workerMaxCapacity: 0,
    workerUtilization: 0,
    executionsPerMinute: 0,
    executionsPerSecond: 0,
    peakExecutionsPerMinute: null,
  })

  const [healthChecks, setHealthChecks] = useState([])
  const [overallHealthStatus, setOverallHealthStatus] = useState('Unknown')
  const [loading, setLoading] = useState(true)
  const [isInitialLoad, setIsInitialLoad] = useState(true)
  const [autoRefreshEnabled, setAutoRefreshEnabled] = useState(() => {
    const saved = localStorage.getItem('dashboard_autoRefresh')
    return saved !== null ? saved === 'true' : true
  })
  const [lastRefreshTime, setLastRefreshTime] = useState(null)

  const formatNumber = (num) => {
    if (num === null || num === undefined) return '0'

    const n = typeof num === 'number' ? num : parseFloat(num)
    if (isNaN(n)) return '0'

    if (n >= 1000000000) return `${(n / 1000000000).toFixed(1)}B`
    if (n >= 1000000) return `${(n / 1000000).toFixed(1)}M`
    if (n >= 1000) return `${(n / 1000).toFixed(1)}K`
    return n.toFixed(0)
  }

  const loadDashboardData = useCallback(async (showLoading = false) => {
    try {
      if (showLoading) {
        setLoading(true)
      }

      const [statsResponse, healthResponse] = await Promise.all([
        dashboardService.getStatistics().catch(err => {
          console.error('Stats error:', err)
          return { data: null }
        }),
        dashboardService.getHealthChecks().catch(err => {
          console.error('Health check error:', err)
          return err.response?.data
        })
      ])

      const statsData = statsResponse?.data?.data || statsResponse?.data

      if (statsData) {
        setStats(statsData)
      }

      const healthData = healthResponse

      if (healthData) {
        setHealthChecks(healthData.checks || [])
        setOverallHealthStatus(healthData.status || 'Unknown')
      }

      // Update last refresh time
      setLastRefreshTime(new Date())
    } catch (err) {
      console.error('Failed to load dashboard data:', err)
    } finally {
      if (showLoading) {
        setLoading(false)
        setIsInitialLoad(false)
      }
    }
  }, [])

  useEffect(() => {
    loadDashboardData(isInitialLoad)

    const interval = setInterval(() => {
      if (autoRefreshEnabled) {
        loadDashboardData(false)
      }
    }, 10000)

    return () => clearInterval(interval)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoRefreshEnabled])

  const getHealthStatusBadge = (status) => {
    const statusMap = {
      'Healthy': { icon: 'check_circle', label: 'Healthy', className: 'healthy' },
      'Unhealthy': { icon: 'cancel', label: 'Unhealthy', className: 'unhealthy' },
      'Degraded': { icon: 'warning', label: 'Degraded', className: 'degraded' },
      'Unknown': { icon: 'help', label: 'Unknown', className: 'unknown' },
    }
    const statusInfo = statusMap[status] || statusMap['Unknown']
    return (
      <span className={'health-badge ' + statusInfo.className}>
        <Icon name={statusInfo.icon} size={16} />
        {statusInfo.label}
      </span>
    )
  }

  const getHealthIcon = (name, tags) => {
    const nameLower = name.toLowerCase()
    const tagsLower = tags?.map(t => t.toLowerCase()) || []

    if (nameLower.includes('database') || nameLower.includes('sql') || nameLower.includes('postgres') || tagsLower.includes('database')) {
      return 'storage'
    }
    if (nameLower.includes('redis') || tagsLower.includes('redis') || tagsLower.includes('cache')) {
      return 'flash_on'
    }
    if (nameLower.includes('rabbit') || nameLower.includes('mq') || tagsLower.includes('rabbitmq') || tagsLower.includes('messaging')) {
      return 'message'
    }
    if (nameLower.includes('http') || nameLower.includes('api')) {
      return 'public'
    }
    if (nameLower.includes('disk') || nameLower.includes('storage')) {
      return 'save'
    }
    if (nameLower.includes('memory') || nameLower.includes('ram')) {
      return 'memory'
    }

    return 'build'
  }

  const formatDurationMs = (duration) => {
    if (!duration) return 'N/A'

    if (typeof duration === 'string' && duration.includes(':')) {
      const parts = duration.split(':')
      const hours = parseInt(parts[0])
      const minutes = parseInt(parts[1])
      const secondsParts = parts[2].split('.')
      const seconds = parseInt(secondsParts[0])
      const milliseconds = secondsParts[1] ? parseFloat('0.' + secondsParts[1]) * 1000 : 0

      const totalMs = (hours * 3600000) + (minutes * 60000) + (seconds * 1000) + milliseconds

      if (totalMs < 1) return `${(totalMs * 1000).toFixed(0)}µs`
      if (totalMs < 1000) return `${totalMs.toFixed(1)}ms`
      if (totalMs < 60000) return `${((totalMs / 1000) || 0).toFixed(2)}s`
      return `${Math.floor(totalMs / 60000)}m ${Math.floor((totalMs % 60000) / 1000)}s`
    }

    const ms = typeof duration === 'number' ? duration : parseFloat(duration)
    if (isNaN(ms)) return 'N/A'

    if (ms < 1) return `${(ms * 1000).toFixed(0)}µs`
    if (ms < 1000) return `${Math.round(ms)}ms`
    if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
    return `${Math.floor(ms / 60000)}m ${Math.floor((ms % 60000) / 1000)}s`
  }

  if (loading) {
    return <div className="loading">Loading dashboard...</div>
  }

  return (
    <div className="dashboard">
      {/* Quick Stats Overview */}
      <div className="quick-stats">
        <div className="quick-stat-card primary">
          <div className="quick-stat-icon">
            <Icon name="trending_up" size={32} />
          </div>
          <div className="quick-stat-content">
            <span className="quick-stat-value" title={(stats.totalExecutions || 0).toLocaleString()}>{formatNumber(stats.totalExecutions)}</span>
            <span className="quick-stat-label">Total Executions</span>
          </div>
        </div>
        <div className="quick-stat-card success">
          <div className="quick-stat-icon">
            <Icon name="target" size={32} />
          </div>
          <div className="quick-stat-content">
            <span className="quick-stat-value">{(stats.successRate || 0).toFixed(2)}%</span>
            <span className="quick-stat-label">Success Rate</span>
          </div>
        </div>
        <div className="quick-stat-card warning">
          <div className="quick-stat-icon">
            <Icon name="schedule" size={32} />
          </div>
          <div className="quick-stat-content">
            <span className="quick-stat-value">{formatDurationMs(stats.averageDuration)}</span>
            <span className="quick-stat-label">Avg Duration</span>
          </div>
        </div>
        <div className="quick-stat-card info">
          <div className="quick-stat-icon">
            <Icon name="computer" size={32} />
          </div>
          <div className="quick-stat-content">
            <span className="quick-stat-value" title={(stats.totalWorkers || 0).toLocaleString()}>{formatNumber(stats.totalWorkers)}</span>
            <span className="quick-stat-label">Active Workers</span>
          </div>
        </div>
      </div>

      {/* Main Content Grid */}
      <div className="dashboard-grid">
        {/* Job Status Card */}
        <div className="dashboard-card job-status-card">
          <div className="card-header">
            <h3>
              <Icon name="assignment" size={20} />
              Job Status
            </h3>
            <span className="card-subtitle">Last 7 days</span>
          </div>
          <div className="status-grid">
            <div
              className="status-item clickable"
              onClick={() => navigate('/executions', { state: { filterByStatus: 2 } })}
            >
              <div className="status-icon success">
                <Icon name="schedule" size={24} />
              </div>
              <div className="status-content">
                <span className="status-value" title={(stats.queuedJobs || 0).toLocaleString()}>{formatNumber(stats.queuedJobs)}</span>
                <span className="status-label">Queued</span>
              </div>
            </div>
            <div
              className="status-item clickable"
              onClick={() => navigate('/executions', { state: { filterByStatus: 3 } })}
            >
              <div className="status-icon failed">
                <Icon name="cancel" size={24} />
              </div>
              <div className="status-content">
                <span className="status-value" title={(stats.failedOccurrences || 0).toLocaleString()}>{formatNumber(stats.failedOccurrences)}</span>
                <span className="status-label">Failed</span>
              </div>
            </div>
            <div
              className="status-item clickable"
              onClick={() => navigate('/executions', { state: { filterByStatus: 1 } })}
            >
              <div className="status-icon running">
                <Icon name="sync" size={24} />
              </div>
              <div className="status-content">
                <span className="status-value" title={(stats.runningJobs || 0).toLocaleString()}>{formatNumber(stats.runningJobs)}</span>
                <span className="status-label">Running</span>
              </div>
            </div>
            <div
              className="status-item clickable"
              onClick={() => navigate('/executions', { state: { filterByStatus: 4 } })}
            >
              <div className="status-icon cancelled">
                <Icon name="warning" size={24} />
              </div>
              <div className="status-content">
                <span className="status-value" title={(stats.cancelledJobs || 0).toLocaleString()}>{formatNumber(stats.cancelledJobs)}</span>
                <span className="status-label">Cancelled</span>
              </div>
            </div>
          </div>
        </div>

        {/* Worker Capacity Card */}
        <div className="dashboard-card worker-capacity-card">
          <div className="card-header">
            <h3>
              <Icon name="computer" size={20} />
              Worker Capacity
            </h3>
          </div>
          <div className="capacity-content">
            <div className="capacity-chart">
              <svg viewBox="0 0 120 120" className="circular-progress">
                <circle cx="60" cy="60" r="50" className="progress-bg" />
                <circle
                  cx="60"
                  cy="60"
                  r="50"
                  className={'progress-bar ' + (stats.workerUtilization >= 80 ? 'warning' : 'success')}
                  style={{
                    strokeDasharray: `${(stats.workerUtilization / 100) * 314} 314`
                  }}
                />
                <text x="60" y="60" className="progress-text">
                  {(stats.workerUtilization || 0).toFixed(2)}%
                </text>
              </svg>
            </div>
            <div className="capacity-stats">
              <div className="capacity-stat">
                <span className="capacity-label">Processing</span>
                <span className="capacity-value" title={(stats.workerCurrentJobs || 0).toLocaleString()}>{formatNumber(stats.workerCurrentJobs)}</span>
              </div>
              <div className="capacity-stat">
                <span className="capacity-label">Capacity</span>
                <span className="capacity-value" title={(stats.workerMaxCapacity || 0).toLocaleString()}>{formatNumber(stats.workerMaxCapacity)}</span>
              </div>
              <div className="capacity-stat">
                <span className="capacity-label">Instances</span>
                <span className="capacity-value" title={(stats.totalWorkerInstances || 0).toLocaleString()}>{formatNumber(stats.totalWorkerInstances)}</span>
              </div>
            </div>
          </div>
        </div>

        {/* Throughput Metrics Card */}
        <div className="dashboard-card throughput-card">
          <div className="card-header">
            <h3>
              <Icon name="speed" size={20} />
              Throughput Metrics
            </h3>
            <span className="card-subtitle">Last 7 days average</span>
          </div>
          <div className="throughput-content">
            <div className="throughput-metric">
              <div className="metric-icon primary">
                <Icon name="bolt" size={32} />
              </div>
              <div className="metric-info">
                <span className="metric-value" title={(stats.executionsPerSecond || 0).toFixed(2)}>{(stats.executionsPerSecond || 0).toFixed(2)}</span>
                <span className="metric-label">Executions per Second</span>
              </div>
            </div>
            <div className="throughput-metric">
              <div className="metric-icon success">
                <Icon name="timeline" size={32} />
              </div>
              <div className="metric-info">
                <span className="metric-value" title={(stats.executionsPerMinute || 0).toFixed(2)}>{(stats.executionsPerMinute || 0).toFixed(2)}</span>
                <span className="metric-label">Executions per Minute</span>
              </div>
            </div>
            {stats.peakExecutionsPerMinute && (
              <div className="throughput-metric">
                <div className="metric-icon warning">
                  <Icon name="trending_up" size={32} />
                </div>
                <div className="metric-info">
                  <span className="metric-value" title={(stats.peakExecutionsPerMinute || 0).toLocaleString()}>{formatNumber(stats.peakExecutionsPerMinute)}</span>
                  <span className="metric-label">Peak Exec/min (Last Hour)</span>
                </div>
              </div>
            )}
          </div>
        </div>

        {/* System Health Card */}
        <div className="dashboard-card health-card">
          <div className="card-header">
            <h3>
              <Icon name="health_and_safety" size={20} />
              System Health
            </h3>
            {getHealthStatusBadge(overallHealthStatus)}
          </div>
          <div className="health-list">
            {healthChecks.length === 0 ? (
              <div className="empty-state">No health checks available</div>
            ) : (
              healthChecks.map((check, index) => (
                <div key={index} className="health-item">
                  <div className="health-item-icon">
                    <Icon name={getHealthIcon(check.name, check.tags)} size={24} />
                  </div>
                  <div className="health-item-content">
                    <div className="health-item-name">{check.name}</div>
                    {check.duration && (
                      <div className="health-item-duration">{formatDurationMs(check.duration)}</div>
                    )}
                  </div>
                  <div className="health-item-status">
                    {getHealthStatusBadge(check.status)}
                  </div>
                </div>
              ))
            )}
          </div>
        </div>
      </div>

      {/* Auto-refresh indicator */}
      <AutoRefreshIndicator
        enabled={autoRefreshEnabled}
        onToggle={() => {
          const newValue = !autoRefreshEnabled
          setAutoRefreshEnabled(newValue)
          localStorage.setItem('dashboard_autoRefresh', newValue.toString())
        }}
        lastRefreshTime={lastRefreshTime}
        intervalSeconds={10}
      />
    </div>
  )
}

export default Dashboard

