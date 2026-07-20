import { useState, useEffect, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import failedOccurrenceService from '../../services/failedOccurrenceService'
import Modal from '../../components/Modal'
import Icon from '../../components/Icon'
import Pagination from '../../components/Pagination'
import TableActions, { ActionButton } from '../../components/TableActions'
import {
  TableToolbar,
  TableSearch,
  SegmentedFilter,
  SelectionBar,
  BulkDeleteButton,
  TimeCell,
  TableEmpty,
  TableFooter,
} from '../../components/TableParts'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { SkeletonTable } from '../../components/Skeleton'
import { useModal } from '../../hooks/useModal'
import { getApiErrorMessage } from '../../utils/errorUtils'
import './FailedOccurrenceList.css'

/** The one thing this page is read for: what is still outstanding. */
const RESOLVED_FILTERS = [
  { value: null, label: 'All' },
  { value: false, label: 'Unresolved' },
  { value: true, label: 'Resolved' },
]

function FailedOccurrenceList() {
  const navigate = useNavigate()
  const [jobs, setJobs] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [debouncedSearchTerm, setDebouncedSearchTerm] = useState('')
  const [currentPage, setCurrentPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [totalCount, setTotalCount] = useState(0)
  const [filterResolved, setFilterResolved] = useState(null) // null = all, true = resolved, false = unresolved
  const [filterFailureType, setFilterFailureType] = useState(null)
  const [selectedJobs, setSelectedJobs] = useState([])
  const [autoRefreshEnabled, setAutoRefreshEnabled] = useState(() => {
    const saved = localStorage.getItem('failedOccurrences_autoRefresh')
    return saved !== null ? saved === 'true' : true
  })
  const [lastRefreshTime, setLastRefreshTime] = useState(null)

  const { modalProps: deleteModalProps, showConfirm, showSuccess, showError } = useModal()
  const { modalProps: resolveModalProps, showModal } = useModal()

  // Debounce search term
  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearchTerm(searchTerm)
      setCurrentPage(1)
    }, 500)

    return () => clearTimeout(timer)
  }, [searchTerm])

  const loadJobs = useCallback(async (showLoading = false) => {
    try {
      if (showLoading) {
        setLoading(true)
      }
      setError(null)

      const requestBody = {
        pageNumber: currentPage,
        rowCount: pageSize
      }

      // Add search term
      if (debouncedSearchTerm) {
        requestBody.searchTerm = debouncedSearchTerm
      }

      // Build filtering criteria
      const criterias = []

      // Filter by resolved status
      if (filterResolved !== null) {
        criterias.push({
          filterBy: "Resolved",
          value: filterResolved,
          type: 5 // Equals
        })
      }

      // Filter by failure type
      if (filterFailureType !== null) {
        criterias.push({
          filterBy: "FailureType",
          value: filterFailureType,
          type: 5 // Equals
        })
      }

      if (criterias.length > 0) {
        requestBody.filtering = { criterias }
      }

      const response = await failedOccurrenceService.getAll(requestBody)

      const data = response?.data?.data || response?.data || []
      const total = response?.data?.totalDataCount || response?.totalDataCount || 0

      setJobs(data)
      setTotalCount(total)
      setLastRefreshTime(new Date())
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load failed jobs'))
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [filterResolved, filterFailureType, currentPage, pageSize, debouncedSearchTerm])

  useEffect(() => {
    loadJobs(true) // Always show loading on navigation

    // Auto-refresh every 30 seconds
    const refreshInterval = setInterval(() => {
      if (autoRefreshEnabled) {
        loadJobs(false) // Don't show loading on auto-refresh
      }
    }, 30000) // 30 seconds

    return () => clearInterval(refreshInterval)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterResolved, filterFailureType, currentPage, pageSize, debouncedSearchTerm, autoRefreshEnabled])

  // Clear selection when filters change
  useEffect(() => {
    setSelectedJobs([])
  }, [currentPage, filterResolved, filterFailureType])

  const handleSelectAll = (e) => {
    if (e.target.checked) {
      setSelectedJobs(jobs.map(job => job.id))
    } else {
      setSelectedJobs([])
    }
  }

  const handleSelectJob = (jobId) => {
    setSelectedJobs(prev =>
      prev.includes(jobId)
        ? prev.filter(id => id !== jobId)
        : [...prev, jobId]
    )
  }

  const handleDelete = async (id) => {
    const jobIds = id ? [id] : selectedJobs

    if (jobIds.length === 0) {
      await showError('Please select at least one failed job to delete.')
      return
    }

    const confirmed = await showConfirm(
      `Are you sure you want to delete ${jobIds.length > 1 ? `${jobIds.length} failed job records` : 'this failed job record'}? This action cannot be undone.`,
      'Delete Failed Job',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    try {
      const response = await failedOccurrenceService.delete(jobIds)

      if (response?.isSuccess === false) {
        const message = response.messages?.[0]?.message || 'Failed to delete job record(s).'
        await showError(message)
        return
      }

      setSelectedJobs([])
      await loadJobs()
      await showSuccess(`${jobIds.length > 1 ? `${jobIds.length} failed jobs` : 'Failed job'} deleted successfully`)
    } catch (err) {
      await showError('Failed to delete job record(s). Please try again.')
      console.error(err)
    }
  }

  const handleResolve = async (job) => {
    const jobIds = job ? [job.id] : selectedJobs

    if (jobIds.length === 0) {
      await showError('Please select at least one failed job to resolve.')
      return
    }

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
        {jobIds.length > 1 && (
          <div className="bulk-info">
            <Icon name="info" size={16} />
            <span>This will mark {jobIds.length} failed jobs as resolved with the same notes.</span>
          </div>
        )}
      </div>
    )

    const confirmed = await showModal(
      modalContent,
      jobIds.length > 1 ? `Resolve ${jobIds.length} Failed Jobs` : 'Resolve Failed Job',
      'Resolve',
      'Cancel'
    )

    if (!confirmed) return

    // Get values from DOM as fallback
    const resolutionActionElement = document.getElementById('resolutionAction')
    const resolutionNoteElement = document.getElementById('resolutionNote')

    const resolutionAction = resolutionActionElement?.value || resolutionActionValue
    const resolutionNote = resolutionNoteElement?.value || resolutionNoteValue

    if (!resolutionNote.trim()) {
      await showError('Please provide resolution notes')
      return
    }

    try {
      await failedOccurrenceService.markAsResolved(jobIds, resolutionNote, resolutionAction)
      setSelectedJobs([])
      await loadJobs()
      await showSuccess(`${jobIds.length > 1 ? `${jobIds.length} failed jobs` : 'Failed job'} marked as resolved`)
    } catch (err) {
      await showError('Failed to resolve job(s). Please try again.')
      console.error(err)
    }
  }

  const getFailureTypeBadge = (failureType) => {
    const info = failedOccurrenceService.getFailureTypeInfo(failureType)
    return (
      <span className={`failure-type-badge ${info.className}`} style={{ borderColor: info.color }}>
        <Icon name={info.icon} size={16} />
        {info.label}
      </span>
    )
  }

  if (loading) return <SkeletonTable rows={pageSize} columns={6} />
  if (error) return <div className="error">{error}</div>

  return (
    <div className="page failed-job-list">
      <Modal {...deleteModalProps} />
      <Modal {...resolveModalProps} />

      {/* Page Header */}
      <div className="page-header">
        <div className="header-content">
          <h1>
            <Icon name="error" size={28} />
            <span>Failed Executions (DLQ)</span>
            <span>({totalCount})</span>
          </h1>
        </div>
      </div>

      <div className="mv-table-card">
        <TableToolbar>
          <TableSearch
            value={searchTerm}
            onChange={setSearchTerm}
            placeholder="Search by job name or resolution note…"
          />

          {/* Resolved vs unresolved is the whole point of a dead letter queue, so it gets
              the one-click control rather than a dropdown. */}
          <SegmentedFilter
            options={RESOLVED_FILTERS}
            value={filterResolved}
            onChange={setFilterResolved}
            label="Filter by resolution"
          />

          <select
            value={filterFailureType ?? ''}
            onChange={(e) => setFilterFailureType(e.target.value === '' ? null : parseInt(e.target.value))}
            aria-label="Filter by failure type"
          >
            <option value="">All failure types</option>
            <option value="1">Max Retries Exceeded</option>
            <option value="2">Timeout</option>
            <option value="3">Worker Crash</option>
            <option value="4">Invalid Job Data</option>
            <option value="5">External Dependency Failure</option>
            <option value="6">Unhandled Exception</option>
            <option value="7">Cancelled</option>
            <option value="8">Zombie Detection</option>
          </select>
        </TableToolbar>

        <SelectionBar count={selectedJobs.length} onClear={() => setSelectedJobs([])}>
          <button type="button" className="mv-link" onClick={() => handleResolve(null)}>
            Mark as resolved
          </button>
          <BulkDeleteButton onClick={() => handleDelete(null)} />
        </SelectionBar>

        <div className="mv-table-scroll">
          <table className="mv-table">
            <thead>
              <tr>
                <th className="mv-col-check">
                  <input
                    type="checkbox"
                    checked={selectedJobs.length > 0 && selectedJobs.length === jobs.length}
                    onChange={handleSelectAll}
                    aria-label="Select all"
                  />
                </th>
                <th>Job</th>
                <th className="mv-col-tight">Status</th>
                <th>Failure</th>
                <th className="mv-col-tight">Failed</th>
                <th className="mv-col-tight">Retries</th>
                <th className="mv-table-actions">Actions</th>
              </tr>
            </thead>
            <tbody>
              {jobs.length === 0 && (
                <TableEmpty
                  colSpan={7}
                  icon="check_circle"
                  message={
                    filterResolved !== null || filterFailureType !== null || debouncedSearchTerm
                      ? 'No failed executions match the current filters.'
                      : 'No failed executions. Everything is running.'
                  }
                />
              )}

              {jobs.map((job) => (
                <tr
                  key={job.id}
                  /* Unresolved failures keep a red edge; once resolved the row steps back,
                     so what is still outstanding is visible without reading a column. */
                  className={'mv-table-row is-clickable ' + (job.resolved ? 'tone-idle' : 'tone-danger')}
                  onClick={() => navigate(`/failed-executions/${job.id}`)}
                >
                  <td className="mv-col-check" onClick={(e) => e.stopPropagation()}>
                    <input
                      type="checkbox"
                      checked={selectedJobs.includes(job.id)}
                      onChange={() => handleSelectJob(job.id)}
                      aria-label="Select failed execution"
                    />
                  </td>

                  <td>
                    <span className="mv-table-primary">{job.jobDisplayName}</span>
                    <span className="mv-table-muted">
                      {job.jobNameInWorker}
                      {job.workerId ? ` · ${job.workerId}` : ''}
                    </span>
                  </td>

                  <td className="mv-col-tight">
                    <span className={'mv-status tone-' + (job.resolved ? 'success' : 'danger')}>
                      <Icon name={job.resolved ? 'check_circle' : 'pending'} size={14} />
                      {job.resolved ? 'Resolved' : 'Unresolved'}
                    </span>
                  </td>

                  <td>{getFailureTypeBadge(job.failureType)}</td>

                  <td className="mv-col-tight">
                    <TimeCell value={job.failedAt} />
                  </td>

                  <td className="mv-col-tight">
                    <span className="mv-table-dim">{job.retryCount ?? 0}</span>
                  </td>

                  <td className="mv-table-actions" onClick={(e) => e.stopPropagation()}>
                    <TableActions>
                      <ActionButton
                        intent="primary"
                        icon="visibility"
                        title="View details"
                        to={`/failed-executions/${job.id}`}
                      />
                      {!job.resolved && (
                        <ActionButton
                          intent="success"
                          icon="check"
                          title="Mark as resolved"
                          onClick={() => handleResolve(job)}
                        />
                      )}
                      <ActionButton
                        intent="danger"
                        icon="delete"
                        title="Delete"
                        onClick={() => handleDelete(job.id)}
                      />
                    </TableActions>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <TableFooter totalCount={totalCount} noun="failed executions">
          <Pagination
            page={currentPage}
            pageSize={pageSize}
            totalCount={totalCount}
            onPageChange={setCurrentPage}
            onPageSizeChange={(size) => { setPageSize(size); setCurrentPage(1) }}
          />
        </TableFooter>
      </div>

      {/* Auto-refresh indicator */}
      <AutoRefreshIndicator
        enabled={autoRefreshEnabled}
        onToggle={() => {
          const newValue = !autoRefreshEnabled
          setAutoRefreshEnabled(newValue)
          localStorage.setItem('failedOccurrences_autoRefresh', newValue.toString())
        }}
        lastRefreshTime={lastRefreshTime}
        intervalSeconds={30}
      />
    </div>
  )
}

export default FailedOccurrenceList


