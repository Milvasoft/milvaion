import { useState, useEffect, useCallback } from 'react'
import { Link } from 'react-router-dom'
import failedOccurrenceService from '../../services/failedOccurrenceService'
import { formatDateTime } from '../../utils/dateUtils'
import Modal from '../../components/Modal'
import Icon from '../../components/Icon'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { useModal } from '../../hooks/useModal'
import './FailedOccurrenceList.css'

function FailedOccurrenceList() {
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

  const [isInitialLoad, setIsInitialLoad] = useState(true)

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
      setError('Failed to load failed jobs')
      console.error(err)
    } finally {
      if (showLoading) {
        setLoading(false)
        setIsInitialLoad(false)
      }
    }
  }, [filterResolved, filterFailureType, currentPage, pageSize, debouncedSearchTerm])

  useEffect(() => {
    loadJobs(isInitialLoad)

    // Auto-refresh every 30 seconds
    const refreshInterval = setInterval(() => {
      if (autoRefreshEnabled) {
        loadJobs(false)
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
      await failedOccurrenceService.delete(jobIds)
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

  if (loading) return <div className="loading">Loading failed jobs...</div>
  if (error) return <div className="error">{error}</div>

  return (
    <div className="failed-job-list">
      <Modal {...deleteModalProps} />
      <Modal {...resolveModalProps} />

      {/* Page Header */}
      <div className="page-header">
        <div className="header-content">
          <h1>
            <Icon name="error" size={28} />
            <span style={{ margin: '0 0 0 1rem' }}>Failed Executions (DLQ)</span>
            <span>({totalCount})</span>
          </h1>
        </div>
        {selectedJobs.length > 0 && (
          <div className="bulk-actions">
            <button
              onClick={() => handleResolve(null)}
              className="bulk-resolve-btn"
            >
              <Icon name="check" size={20} />
              Mark as Resolved ({selectedJobs.length})
            </button>
            <button
              onClick={() => handleDelete(null)}
              className="bulk-delete-btn"
            >
              <Icon name="delete" size={20} />
              Delete Selected ({selectedJobs.length})
            </button>
          </div>
        )}
      </div>

      {/* Filters */}
      <div className="filters-section">
        <div className="search-box">
          <input
            type="text"
            placeholder="Search by job name or resolution note..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="search-input"
          />
          {searchTerm && (
            <button
              onClick={() => setSearchTerm('')}
              className="clear-search-btn"
              title="Clear search"
            >
              <Icon name="close" size={16} />
            </button>
          )}
        </div>

        <div className="filter-buttons">
          <button
            className={`filter-btn ${filterResolved === null ? 'active' : ''}`}
            onClick={() => setFilterResolved(null)}
          >
            All
          </button>
          <button
            className={`filter-btn ${filterResolved === false ? 'active' : ''}`}
            onClick={() => setFilterResolved(false)}
          >
            <Icon name="pending" size={16} />
            Unresolved
          </button>
          <button
            className={`filter-btn ${filterResolved === true ? 'active' : ''}`}
            onClick={() => setFilterResolved(true)}
          >
            <Icon name="check_circle" size={16} />
            Resolved
          </button>
        </div>

        <div className="filter-select">
          <label>Failure Type:</label>
          <select
            value={filterFailureType ?? ''}
            onChange={(e) => setFilterFailureType(e.target.value === '' ? null : parseInt(e.target.value))}
            className="failure-type-select"
          >
            <option value="">All Types</option>
            <option value="1">Max Retries Exceeded</option>
            <option value="2">Timeout</option>
            <option value="3">Worker Crash</option>
            <option value="4">Invalid Job Data</option>
            <option value="5">External Dependency Failure</option>
            <option value="6">Unhandled Exception</option>
            <option value="7">Cancelled</option>
            <option value="8">Zombie Detection</option>
          </select>
        </div>
      </div>

      {/* Failed Jobs Table */}
      {jobs.length === 0 ? (
        <div className="empty-state-card">
          <div className="empty-icon">
            <Icon name="check_circle" size={64} />
          </div>
          <h3>No Failed Jobs</h3>
          <p>
            {filterResolved !== null || filterFailureType !== null
              ? 'No jobs match the selected filters. Try adjusting your filters.'
              : 'All jobs are running successfully! 🎉'
            }
          </p>
        </div>
      ) : (
        <>
          <div className="failed-jobs-table-container">
            <table className="failed-jobs-table">
              <thead>
                <tr>
                  <th className="checkbox-column">
                    <input
                      type="checkbox"
                      checked={selectedJobs.length > 0 && selectedJobs.length === jobs.length}
                      onChange={handleSelectAll}
                    />
                  </th>
                  <th>Job Name</th>
                  <th>Failure Type</th>
                  <th>Failed At</th>
                  <th>Retry Count</th>
                  <th>Worker ID</th>
                  <th>Status</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {jobs.map((job) => (
                  <tr
                    key={job.id}
                    className={job.resolved ? 'resolved-row' : ''}
                    onClick={() => window.location.href = `/failed-executions/${job.id}`}
                    style={{ cursor: 'pointer' }}
                  >
                    <td className="checkbox-column" onClick={(e) => e.stopPropagation()}>
                      <input
                        type="checkbox"
                        checked={selectedJobs.includes(job.id)}
                        onChange={() => handleSelectJob(job.id)}
                      />
                    </td>
                    <td>
                        <strong>{job.jobDisplayName}  </strong>
                        <small>({job.jobNameInWorker})</small>
                    </td>
                    <td>{getFailureTypeBadge(job.failureType)}</td>
                    <td>
                      <span className="date-cell">{formatDateTime(job.failedAt)}</span>
                    </td>
                    <td>
                      <span className="retry-count">{job.retryCount ?? 0} attempts</span>
                    </td>
                    <td>
                      <code className="worker-id">{job.workerId || 'N/A'}</code>
                    </td>
                    <td>
                      {job.resolved ? (
                        <span className="status-badge resolved">
                          <Icon name="check_circle" size={16} />
                          Resolved
                        </span>
                      ) : (
                        <span className="status-badge unresolved">
                          <Icon name="pending" size={16} />
                          Unresolved
                        </span>
                      )}
                    </td>
                    <td onClick={(e) => e.stopPropagation()}>
                      <div className="action-buttons">
                        <Link
                          to={`/failed-executions/${job.id}`}
                          className="action-btn view"
                          title="View details"
                        >
                          <Icon name="visibility" size={18} />
                        </Link>
                        {!job.resolved && (
                          <button
                            onClick={() => handleResolve(job)}
                            className="action-btn resolve"
                            title="Mark as resolved"
                          >
                            <Icon name="check" size={18} />
                          </button>
                        )}
                        <button
                          onClick={() => handleDelete(job.id)}
                          className="action-btn delete"
                          title="Delete"
                        >
                          <Icon name="delete" size={18} />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Pagination */}
          <div className="pagination-container">
            <div className="pagination">
              {(() => {
                const totalPages = Math.ceil(totalCount / pageSize)
                if (totalPages <= 1) return null

                const maxVisiblePages = 5
                let startPage = Math.max(1, currentPage - Math.floor(maxVisiblePages / 2))
                let endPage = Math.min(totalPages, startPage + maxVisiblePages - 1)

                if (endPage - startPage + 1 < maxVisiblePages) {
                  startPage = Math.max(1, endPage - maxVisiblePages + 1)
                }

                return (
                  <>
                    <button
                      className="btn btn-sm"
                      onClick={() => setCurrentPage(1)}
                      disabled={currentPage === 1}
                    >
                      <Icon name="first_page" size={18} />
                    </button>
                    <button
                      className="btn btn-sm"
                      onClick={() => setCurrentPage(currentPage - 1)}
                      disabled={currentPage === 1}
                    >
                      <Icon name="chevron_left" size={18} />
                    </button>

                    {startPage > 1 && <span className="page-ellipsis">...</span>}

                    {Array.from({ length: endPage - startPage + 1 }, (_, i) => startPage + i).map(page => (
                      <button
                        key={page}
                        className={'btn btn-sm' + (page === currentPage ? ' btn-primary' : '')}
                        onClick={() => setCurrentPage(page)}
                      >
                        {page}
                      </button>
                    ))}

                    {endPage < totalPages && <span className="page-ellipsis">...</span>}

                    <button
                      className="btn btn-sm"
                      onClick={() => setCurrentPage(currentPage + 1)}
                      disabled={currentPage === totalPages}
                    >
                      <Icon name="chevron_right" size={18} />
                    </button>
                    <button
                      className="btn btn-sm"
                      onClick={() => setCurrentPage(totalPages)}
                      disabled={currentPage === totalPages}
                    >
                      <Icon name="last_page" size={18} />
                    </button>

                    <span className="page-info">
                      Page {currentPage} of {totalPages} ({totalCount} total)
                    </span>
                  </>
                )
              })()}
            </div>

            <div className="page-size-selector">
              <label htmlFor="pageSize">Rows per page:</label>
              <select
                id="pageSize"
                value={pageSize}
                onChange={(e) => {
                  setPageSize(parseInt(e.target.value))
                  setCurrentPage(1)
                }}
                className="page-size-select"
              >
                <option value={10}>10</option>
                <option value={20}>20</option>
                <option value={50}>50</option>
                <option value={100}>100</option>
              </select>
            </div>
          </div>
        </>
      )}

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


