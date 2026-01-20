import { useState, useEffect, useCallback } from 'react'
import api from '../../services/api'
import Icon from '../Icon'
import './ServiceMemoryStats.css'

function ServiceMemoryStats() {
  const [memoryStats, setMemoryStats] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)

  const loadMemoryStats = useCallback(async () => {
    try {
      setError(null)
      const response = await api.get('/admin/diagnostics/services')
      const data = response?.data?.data || response?.data || response
      setMemoryStats(data)
    } catch (err) {
      console.error('Failed to load memory stats:', err)
      setError('Failed to load memory statistics')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    loadMemoryStats()

    // Refresh every 30 seconds
    const interval = setInterval(loadMemoryStats, 30000)
    return () => clearInterval(interval)
  }, [loadMemoryStats])

  const formatBytes = (bytes) => {
    if (bytes === 0) return '0 B'
    const k = 1024
    const sizes = ['B', 'KB', 'MB', 'GB']
    const i = Math.floor(Math.log(bytes) / Math.log(k))
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i]
  }

  const formatUptime = (startTime) => {
    if (!startTime) return 'N/A'
    const start = new Date(startTime)
    const now = new Date()
    const diff = now - start

    const hours = Math.floor(diff / (1000 * 60 * 60))
    const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60))

    if (hours > 24) {
      const days = Math.floor(hours / 24)
      return `${days}d ${hours % 24}h`
    }
    return `${hours}h ${minutes}m`
  }

  const getGrowthClass = (growthBytes) => {
    if (growthBytes > 100 * 1024 * 1024) return 'growth-positive' // > 100MB
    if (growthBytes < 0) return 'growth-negative'
    return 'growth-neutral'
  }

  const getMemoryBarClass = (currentMB, initialMB) => {
    const ratio = initialMB > 0 ? currentMB / initialMB : 1
    if (ratio > 2) return 'error'
    if (ratio > 1.5) return 'warning'
    return ''
  }

  const getMemoryBarWidth = (currentMB, processMB) => {
    if (processMB === 0) return 0
    return Math.min((currentMB / processMB) * 100, 100)
  }

  if (loading) {
    return (
      <div className="dashboard-card service-memory-stats">
        <div className="card-header">
          <h3>
            <Icon name="memory" size={20} />
            Background Service Memory
          </h3>
        </div>
        <div className="card-content">
          <div className="memory-stats-loading">
            <div className="spinner"></div>
            <span>Loading memory statistics...</span>
          </div>
        </div>
      </div>
    )
  }

  if (error || !memoryStats) {
    return (
      <div className="dashboard-card service-memory-stats">
        <div className="card-header">
          <h3>
            <Icon name="memory" size={20} />
            Background Service Memory
          </h3>
        </div>
        <div className="card-content">
          <div className="memory-stats-empty">
            <Icon name="error_outline" size={48} className="empty-icon" />
            <p>{error || 'No memory statistics available'}</p>
            <button className="refresh-btn-small" onClick={loadMemoryStats}>
              <Icon name="refresh" size={16} />
              Retry
            </button>
          </div>
        </div>
      </div>
    )
  }

  const serviceStats = memoryStats.serviceStats || []
  const hasLeaks = memoryStats.servicesWithPotentialLeaks > 0

  return (
    <div className="dashboard-card service-memory-stats">
      <div className="card-header">
        <h3>
          <Icon name="memory" size={20} />
          Background Service Memory
        </h3>
        <div className="header-actions">
          <button className="refresh-btn-small" onClick={loadMemoryStats} title="Refresh">
            <Icon name="refresh" size={16} />
            Refresh
          </button>
        </div>
      </div>
      <div className="card-content">
        {/* Overview Stats */}
        <div className="memory-overview">
          <div className="memory-stat-box">
            <span className="memory-stat-value primary">
              {memoryStats.totalManagedMemoryMB?.toFixed(1) || '0'} MB
            </span>
            <span className="memory-stat-label">Managed Memory</span>
          </div>
          <div className="memory-stat-box">
            <span className="memory-stat-value info">
              {memoryStats.totalProcessMemoryMB?.toFixed(1) || '0'} MB
            </span>
            <span className="memory-stat-label">Process Memory</span>
          </div>
          <div className="memory-stat-box">
            <span className="memory-stat-value success">
              {memoryStats.runningServicesCount || 0}
            </span>
            <span className="memory-stat-label">Running Services</span>
          </div>
          <div className={`memory-stat-box ${hasLeaks ? 'error' : ''}`}>
            <span className={`memory-stat-value ${hasLeaks ? 'error' : 'success'}`}>
              {memoryStats.servicesWithPotentialLeaks || 0}
            </span>
            <span className="memory-stat-label">Potential Leaks</span>
          </div>
        </div>

        {/* GC Overview */}
        <div className="gc-stats">
          <div className="gc-stats-title">Garbage Collection Statistics</div>
          <div className="gc-stats-row">
            <div className="gc-stat">
              <div className="gc-stat-value">{memoryStats.gen0Collections || 0}</div>
              <div className="gc-stat-label">Gen 0</div>
            </div>
            <div className="gc-stat">
              <div className="gc-stat-value">{memoryStats.gen1Collections || 0}</div>
              <div className="gc-stat-label">Gen 1</div>
            </div>
            <div className="gc-stat">
              <div className="gc-stat-value">{memoryStats.gen2Collections || 0}</div>
              <div className="gc-stat-label">Gen 2</div>
            </div>
          </div>
        </div>

        {/* Service-by-Service Stats */}
        {serviceStats.length > 0 && (
          <>
            <h4 className="section-title">Service Details</h4>
            <div className="service-cards-grid">
              {serviceStats.map((service) => (
                <div
                  key={service.serviceName}
                  className={`service-card ${service.potentialMemoryLeak ? 'has-leak' : ''}`}
                >
                  <div className="service-card-header">
                    <div className="service-card-title">
                      <Icon name="settings" size={18} />
                      <h4>{service.serviceName}</h4>
                    </div>
                    <div className="service-status">
                      <span className={`status-dot ${service.isRunning ? 'running' : 'stopped'}`}></span>
                      <span className="status-text">{service.isRunning ? 'Running' : 'Stopped'}</span>
                    </div>
                  </div>
                  <div className="service-card-body">
                    <div className="service-stats-grid">
                      <div className="service-stat-item">
                        <span className="stat-label">Current Memory</span>
                        <span className="stat-value">{service.currentMemoryMB?.toFixed(2)} MB</span>
                      </div>
                      <div className="service-stat-item">
                        <span className="stat-label">Initial Memory</span>
                        <span className="stat-value">{service.initialMemoryMB?.toFixed(2)} MB</span>
                      </div>
                      <div className="service-stat-item">
                        <span className="stat-label">Memory Growth</span>
                        <span className={`stat-value ${getGrowthClass(service.totalGrowthBytes)}`}>
                          {service.totalGrowthMB >= 0 ? '+' : ''}{service.totalGrowthMB?.toFixed(2)} MB
                        </span>
                      </div>
                      <div className="service-stat-item">
                        <span className="stat-label">Uptime</span>
                        <span className="stat-value">{formatUptime(service.startTime)}</span>
                      </div>
                    </div>

                    {/* Memory Bar */}
                    <div className="memory-bar-container">
                      <div className="memory-bar-label">
                        <span>Managed vs Process</span>
                        <span>{((service.currentMemoryBytes / service.processMemoryBytes) * 100).toFixed(1)}%</span>
                      </div>
                      <div className="memory-bar">
                        <div
                          className={`memory-bar-fill ${getMemoryBarClass(service.currentMemoryMB, service.initialMemoryMB)}`}
                          style={{ width: `${getMemoryBarWidth(service.currentMemoryBytes, service.processMemoryBytes)}%` }}
                        />
                      </div>
                    </div>

                    {/* Leak Warning */}
                    {service.potentialMemoryLeak && (
                      <div className="leak-warning">
                        <Icon name="warning" size={16} />
                        <span>Potential memory leak detected!</span>
                      </div>
                    )}
                  </div>
                </div>
              ))}
            </div>
          </>
        )}

        {serviceStats.length === 0 && (
          <div className="memory-stats-empty">
            <Icon name="info_outline" size={32} className="empty-icon" />
            <p>No background services are currently registered</p>
          </div>
        )}

        {/* Timestamp */}
        <div className="memory-stats-timestamp">
          <Icon name="schedule" size={14} />
          <span>Last updated: {new Date(memoryStats.timestamp).toLocaleTimeString()}</span>
        </div>
      </div>
    </div>
  )
}

export default ServiceMemoryStats
