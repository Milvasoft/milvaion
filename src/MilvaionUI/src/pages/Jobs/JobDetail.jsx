import { useState, useEffect, useRef, useCallback } from 'react'
import { useParams, Link } from 'react-router-dom'
import jobService from '../../services/jobService'
import occurrenceService from '../../services/occurrenceService'
import signalRService from '../../services/signalRService'
import { formatDate } from '../../utils/dateUtils'
import Icon from '../../components/Icon'
import JsonViewer from '../../components/JsonViewer'
import JsonEditor from '../../components/JsonEditor'
import Modal from '../../components/Modal'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { useTriggerJob } from '../../hooks/useTriggerJob'
import { useModal } from '../../hooks/useModal'
import './JobDetail.css'
import CronDisplay from '../../components/CronDisplay'
import OccurrenceTable from '../../components/OccurrenceTable'

function JobDetail() {
const { id } = useParams()
const [job, setJob] = useState(null)
const [occurrences, setOccurrences] = useState([])
const [totalCount, setTotalCount] = useState(0)
const [loading, setLoading] = useState(true)
const [error, setError] = useState(null)
const [signalRConnected, setSignalRConnected] = useState(false)
const [filterStatus, setFilterStatus] = useState(null)
const [currentPage, setCurrentPage] = useState(1)
const [pageSize, setPageSize] = useState(10)
const [showTriggerModal, setShowTriggerModal] = useState(false)
const [triggerJobData, setTriggerJobData] = useState('')
const [useCustomData, setUseCustomData] = useState(false)
const [autoRefreshEnabled, setAutoRefreshEnabled] = useState(() => {
  const saved = localStorage.getItem('jobDetail_autoRefresh')
  return saved !== null ? saved === 'true' : true
})
const [lastRefreshTime, setLastRefreshTime] = useState(null)

const subscribedOccurrences = useRef(new Set())

const { triggerJob, triggering, modalProps } = useTriggerJob()
const { modalProps: versionModalProps, showModal } = useModal()
const { modalProps: deleteModalProps, showConfirm, showSuccess, showError } = useModal()

  const subscribeToPageOccurrences = useCallback(async (pageOccurrences) => {
    if (!signalRService.isConnected()) return

    for (const occId of subscribedOccurrences.current) {
      try {
        await signalRService.invoke('UnsubscribeFromOccurrence', occId)
      } catch (err) {
        console.error('Failed to unsubscribe from occurrence:', occId, err)
      }
    }
    subscribedOccurrences.current.clear()

    for (const occ of pageOccurrences) {
      try {
        await signalRService.invoke('SubscribeToOccurrence', occ.id)
        subscribedOccurrences.current.add(occ.id)
      } catch (err) {
        console.error('Failed to subscribe to occurrence:', occ.id, err)
      }
    }
  }, [])

  const isInitialLoadRef = useRef(true)

  const loadJobDetails = useCallback(async (showLoading = false) => {
    try {
      if (showLoading) {
        setLoading(true)
      }
      setError(null)

      const jobResponse = await jobService.getById(id)

      setJob(prev => {
        if (!prev) return jobResponse.data

        const hasChanges = Object.keys(jobResponse.data).some(
          key => JSON.stringify(prev[key]) !== JSON.stringify(jobResponse.data[key])
        )

        return hasChanges ? { ...prev, ...jobResponse.data } : prev
      })
      
      setLastRefreshTime(new Date())
    } catch (err) {
      setError('Failed to load job details')
      console.error(err)
    } finally {
      if (showLoading) {
        setLoading(false)
      }
    }
  }, [id])

  const loadOccurrences = useCallback(async () => {
    try {
      const occurrencesResponse = await jobService.getOccurrences(id, {
        pageNumber: currentPage,
        rowCount: pageSize,
        status: filterStatus
      })

      let data = []
      let total = 0

      if (occurrencesResponse.data) {
        if (occurrencesResponse.data.data) {
          data = occurrencesResponse.data.data
          total = occurrencesResponse.data.totalDataCount || 0
        } else if (Array.isArray(occurrencesResponse.data)) {
          data = occurrencesResponse.data
          total = occurrencesResponse.totalDataCount || data.length
        } else {
          data = occurrencesResponse.data
          total = occurrencesResponse.totalDataCount || 0
        }
      }

      setOccurrences(data)
      setTotalCount(total)
    } catch (err) {
      console.error('Failed to load occurrences:', err)
    }
  }, [id, currentPage, pageSize, filterStatus])

  useEffect(() => {
    loadJobDetails(isInitialLoadRef.current)
    isInitialLoadRef.current = false

    const pollInterval = setInterval(() => {
      if (autoRefreshEnabled) {
        loadJobDetails(false)
      }
    }, 10000)

    return () => clearInterval(pollInterval)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, autoRefreshEnabled])

  useEffect(() => {
    loadOccurrences()
  }, [loadOccurrences])

  useEffect(() => {
    let statusCleanup = null

    const connectSignalR = async () => {
      await signalRService.connect()
      setSignalRConnected(signalRService.isConnected())

      if (occurrences.length > 0) {
        await subscribeToPageOccurrences(occurrences)
      }

      const statusInterval = setInterval(() => {
        const connected = signalRService.isConnected()
        setSignalRConnected(connected)
      }, 5000)

      statusCleanup = () => clearInterval(statusInterval)
    }

    connectSignalR()

    const unsubscribeOccurrenceCreated = signalRService.on('OccurrenceCreated', (occurrence) => {
      const jobId = occurrence.jobId || occurrence.scheduledJobId
      if (jobId === id) {
        if (currentPage === 1) {
          setOccurrences(prev => {
            const newList = [occurrence, ...prev]
            return newList.slice(0, pageSize)
          })
          setTotalCount(prev => prev + 1)

          if (signalRService.isConnected()) {
            signalRService.subscribeToOccurrence(occurrence.id)
              .then(() => subscribedOccurrences.current.add(occurrence.id))
              .catch(console.error)
          }
        } else {
          setTotalCount(prev => prev + 1)
        }
      }
    })

    const unsubscribeOccurrenceUpdated = signalRService.on('OccurrenceUpdated', (updatedOccurrence) => {
      if (subscribedOccurrences.current.has(updatedOccurrence.id)) {
        setOccurrences(prev => prev.map(occ =>
          occ.id === updatedOccurrence.id ? { ...occ, ...updatedOccurrence } : occ
        ))
      }
    })

    return () => {
      unsubscribeOccurrenceCreated()
      unsubscribeOccurrenceUpdated()

      if (statusCleanup) statusCleanup()

      for (const occId of subscribedOccurrences.current) {
        signalRService.unsubscribeFromOccurrence(occId).catch(console.error)
      }
      subscribedOccurrences.current.clear()
    }
  }, [id, currentPage, pageSize, subscribeToPageOccurrences, occurrences])

  useEffect(() => {
    if (occurrences.length > 0 && signalRService.isConnected()) {
      subscribeToPageOccurrences(occurrences)
    }
  }, [occurrences, subscribeToPageOccurrences])

  const handleTrigger = () => {
    // Reset state and show modal
    setTriggerJobData(job?.jobData || '')
    setUseCustomData(false)
    setShowTriggerModal(true)
  }

  const handleTriggerConfirm = async () => {
    setShowTriggerModal(false)
    const customData = useCustomData ? triggerJobData : null
    await triggerJob(id, 'Manual trigger by user', false, customData, () => {
      if (currentPage === 1) {
        loadOccurrences()
      } else {
        setCurrentPage(1)
      }
    })
  }

  const handleTriggerCancel = () => {
    setShowTriggerModal(false)
    setTriggerJobData('')
    setUseCustomData(false)
  }

  const handleShowVersionHistory = () => {
    if (!job.jobVersions || job.jobVersions.length === 0) {
      showModal({
        title: 'ℹ️ No Version History',
        message: 'This job has no previous versions yet.',
        confirmText: 'OK',
        showCancel: false
      })
      return
    }

    const versionHistory = job.jobVersions.map((versionJson, index) => {
      try {
        return {
          version: job.jobVersions.length - index, // Reverse order (newest first)
          data: JSON.parse(versionJson)
        }
      } catch (err) {
        console.error('Failed to parse version JSON:', err)
        return {
          version: job.jobVersions.length - index,
          data: { error: 'Failed to parse version data' }
        }
      }
    })

    const modalContent = (
      <div className="version-history-modal">
        <div className="current-version-info">
          <div className="version-badge-large">
            <Icon name="history" size={20} />
            Current Version: v{job.version || 1}
          </div>
          <p className="version-note">
            Showing {versionHistory.length} previous {versionHistory.length === 1 ? 'version' : 'versions'}
          </p>
        </div>

        <div className="version-list">
          {versionHistory.map((version, index) => (
            <div key={index} className="version-item">
              <div className="version-header">
                <span className="version-number">
                  <Icon name="label" size={16} />
                  Version {version.version}
                </span>
                <span className="version-date">
                  {version.data.creationDate ? formatDate(version.data.creationDate) : 'Unknown date'}
                </span>
              </div>
              <div className="version-content">
                <JsonViewer
                  data={version.data}
                  title={`Version ${version.version} Details`}
                  defaultExpanded={index === 0}
                />
              </div>
            </div>
          ))}
        </div>
      </div>
    )

    showModal({
      message: modalContent,
      title: `Version History - ${job.displayName || job.name}`,
      confirmText: 'Close',
      showCancel: false,
      className: 'modal-large',
      type: 'custom'
    })
  }

  const totalPages = Math.ceil(totalCount / pageSize)

  const handlePageChange = (newPage) => {
    if (newPage >= 1 && newPage <= totalPages) {
      setCurrentPage(newPage)
    }
  }

  const handleBulkDelete = async (occurrenceIds) => {
    const confirmed = await showConfirm(
      `Are you sure you want to delete ${occurrenceIds.length} execution${occurrenceIds.length > 1 ? 's' : ''}? This action cannot be undone.`,
      'Delete Executions',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    try {
      await occurrenceService.delete(occurrenceIds)
      await loadOccurrences()
      await showSuccess(`${occurrenceIds.length} execution${occurrenceIds.length > 1 ? 's' : ''} deleted successfully`)
    } catch (err) {
      await showError('Failed to delete execution(s). Please try again.')
      console.error(err)
    }
  }

  const handleDeleteJob = async () => {
    const confirmed = await showConfirm(
      `Are you sure you want to delete "${job.displayName || job.name}"? This action cannot be undone and will remove all associated data.`,
      'Delete Job',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    try {
      await jobService.delete(id)
      await showSuccess('Job deleted successfully')
      // Navigate back to jobs list after successful deletion
      window.location.href = '/jobs'
    } catch (err) {
      await showError('Failed to delete job. Please try again.')
      console.error(err)
    }
  }

  if (loading) return <div className="loading">Loading job details...</div>
  if (error) return <div className="error">{error}</div>
  if (!job) return <div className="error">Job not found</div>

  // Check if job was auto-disabled
  const isAutoDisabled = job.autoDisableSettings?.disabledAt && !job.isActive

  return (
    <div className="job-detail">
      <Modal {...modalProps} />
      <Modal {...versionModalProps} />
      <Modal {...deleteModalProps} />

      {/* Trigger Job Modal */}
      {showTriggerModal && (
        <Modal
          isOpen={true}
          onClose={handleTriggerCancel}
          title="🚀 Trigger Job"
          message={
            <div className="trigger-modal-content">
              <p className="trigger-modal-description">
                You are about to manually trigger <strong>{job.displayName || job.name}</strong>.
              </p>

              <div className="trigger-option">
                <label className="trigger-checkbox-label">
                  <input
                    type="checkbox"
                    checked={useCustomData}
                    onChange={(e) => setUseCustomData(e.target.checked)}
                  />
                  <span>Use custom Job Data for this execution</span>
                </label>
              </div>

              {useCustomData && (
                <div className="trigger-jobdata-input">
                  <JsonEditor
                    name="triggerJobData"
                    value={triggerJobData}
                    onChange={(e) => setTriggerJobData(e.target.value)}
                    rows={8}
                    placeholder='{"key": "value"}'
                    hint="Leave empty to use job's existing data. If provided, this data will be used only for this execution."
                  />
                </div>
              )}

              {!useCustomData && job.jobData && (
                <div className="trigger-current-data">
                  <label>Current Job Data:</label>
                  <pre>{JSON.stringify(JSON.parse(job.jobData || '{}'), null, 2)}</pre>
                </div>
              )}
            </div>
          }
          confirmText={triggering ? 'Triggering...' : 'Trigger Job'}
          cancelText="Cancel"
          showCancel={true}
          onConfirm={handleTriggerConfirm}
          onCancel={handleTriggerCancel}
          type="confirm"
        />
      )}

      {/* Auto-Disabled Warning Banner */}
      {isAutoDisabled && (
        <div className="auto-disabled-warning">
          <div className="warning-icon">
            <Icon name="warning" size={24} />
          </div>
          <div className="warning-content">
            <h3>Job Auto-Disabled</h3>
            <p>
              This job was automatically disabled after {job.autoDisableSettings.consecutiveFailureCount || 0} consecutive failures.
              {job.autoDisableSettings.disabledAt && (
                <> Disabled {formatDate(job.autoDisableSettings.disabledAt)}.</>
              )}
            </p>
          </div>
          <Link to={`/jobs/${id}/edit`} className="warning-action">
            Re-enable
          </Link>
        </div>
      )}

      {/* Header Section */}
      <div className="detail-header">
        <div className="header-content">
          <div className="header-left">
            <Link to="/jobs" className="back-icon-btn" title="Back to Jobs">
              <Icon name="arrow_back" size={24} />
            </Link>

            <div className="title-content">
              <div className="title-section">
                <h1 >{job.displayName || job.name}</h1>
                <span className={`job-status-badge ${job.isActive ? 'active' : 'inactive'} ${isAutoDisabled ? 'auto-disabled' : ''}`}>
                  <Icon name={job.isActive ? 'check_circle' : isAutoDisabled ? 'power_off' : 'pause_circle'} size={16} />
                  {isAutoDisabled ? 'Auto-Disabled' : job.isActive ? 'Active' : 'Inactive'}
                </span>
              </div>

              {job.tags && (
                <div className="job-tags">
                  {job.tags.split(',').map((tag, index) => (
                    <Link
                      key={index}
                      to="/jobs"
                      state={{ filterByTag: tag.trim() }}
                      className="tag-badge"
                    >
                      <Icon name="label" size={14} />
                      {tag.trim()}
                    </Link>
                  ))}
                </div>
              )}
            </div>
          </div>

          <div className="header-actions">
            <button
              onClick={handleTrigger}
              className="action-btn trigger-btn"
              disabled={!job.isActive || triggering}
              title="Trigger job now"
            >
              <Icon name="play_arrow" size={20} />
              <span>{triggering ? 'Triggering...' : 'Trigger Now'}</span>
            </button>
            <Link to={`/jobs/${id}/edit`} className="action-btn edit-btn">
              <Icon name="edit" size={20} />
              <span>Edit Job</span>
            </Link>
            <button
              onClick={handleDeleteJob}
              className="action-btn delete-btn"
              title="Delete job"
            >
              <Icon name="delete" size={20} />
              <span>Delete</span>
            </button>
          </div>
        </div>
      </div>

      {/* Main Content Grid */}
      <div className="content-grid">
        {/* Job Configuration Card */}
        <div className="info-card config-card">
          <div className="card-header">
            <h3>
              <Icon name="settings" size={20} />
              Configuration
            </h3>
          </div>
          <div className="card-body">
            <div className="info-row">
              <span className="info-label">Job Type</span>
              <span className="info-value code">{job.jobType}</span>
            </div>

            <div className="info-row">
              <span className="info-label">Version</span>
              <span className="info-value">
                <span className="version-badge">
                  <Icon name="history" size={14} />
                  v{job.version || 1}
                </span>
                {job.jobVersions && job.jobVersions.length > 0 && (
                  <button
                    onClick={handleShowVersionHistory}
                    className="version-history-btn"
                    title="View version history"
                  >
                    <Icon name="visibility" size={14} />
                    {job.jobVersions.length} {job.jobVersions.length === 1 ? 'version' : 'versions'}
                  </button>
                )}
              </span>
            </div>

            {job.workerId && (
              <div className="info-row">
                <span className="info-label">Worker</span>
                <span className="info-value worker-badge">{job.workerId}</span>
              </div>
            )}

            <div className="info-row">
              <span className="info-label">Schedule Type</span>
              <span className="info-value">
                {job.cronExpression ? (
                  <span className="schedule-type-badge recurring">
                    <Icon name="repeat" size={16} />
                    Recurring
                  </span>
                ) : (
                  <span className="schedule-type-badge one-time">
                    <Icon name="event" size={16} />
                    One-Time
                  </span>
                )}
              </span>
            </div>

            {job.cronExpression && (
              <div className="info-row">
                <span className="info-label">Schedule</span>
                <div className="info-value">
                  <CronDisplay expression={job.cronExpression} showTooltip={true} />
                </div>
              </div>
            )}

            {job.executeAt && (
              <div className="info-row">
                <span className="info-label">{job.cronExpression ? 'Next Execution' : 'Execute At'}</span>
                <span className="info-value">{formatDate(job.executeAt)}</span>
              </div>
            )}

            <div className="info-row">
              <span className="info-label">Concurrent Policy</span>
              <span className="info-value">
                {job.concurrentExecutionPolicy === 0 && (
                  <>
                    <Icon name="block" size={16} />
                    Skip
                  </>
                )}
                {job.concurrentExecutionPolicy === 1 && (
                  <>
                    <Icon name="schedule" size={16} />
                    Queue
                  </>
                )}
                {job.concurrentExecutionPolicy === 2 && (
                  <>
                    <Icon name="check" size={16} />
                    Allow
                  </>
                )}
              </span>
            </div>

            {job.description && (
              <div className="info-row full-width">
                <span className="info-label">Description</span>
                <p className="info-value description">{job.description}</p>
              </div>
            )}

            {job.zombieTimeoutMinutes && (
              <div className="info-row">
                <span className="info-label">Zombie Timeout</span>
                <span className="info-value">{job.zombieTimeoutMinutes} minutes</span>
              </div>
            )}

            {job.executionTimeoutSeconds && (
              <div className="info-row">
                <span className="info-label">Execution Timeout</span>
                <span className="info-value">{job.executionTimeoutSeconds} seconds</span>
              </div>
            )}

            {job.jobData && (
              <div className="info-row full-width">
                <JsonViewer data={job.jobData} title="Job Data (JSON)" />
              </div>
            )}
          </div>
        </div>

        {/* Auto-Disable Settings Card */}
        {job.autoDisableSettings && (
          <div className="info-card auto-disable-card">
            <div className="card-header">
              <h3>
                <Icon name="power_off" size={20} />
                Auto-Disable (Circuit Breaker)
              </h3>
            </div>
            <div className="card-body">
              <div className="info-row">
                <span className="info-label">Status</span>
                <span className={`info-value badge ${job.autoDisableSettings.enabled !== false ? 'enabled' : 'disabled'}`}>
                  <Icon name={job.autoDisableSettings.enabled !== false ? 'check_circle' : 'cancel'} size={16} />
                  {job.autoDisableSettings.enabled !== false ? 'Enabled' : 'Disabled'}
                </span>
              </div>

              {job.autoDisableSettings.enabled !== false && (
                <>
                  <div className="info-row">
                    <span className="info-label">Failure Threshold</span>
                    <span className="info-value">
                      {job.autoDisableSettings.threshold || 'Default (5)'}
                    </span>
                  </div>

                  <div className="info-row">
                    <span className="info-label">Current Failure Count</span>
                    <span className={`info-value ${job.autoDisableSettings.consecutiveFailureCount > 0 ? 'warning' : ''}`}>
                      {job.autoDisableSettings.consecutiveFailureCount || 0}
                    </span>
                  </div>

                  {job.autoDisableSettings.lastFailureTime && (
                    <div className="info-row">
                      <span className="info-label">Last Failure</span>
                      <span className="info-value">
                        {formatDate(job.autoDisableSettings.lastFailureTime)}
                      </span>
                    </div>
                  )}

                  {job.autoDisableSettings.disabledAt && (
                    <div className="info-row">
                      <span className="info-label">Auto-Disabled At</span>
                      <span className="info-value danger">
                        {formatDate(job.autoDisableSettings.disabledAt)}
                      </span>
                    </div>
                  )}
                </>
              )}
            </div>
          </div>
        )}

        {/* Statistics Card */}
        {job.totalExecutions > 0 && (
          <div className="info-card stats-card">
            <div className="card-header">
              <h3>
                <Icon name="bar_chart" size={20} />
                Statistics
              </h3>
            </div>
            <div className="card-body">
              <div className="stat-item">
                <span className="stat-label">Total Runs</span>
                <span className="stat-value">{job.totalExecutions}</span>
              </div>
              <div className="stat-item">
                <span className="stat-label">Success Rate</span>
                <span className={`stat-value ${(job.successRate || 0) >= 90 ? 'success' : (job.successRate || 0) >= 70 ? 'warning' : 'danger'}`}>
                  {job.successRate != null ? `${job.successRate}%` : 'N/A'}
                </span>
              </div>
              <div className="stat-item">
                <span className="stat-label">Avg Duration</span>
                <span className="stat-value">
                  {job.avarageDuration != null
                    ? job.avarageDuration >= 1000
                      ? `${(job.avarageDuration / 1000).toFixed(2)}s`
                      : `${Math.round(job.avarageDuration)}ms`
                    : 'N/A'
                  }
                </span>
              </div>
            </div>
          </div>
        )}

      </div>

      {/* Occurrences Section */}
      <div className="occurrences-card">
        <div className="card-header">
          <div>
            <h3>
              <Icon name="history" size={20} />
              Execution History
            </h3>
          </div>
          {totalCount > 0 && <span className="count-badge">{totalCount} total</span>}
          <div className={`signalr-indicator ${signalRConnected ? 'connected' : 'disconnected'}`}>
            <span className="indicator-dot"></span>
            <span>{signalRConnected ? 'Live' : 'Reconnecting...'}</span>
          </div>
        </div>

        <OccurrenceTable
          occurrences={occurrences}
          loading={false}
          totalCount={totalCount}
          currentPage={currentPage}
          pageSize={pageSize}
          filterStatus={filterStatus}
          onFilterChange={(status) => {
            setFilterStatus(status)
            setCurrentPage(1)
          }}
          onPageChange={handlePageChange}
          onPageSizeChange={(newSize) => {
            setPageSize(newSize)
            setCurrentPage(1)
          }}
          showJobName={false}
          onBulkDelete={handleBulkDelete}
        />
      </div>

      {/* Auto-refresh indicator */}
      <AutoRefreshIndicator
        enabled={autoRefreshEnabled}
        onToggle={() => {
          const newValue = !autoRefreshEnabled
          setAutoRefreshEnabled(newValue)
          localStorage.setItem('jobDetail_autoRefresh', newValue.toString())
        }}
        lastRefreshTime={lastRefreshTime}
        intervalSeconds={10}
      />
    </div>
  )
}

export default JobDetail
