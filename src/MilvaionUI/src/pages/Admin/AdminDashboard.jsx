import { useState, useEffect, useCallback } from 'react'
import api from '../../services/api'
import Icon from '../../components/Icon'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import DatabaseStatistics from '../../components/DatabaseStatistics/DatabaseStatistics'
import ServiceMemoryStats from '../../components/ServiceMemoryStats/ServiceMemoryStats'
import './AdminDashboard.css'

function AdminDashboard() {
  const [healthData, setHealthData] = useState(null)
  const [jobStats, setJobStats] = useState(null)
  const [circuitBreakerStats, setCircuitBreakerStats] = useState(null)
  const [loading, setLoading] = useState(true)
  const [autoRefresh, setAutoRefresh] = useState(() => {
    const saved = localStorage.getItem('adminDashboard_autoRefresh')
    return saved !== null ? saved === 'true' : true
  })
  const [lastRefreshTime, setLastRefreshTime] = useState(null)
  const [emergencyStopDialogOpen, setEmergencyStopDialogOpen] = useState(false)
  const [stopReason, setStopReason] = useState('')

  const loadHealthData = useCallback(async () => {
    try {
      const response = await api.get('/admin/system-health')
      let healthData = response?.data?.data || response?.data || response

      if (healthData) {
        const systemHealthMap = {
          0: 'Healthy',
          1: 'Warning',
          2: 'Critical',
          3: 'Degraded'
        }
        healthData.overallHealth = systemHealthMap[healthData.overallHealth] || 'Unknown'

        if (healthData.queueStats) {
          healthData.queueStats = healthData.queueStats.map(queue => ({
            ...queue,
            healthStatus: systemHealthMap[queue.healthStatus] || 'Unknown'
          }))
        }
      }

      setHealthData(healthData)
      setLastRefreshTime(new Date())
    } catch (err) {
      console.error('Failed to load health data:', err)
    }
  }, []) // Empty dependency array

  const loadJobStats = useCallback(async () => {
    try {
      const response = await api.get('/admin/job-stats')
      const statsData = response?.data?.data || response?.data || response
      setJobStats(statsData)
      setLastRefreshTime(new Date())
    } catch (err) {
      console.error('Failed to load job stats:', err)
    }
  }, []) // Empty dependency array

  const loadCircuitBreakerStats = useCallback(async () => {
    try {
      const response = await api.get('/admin/redis-circuit-breaker')
      const statsData = response?.data?.data || response?.data || response
      setCircuitBreakerStats(statsData)
      setLastRefreshTime(new Date())
    } catch (err) {
      console.error('Failed to load circuit breaker stats:', err)
    }
  }, []) // Empty dependency array

  const loadData = useCallback(async () => {
    setLoading(true)
    await Promise.all([loadHealthData(), loadJobStats(), loadCircuitBreakerStats()])
    setLastRefreshTime(new Date())
    setLoading(false)
  }, [loadHealthData, loadJobStats, loadCircuitBreakerStats]) // Now these are stable references

  useEffect(() => {
    loadData()

    let healthInterval
    let statsInterval
    let circuitInterval

    if (autoRefresh) {
      healthInterval = setInterval(() => loadHealthData(), 5000)
      statsInterval = setInterval(() => loadJobStats(), 10000)
      circuitInterval = setInterval(() => loadCircuitBreakerStats(), 5000)
    }

    return () => {
      if (healthInterval) clearInterval(healthInterval)
      if (statsInterval) clearInterval(statsInterval)
      if (circuitInterval) clearInterval(circuitInterval)
    }
  }, [autoRefresh, loadData, loadHealthData, loadJobStats, loadCircuitBreakerStats])

  const handleEmergencyStop = async () => {
    try {
      await api.post(`/admin/jobdispatcher/stop?reason=${encodeURIComponent(stopReason || 'Manual emergency stop')}`)
      setEmergencyStopDialogOpen(false)
      setStopReason('')
      await loadHealthData()
    } catch (err) {
      console.error('Emergency stop failed:', err)
      alert('Failed to trigger emergency stop. Check console for details.')
    }
  }

  const handleResume = async () => {
    try {
      await api.post('/admin/jobdispatcher/resume')
      await loadHealthData()
    } catch (err) {
      console.error('Resume failed:', err)
      alert('Failed to resume operations. Check console for details.')
    }
  }

  const getHealthColor = (status) => {
    switch (status) {
      case 'Healthy': return 'success'
      case 'Warning': return 'warning'
      case 'Critical':
      case 'Degraded': return 'error'
      default: return 'default'
    }
  }

  const getHealthIcon = (status) => {
    switch (status) {
      case 'Healthy': return 'check_circle'
      case 'Warning': return 'warning'
      case 'Critical':
      case 'Degraded': return 'error'
      default: return 'help'
    }
  }

  const getQueueHealthPercent = (messageCount) => {
    const criticalThreshold = 10000
    return Math.min((messageCount / criticalThreshold) * 100, 100)
  }

  if (loading) {
    return <div className="loading">Loading monitoring dashboard...</div>
  }

  return (
    <div className="admin-dashboard">
      {/* Page Header */}
      <div className="page-header">
        <h1 >
          <Icon name="monitor_heart" size={28} />
          System Monitoring & Control
        </h1>
        <div className="header-actions">
          <button className="btn btn-secondary" onClick={loadData} title="Refresh">
            <Icon name="refresh" size={20} />
            Refresh
          </button>
        </div>
      </div>

      {/* System Health Alert */}
      {healthData?.overallHealth !== 'Healthy' && (
        <div className={`alert alert-${getHealthColor(healthData?.overallHealth)}`}>
          <Icon name={getHealthIcon(healthData?.overallHealth)} size={20} />
          <div>
            <strong>System Health: {healthData?.overallHealth}</strong>
            {healthData?.overallHealth === 'Degraded' && !healthData?.dispatcherEnabled && (
              <p>Job dispatcher is currently disabled. System running in degraded mode.</p>
            )}
            {healthData?.queueStats?.some(q => q.healthStatus === 'Critical') && (
              <p>One or more queues are at critical capacity!</p>
            )}
          </div>
        </div>
      )}

      <div className="dashboard-grid">
        {/* System Status Card */}
        <div className="dashboard-card system-status">
          <div className="card-header">
            <h3 >
              <Icon name={getHealthIcon(healthData?.overallHealth)} size={20} />
              System Status
            </h3>
          </div>
          <div className="card-content">
            <div className="status-item">
              <span className="status-label">Overall Health</span>
              <span className={`badge badge-${getHealthColor(healthData?.overallHealth)}`}>
                {healthData?.overallHealth || 'Unknown'}
              </span>
            </div>
            <div className="status-item">
              <span className="status-label">Dispatcher Status</span>
              <span className={`badge badge-${healthData?.dispatcherEnabled ? 'success' : 'error'}`}>
                {healthData?.dispatcherEnabled ? 'ENABLED' : 'DISABLED'}
              </span>
            </div>
            <div className="status-item">
              <span className="status-label">Active Jobs</span>
              <span className="status-value">{healthData?.totalActiveJobs || 0}</span>
            </div>

            <div className="control-buttons">
              {healthData?.dispatcherEnabled ? (
                <button
                  className="btn btn-error"
                  onClick={() => setEmergencyStopDialogOpen(true)}
                >
                  <Icon name="cancel" size={18} />
                  Emergency Stop
                </button>
              ) : (
                <button
                  className="btn btn-success"
                  onClick={handleResume}
                >
                  <Icon name="play_arrow" size={18} />
                  Resume Operations
                </button>
              )}
            </div>
          </div>
        </div>

        {/* Redis Circuit Breaker Card */}
        <div className="dashboard-card circuit-breaker">
          <div className="card-header">
            <h3>
              <Icon name="security" size={20} />
              Redis Circuit Breaker
            </h3>
          </div>
          <div className="card-content">
            <div className="circuit-status">
              <div className="circuit-state-item">
                <span className="status-label">State</span>
                <span className={`badge badge-${getHealthColor(circuitBreakerStats?.healthStatus)}`}>
                  {circuitBreakerStats?.state || 'Unknown'}
                </span>
              </div>
              <div className="circuit-state-item">
                <span className="status-label">Health</span>
                <span className={`badge badge-${getHealthColor(circuitBreakerStats?.healthStatus)}`}>
                  {circuitBreakerStats?.healthStatus || 'Unknown'}
                </span>
              </div>
            </div>

            <div className="circuit-message">
              <p>{circuitBreakerStats?.healthMessage || 'Loading...'}</p>
            </div>

            <div className="stats-grid">
              <div className="stat-box">
                <span className="stat-value success">
                  {circuitBreakerStats?.successRatePercentage?.toFixed(2) || '0.00'}%
                </span>
                <span className="stat-label">Success Rate</span>
              </div>
              <div className="stat-box">
                <span className="stat-value primary">
                  {circuitBreakerStats?.totalOperations?.toLocaleString() || '0'}
                </span>
                <span className="stat-label">Operations</span>
              </div>
              <div className="stat-box">
                <span className="stat-value error">
                  {circuitBreakerStats?.totalFailures || '0'}
                </span>
                <span className="stat-label">Failures</span>
              </div>
              <div className="stat-box">
                <span className="stat-value secondary">
                  {circuitBreakerStats?.timeSinceLastFailure || 'N/A'}
                </span>
                <span className="stat-label">Last Failure</span>
              </div>
            </div>

            {circuitBreakerStats?.healthStatus === 'Critical' && (
              <div className="alert alert-error" style={{ marginTop: '1rem' }}>
                <Icon name="error" size={16} />
                <div>
                  <strong>Action Required</strong>
                  <p>{circuitBreakerStats?.recommendation}</p>
                </div>
              </div>
            )}

            {circuitBreakerStats?.healthStatus === 'Warning' && (
              <div className="alert alert-warning" style={{ marginTop: '1rem' }}>
                <Icon name="warning" size={16} />
                <div>
                  <strong>Note</strong>
                  <p>{circuitBreakerStats?.recommendation}</p>
                </div>
              </div>
            )}
          </div>
        </div>

        {/* Job Statistics Card */}
        <div className="dashboard-card job-statistics">
          <div className="card-header">
            <h3>
              <Icon name="assessment" size={20} />
              Job Statistics
            </h3>
          </div>
          <div className="card-content">
            <div className="stats-grid">
              <div className="stat-box">
                <span className="stat-value primary">{jobStats?.totalJobs || 0}</span>
                <span className="stat-label">Total Jobs</span>
              </div>
              <div className="stat-box">
                <span className="stat-value success">{jobStats?.activeJobs || 0}</span>
                <span className="stat-label">Active</span>
              </div>
              <div className="stat-box">
                <span className="stat-value secondary">{jobStats?.inactiveJobs || 0}</span>
                <span className="stat-label">Inactive</span>
              </div>
              <div className="stat-box">
                <span className="stat-value info">{jobStats?.recurringJobs || 0}</span>
                <span className="stat-label">Recurring</span>
              </div>
              <div className="stat-box">
                <span className="stat-value warning">{jobStats?.oneTimeJobs || 0}</span>
                <span className="stat-label">One-time</span>
              </div>
            </div>
          </div>
        </div>

        {/* Queue Health Card */}
        <div className="dashboard-card queue-health">
          <div className="card-header">
            <h3>
              <Icon name="storage" size={20} />
              Queue Health
            </h3>
          </div>
          <div className="card-content">
            <div className="queue-table">
              <div className="queue-table-header">
                <div className="queue-col-name">Queue Name</div>
                <div className="queue-col-messages">Messages</div>
                <div className="queue-col-consumers">Consumers</div>
                <div className="queue-col-health">Health</div>
                <div className="queue-col-capacity">Capacity</div>
              </div>
              {healthData?.queueStats?.map((queue) => (
                <div key={queue.queueName} className="queue-table-row">
                  <div className="queue-col-name" data-label="Queue:">{queue.queueName}</div>
                  <div className="queue-col-messages" data-label="Messages:">
                    <span className={queue.messageCount > 5000 ? 'text-error' : queue.messageCount > 1000 ? 'text-warning' : ''}>
                      {queue.messageCount.toLocaleString()}
                    </span>
                  </div>
                  <div className="queue-col-consumers" data-label="Consumers:">{queue.consumerCount}</div>
                  <div className="queue-col-health" data-label="Health:">
                    <span className={`badge badge-${getHealthColor(queue.healthStatus)}`}>
                      {queue.healthStatus}
                    </span>
                  </div>
                  <div className="queue-col-capacity" data-label="Capacity:">
                    <div className="capacity-bar-container">
                      <div
                        className={`capacity-bar capacity-${getHealthColor(queue.healthStatus)}`}
                        style={{ width: `${getQueueHealthPercent(queue.messageCount)}%` }}
                      />
                    </div>
                    <span className="capacity-text">
                      {getQueueHealthPercent(queue.messageCount).toFixed(0)}%
                    </span>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>

        {/* Database Statistics Card */}
        <DatabaseStatistics />

        {/* Background Service Memory Statistics Card */}
        <ServiceMemoryStats />
      </div>

      {/* Emergency Stop Dialog */}
      {emergencyStopDialogOpen && (
        <div className="modal-overlay" onClick={() => setEmergencyStopDialogOpen(false)}>
          <div className="modal" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h3>
                <Icon name="error" size={24} />
                Emergency Stop Confirmation
              </h3>
              <button className="modal-close" onClick={() => setEmergencyStopDialogOpen(false)}>
                <Icon name="close" size={20} />
              </button>
            </div>
            <div className="modal-content">
              <div className="alert alert-error">
                <strong>Warning</strong>
                <p>This will disable the job dispatcher. No new jobs will be dispatched until manually resumed.</p>
              </div>
              <div className="form-group">
                <label htmlFor="stopReason">Reason for Emergency Stop</label>
                <textarea
                  id="stopReason"
                  value={stopReason}
                  onChange={(e) => setStopReason(e.target.value)}
                  placeholder="e.g., High queue depth, System maintenance"
                  rows={3}
                />
              </div>
            </div>
            <div className="modal-footer">
              <button
                className="btn btn-secondary"
                onClick={() => setEmergencyStopDialogOpen(false)}
              >
                Cancel
              </button>
              <button
                className="btn btn-error"
                onClick={handleEmergencyStop}
              >
                <Icon name="cancel" size={18} />
                Confirm Emergency Stop
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Auto-refresh indicator */}
      <AutoRefreshIndicator
        enabled={autoRefresh}
        onToggle={() => {
          const newValue = !autoRefresh
          setAutoRefresh(newValue)
          localStorage.setItem('adminDashboard_autoRefresh', newValue.toString())
        }}
        lastRefreshTime={lastRefreshTime}
        intervalSeconds={5}
      />
    </div>
  )
}

export default AdminDashboard
