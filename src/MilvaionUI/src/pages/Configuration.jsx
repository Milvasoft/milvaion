import { useState, useEffect, useCallback } from 'react'
import Icon from '../components/Icon'
import configurationService from '../services/configurationService'
import './Configuration.css'

function Configuration() {
  const [config, setConfig] = useState(null)
  const [loading, setLoading] = useState(true)

  const loadConfiguration = useCallback(async () => {
    try {
      setLoading(true)
      const response = await configurationService.getConfiguration()

      // API returns nested structure: { data: { data: {...} } }
      const configData = response?.data || response
      setConfig(configData)
    } catch (err) {
      console.error('Failed to load configuration:', err)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    loadConfiguration()
  }, [loadConfiguration])

  const formatUptime = (uptime) => {
    if (!uptime) return 'N/A'

    // Parse TimeSpan string (e.g., "1.02:30:45.123")
    if (typeof uptime === 'string') {
      const parts = uptime.split(':')
      if (parts.length >= 3) {
        const dayHours = parts[0].split('.')
        const days = dayHours.length > 1 ? parseInt(dayHours[0]) : 0
        const hours = parseInt(dayHours[dayHours.length - 1])
        const minutes = parseInt(parts[1])

        if (days > 0) return `${days}d ${hours}h ${minutes}m`
        if (hours > 0) return `${hours}h ${minutes}m`
        return `${minutes}m`
      }
    }

    return uptime
  }

  if (loading) {
    return <div className="loading">Loading configuration...</div>
  }

  if (!config) {
    return <div className="error">Failed to load configuration</div>
  }

  return (
    <div className="configuration">
      <div className="configuration-header">
        <h1>
          <Icon name="settings" size={28} />
          System Configuration
        </h1>
        <p className="configuration-subtitle">Read-only view of system settings</p>
      </div>


      {/* System Resources */}
      <div className="config-section">
          <h2 className="config-header-title">
          <Icon name="bar_chart" size={24} />
          System Resources
          </h2>
        <div className="resource-grid">
          {/* CPU */}
          <div className="resource-card">
            <div className="resource-header">
              <Icon name="speed" size={20} className="resource-icon" />
              <span className="resource-title">CPU</span>
            </div>
            <div className="resource-value">{config.systemResources.cpuUsagePercent.toFixed(1)}%</div>
            <div className="resource-bar">
              <div
                className={'resource-bar-fill ' + (config.systemResources.cpuUsagePercent >= 80 ? 'high' : config.systemResources.cpuUsagePercent >= 50 ? 'medium' : 'low')}
                style={{ width: `${config.systemResources.cpuUsagePercent}%` }}
              />
            </div>
            <div className="resource-label">Process Usage</div>
          </div>

          {/* Memory */}
          <div className="resource-card">
            <div className="resource-header">
              <Icon name="memory" size={20} className="resource-icon" />
              <span className="resource-title">Memory</span>
            </div>
            <div className="resource-value">{config.systemResources.memoryUsagePercent.toFixed(1)}%</div>
            <div className="resource-bar">
              <div
                className={'resource-bar-fill ' + (config.systemResources.memoryUsagePercent >= 80 ? 'high' : config.systemResources.memoryUsagePercent >= 50 ? 'medium' : 'low')}
                style={{ width: `${config.systemResources.memoryUsagePercent}%` }}
              />
            </div>
            <div className="resource-detail">
              <span>{config.systemResources.usedMemoryMB} MB / {config.systemResources.totalMemoryMB} MB</span>
            </div>
            <div className="resource-label">Process: {config.systemResources.processMemoryMB} MB</div>
          </div>

          {/* Disk */}
          <div className="resource-card">
            <div className="resource-header">
              <Icon name="storage" size={20} className="resource-icon" />
              <span className="resource-title">Disk</span>
            </div>
            <div className="resource-value">{config.systemResources.diskUsagePercent.toFixed(1)}%</div>
            <div className="resource-bar">
              <div
                className={'resource-bar-fill ' + (config.systemResources.diskUsagePercent >= 80 ? 'high' : config.systemResources.diskUsagePercent >= 50 ? 'medium' : 'low')}
                style={{ width: `${config.systemResources.diskUsagePercent}%` }}
              />
            </div>
            <div className="resource-detail">
              <span>{config.systemResources.availableDiskGB} GB available / {config.systemResources.totalDiskGB} GB total</span>
            </div>
          </div>
        </div>
      </div>

      {/* System Information */}
      <div className="config-section">
        <h2 className="config-header-title">
          <Icon name="computer" size={24} />
          System Information
        </h2>
        <div className="config-grid">
          <div className="config-item">
            <span className="config-label">Version</span>
            <span className="config-value">{config.version}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Environment</span>
            <span className={'config-value badge ' + (config.environment === 'Production' ? 'production' : 'development')}>
              {config.environment}
            </span>
          </div>
          <div className="config-item">
            <span className="config-label">Hostname</span>
            <span className="config-value">{config.hostName}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Startup Time</span>
            <span className="config-value">{new Date(config.startupTime).toLocaleString()}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Uptime</span>
            <span className="config-value">{formatUptime(config.uptime)}</span>
          </div>
        </div>
      </div>


      {/* Job Dispatcher */}
      <div className="config-section">
        <h2 className="config-header-title">
          <Icon name="rocket_launch" size={24} />
          Job Dispatcher
        </h2>
        <div className="config-grid">
          <div className="config-item">
            <span className="config-label">Enabled</span>
            <span className={'config-value badge ' + (config.jobDispatcher.enabled ? 'enabled' : 'disabled')}>
              <Icon name={config.jobDispatcher.enabled ? 'check_circle' : 'cancel'} size={16} />
              {config.jobDispatcher.enabled ? 'Enabled' : 'Disabled'}
            </span>
          </div>
          <div className="config-item">
            <span className="config-label">Polling Interval</span>
            <span className="config-value">{config.jobDispatcher.pollingIntervalSeconds}s</span>
          </div>
          <div className="config-item">
            <span className="config-label">Batch Size</span>
            <span className="config-value">{config.jobDispatcher.batchSize}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Lock TTL</span>
            <span className="config-value">{config.jobDispatcher.lockTtlSeconds}s</span>
          </div>
          <div className="config-item">
            <span className="config-label">Startup Recovery</span>
            <span className={'config-value badge ' + (config.jobDispatcher.enableStartupRecovery ? 'enabled' : 'disabled')}>
              <Icon name={config.jobDispatcher.enableStartupRecovery ? 'check_circle' : 'cancel'} size={16} />
              {config.jobDispatcher.enableStartupRecovery ? 'Enabled' : 'Disabled'}
            </span>
          </div>
        </div>
      </div>

      {/* Job Auto-Disable (Circuit Breaker) */}
      {config.jobAutoDisable && (
        <div className="config-section">
          <h2 className="config-header-title">
            <Icon name="power_off" size={24} />
            Job Auto-Disable (Circuit Breaker)
          </h2>
          <p className="section-description">
            Automatically disables jobs that fail repeatedly to prevent resource waste.
          </p>
          <div className="config-grid">
            <div className="config-item">
              <span className="config-label">Enabled</span>
              <span className={'config-value badge ' + (config.jobAutoDisable.enabled ? 'enabled' : 'disabled')}>
                <Icon name={config.jobAutoDisable.enabled ? 'check_circle' : 'cancel'} size={16} />
                {config.jobAutoDisable.enabled ? 'Enabled' : 'Disabled'}
              </span>
            </div>
            <div className="config-item">
              <span className="config-label">Failure Threshold</span>
              <span className="config-value">
                {config.jobAutoDisable.consecutiveFailureThreshold} consecutive failures
              </span>
            </div>
            <div className="config-item">
              <span className="config-label">Failure Window</span>
              <span className="config-value">{config.jobAutoDisable.failureWindowMinutes} min</span>
            </div>
          </div>

          {/* Circuit Breaker Info Box */}
          <div className="info-box">
            <Icon name="info" size={20} />
            <div className="info-content">
              <strong>How it works:</strong>
              <ul>
                <li>Jobs are monitored for consecutive failures within a {config.jobAutoDisable.failureWindowMinutes}-minute window.</li>
                <li>After {config.jobAutoDisable.consecutiveFailureThreshold} consecutive failures, the job is automatically disabled.</li>
                {/*<li>Administrators receive a notification when a job is auto-disabled.</li>*/}
                <li>Jobs can be manually re-enabled from the Jobs page.</li>
                {config.jobAutoDisable.autoReEnableAfterCooldown && (
                  <li>Jobs will automatically re-enable after {config.jobAutoDisable.autoReEnableCooldownMinutes} minutes.</li>
                )}
              </ul>
            </div>
          </div>
        </div>
      )}

      {/* Database */}
      <div className="config-section">
        <h2 className="config-header-title">
          <Icon name="storage" size={24} />
          Database
          </h2>
        <div className="config-grid">
          <div className="config-item">
            <span className="config-label">Provider</span>
            <span className="config-value">{config.database.provider}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Database Name</span>
            <span className="config-value code">{config.database.databaseName}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Host</span>
            <span className="config-value code">{config.database.host}</span>
          </div>
        </div>
      </div>

      {/* Redis */}
      <div className="config-section">
        <h2 className="config-header-title">
          <Icon name="flash_on" size={24} />
          Redis
        </h2>
        <div className="config-grid">
          <div className="config-item">
            <span className="config-label">Connection String</span>
            <span className="config-value code">{config.redis.connectionString}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Database</span>
            <span className="config-value">{config.redis.database}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Connect Timeout</span>
            <span className="config-value">{config.redis.connectTimeout}ms</span>
          </div>
          <div className="config-item">
            <span className="config-label">Sync Timeout</span>
            <span className="config-value">{config.redis.syncTimeout}ms</span>
          </div>
          <div className="config-item">
            <span className="config-label">Key Prefix</span>
            <span className="config-value code">{config.redis.keyPrefix}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Default Lock TTL</span>
            <span className="config-value">{config.redis.defaultLockTtlSeconds}s</span>
          </div>
        </div>
      </div>

      {/* RabbitMQ */}
      <div className="config-section">
        <h2 className="config-header-title">
          <Icon name="message" size={24} />
          RabbitMQ
        </h2>
        <div className="config-grid">
          <div className="config-item">
            <span className="config-label">Host</span>
            <span className="config-value code">{config.rabbitMQ.host}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Port</span>
            <span className="config-value">{config.rabbitMQ.port}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Virtual Host</span>
            <span className="config-value code">{config.rabbitMQ.virtualHost}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Durable</span>
            <span className={'config-value badge ' + (config.rabbitMQ.durable ? 'enabled' : 'disabled')}>
              <Icon name={config.rabbitMQ.durable ? 'check_circle' : 'cancel'} size={16} />
              {config.rabbitMQ.durable ? 'Yes' : 'No'}
            </span>
          </div>
          <div className="config-item">
            <span className="config-label">Auto Delete</span>
            <span className={'config-value badge ' + (config.rabbitMQ.autoDelete ? 'enabled' : 'disabled')}>
              <Icon name={config.rabbitMQ.autoDelete ? 'check_circle' : 'cancel'} size={16} />
              {config.rabbitMQ.autoDelete ? 'Yes' : 'No'}
            </span>
          </div>
          <div className="config-item">
            <span className="config-label">Connection Timeout</span>
            <span className="config-value">{config.rabbitMQ.connectionTimeout}s</span>
          </div>
          <div className="config-item">
            <span className="config-label">Heartbeat</span>
            <span className="config-value">{config.rabbitMQ.heartbeat}s</span>
          </div>
          <div className="config-item">
            <span className="config-label">Automatic Recovery</span>
            <span className={'config-value badge ' + (config.rabbitMQ.automaticRecoveryEnabled ? 'enabled' : 'disabled')}>
              <Icon name={config.rabbitMQ.automaticRecoveryEnabled ? 'check_circle' : 'cancel'} size={16} />
              {config.rabbitMQ.automaticRecoveryEnabled ? 'Enabled' : 'Disabled'}
            </span>
          </div>
          <div className="config-item">
            <span className="config-label">Network Recovery Interval</span>
            <span className="config-value">{config.rabbitMQ.networkRecoveryInterval}s</span>
          </div>
          <div className="config-item">
            <span className="config-label">Queue Depth Warning</span>
            <span className="config-value">{config.rabbitMQ.queueDepthWarningThreshold}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Queue Depth Critical</span>
            <span className="config-value">{config.rabbitMQ.queueDepthCriticalThreshold}</span>
          </div>
        </div>

        <h3 className="config-header-title">
          <Icon name="alt_route" size={20} />
          Exchanges
        </h3>
        <div className="config-grid">
          <div className="config-item">
            <span className="config-label">Main Exchange</span>
            <span className="config-value code">{config.rabbitMQ.exchange}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Dead Letter Exchange</span>
            <span className="config-value code">{config.rabbitMQ.deadLetterExchange}</span>
          </div>
        </div>

        <h3 className="config-header-title">
          <Icon name="inbox" size={20} />
          Queues
        </h3>
        <div className="config-grid">
          <div className="config-item">
            <span className="config-label">Scheduled Jobs</span>
            <span className="config-value code">{config.rabbitMQ.queues.scheduledJobs}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Worker Logs</span>
            <span className="config-value code">{config.rabbitMQ.queues.workerLogs}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Worker Heartbeat</span>
            <span className="config-value code">{config.rabbitMQ.queues.workerHeartbeat}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Worker Registration</span>
            <span className="config-value code">{config.rabbitMQ.queues.workerRegistration}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Status Updates</span>
            <span className="config-value code">{config.rabbitMQ.queues.statusUpdates}</span>
          </div>
          <div className="config-item">
            <span className="config-label">Failed Jobs</span>
            <span className="config-value code">{config.rabbitMQ.queues.failedOccurrences}</span>
          </div>
        </div>
      </div>
    </div>
  )
}

export default Configuration
