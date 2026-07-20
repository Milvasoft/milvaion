import { useState, useEffect, useCallback } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import failedOccurrenceService from '../../services/failedOccurrenceService'
import { formatDateTime } from '../../utils/dateUtils'
import Modal from '../../components/Modal'
import Icon from '../../components/Icon'
import CollapsibleSection from '../../components/CollapsibleSection'
import JsonView from '../../components/JsonView'
import { SkeletonDetail } from '../../components/Skeleton'
import { useModal } from '../../hooks/useModal'
import { getApiErrorMessage } from '../../utils/errorUtils'
import './FailedOccurrenceDetail.css'

function FailedOccurrenceDetail() {
  const { id } = useParams()
  const navigate = useNavigate()
  const [job, setJob] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)

  const { modalProps, showConfirm, showSuccess, showError, showModal } = useModal()

  const loadJob = useCallback(async () => {
    try {
      setLoading(true)
      setError(null)
      const response = await failedOccurrenceService.getById(id)
      setJob(response.data)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load job details'))
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [id])

  useEffect(() => {
    loadJob()

    // Auto-refresh every 30 seconds (seamless data refresh)
    const refreshInterval = setInterval(() => {
      loadJob()
    }, 30000) // 30 seconds

    return () => clearInterval(refreshInterval)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  const handleDelete = async () => {
    const confirmed = await showConfirm(
      'Are you sure you want to delete this failed job record? This action cannot be undone.',
      'Delete Failed Job',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    try {
      const response = await failedOccurrenceService.delete(id)

      if (response?.isSuccess === false) {
        const message = response.messages?.[0]?.message || 'Failed to delete job record.'
        await showError(message)
        return
      }

      await showSuccess('Failed job deleted successfully')
      navigate('/failed-executions')
    } catch (err) {
      await showError('Failed to delete job record. Please try again.')
      console.error(err)
    }
  }

  const handleResolve = async () => {
    // Create form state to track input values
    let resolutionActionValue = 'Manually resolved'
    let resolutionNoteValue = ''

    const modalContent = (
      <div className="resolve-form">
        <div className="form-group">
          <label htmlFor="resolutionAction">Resolution Action *</label>
          <select
            id="resolutionAction"
            className="form-control"
            defaultValue="Manually resolved"
            onChange={(e) => { resolutionActionValue = e.target.value }}
          >
            <option value="Manually resolved">Manually resolved</option>
            <option value="Retried manually">Retried manually</option>
            <option value="Fixed data and re-queued">Fixed data and re-queued</option>
            <option value="Ignored - invalid data">Ignored - invalid data</option>
            <option value="Ignored - duplicate">Ignored - duplicate</option>
            <option value="Fixed configuration">Fixed configuration</option>
            <option value="Other">Other</option>
          </select>
        </div>
        <div className="form-group">
          <label htmlFor="resolutionNote">Resolution Notes *</label>
          <textarea
            id="resolutionNote"
            className="form-control"
            rows="4"
            placeholder="Describe what was done to resolve this issue..."
            onChange={(e) => { resolutionNoteValue = e.target.value }}
            required
          />
        </div>
      </div>
    )

    const confirmed = await showModal(
      modalContent,
      'Resolve Failed Job',
      'Resolve',
      'Cancel'
    )

    if (!confirmed) return

    // Get values from DOM as fallback (in case onChange didn't fire)
    const resolutionActionElement = document.getElementById('resolutionAction')
    const resolutionNoteElement = document.getElementById('resolutionNote')

    const resolutionAction = resolutionActionElement?.value || resolutionActionValue
    const resolutionNote = resolutionNoteElement?.value || resolutionNoteValue

    if (!resolutionNote.trim()) {
      await showError('Please provide resolution notes')
      return
    }

    try {
      const response = await failedOccurrenceService.markAsResolved(id, resolutionNote, resolutionAction)

      if (response?.isSuccess === false) {
        const message = response.messages?.[0]?.message || 'Failed to resolve job.'
        await showError(message)
        return
      }

      await loadJob()
      await showSuccess('Failed job marked as resolved')
    } catch (err) {
      await showError('Failed to resolve job. Please try again.')
      console.error(err)
    }
  }

  if (loading) return <SkeletonDetail />
  if (error) return <div className="error">{error}</div>
  if (!job) return <div className="error">Job not found</div>

  const failureTypeInfo = failedOccurrenceService.getFailureTypeInfo(job.failureType)

  return (
    <div className="page failed-job-detail">
      <Modal {...modalProps} />

      {/* Header */}
      <div className="dtl-header">
        <div className="dtl-header-left">
          <Link to="/failed-executions" className="dtl-back" title="Back to Failed Executions">
            <Icon name="arrow_back" size={24} />
          </Link>
          <div className="dtl-title">
            <h1>
              <Icon name="error" size={28} />
              {job.jobDisplayName}
              <span className={`dtl-badge ${job.resolved ? 'dtl-badge--success' : 'dtl-badge--danger'}`}>
                <Icon name={job.resolved ? 'check_circle' : 'pending'} size={14} />
                {job.resolved ? 'Resolved' : 'Unresolved'}
              </span>
            </h1>
          </div>
        </div>
        <div className="dtl-actions">
          {!job.resolved && (
            <button onClick={handleResolve} className="dtl-btn dtl-btn--success">
              <Icon name="check" size={18} /> Mark as Resolved
            </button>
          )}
          {/* Silme düğmesi opacity 0.3 ile neredeyse görünmezdi; tıklanabilir bir
              şeyin görünmemesi, yanlışlıkla basılmasını engellemenin yolu değil. */}
          <button onClick={handleDelete} className="dtl-btn dtl-btn--danger">
            <Icon name="delete" size={18} /> Delete
          </button>
        </div>
      </div>

      {/* Özet şeridi: hata türü, ne zaman ve kaç denemeden sonra. Bunlar üç ayrı
          satırda, ilk kartın içindeydi; sayfaya gelen önce bunlara bakıyor. */}
      <div className={`fo-summary fo-summary--${job.resolved ? 'resolved' : 'open'}`}>
        <div className="fo-summary-metric">
          <label>Failure type</label>
          <span className={`fo-summary-type ${failureTypeInfo.className}`}>
            <Icon name={failureTypeInfo.icon} size={16} />
            {failureTypeInfo.label}
          </span>
        </div>

        <div className="fo-summary-metric">
          <label>Failed at</label>
          <span className="fo-summary-value">{formatDateTime(job.failedAt)}</span>
        </div>

        <div className="fo-summary-metric">
          <label>Attempts</label>
          <span className="fo-summary-value">
            {job.retryCount}
            <span className="fo-summary-unit">{job.retryCount === 1 ? 'try' : 'tries'}</span>
          </span>
        </div>

        <div className="fo-summary-metric">
          <label>Worker</label>
          <span className="fo-summary-value fo-summary-value--text">{job.workerId || '—'}</span>
        </div>
      </div>

      {/* Sayfaya gelinme sebebi bu; en alttaki üçüncü kart yerine burada. */}
      <div className="fo-exception">
        <div className="fo-exception-head">
          <Icon name="bug_report" size={18} />
          <strong>Exception</strong>
        </div>
        <pre>{job.exception}</pre>
      </div>

      {/* Main Content Grid */}
      <div className="detail-grid">
        <CollapsibleSection
          className="detail-card"
          storageKey="milvaion.failedDetail.failureOpen"
          icon="error"
          title="Failure Information"
        >
          <div className="card-content">
            <div className="info-row">
              <span className="label">Original execute at</span>
              <span className="value">{job.originalExecuteAt ? formatDateTime(job.originalExecuteAt) : 'N/A'}</span>
            </div>
            <div className="info-row">
              <span className="label">Failure type</span>
              <span className={`value failure-type-badge ${failureTypeInfo.className}`}>
                <Icon name={failureTypeInfo.icon} size={18} />
                {failureTypeInfo.label}
              </span>
            </div>
          </div>
        </CollapsibleSection>

        {/* Job Information Card */}
        <CollapsibleSection
          className="detail-card"
          storageKey="milvaion.failedDetail.jobOpen"
          icon="work"
          title="Job Information"
        >
          <div className="card-content">
            <div className="info-row">
              <span className="label">Job ID</span>
              <span className="value">
                <Link to={`/jobs/${job.jobId}`} className="job-link">
                  <code>{job.jobId}</code>
                  <Icon name="open_in_new" size={16} />
                </Link>
              </span>
            </div>
            <div className="info-row">
              <span className="label">Occurrence ID</span>
              <span className="value">
                <Link to={`/occurrences/${job.occurrenceId}`} className="job-link">
                  <code>{job.occurrenceId}</code>
                  <Icon name="open_in_new" size={16} />
                </Link>
              </span>
            </div>
            <div className="info-row">
              <span className="label">Correlation ID</span>
              <span className="value"><code>{job.correlationId}</code></span>
            </div>
            <div className="info-row">
              <span className="label">Job Type</span>
              <span className="value"><code>{job.jobNameInWorker}</code></span>
            </div>
          </div>
        </CollapsibleSection>

        {/* Job Data Card */}
        {job.jobData && (
          <CollapsibleSection
            className="detail-card full-width"
            storageKey="milvaion.failedDetail.dataOpen"
            icon="data_object"
            title="Job Data"
          >
            <div className="card-content">
              {/* `JsonView` parses defensively. The previous version called `JSON.parse`
                  inline, so a payload that would not parse threw during render and blanked
                  the page - on the one screen whose whole purpose is to explain a failure,
                  and where "invalid job data" is itself one of the recorded failure
                  types. */}
              <JsonView data={job.jobData} name="jobData" />
            </div>
          </CollapsibleSection>
        )}

        {/* Resolution Information Card */}
        {job.resolved && (
          <CollapsibleSection
            className="detail-card full-width resolution-card"
            storageKey="milvaion.failedDetail.resolutionOpen"
            icon="check_circle"
            title="Resolution Information"
          >
            <div className="card-content">
              <div className="info-row">
                <span className="label">Resolved By</span>
                <span className="value">{job.resolvedBy}</span>
              </div>
              <div className="info-row">
                <span className="label">Resolved At</span>
                <span className="value">{formatDateTime(job.resolvedAt)}</span>
              </div>
              <div className="info-row">
                <span className="label">Resolution Action</span>
                <span className="value resolution-action">{job.resolutionAction}</span>
              </div>
              <div className="info-row">
                <span className="label">Resolution Note</span>
                <div className="value resolution-notes">
                  <p>{job.resolutionNote}</p>
                </div>
              </div>
            </div>
          </CollapsibleSection>
        )}
      </div>
    </div>
  )
}

export default FailedOccurrenceDetail


