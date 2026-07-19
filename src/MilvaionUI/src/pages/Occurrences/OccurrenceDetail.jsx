import { useState, useEffect, useRef, useCallback, useMemo } from 'react'
import { useParams, Link, useNavigate } from 'react-router-dom'
import occurrenceService from '../../services/occurrenceService'
import signalRService from '../../services/signalRService'
import { formatDateTime, formatDuration, formatTime } from '../../utils/dateUtils'
import Icon from '../../components/Icon'
import JsonViewer from '../../components/JsonViewer'
import Modal from '../../components/Modal'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { SkeletonDetail } from '../../components/Skeleton'
import { useModal } from '../../hooks/useModal'
import CollapsibleSection from '../../components/CollapsibleSection'
import { getApiErrorMessage } from '../../utils/errorUtils'
import './OccurrenceDetail.css'

function OccurrenceDetail() {
const { id } = useParams()
const navigate = useNavigate()
const [occurrence, setOccurrence] = useState(null)
const [logs, setLogs] = useState([])
const [loading, setLoading] = useState(true)
const [error, setError] = useState(null)
const [signalRConnected, setSignalRConnected] = useState(false)
const [deleting, setDeleting] = useState(false)
const logsContainerRef = useRef(null)
const [autoScroll, setAutoScroll] = useState(true)
const [showCancelModal, setShowCancelModal] = useState(false)
const [cancelReason, setCancelReason] = useState('')
const [lastRefreshTime, setLastRefreshTime] = useState(null)
const [logLevelFilter, setLogLevelFilter] = useState('all')
const [logSearch, setLogSearch] = useState('')

const { modalProps, showModal } = useModal()

  // Ref to track if initial load has been done
  const initialLoadDoneRef = useRef(false)

  // Helper function to check if status is final (component scope)
  const isFinalStatus = useCallback((status) => {
    // 2: Completed, 3: Failed, 4: Cancelled, 5: Timed Out
    return status === 2 || status === 3 || status === 4 || status === 5 || status === 6
  }, [])

  useEffect(() => {
    if (autoScroll && logsContainerRef.current) {
      logsContainerRef.current.scrollTop = logsContainerRef.current.scrollHeight
    }
  }, [logs, autoScroll])

  // Detect manual scroll to disable auto-scroll
  const handleScroll = useCallback(() => {
    if (logsContainerRef.current) {
      const { scrollTop, scrollHeight, clientHeight } = logsContainerRef.current
      const isAtBottom = Math.abs(scrollHeight - clientHeight - scrollTop) < 50
      setAutoScroll(isAtBottom)
    }
  }, [])

  const loadOccurrenceDetails = useCallback(async () => {
    // Prevent duplicate calls
    if (initialLoadDoneRef.current) {
      return
    }
    initialLoadDoneRef.current = true

    try {
      setLoading(true)
      setError(null)

      const occurrenceResponse = await occurrenceService.getById(id)

      // Backend returns Response with data property
      const occurrenceData = occurrenceResponse.data

      // Guard against null/undefined data
      if (!occurrenceData) {
        setError('Occurrence not found')
        setOccurrence(null)
        return
      }

      setOccurrence(occurrenceData)

      // Update last refresh time
      setLastRefreshTime(new Date())

      // Logs are already in the occurrence data from backend
      if (occurrenceData?.logs && Array.isArray(occurrenceData.logs)) {
        // Sort logs by timestamp (ascending - oldest first)
        const sortedLogs = [...occurrenceData.logs].sort((a, b) => {
          // Support both PascalCase (from C#) and camelCase
          const timeA = new Date(a.timestamp || a.Timestamp  || 0).getTime()
          const timeB = new Date(b.timestamp || b.Timestamp  || 0).getTime()
          return timeA - timeB
        })
        setLogs(sortedLogs)
      } else {
        setLogs([])
      }

      // Check if occurrence has final status on initial load
      if (occurrenceData.status !== undefined && isFinalStatus(occurrenceData.status)) {
        setSignalRConnected(false)
      }
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load occurrence details'))
      setOccurrence(null)
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [id, isFinalStatus])

  useEffect(() => {
    // Reset ref when id changes
    initialLoadDoneRef.current = false
    loadOccurrenceDetails()
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  // Ref to track if SignalR has been initialized for this occurrence
  const signalRInitializedRef = useRef(false)
  const occurrenceStatusRef = useRef(null)

  // Update status ref when occurrence changes
  useEffect(() => {
    if (occurrence?.status !== undefined) {
      occurrenceStatusRef.current = occurrence.status
    }
  }, [occurrence?.status])

  useEffect(() => {
    let statusCleanup = null
    let isComponentMounted = true

    // Connect to SignalR and check initial status
    const connectSignalR = async () => {
      // Already initialized for this id
      if (signalRInitializedRef.current) {
        return
      }

      // Wait for occurrence to load (check via ref)
      if (occurrenceStatusRef.current === null) {
        return
      }

      // Don't connect if already in final status
      if (isFinalStatus(occurrenceStatusRef.current)) {
        setSignalRConnected(false)
        return
      }

      signalRInitializedRef.current = true

      // Connect to SignalR
      console.log('🔌 Connecting to SignalR for live updates...')
      await signalRService.connect()

      if (!isComponentMounted) return

      const isConnected = signalRService.isConnected()
      setSignalRConnected(isConnected)

      if (!isConnected) {
        console.warn('⚠️ SignalR connection failed')
        return
      }

      // Subscribe to occurrence updates
      if (id) {
        await signalRService.subscribeToOccurrence(id)
      }

      // Check connection status periodically
      const statusInterval = setInterval(() => {
        if (isComponentMounted) {
          const currentStatus = signalRService.isConnected()
          setSignalRConnected(currentStatus)
        }
      }, 5000)

      statusCleanup = () => {
        clearInterval(statusInterval)
      }
    }

    // Try to connect when occurrence is loaded
    if (occurrence) {
      connectSignalR()
    }

    // Subscribe to occurrence updated event
    const unsubscribeOccurrenceUpdated = signalRService.on('OccurrenceUpdated', (updatedOccurrence) => {
      if (updatedOccurrence && updatedOccurrence.id === id) {

        setOccurrence(prev => prev ? ({
          ...prev,
          ...updatedOccurrence
        }) : updatedOccurrence)

        // Update logs if they exist in the update
        if (updatedOccurrence.logs && Array.isArray(updatedOccurrence.logs)) {
          // Sort logs by timestamp (ascending - oldest first)
          const sortedLogs = [...updatedOccurrence.logs].sort((a, b) => {
            // Support both PascalCase (from C#) and camelCase
            const timeA = new Date(a.timestamp || a.Timestamp || a.createdAt || a.CreatedAt || 0).getTime()
            const timeB = new Date(b.timestamp || b.Timestamp || b.createdAt || b.CreatedAt || 0).getTime()
            return timeA - timeB
          })
          setLogs(sortedLogs)
        }

        // If final status reached, disconnect SignalR after delay to receive remaining logs
        if (updatedOccurrence.status !== undefined && isFinalStatus(updatedOccurrence.status)) {
          console.log('✅ Final status reached, waiting 3 seconds for remaining logs before disconnecting SignalR...')

          // Delay disconnect to allow final logs to arrive
          setTimeout(() => {
            console.log('🔌 Disconnecting SignalR after final status delay...')
            setSignalRConnected(false)
            if (statusCleanup) statusCleanup()
            signalRService.unsubscribeFromOccurrence(id).catch(console.error)
          }, 3000) // 3 second delay
        }
      }
    })

    // Subscribe to new log event
    const unsubscribeLogAdded = signalRService.on('OccurrenceLogAdded', (logData) => {
      if (logData && logData.occurrenceId === id) {
        console.log('🔔 OccurrenceLogAdded event:', logData)

        setLogs(prev => {
          const newLogs = [...prev, logData.log]
          // Sort logs by timestamp (ascending - oldest first)
          return newLogs.sort((a, b) => {
            const timeA = new Date(a.timestamp || a.Timestamp || 0).getTime()
            const timeB = new Date(b.timestamp || b.Timestamp || 0).getTime()
            return timeA - timeB
          })
        })
      }
    })

    // Cleanup on unmount
    return () => {
      isComponentMounted = false
      signalRInitializedRef.current = false
      unsubscribeOccurrenceUpdated()
      unsubscribeLogAdded()

      if (statusCleanup) statusCleanup()

      // Unsubscribe from occurrence updates
      if (id) {
        signalRService.unsubscribeFromOccurrence(id).catch(console.error)
      }
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, isFinalStatus, occurrence?.id])

  const handleCancel = async () => {
    if (deleting) return

    if (occurrence.status === 0) {
      showModal({
        title: '⚠️ Cannot Cancel',
        message: 'Cannot cancel queued occurrence. Please wait for execution to start.',
        confirmText: 'OK'
      })
      return
    }

    if (occurrence.status !== 1) {
      showModal({
        title: '⚠️ Cannot Cancel',
        message: 'Only running occurrences can be cancelled.',
        confirmText: 'OK'
      })
      return
    }

    // Show cancel modal with reason input
    setShowCancelModal(true)
    setCancelReason('')
  }

  const handleCancelConfirm = async () => {
    setDeleting(true)
    try {
      await occurrenceService.cancel(id, cancelReason || null)
      setShowCancelModal(false)
      setCancelReason('')
      showModal({
        title: '✅ Cancellation Requested',
        message: 'Cancellation request sent successfully. The worker will stop the execution.',
        confirmText: 'OK',
        onConfirm: () => {
          initialLoadDoneRef.current = false
          loadOccurrenceDetails()
        }
      })
    } catch (err) {
      console.error('Cancel error:', err)
      const errorMessage = err.response?.data?.message || 'Failed to cancel occurrence'
      showModal({
        title: '❌ Cancellation Failed',
        message: errorMessage,
        confirmText: 'OK'
      })
    } finally {
      setDeleting(false)
    }
  }

  const handleCancelModalClose = () => {
    setShowCancelModal(false)
    setCancelReason('')
  }

  const handleDelete = async () => {
    if (deleting) return

    const statusNames = ['Queued', 'Running', 'Completed', 'Failed', 'Cancelled', 'Timed Out', 'Unknown']
    const currentStatus = statusNames[occurrence.status] || 'Unknown'

    if (occurrence.status === 0) {
      showModal({
        title: '⚠️ Cannot Delete',
        message: 'Cannot delete queued occurrence. Please cancel it first or wait for execution.',
        confirmText: 'OK'
      })
      return
    }

    if (occurrence.status === 1) {
      showModal({
        title: '⚠️ Cannot Delete',
        message: 'Cannot delete running occurrence. Please cancel it first.',
        confirmText: 'OK'
      })
      return
    }

    showModal({
      title: '⚠️ Delete Occurrence',
      message: `Are you sure you want to delete this ${currentStatus} occurrence?\n\nThis action cannot be undone.`,
      confirmText: 'Yes, Delete',
      cancelText: 'No',
      showCancel: true,
      onConfirm: async () => {
        setDeleting(true)
        try {
          const response = await occurrenceService.delete(id)

          if (response?.isSuccess === false) {
            const errorMessage = response.messages?.[0]?.message || 'Failed to delete occurrence'
            showModal({
              title: '❌ Delete Failed',
              message: errorMessage,
              confirmText: 'OK'
            })
            return
          }

          showModal({
            title: '✅ Occurrence Deleted',
            message: 'Occurrence deleted successfully',
            confirmText: 'OK',
            onConfirm: () => {
              if (occurrence.jobId) {
                navigate(`/jobs/${occurrence.jobId}`)
              } else {
                navigate('/executions')
              }
            }
          })
        } catch (err) {
          console.error('Delete error:', err)
          const errorMessage = err.response?.data?.message || 'Failed to delete occurrence'
          showModal({
            title: '❌ Delete Failed',
            message: errorMessage,
            confirmText: 'OK'
          })
        } finally {
          setDeleting(false)
        }
      }
    })
  }

  // Seviye başına kayıt sayısı. Filtre düğmelerinde gösteriliyor ve sıfır olan
  // seviyenin düğmesi hiç çizilmiyor - tıklanınca boş liste veren bir filtre,
  // filtrenin bozuk olduğunu düşündürüyor.
  const logCounts = useMemo(() => {
    const counts = {}

    for (const log of logs) {
      const level = (log.level || 'information').toLowerCase()

      // Backend bazı yerlerde 'Info', bazı yerlerde 'Information' yazıyor.
      const key = level === 'info' ? 'information' : level

      counts[key] = (counts[key] ?? 0) + 1
    }

    return counts
  }, [logs])

  const visibleLogs = useMemo(() => {
    const search = logSearch.trim().toLowerCase()

    return logs.filter(log => {
      if (logLevelFilter !== 'all') {
        const level = (log.level || 'information').toLowerCase()
        const key = level === 'info' ? 'information' : level

        if (key !== logLevelFilter) return false
      }

      if (search && !(log.message || '').toLowerCase().includes(search)) return false

      return true
    })
  }, [logs, logLevelFilter, logSearch])

  const getStatusBadge = (status, noAnimate = false) => {
    const statusMap = {
      0: { icon: 'schedule', label: 'Queued', className: 'queued' },
      1: { icon: 'sync', label: 'Running', className: 'running' },
      2: { icon: 'check_circle', label: 'Completed', className: 'success' },
      3: { icon: 'cancel', label: 'Failed', className: 'failed' },
      4: { icon: 'block', label: 'Cancelled', className: 'cancelled' },
      5: { icon: 'schedule', label: 'Timed Out', className: 'timeout' },
      6: { icon: 'help_outline', label: 'Unknown', className: 'unknown' },
      7: { icon: 'skip_next', label: 'Skipped', className: 'skipped' },
    }

    const statusInfo = statusMap[status] || { icon: 'help', label: `Status ${status}`, className: 'default' }
    return (
      <span className={`occurrence-status ${statusInfo.className}${noAnimate ? ' no-animate' : ''}`}>
        <Icon name={statusInfo.icon} size={20} />
        {statusInfo.label}
      </span>
    )
  }

  const getDuration = () => {
    if (!occurrence?.startTime) return '-'
    if (occurrence?.durationMs) {
      // Backend provides duration in milliseconds
      const ms = occurrence.durationMs
      if (ms < 1000) {
        return `${ms}ms`
      }
      const seconds = Math.floor(ms / 1000)
      const minutes = Math.floor(seconds / 60)
      const hours = Math.floor(minutes / 60)

      if (hours > 0) {
        return `${hours}h ${minutes % 60}m ${seconds % 60}s`
      } else if (minutes > 0) {
        return `${minutes}m ${seconds % 60}s`
      } else {
        return `${seconds}s`
      }
    }
    return formatDuration(occurrence.startTime, occurrence.endTime)
  }

  if (loading) return <SkeletonDetail />
  if (error) return <div className="error">{error}</div>
  if (!occurrence) return <div className="error">Occurrence not found</div>

  // Özet şeridinin kenar rengi. Durum kodu yerine tona indirgiyoruz: sekiz durum
  // için sekiz ayrı renk, bakışta ayırt edilebilir olmaktan çıkıyor.
  const statusTone = {
    0: 'idle', 1: 'running', 2: 'success', 3: 'danger',
    4: 'idle', 5: 'warning', 6: 'idle', 7: 'idle',
  }[occurrence.status] ?? 'idle'

  return (
    <div className="page occurrence-detail">
      <Modal {...modalProps} />

      {/* Cancel Modal with Reason Input */}
      {showCancelModal && (
        <Modal
          isOpen={true}
          onClose={handleCancelModalClose}
          title="⚠️ Cancel Occurrence"
          message={
            <div className="cancel-modal-content">
              <p>Are you sure you want to cancel this running occurrence?</p>
              <p className="cancel-warning">This will send a cancellation request to the worker.</p>
              <div className="form-group" style={{ marginTop: '1rem' }}>
                <label htmlFor="cancelReason">Cancellation Reason (optional)</label>
                <textarea
                  id="cancelReason"
                  value={cancelReason}
                  onChange={(e) => setCancelReason(e.target.value)}
                  placeholder="Enter reason for cancellation..."
                  rows={3}
                  style={{ width: '100%', marginTop: '0.5rem' }}
                />
              </div>
            </div>
          }
          confirmText={deleting ? 'Cancelling...' : 'Yes, Cancel'}
          cancelText="No, Keep Running"
          showCancel={true}
          onConfirm={handleCancelConfirm}
          onCancel={handleCancelModalClose}
          type="confirm"
        />
      )}

      <div className="dtl-header">
        <div className="dtl-header-left">
          <Link
            to={occurrence.jobId ? `/jobs/${occurrence.jobId}` : '/jobs'}
            className="dtl-back"
            title="Back to Job"
          >
            <Icon name="arrow_back" size={24} />
          </Link>
          <div className="dtl-title">
            <h1>
              <Icon name="play_circle" size={28} />
              {occurrence.jobName || 'Execution'}
            </h1>
          </div>
        </div>
        <div className="dtl-actions">
          <div className={`signalr-status ${signalRConnected ? 'connected' : occurrence && isFinalStatus(occurrence.status) ? 'disconnected' : 'reconnecting'}`}>
            <span className="status-dot"></span>
            <span className="status-text">
              {signalRConnected
                ? 'Live Updates'
                : occurrence && isFinalStatus(occurrence.status)
                  ? 'Disconnected'
                  : 'Reconnecting...'}
            </span>
          </div>

          {/* Show cancel button for queued or running occurrences */}
          {(occurrence.status === 0 || occurrence.status === 1) && (
            <button
              onClick={handleCancel}
              disabled={deleting || occurrence.externalJobId}
              className="dtl-btn dtl-btn--warning"
              title={occurrence.externalJobId ? "External job occurrences cannot be cancelled from Milvaion" : "Cancel Occurrence"}
            >
              <Icon name="cancel" size={18} />
              {deleting ? 'Cancelling...' : 'Cancel'}
            </button>
          )}

          {/* Show delete button only for completed, failed, cancelled, timed out occurrences */}
          {(occurrence.status === 2 || occurrence.status === 3 || occurrence.status === 4 || occurrence.status === 5 || occurrence.status === 6) && (
            <button
              onClick={handleDelete}
              disabled={deleting}
              className="dtl-btn dtl-btn--danger"
              title="Delete Occurrence"
            >
              <Icon name="delete" size={18} />
              {deleting ? 'Deleting...' : 'Delete'}
            </button>
          )}
        </div>
      </div>

      {/* Özet şeridi: sayfanın cevapladığı ilk soru durum ve süre, o yüzden
          ölçü kartlarının arasında değil en üstte duruyor. */}
      <div className={`occ-summary occ-summary--${statusTone}`}>
        <div className="occ-summary-status">
          {getStatusBadge(occurrence.status)}
          {occurrence.retryCount > 0 && (
            <span className="occ-summary-retry" title="Retry attempts before this result">
              <Icon name="replay" size={13} /> {occurrence.retryCount} {occurrence.retryCount === 1 ? 'retry' : 'retries'}
            </span>
          )}
        </div>

        <div className="occ-summary-timeline">
          <div className="occ-summary-stat">
            <label>Started</label>
            <span>{occurrence.startTime ? formatDateTime(occurrence.startTime) : '—'}</span>
          </div>
          <Icon name="arrow_forward" size={16} className="occ-summary-arrow" />
          <div className="occ-summary-stat">
            <label>Duration</label>
            <span className="occ-summary-duration">{getDuration()}</span>
          </div>
          <Icon name="arrow_forward" size={16} className="occ-summary-arrow" />
          <div className="occ-summary-stat">
            <label>Finished</label>
            <span>{occurrence.endTime ? formatDateTime(occurrence.endTime) : '—'}</span>
          </div>
        </div>
      </div>

      {/* Hata varsa ölçü kartlarının arasına gömmek yerine kendi bölümünde ve
          en üstte: sayfaya gelinme sebebi genelde bu. */}
      {occurrence.exception && (
        <div className="occ-exception">
          <div className="occ-exception-head">
            <Icon name="error" size={18} />
            <strong>Exception</strong>
          </div>
          <pre>{occurrence.exception}</pre>
        </div>
      )}

      <CollapsibleSection
        className="occurrence-info-card"
        storageKey="milvaion.occurrenceDetail.infoOpen"
        icon="info"
        title="Execution Information"
      >
        <div className="info-grid">
          {occurrence.workerId && (
            <div className="info-item">
              <label>Worker</label>
              <span>{occurrence.workerId}</span>
            </div>
          )}
          <div className="info-item">
            <label>Job</label>
            {occurrence.jobId ? (
              <Link to={`/jobs/${occurrence.jobId}`} className="job-link">
                {occurrence.jobName || 'View job'} <Icon name="arrow_forward" size={13} />
              </Link>
            ) : (
              <span className="text-muted">—</span>
            )}
          </div>
          <div className="info-item">
            <label>Job version</label>
            <span className="version-badge">
              <Icon name="history" size={14} />
              v{occurrence.jobVersion || 1}
            </span>
          </div>
          <div className="info-item">
            <label>Execution id</label>
            <span className="correlation-id">{occurrence.id}</span>
          </div>
          {occurrence.correlationId && (
            <div className="info-item">
              <label>Correlation id</label>
              <span className="correlation-id">{occurrence.correlationId}</span>
            </div>
          )}
          {occurrence.externalJobId && (
            <div className="info-item">
              <label>External id</label>
              <span className="external-badge-detail" title="This occurrence is from an external scheduler (Quartz, Hangfire, etc.)">
                <Icon name="cloud_sync" size={14} />
                {occurrence.externalJobId}
              </span>
            </div>
          )}
        </div>

        {occurrence.result && (
          <div className="occ-result">
            <JsonViewer data={occurrence.result} title="Result Data" />
          </div>
        )}
      </CollapsibleSection>

      {logs.length > 0 && (
        <CollapsibleSection
          className="logs-section"
          storageKey="milvaion.occurrenceDetail.logsOpen"
          icon="article"
          title={`Execution Logs (${logs.length})`}
        >
          {/* Seviye sayıları filtrenin üstünde duruyor: kaç hata olduğunu görmek için
              filtreyi denemek gerekmiyor. Sıfır olan seviye hiç gösterilmiyor. */}
          <div className="occ-log-toolbar">
            <div className="occ-log-levels">
              <button
                type="button"
                className={`occ-log-level${logLevelFilter === 'all' ? ' is-active' : ''}`}
                onClick={() => setLogLevelFilter('all')}
              >
                All <span className="occ-log-level-count">{logs.length}</span>
              </button>

              {['error', 'warning', 'information', 'debug'].map(level => {
                const count = logCounts[level] ?? 0

                if (count === 0) return null

                return (
                  <button
                    key={level}
                    type="button"
                    className={`occ-log-level occ-log-level--${level}${logLevelFilter === level ? ' is-active' : ''}`}
                    onClick={() => setLogLevelFilter(level)}
                  >
                    {level === 'information' ? 'Info' : level.charAt(0).toUpperCase() + level.slice(1)}
                    <span className="occ-log-level-count">{count}</span>
                  </button>
                )
              })}
            </div>

            <div className="occ-log-search">
              <Icon name="search" size={15} />
              <input
                type="text"
                value={logSearch}
                onChange={e => setLogSearch(e.target.value)}
                placeholder="Filter messages"
                aria-label="Filter log messages"
              />
              {logSearch && (
                <button type="button" onClick={() => setLogSearch('')} aria-label="Clear filter">
                  <Icon name="close" size={14} />
                </button>
              )}
            </div>
          </div>

          {visibleLogs.length === 0 ? (
            <p className="occ-log-empty">
              No entries match this filter.{' '}
              <button type="button" onClick={() => { setLogLevelFilter('all'); setLogSearch('') }}>
                Clear it
              </button>
            </p>
          ) : (
          <div className="logs-container" onScroll={handleScroll} ref={logsContainerRef}>
            {visibleLogs.map((log, index) => {
              // Generate unique key: timestamp + index (in case of duplicate timestamps)
              const logKey = `${log.timestamp || log.createdAt || 0}-${index}`

              return (
                <div key={logKey} className={`log-entry log-${log.level?.toLowerCase() || 'information'}`}>
                  <span className="log-time">
                    {log.timestamp ? formatTime(log.timestamp) : (log.createdAt ? formatTime(log.createdAt) : '-')}
                  </span>
                  <span className={`log-level level-${log.level?.toLowerCase() || 'information'}`}>
                    {log.level === 'Error' && <Icon name="error" size={16} />}
                    {log.level === 'Warning' && <Icon name="warning" size={16} />}
                    {(log.level === 'Information' || log.level === 'Info') && <Icon name="info" size={16} />}
                    {log.level === 'Debug' && <Icon name="bug_report" size={16} />}
                    {!log.level && <Icon name="info" size={16} />}
                    {' '}{log.level || 'Info'}
                  </span>
                  <span className="log-message">{log.message || 'No message'}</span>
                  {log.data && (typeof log.data === 'object' || typeof log.data === 'string') && (
                    <JsonViewer data={log.data} title="Log Data" />
                  )}
                </div>
              )
            })}
          </div>
          )}
        </CollapsibleSection>
      )}

      {occurrence.statusChangeLogs && occurrence.statusChangeLogs.length > 0 && (
        <CollapsibleSection
          className="status-history-section"
          storageKey="milvaion.occurrenceDetail.historyOpen"
          icon="history"
          title={`Status Change History (${occurrence.statusChangeLogs.length})`}
        >
          <div className="status-history-timeline">
            {occurrence.statusChangeLogs
              .sort((a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime())
              .map((change, index) => (
                <div key={index} className="status-change-item">
                  <div className="status-change-time">
                    <Icon name="schedule" size={16} />
                    {formatDateTime(change.timestamp)}
                  </div>
                  <div className="status-change-flow">
                    {getStatusBadge(change.from, true)}
                    <Icon name="arrow_forward" size={20} className="status-arrow" />
                    {getStatusBadge(change.to, true)}
                  </div>
                </div>
              ))}
          </div>
        </CollapsibleSection>
      )}

      {/* Auto-refresh indicator - only show when SignalR is connected */}
      {signalRConnected && (
        <AutoRefreshIndicator
          enabled={signalRConnected}
          onToggle={() => {}}
          lastRefreshTime={lastRefreshTime}
          intervalSeconds={10}
        />
      )}
    </div>
  )
}

export default OccurrenceDetail
