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

  const getMemoryBarClass = (currentMB, initialMB) => {
    const ratio = initialMB > 0 ? currentMB / initialMB : 1
    if (ratio > 2) return 'error'
    if (ratio > 1.5) return 'warning'
    return ''
  }

  // Formats a value given in megabytes, switching to GB once it reaches 1024 MB.
  const formatMemoryMB = (mb) => {
    if (mb == null || isNaN(mb)) return '0 MB'
    if (mb >= 1024) return `${(mb / 1024).toFixed(2)} GB`
    return `${mb.toFixed(2)} MB`
  }

  if (loading) {
    return (
      <div className="stats-body">
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
      <div className="stats-body">
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
  const totalAllocatedBytes = serviceStats.reduce((sum, s) => sum + (s.allocatedBytes || 0), 0)

  return (
    <div className="stats-body">
        <div className="db-toolbar">
      </div>
      <div className="card-content">
        {/* Overview Stats */}
        <div className="memory-overview">
          <div className="memory-stat-box">
            <span className="memory-stat-value primary">
              {formatMemoryMB(memoryStats.totalManagedMemoryMB)}
            </span>
            <span className="memory-stat-label">Managed Memory</span>
          </div>
          <div className="memory-stat-box">
            <span className="memory-stat-value info">
              {formatMemoryMB(memoryStats.totalProcessMemoryMB)}
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
                        <span className="stat-label">Allocated</span>
                        <span className="stat-value">{formatMemoryMB(service.allocatedMB)}</span>
                      </div>
                      <div className="service-stat-item">
                        <span className="stat-label">Alloc Rate</span>
                        <span className="stat-value">{service.allocationRatePerSecondMB?.toFixed(2)} MB/s</span>
                      </div>
                      <div className="service-stat-item">
                        <span className="stat-label">Recent Alloc</span>
                        <span className="stat-value">{formatMemoryMB(service.recentAllocatedMB)}</span>
                      </div>
                      <div className="service-stat-item">
                        <span className="stat-label">Uptime</span>
                        <span className="stat-value">{formatUptime(service.startTime)}</span>
                      </div>
                    </div>

                    {/* Memory Bar */}
                    <div className="memory-bar-container">
                      <div className="memory-bar-label">
                        <span>Share of allocations</span>
                        <span>{totalAllocatedBytes > 0 ? (((service.allocatedBytes || 0) / totalAllocatedBytes) * 100).toFixed(1) : '0.0'}%</span>
                      </div>
                      <div className="memory-bar">
                        <div
                          className={`memory-bar-fill ${getMemoryBarClass(service.currentMemoryMB, service.initialMemoryMB)}`}
                          style={{ width: `${totalAllocatedBytes > 0 ? Math.min(((service.allocatedBytes || 0) / totalAllocatedBytes) * 100, 100) : 0}%` }}
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

        {/* Legend / explanation of the values */}
        <div className="memory-legend">
          <div className="memory-legend-title">
            <Icon name="help_outline" size={16} />
            <span>What do these values mean?</span>
          </div>
          <ul className="memory-legend-list">
            <li>
              <strong>Managed Memory</strong> — Total managed heap currently held by the whole
              application (all services share one GC heap; this is process-wide, not per service).
            </li>
            <li>
              <strong>Process Memory</strong> — Total physical memory (working set) of the API
              process, including native memory. Also process-wide.
            </li>
            <li>
              <strong>Allocated</strong> — Cumulative memory this service has allocated since it
              started. It is a lifetime counter, so it only ever goes up, it is <em>not</em> the
              memory currently held. A high number here is normal.
            </li>
            <li>
              <strong>Alloc Rate</strong> — Current allocation throughput (MB per second) measured
              over the last check interval. Because most allocations are short-lived and reclaimed
              by the GC, this can legitimately exceed the process memory.
            </li>
            <li>
              <strong>Recent Alloc</strong> — Memory allocated by this service during the most
              recent check interval. Useful for spotting sudden activity spikes.
            </li>
            <li>
              <strong>Share of allocations</strong> — This service's portion of the total
              allocations across all services, indicating which service produces the most GC pressure.
            </li>
            <li>
              <strong>GC Gen 0/1/2</strong> — Number of garbage collections per generation
              (process-wide). Frequent Gen 2 collections can indicate high memory pressure.
            </li>
          </ul>
          <p className="memory-legend-note">
            A real memory leak shows up as steadily rising <strong>Process Memory</strong> and a
            non-zero <strong>Potential Leaks</strong> count — not as a large cumulative Allocated value.
          </p>
        </div>

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
