import { useState, useEffect, useCallback } from 'react'
import workerService from '../../services/workerService'
import Icon from '../../components/Icon'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { formatDateTime, formatTimeSince } from '../../utils/dateUtils'
import './WorkerList.css'

function WorkerList() {
  const [workers, setWorkers] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [expandedWorker, setExpandedWorker] = useState(null)
  const [autoRefreshEnabled, setAutoRefreshEnabled] = useState(true)
  const [lastRefreshTime, setLastRefreshTime] = useState(null)

  const loadWorkers = useCallback(async () => {
    try {
      setError(null)
      const response = await workerService.getAll()

      let workerData = []
      if (response.data?.data) {
        workerData = response.data.data
      } else if (Array.isArray(response.data)) {
        workerData = response.data
      }

      setWorkers(workerData)
      setLastRefreshTime(new Date())
    } catch (err) {
      setError('Failed to load workers')
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    loadWorkers()

    // Auto-refresh workers every 30 seconds
    const refreshInterval = setInterval(() => {
      if (autoRefreshEnabled) {
        loadWorkers()
      }
    }, 30000)

    // Update current time every second for real-time "time ago" display
    const timeInterval = setInterval(() => {
      // Force re-render for time updates
      setWorkers(prev => [...prev])
    }, 1000)

    return () => {
      clearInterval(refreshInterval)
      clearInterval(timeInterval)
    }
  }, [loadWorkers, autoRefreshEnabled])

  const getStatusBadge = (status) => {
    const statusMap = {
      'Active': { icon: 'check_circle', label: 'Active', className: 'active' },
      'Inactive': { icon: 'cancel', label: 'Inactive', className: 'inactive' },
      'Zombie': { icon: 'dangerous', label: 'Zombie', className: 'zombie' },
      0: { icon: 'check_circle', label: 'Active', className: 'active' },
      1: { icon: 'cancel', label: 'Inactive', className: 'inactive' },
      2: { icon: 'dangerous', label: 'Zombie', className: 'zombie' },
    }
    const statusInfo = statusMap[status] || { icon: 'help', label: status, className: 'unknown' }
    return (
      <span className={`status-badge ${statusInfo.className}`}>
        <Icon name={statusInfo.icon} size={16} />
        {statusInfo.label}
      </span>
    )
  }

  const parseMetadata = (metadata) => {
    try {
      return JSON.parse(metadata)
    } catch {
      return null
    }
  }

  const toggleExpand = (workerId) => {
    setExpandedWorker(expandedWorker === workerId ? null : workerId)
  }

  if (loading) return <div className="loading">Loading workers...</div>
  if (error) return <div className="error">{error}</div>

  return (
    <div className="worker-list-page">
      <div className="page-header">
        <h1>
          <Icon name="engineering" size={28} />
          Workers
        </h1>
        <div className="header-actions">
          <button onClick={loadWorkers} className="btn btn-secondary">
            <Icon name="refresh" size={18} />
            Refresh
          </button>
        </div>
      </div>

      <div className="worker-stats">
        <div className="stat-card">
          <div className="stat-icon">
            <Icon name="groups" size={24} />
          </div>
          <div className="stat-content">
            <div className="stat-value">{workers.length}</div>
            <div className="stat-label">Total Workers</div>
          </div>
        </div>
        <div className="stat-card active">
          <div className="stat-icon">
            <Icon name="check_circle" size={24} />
          </div>
          <div className="stat-content">
            <div className="stat-value">{workers.filter(w => w.status === 'Active' || w.status === 0).length}</div>
            <div className="stat-label">Active</div>
          </div>
        </div>
        <div className="stat-card">
          <div className="stat-icon">
            <Icon name="dns" size={24} />
          </div>
          <div className="stat-content">
            <div className="stat-value">{workers.reduce((sum, w) => sum + (w.instances?.length || 0), 0)}</div>
            <div className="stat-label">Total Instances</div>
          </div>
        </div>
        <div className="stat-card running">
          <div className="stat-icon">
            <Icon name="play_circle" size={24} />
          </div>
          <div className="stat-content">
            <div className="stat-value">{workers.reduce((sum, w) => sum + (w.currentJobs || 0), 0)}</div>
            <div className="stat-label">Running Jobs</div>
          </div>
        </div>
      </div>

      {workers.length === 0 ? (
        <div className="empty-state">
          <p>No workers registered</p>
          <small>Workers will appear here when they connect to the system</small>
        </div>
      ) : (
        <div className="worker-cards">
          {workers.map((worker) => {
            const metadata = parseMetadata(worker.metadata)
            const isExpanded = expandedWorker === worker.workerId

            return (
              <div key={worker.workerId} className={`worker-card ${isExpanded ? 'expanded' : ''}`}>
                <div className="worker-header" onClick={() => toggleExpand(worker.workerId)}>
                  <div className="worker-title">
                    <h3>{worker.workerId}</h3>
                    {getStatusBadge(worker.status)}
                  </div>
                  <div className="worker-meta">
                    <span className="heartbeat" title="Last Heartbeat">
                      <Icon name="favorite" size={16} />
                      {formatTimeSince(worker.lastHeartbeat)}
                    </span>
                    <span className="expand-icon">
                      <Icon name={isExpanded ? 'expand_more' : 'chevron_right'} size={20} />
                    </span>
                  </div>
                </div>

                <div className="worker-summary">
                  <div className="summary-item">
                    <span className="label">Instances:</span>
                    <span className="value">{worker.instances?.length || 0}</span>
                  </div>
                  <div className="summary-item">
                    <span className="label">Running Jobs:</span>
                    <span className="value">{worker.currentJobs || 0}</span>
                  </div>
                  <div className="summary-item">
                    <span className="label">Version:</span>
                    <span className="value">{worker.version || '-'}</span>
                  </div>
                </div>

                <div className="worker-jobs">
                  <span className="label">Job Types:</span>
                  <div className="job-tags">
                    {worker.jobNames?.map((jobName) => (
                      <span key={jobName} className="job-tag">{jobName}</span>
                    ))}
                  </div>
                </div>

                <div className="worker-patterns">
                  <span className="label">Routing Patterns:</span>
                  <div className="pattern-tags">
                    {worker.routingPatterns && typeof worker.routingPatterns === 'object' ? (
                      Object.entries(worker.routingPatterns).map(([jobName, patterns]) => (
                        <div key={jobName} className="job-pattern-group">
                          <span className="job-name-tag">{patterns}</span>
                        </div>
                      ))
                    ) : Array.isArray(worker.routingPatterns) ? (
                      // Old format: List<string> (backwards compatibility)
                      worker.routingPatterns.map((pattern) => (
                        <span key={pattern} className="pattern-tag">{pattern}</span>
                      ))
                    ) : null}
                  </div>
                </div>

                {isExpanded && (
                  <div className="worker-details">
                    <div className="detail-section">
                      <h4 className="header-title">
                        <Icon name="bar_chart" size={18} />
                        System Info
                      </h4>
                      {metadata && (
                        <div className="metadata-grid">
                          <div className="metadata-item">
                            <span className="label">OS:</span>
                            <span className="value">{metadata.OSVersion}</span>
                          </div>
                          <div className="metadata-item">
                            <span className="label">Runtime:</span>
                            <span className="value">.NET {metadata.RuntimeVersion}</span>
                          </div>
                          <div className="metadata-item">
                            <span className="label">CPU Cores:</span>
                            <span className="value">{metadata.ProcessorCount}</span>
                          </div>
                          <div className="metadata-item">
                            <span className="label">Registered:</span>
                            <span className="value">{formatDateTime(worker.registeredAt)}</span>
                          </div>
                        </div>
                      )}
                    </div>

                    {metadata?.JobConfigs && (
                      <div className="detail-section">
                        <h4 className="header-title">
                          <Icon name="settings" size={18} />
                          Job Configurations
                        </h4>
                        <div className="table-container">
                          <table className="job-configs-table">
                            <thead>
                              <tr>
                                <th>Job Type</th>
                                <th>Consumer ID</th>
                                <th>Max Parallel</th>
                                <th>Timeout</th>
                              </tr>
                            </thead>
                            <tbody>
                              {metadata.JobConfigs.map((config) => (
                                <tr key={config.JobType}>
                                  <td>{config.JobType}</td>
                                  <td><code>{config.ConsumerId}</code></td>
                                  <td>{config.MaxParallelJobs}</td>
                                  <td>
                                    {config.ExecutionTimeoutSeconds
                                      ? `${Math.floor(config.ExecutionTimeoutSeconds / 60)}m ${config.ExecutionTimeoutSeconds % 60}s`
                                      : '-'}
                                  </td>
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        </div>
                      </div>
                    )}

                    {worker.instances && worker.instances.length > 0 && (
                      <div className="detail-section">
                        <h4 className="header-title">
                          <Icon name="computer" size={18} />
                          Instances ({worker.instances.length})
                        </h4>
                        <div className="table-container">
                          <table className="instances-table">
                            <thead>
                              <tr>
                                <th>Instance ID</th>
                                <th>Hostname</th>
                                <th>IP Address</th>
                                <th>Jobs</th>
                                <th>Status</th>
                                <th>Registered At</th>
                                <th>Last Heartbeat</th>
                              </tr>
                            </thead>
                            <tbody>
                              {worker.instances.map((instance) => (
                                <tr key={instance.instanceId}>
                                  <td><code>{instance.instanceId}</code></td>
                                  <td>{instance.hostName}</td>
                                  <td>{instance.ipAddress}</td>
                                  <td>{instance.currentJobs}</td>
                                  <td>{getStatusBadge(instance.status)}</td>
                                  <td>{formatTimeSince(instance.registeredAt)}</td>
                                  <td>{formatTimeSince(instance.lastHeartbeat)}</td>
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        </div>
                      </div>
                    )}
                  </div>
                )}
              </div>
            )
          })}
        </div>
      )}

      {/* Auto-refresh indicator */}
      <AutoRefreshIndicator
        enabled={autoRefreshEnabled}
        onToggle={() => setAutoRefreshEnabled(!autoRefreshEnabled)}
        lastRefreshTime={lastRefreshTime}
        intervalSeconds={30}
      />
    </div>
  )
}

export default WorkerList
