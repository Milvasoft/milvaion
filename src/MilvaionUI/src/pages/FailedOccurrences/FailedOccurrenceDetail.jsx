import { useState, useEffect, useCallback } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import failedOccurrenceService from '../../services/failedOccurrenceService'
import { formatDateTime } from '../../utils/dateUtils'
import Modal from '../../components/Modal'
import Icon from '../../components/Icon'
import { useModal } from '../../hooks/useModal'
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
      setError('Failed to load job details')
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
      await failedOccurrenceService.delete(id)
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
      await failedOccurrenceService.markAsResolved(id, resolutionNote, resolutionAction)
      await loadJob()
      await showSuccess('Failed job marked as resolved')
    } catch (err) {
      await showError('Failed to resolve job. Please try again.')
      console.error(err)
    }
  }

  if (loading) return <div className="loading">Loading failed job details...</div>
  if (error) return <div className="error">{error}</div>
  if (!job) return <div className="error">Job not found</div>

  const failureTypeInfo = failedOccurrenceService.getFailureTypeInfo(job.failureType)

  return (
    <div className="failed-job-detail">
      <Modal {...modalProps} />

      {/* Breadcrumb */}
      <div className="breadcrumb">
        <Link to="/failed-executions" className="breadcrumb-link">
          <Icon name="error" size={18} />
          Failed Executions
        </Link>
        <Icon name="chevron_right" size={18} />
        <span>{job.jobDisplayName}</span>
      </div>

      {/* Header */}
      <div className="detail-header">
        <div className="header-left">
          <Link to="/failed-executions" className="back-icon-btn" title="Back to Failed Executions">
            <Icon name="arrow_back" size={24} />
          </Link>
          <div className="header-info">
            <h1>{job.jobDisplayName}</h1>
            {job.resolved ? (
              <span className="status-badge resolved">
                <Icon name="check_circle" size={20} />
                Resolved
              </span>
            ) : (
              <span className="status-badge unresolved">
                <Icon name="pending" size={20} />
                Unresolved
              </span>
            )}
          </div>
        </div>
        <div className="header-actions">
          {!job.resolved && (
            <button onClick={handleResolve} className="btn btn-success" >
              <Icon name="check" size={18} />
              Mark as Resolved
            </button>
          )}
          <button onClick={handleDelete} className="btn btn-danger" style={{ opacity: 0.3, color: '#ffff' }}>
            <Icon name="delete" size={18} />
            Delete
          </button>
        </div>
      </div>

      {/* Main Content Grid */}
      <div className="detail-grid">
        {/* Failure Information Card */}
        <div className="detail-card">
          <div className="card-header">
            <Icon name="error" size={24} />
            <h2>Failure Information</h2>
          </div>
          <div className="card-content">
            <div className="info-row">
              <span className="label">Failure Type</span>
              <span className={`value failure-type-badge ${failureTypeInfo.className}`}>
                <Icon name={failureTypeInfo.icon} size={18} />
                {failureTypeInfo.label}
              </span>
            </div>
            <div className="info-row">
              <span className="label">Failed At</span>
              <span className="value">{formatDateTime(job.failedAt)}</span>
            </div>
            <div className="info-row">
              <span className="label">Retry Count</span>
              <span className="value">{job.retryCount} attempts</span>
            </div>
            <div className="info-row">
              <span className="label">Worker ID</span>
              <span className="value"><code>{job.workerId || 'N/A'}</code></span>
            </div>
            <div className="info-row">
              <span className="label">Original Execute At</span>
              <span className="value">{job.originalExecuteAt ? formatDateTime(job.originalExecuteAt) : 'N/A'}</span>
            </div>
          </div>
        </div>

        {/* Job Information Card */}
        <div className="detail-card">
          <div className="card-header">
            <Icon name="work" size={24} />
            <h2>Job Information</h2>
          </div>
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
        </div>

        {/* Exception Details Card */}
        <div className="detail-card full-width">
          <div className="card-header">
            <Icon name="bug_report" size={24} />
            <h2>Exception Details</h2>
          </div>
          <div className="card-content">
            <div className="exception-content">
              <pre>{job.exception}</pre>
            </div>
          </div>
        </div>

        {/* Job Data Card */}
        {job.jobData && (
          <div className="detail-card full-width">
            <div className="card-header">
              <Icon name="data_object" size={24} />
              <h2>Job Data</h2>
            </div>
            <div className="card-content">
              <div className="json-content">
                <pre>{JSON.stringify(JSON.parse(job.jobData || '{}'), null, 2)}</pre>
              </div>
            </div>
          </div>
        )}

        {/* Resolution Information Card */}
        {job.resolved && (
          <div className="detail-card full-width resolution-card">
            <div className="card-header">
              <Icon name="check_circle" size={24} />
              <h2>Resolution Information</h2>
            </div>
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
          </div>
        )}
      </div>
    </div>
  )
}

export default FailedOccurrenceDetail


