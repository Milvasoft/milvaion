import { useState, useEffect, useRef, useCallback } from 'react'
import { useParams, Link, useNavigate } from 'react-router-dom'
import jobService from '../../services/jobService'
import occurrenceService from '../../services/occurrenceService'
import signalRService from '../../services/signalRService'
import { formatDate } from '../../utils/dateUtils'
import Icon from '../../components/Icon'
import JsonView from '../../components/JsonView'
import JsonEditor from '../../components/JsonEditor'
import Modal from '../../components/Modal'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { SkeletonDetail } from '../../components/Skeleton'
import CollapsibleSection from '../../components/CollapsibleSection'
import { getApiErrorMessage } from '../../utils/errorUtils'
import { useTriggerJob } from '../../hooks/useTriggerJob'
import { useModal } from '../../hooks/useModal'
import './JobDetail.css'
import CronDisplay from '../../components/CronDisplay'
import OccurrenceTable from '../../components/OccurrenceTable'
import AuditInfoCard from '../../components/AuditInfoCard'

function JobDetail() {
const { id } = useParams()
const navigate = useNavigate()
const [job, setJob] = useState(null)
const [occurrences, setOccurrences] = useState([])
// Ids of executions that just streamed in, so their row flashes on arrival.
const [flashIds, setFlashIds] = useState(() => new Set())
const [loading, setLoading] = useState(true)
const [error, setError] = useState(null)
const [signalRConnected, setSignalRConnected] = useState(false)
const [filterStatus, setFilterStatus] = useState(null)
// The occurrences endpoint is cursor paginated: no page numbers and no total count. cursorHistory doubles as the
// page counter and as the way back, since a cursor only ever points forwards.
const [paginationState, setPaginationState] = useState({
  cursor: null,
  cursorHistory: [],
  hasNextPage: false,
  nextCursor: null,
})
const [pageSize, setPageSize] = useState(10)
const [showTriggerModal, setShowTriggerModal] = useState(false)
const [triggerJobData, setTriggerJobData] = useState('')
const [useCustomData, setUseCustomData] = useState(false)
const [autoRefreshEnabled, setAutoRefreshEnabled] = useState(() => {
  const saved = localStorage.getItem('jobDetail_autoRefresh')
  return saved !== null ? saved === 'true' : true
})
const [lastRefreshTime, setLastRefreshTime] = useState(null)

/* Deleting a job removes every occurrence and log line it ever produced, which on a job
   with a short schedule is a lot of rows and takes real time. Without this the button sat
   there looking untouched and the only honest reading was that the click had not
   registered. */
const [deleting, setDeleting] = useState(false)

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
      setError(getApiErrorMessage(err, 'Failed to load job details'))
      console.error(err)
    } finally {
      if (showLoading) {
        setLoading(false)
      }
    }
  }, [id])

  const loadOccurrences = useCallback(async () => {
    try {
      const response = await jobService.getOccurrences(id, {
        cursor: paginationState.cursor || undefined,
        rowCount: pageSize,
        status: filterStatus
      })

      setOccurrences(response?.data || [])

      // nextCursor is what the following page is fetched with; hasNextPage is what enables the Next button.
      setPaginationState(prev => ({
        ...prev,
        hasNextPage: response?.hasNextPage ?? false,
        nextCursor: response?.nextCursor ?? null,
      }))
    } catch (err) {
      console.error('Failed to load occurrences:', err)
    }
  }, [id, paginationState.cursor, pageSize, filterStatus])

  const handleNextPage = () => {
    setPaginationState(prev => {
      if (!prev.nextCursor) return prev
      return {
        cursor: prev.nextCursor,
        cursorHistory: [...prev.cursorHistory, prev.cursor],
        hasNextPage: false,
        nextCursor: null,
      }
    })
  }

  const handlePreviousPage = () => {
    setPaginationState(prev => {
      if (prev.cursorHistory.length === 0) return prev
      const newHistory = [...prev.cursorHistory]
      const previousCursor = newHistory.pop() ?? null
      return {
        cursor: previousCursor,
        cursorHistory: newHistory,
        hasNextPage: false,
        nextCursor: null,
      }
    })
  }

  const resetPagination = () =>
    setPaginationState({ cursor: null, cursorHistory: [], hasNextPage: false, nextCursor: null })

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
        // Only prepend on the first page. On a later page the list is anchored to a cursor, so injecting a newer
        // row there would show something that does not belong to the window the user is looking at.
        if (paginationState.cursorHistory.length === 0) {
          setOccurrences(prev => {
            const newList = [occurrence, ...prev]
            return newList.slice(0, pageSize)
          })

          // Flash the new row briefly, then clear the marker so it does not replay.
          if (occurrence.id != null) {
            setFlashIds(prev => new Set(prev).add(occurrence.id))
            setTimeout(() => {
              setFlashIds(prev => {
                const next = new Set(prev)
                next.delete(occurrence.id)
                return next
              })
            }, 1200)
          }

          if (signalRService.isConnected()) {
            signalRService.subscribeToOccurrence(occurrence.id)
              .then(() => subscribedOccurrences.current.add(occurrence.id))
              .catch(console.error)
          }
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
  }, [id, paginationState.cursorHistory.length, pageSize, subscribeToPageOccurrences, occurrences])

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
      // Back to the newest page, where the run that was just started will actually be.
      if (paginationState.cursorHistory.length === 0) {
        loadOccurrences()
      } else {
        resetPagination()
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
        const parsed = JSON.parse(versionJson)
        return {
          version: job.jobVersions.length - index,
          data: parsed,
          error: null
        }
      } catch (err) {
        console.error('Failed to parse version JSON:', err)
        return {
          version: job.jobVersions.length - index,
          data: null,
          error: `Parse error: ${err.message}`
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
                  {version.data?.creationDate ? formatDate(version.data.creationDate) : 'Unknown date'}
                </span>
              </div>
              <div className="version-content">
                {version.error ? (
                  <div className="version-error">
                    <Icon name="error" size={20} />
                    <div>
                      <strong>Failed to parse version data</strong>
                      <p>{version.error}</p>
                    </div>
                  </div>
                ) : (
                  <JsonView data={version.data} name={`version ${version.version}`} maxHeight={320} />
                )}
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

  const handleBulkDelete = async (occurrenceIds) => {
    const confirmed = await showConfirm(
      `Are you sure you want to delete ${occurrenceIds.length} execution${occurrenceIds.length > 1 ? 's' : ''}? This action cannot be undone.`,
      'Delete Executions',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    try {
      const response = await occurrenceService.delete(occurrenceIds)

      if (response?.isSuccess === false) {
        const message = response.messages?.[0]?.message || 'Failed to delete execution(s).'
        await showError(message)
        return
      }

      await loadOccurrences()
      await showSuccess(`${occurrenceIds.length} execution${occurrenceIds.length > 1 ? 's' : ''} deleted successfully`)
    } catch (err) {
      await showError('Failed to delete execution(s). Please try again.')
      console.error(err)
    }
  }

  const handleDeleteJob = async () => {
    /*
     * The whole flow is guarded, not just the request.
     *
     * Only the `jobService.delete` call used to be inside a `try`, so anything that failed
     * before it - opening the confirmation, resolving it - threw into nothing and the
     * button simply appeared dead: no request, no message, no way to tell whether the
     * click had even registered. A user-facing action should never fail silently.
     */
    try {
      await runDelete()
    } catch (err) {
      console.error('Delete flow failed before the request was sent:', err)
      await showError('Something went wrong before the delete request was sent. The console has the details.')
    }
  }

  const runDelete = async () => {
    const confirmed = await showConfirm(
      `Are you sure you want to delete "${job.displayName || job.name}"? This action cannot be undone and will remove all associated data.`,
      'Delete Job',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    setDeleting(true)

    try {
      const response = await jobService.delete(id)

      if (response?.isSuccess === false) {
        const message = response.messages?.[0]?.message || 'Failed to delete job.'
        await showError(message)
        return
      }

      await showSuccess('Job deleted successfully')
      // Navigate back to jobs list after successful deletion
      navigate('/jobs')
    } catch (err) {
      await showError('Failed to delete job. Please try again.')
      console.error(err)
    } finally {
      setDeleting(false)
    }
  }

  if (loading) return <SkeletonDetail />
  if (error) return <div className="error">{error}</div>
  if (!job) return <div className="error">Job not found</div>

  // Başarı oranının rengi. Hiç çalışmamış bir iş nötr kalıyor: %0 başarı ile
  // "henüz veri yok" aynı şey değil ve kırmızı göstermek yanlış alarm veriyor.
  const healthTone = job.totalExecutions > 0
    ? (job.successRate ?? 0) >= 90 ? 'good' : (job.successRate ?? 0) >= 70 ? 'warn' : 'bad'
    : 'idle'

  // Check if job was auto-disabled
  const isAutoDisabled = job.autoDisableSettings?.disabledAt && !job.isActive

  return (
    <div className="page job-detail">
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
      <div className="dtl-header">
        <div className="dtl-header-left">
          <Link to="/jobs" className="dtl-back" title="Back to Jobs">
            <Icon name="arrow_back" size={24} />
          </Link>

          <div className="dtl-title">
            <h1>
              <Icon name="work" size={28} />
              {/* The name in its own element so it can be told to take the rest of the
                  title row and wrap inside itself, keeping it beside the icon rather than
                  breaking onto a line of its own on a phone. */}
              <span className="dtl-title-text">{job.displayName || job.name}</span>
              <span className={`job-status-badge ${job.isActive ? 'active' : 'inactive'} ${isAutoDisabled ? 'auto-disabled' : ''}`}>
                <Icon name={job.isActive ? 'check_circle' : isAutoDisabled ? 'power_off' : 'pause_circle'} size={16} />
                {isAutoDisabled ? 'Auto-Disabled' : job.isActive ? 'Active' : 'Inactive'}
              </span>
              {/* Ayrı bir rozet: job hâlâ aktif, sadece çalışacak bir şeyi kalmadı.
                  İkisini tek rozette birleştirmek "kullanıcı kapattı" ile "bitti"yi
                  aynı şey gibi gösterirdi. */}
              {job.completedAt && (
                <span
                  className="job-status-badge completed"
                  title={`One-time job, dispatched ${formatDate(job.completedAt)}. It will not be scheduled again.`}
                >
                  <Icon name="task_alt" size={16} />
                  Completed
                </span>
              )}
            </h1>

            <div className="title-content">
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

              {job.externalJobInfo && (
                <div className="external-job-info">
                  <span className="external-badge-large" title="This job is managed by an external scheduler (Quartz, Hangfire, etc.)">
                    <Icon name="cloud_sync" size={16} />
                    External Job
                  </span>
                  <span className="external-id">ID: {job.externalJobInfo.externalJobId}</span>
                </div>
              )}
            </div>
          </div>
        </div>

        <div className="dtl-actions">
          <Link to={`/jobs/${id}/edit`} className="dtl-btn dtl-btn--secondary">
            <Icon name="edit" size={18} /> Edit Job
          </Link>
          <button
            onClick={handleTrigger}
            className="dtl-btn dtl-btn--primary"
            disabled={!job.isActive || triggering || job.externalJobInfo}
            title={job.externalJobInfo ? "External jobs cannot be triggered from Milvaion" : "Trigger job now"}
          >
            <Icon name="play_arrow" size={18} /> {triggering ? 'Triggering...' : 'Trigger Now'}
          </button>
          <button
            onClick={handleDeleteJob}
            className="dtl-btn dtl-btn--danger"
            disabled={!!job.externalJobInfo || deleting}
            title={job.externalJobInfo ? "External jobs cannot be deleted from Milvaion" : "Delete job"}
          >
            {deleting
              ? <><Icon name="progress_activity" size={18} className="dtl-spin" /> Deleting…</>
              : <><Icon name="delete" size={18} /> Delete</>}
          </button>
          <AuditInfoCard auditInfo={job.auditInfo} />
        </div>
      </div>

      {/* Sağlık özeti: bir işe bakan önce "sağlıklı mı, ne zaman çalışacak" diye
          soruyor. Bu üç sayı grid'in üçüncü kartında gömülüydü. */}
      <div className="jd-summary">
        <div className={`jd-summary-metric jd-summary-metric--${healthTone}`}>
          <label>Success rate</label>
          <span className="jd-summary-value">
            {job.successRate != null ? `${job.successRate}%` : '—'}
          </span>
          <span className="jd-summary-note">
            {job.totalExecutions > 0
              ? `over ${job.totalExecutions} ${job.totalExecutions === 1 ? 'run' : 'runs'}`
              : 'no runs yet'}
          </span>
        </div>

        <div className="jd-summary-metric">
          <label>Average duration</label>
          <span className="jd-summary-value">
            {job.avarageDuration != null
              ? job.avarageDuration >= 1000
                ? `${(job.avarageDuration / 1000).toFixed(1)}s`
                : `${Math.round(job.avarageDuration)}ms`
              : '—'}
          </span>
          <span className="jd-summary-note">per execution</span>
        </div>

        <div className="jd-summary-metric">
          <label>{job.cronExpression ? 'Next run' : 'Scheduled for'}</label>
          <span className="jd-summary-value jd-summary-value--time">
            {job.executeAt ? formatDate(job.executeAt) : '—'}
          </span>
          <span className="jd-summary-note">
            {job.isActive
              ? (job.cronExpression ? 'recurring' : 'one-time')
              : 'job is paused'}
          </span>
        </div>

        <div className="jd-summary-metric">
          <label>Worker</label>
          <span className="jd-summary-value jd-summary-value--time">{job.workerId || '—'}</span>
          <span className="jd-summary-note">{job.jobType || 'no job type'}</span>
        </div>
      </div>

      {/* Main Content Grid */}
      <div className="content-grid">
        {/* Job Configuration Card */}
        <CollapsibleSection
          className="info-card config-card"
          storageKey="milvaion.jobDetail.configOpen"
          icon="settings"
          title="Configuration"
        >
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
                <JsonView data={job.jobData} name="jobData" />
              </div>
            )}
          </div>
        </CollapsibleSection>

        {/* Auto-Disable Settings Card */}
        {job.autoDisableSettings && (
          <CollapsibleSection
            className="info-card auto-disable-card"
            storageKey="milvaion.jobDetail.autoDisableOpen"
            icon="power_off"
            title="Auto-Disable (Circuit Breaker)"
          >
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
                    <span className="info-label">Failure Window</span>
                    <span className="info-value">
                      {job.autoDisableSettings.failureWindowMinutes ? `${job.autoDisableSettings.failureWindowMinutes} min` : 'Default (60 min)'}
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
          </CollapsibleSection>
        )}

      </div>

      {/* Occurrences Section */}
      <CollapsibleSection
        className="occurrences-card"
        storageKey="milvaion.jobDetail.historyOpen"
        icon="history"
        title="Execution History"
        actions={
          <div className={`signalr-indicator ${signalRConnected ? 'connected' : 'disconnected'}`}>
            <span className="indicator-dot"></span>
            <span>{signalRConnected ? 'Live' : 'Reconnecting...'}</span>
          </div>
        }
      >

        <OccurrenceTable
          occurrences={occurrences}
          flashIds={flashIds}
          loading={false}
          currentPage={paginationState.cursorHistory.length + 1}
          pageSize={pageSize}
          filterStatus={filterStatus}
          onFilterChange={(status) => {
            setFilterStatus(status)
            resetPagination()
          }}
          onPageSizeChange={(newSize) => {
            setPageSize(newSize)
            resetPagination()
          }}
          useCursorPagination={true}
          hasNextPage={paginationState.hasNextPage}
          hasPreviousPage={paginationState.cursorHistory.length > 0}
          onNextPage={handleNextPage}
          onPreviousPage={handlePreviousPage}
          showJobName={false}
          onBulkDelete={handleBulkDelete}
        />
      </CollapsibleSection>

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
