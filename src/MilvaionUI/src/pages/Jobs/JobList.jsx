import { useState, useEffect, useCallback, useRef } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import jobService from '../../services/jobService'
import CronDisplay from '../../components/CronDisplay'
import Modal from '../../components/Modal'
import Icon from '../../components/Icon'
import JsonEditor from '../../components/JsonEditor'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { SkeletonJobList } from '../../components/Skeleton'
import { useModal } from '../../hooks/useModal'
import { useTriggerJob } from '../../hooks/useTriggerJob'
import { getApiErrorMessage } from '../../utils/errorUtils'
import './JobList.css'

function JobList() {
  const location = useLocation()
  const navigate = useNavigate()
  const [jobs, setJobs] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [filterTag, setFilterTag] = useState(location.state?.filterByTag || null)
  const [searchTerm, setSearchTerm] = useState('')
  const [debouncedSearchTerm, setDebouncedSearchTerm] = useState('')
  const [currentPage, setCurrentPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [totalCount, setTotalCount] = useState(0)
  const [autoRefreshEnabled, setAutoRefreshEnabled] = useState(() => {
    const saved = localStorage.getItem('jobList_autoRefresh')
    return saved !== null ? saved === 'true' : true
  })
  const [lastRefreshTime, setLastRefreshTime] = useState(null)
  const [viewMode, setViewMode] = useState(() => {
    const savedViewMode = localStorage.getItem('jobListViewMode')
    return savedViewMode || 'list'
  })

  // Card view infinite scroll state
  const [cardJobs, setCardJobs] = useState([])
  const [cardPage, setCardPage] = useState(1)
  const [cardHasMore, setCardHasMore] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const sentinelRef = useRef(null)

  // Trigger modal state
  const [showTriggerModal, setShowTriggerModal] = useState(false)
  const [triggerJobData, setTriggerJobData] = useState('')
  const [useCustomData, setUseCustomData] = useState(false)
  const [selectedJobForTrigger, setSelectedJobForTrigger] = useState(null)

  const { modalProps: deleteModalProps, showConfirm, showSuccess, showError } = useModal()
  const { triggerJob, triggering, modalProps: triggerModalProps } = useTriggerJob()

  // Debounce search term
  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearchTerm(searchTerm)
      setCurrentPage(1)
    }, 500)

    return () => clearTimeout(timer)
  }, [searchTerm])

  const buildRequestBody = useCallback((pageNumber, rowCount) => {
    const requestBody = { pageNumber, rowCount }
    if (debouncedSearchTerm) requestBody.searchTerm = debouncedSearchTerm
    if (filterTag) {
      requestBody.filtering = {
        criterias: [{ filterBy: 'Tags', value: filterTag, type: 1 }]
      }
    }
    return requestBody
  }, [debouncedSearchTerm, filterTag])

  const loadJobs = useCallback(async (showLoading = false) => {
    try {
      if (showLoading) setLoading(true)
      setError(null)

      const response = await jobService.getAll(buildRequestBody(currentPage, pageSize))

      const data = response?.data?.data || response?.data || []
      const total = response?.data?.totalDataCount || response?.totalDataCount || 0

      setJobs(data)
      setTotalCount(total)
      setLastRefreshTime(new Date())
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load jobs'))
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [buildRequestBody, currentPage, pageSize])

  const loadCardJobs = useCallback(async (page, append = false) => {
    try {
      if (page === 1 && !append) setLoading(true)
      else setLoadingMore(true)
      setError(null)

      const response = await jobService.getAll(buildRequestBody(page, 20))

      const data = response?.data?.data || response?.data || []
      const total = response?.data?.totalDataCount || response?.totalDataCount || 0

      setCardJobs(prev => append ? [...prev, ...data] : data)
      setTotalCount(total)
      setCardHasMore(page * 20 < total)
      setLastRefreshTime(new Date())
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load jobs'))
      console.error(err)
    } finally {
      setLoading(false)
      setLoadingMore(false)
    }
  }, [buildRequestBody])

  // Refresh already-loaded card pages without collapsing back to page 1
  const loadCardJobsRefresh = useCallback(async (pagesLoaded) => {
    try {
      const rowCount = Math.max(pagesLoaded, 1) * 20
      const response = await jobService.getAll(buildRequestBody(1, rowCount))

      const data = response?.data?.data || response?.data || []
      const total = response?.data?.totalDataCount || response?.totalDataCount || 0

      setCardJobs(data)
      setTotalCount(total)
      setCardHasMore(rowCount < total)
      setLastRefreshTime(new Date())
    } catch (err) {
      console.error(err)
    }
  }, [buildRequestBody])

  // Reset card state when search/filter changes
  useEffect(() => {
    setCardPage(1)
    setCardJobs([])
  }, [debouncedSearchTerm, filterTag])

  // Table view effect (pagination)
  useEffect(() => {
    if (viewMode !== 'table') return
    loadJobs(true)

    const refreshInterval = setInterval(() => {
      if (autoRefreshEnabled) loadJobs(false)
    }, 30000)

    return () => clearInterval(refreshInterval)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [viewMode, filterTag, currentPage, pageSize, debouncedSearchTerm, autoRefreshEnabled])

  // Card view effect (infinite scroll)
  useEffect(() => {
    if (viewMode !== 'list') return
    loadCardJobs(cardPage, cardPage > 1)

    const refreshInterval = setInterval(() => {
      if (autoRefreshEnabled) loadCardJobsRefresh(cardPage)
    }, 30000)

    return () => clearInterval(refreshInterval)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [viewMode, cardPage, debouncedSearchTerm, filterTag, autoRefreshEnabled])

  // Infinite scroll sentinel observer
  useEffect(() => {
    if (viewMode !== 'list') return
    const sentinel = sentinelRef.current
    if (!sentinel) return

    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0].isIntersecting && cardHasMore && !loadingMore) {
          setCardPage(prev => prev + 1)
        }
      },
      { root: null, rootMargin: '200px', threshold: 0 }
    )

    observer.observe(sentinel)
    return () => observer.disconnect()
  }, [viewMode, cardHasMore, loadingMore])

  const handleDelete = async (id) => {
    const confirmed = await showConfirm(
      'Are you sure you want to delete this job? This action cannot be undone.',
      'Delete Job',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    try {
      const response = await jobService.delete(id)

      if (response?.isSuccess === false) {
        const message = response.messages?.[0]?.message || 'Failed to delete job.'
        await showError(message)
        return
      }

      if (viewMode === 'list') {
        setCardPage(1)
        setCardJobs([])
        await loadCardJobs(1, false)
      } else {
        await loadJobs()
      }
      await showSuccess('Job deleted successfully')
    } catch (err) {
      await showError('Failed to delete job. Please try again.')
      console.error(err)
    }
  }

  const handleTrigger = (job, e) => {
    e.preventDefault()
    e.stopPropagation()

    // Open trigger modal
    setSelectedJobForTrigger(job)
    setTriggerJobData(job?.jobData || '')
    setUseCustomData(false)
    setShowTriggerModal(true)
  }

  const handleTriggerConfirm = async () => {
    if (!selectedJobForTrigger) return

    setShowTriggerModal(false)
    const customData = useCustomData ? triggerJobData : null

    await triggerJob(selectedJobForTrigger.id, 'Manual trigger from job list', false, customData, () => {
      // onSuccess: reload jobs to update latest run info
      if (viewMode === 'list') {
        setCardPage(1)
        setCardJobs([])
        loadCardJobs(1, false)
      } else {
        loadJobs()
      }
    })

    // Reset state
    setSelectedJobForTrigger(null)
    setTriggerJobData('')
    setUseCustomData(false)
  }

  const handleTriggerCancel = () => {
    setShowTriggerModal(false)
    setSelectedJobForTrigger(null)
    setTriggerJobData('')
    setUseCustomData(false)
  }

  const toggleViewMode = () => {
    const newMode = viewMode === 'list' ? 'table' : 'list'
    setViewMode(newMode)
    localStorage.setItem('jobListViewMode', newMode)
    // Reset card state when switching to card view
    if (newMode === 'list') {
      setCardPage(1)
      setCardJobs([])
    }
  }

  const truncateText = (text, maxLength = 50) => {
    if (!text || text.length <= maxLength) return text
    return text.substring(0, maxLength) + '...'
  }

  const displayJobs = viewMode === 'list' ? cardJobs : jobs

  if (loading && displayJobs.length === 0) return <SkeletonJobList rows={pageSize} />
  if (error) return <div className="error">{error}</div>

  return (
    <div className="job-list">
      <Modal {...deleteModalProps} />
      <Modal {...triggerModalProps} />

      {/* Trigger Job Modal */}
      {showTriggerModal && selectedJobForTrigger && (
        <Modal
          isOpen={true}
          onClose={handleTriggerCancel}
          title="🚀 Trigger Job"
          message={
            <div className="trigger-modal-content">
              <p className="trigger-modal-description">
                You are about to manually trigger <strong>{selectedJobForTrigger.displayName || selectedJobForTrigger.name}</strong>.
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

              {!useCustomData && selectedJobForTrigger.jobData && (
                <div className="trigger-current-data">
                  <label>Current Job Data:</label>
                  <pre>{JSON.stringify(JSON.parse(selectedJobForTrigger.jobData || '{}'), null, 2)}</pre>
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

      <div className="page-header">
        <div className="header-content">
          <h1>
            <Icon name="work" size={28}  />
            <span style={{ margin: '0 0 0 1rem' }}>Scheduled Jobs</span>
            <span >({totalCount})</span>
          </h1>
        </div>
        <div className="header-actions">
          <div className="search-box">
            <input
              type="text"
              placeholder="Search jobs by name or tag..."
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
          <div className="view-mode-selector">
            <button
              className={`view-mode-btn ${viewMode === 'list' ? 'active' : ''}`}
              onClick={() => {
                if (viewMode !== 'list') toggleViewMode()
              }}
              title="Card View"
            >
              <Icon name="view_list" size={20} />
              <span>Cards</span>
            </button>
            <button
              className={`view-mode-btn ${viewMode === 'table' ? 'active' : ''}`}
              onClick={() => {
                if (viewMode !== 'table') toggleViewMode()
              }}
              title="Table View"
            >
              <Icon name="table_rows" size={20} />
              <span>Table</span>
            </button>
          </div>
          <Link to="/jobs/new" className="create-job-btn">
            <Icon name="add" size={20} />
            <span>Create New Job</span>
          </Link>
        </div>
      </div>

      {filterTag && (
        <div className="filter-tag-display">
          <Icon name="filter_alt" size={18} />
          <span className="filter-label">Filtering by tag:</span>
          <span className="tag-chip" title={filterTag}>{filterTag}</span>
          <button onClick={() => setFilterTag(null)} className="clear-filter-btn" title="Clear filter">
            <Icon name="close" size={16} />
          </button>
        </div>
      )}

      {displayJobs.length === 0 && !loading ? (
        <div className="empty-state-card">
          <div className="empty-icon">
            <Icon name="assignment" size={64} />
          </div>
          <h3>No Jobs Found</h3>
          <p>
            {filterTag
              ? `No jobs found with tag "${filterTag}". Try clearing the filter.`
              : 'Get started by creating your first scheduled job.'}
          </p>
          {!filterTag && (
            <Link to="/jobs/new" className="empty-action-btn">
              Create Your First Job
            </Link>
          )}
        </div>
      ) : (
        <>
          {viewMode === 'list' ? (
            <div className="jobs-grid">
              {displayJobs.map((job) => (
                <div
                  key={job.id}
                  className={`job-card ${job.isActive ? 'active' : 'inactive'}`}
                  onClick={() => navigate(`/jobs/${job.id}`)}
                  style={{ cursor: 'pointer' }}
                >
                  <div className="job-card-header">
                    <div className="job-title-section">
                      <Link
                        to={`/jobs/${job.id}`}
                        className="job-name"
                        style={{ textDecoration: 'none', color: 'inherit' }}
                      >
                        {job.displayName || job.name}
                        {job.isExternal && <span className="external-badge" title="External job (Quartz/Hangfire)">External</span>}
                      </Link>
                    </div>

                    <div className="job-actions" onClick={(e) => e.stopPropagation()}>
                      <button
                        onClick={(e) => handleTrigger(job, e)}
                        className="action-btn trigger"
                        title={job.isExternal ? "External jobs cannot be triggered from Milvaion" : "Trigger now"}
                        disabled={!job.isActive || triggering || job.isExternal}
                      >
                        <Icon name="play_arrow" size={18} />
                      </button>
                      <Link
                        to={`/jobs/${job.id}/edit`}
                        className="action-btn edit"
                        title="Edit"
                        onClick={(e) => e.stopPropagation()}
                      >
                        <Icon name="edit" size={18} />
                      </Link>
                      <button
                        onClick={(e) => {
                          e.preventDefault()
                          e.stopPropagation()
                          handleDelete(job.id)
                        }}
                        className="action-btn delete"
                        title={job.isExternal ? "External jobs cannot be deleted from Milvaion" : "Delete"}
                        disabled={job.isExternal}
                      >
                        <Icon name="delete" size={18} />
                      </button>
                    </div>
                  </div>

                  <div className="job-card-body">
                    <div className="job-info-row">
                      <span className="info-label">Description</span>
                      <span className="info-value job-description">
                        {job.description ? truncateText(job.description, 20) : <span className="no-description">No description</span>}
                      </span>
                    </div>

                    <div className="job-info-row">
                      <span className="info-label">Type</span>
                      <span className="info-value job-type">{job.jobType}</span>
                    </div>

                    <div className="job-info-row">
                      <span className="info-label">Schedule</span>
                      <div className="info-value">
                        <CronDisplay expression={job.cronExpression} showTooltip={false} />
                      </div>
                    </div>

                    <div className="job-info-row">
                      <span className="info-label">Concurrent Policy</span>
                      <span className="info-value concurrent-policy">
                        {job.concurrentExecutionPolicy === 0 ? 'Skip' :
                          job.concurrentExecutionPolicy === 1 ? 'Queue' : 'Unknown'}
                      </span>
                    </div>
                  </div>
                </div>
              ))}
              {/* Infinite scroll sentinel */}
              <div ref={sentinelRef} className="infinite-scroll-sentinel" />
              {loadingMore && (
                <div className="infinite-scroll-loading">
                  <span className="loading-spinner" />
                  <span>Loading more jobs...</span>
                </div>
              )}
              {!cardHasMore && cardJobs.length > 0 && (
                <div className="infinite-scroll-end">
                  Showing all {cardJobs.length} jobs
                </div>
              )}
            </div>
          ) : (
            <div className="jobs-table-container">
              <table className="jobs-table">
                <thead>
                  <tr>
                    <th>Status</th>
                    <th>Name</th>
                    <th>Type</th>
                    <th>Schedule</th>
                    <th>Description</th>
                    <th>Concurrent Policy</th>
                    <th>Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {jobs.map((job) => (
                    <tr
                      key={job.id}
                      className={`job-table-row ${job.isActive ? 'active' : 'inactive'}`}
                      onClick={() => navigate(`/jobs/${job.id}`)}
                      style={{ cursor: 'pointer' }}
                    >
                      <td>
                        <div className={`job-status-indicator`}>
                          <Icon name={job.isActive ? 'check_circle' : 'cancel'} size={20} />
                        </div>
                      </td>
                      <td>
                        <Link
                          to={`/jobs/${job.id}`}
                          className="job-name-link"
                          onClick={(e) => e.stopPropagation()}
                        >
                          {job.displayName || job.name}
                          {job.isExternal && <span className="external-badge" title="External job (Quartz/Hangfire)">External</span>}
                        </Link>
                      </td>
                      <td>
                        <span className="job-type-badge">{job.jobType}</span>
                      </td>
                      <td>
                        <CronDisplay expression={job.cronExpression} showTooltip={false} />
                      </td>
                      <td>
                        <span className="job-description-cell" title={job.description || 'No description'}>
                          {job.description ? truncateText(job.description, 20) : <span className="no-description">-</span>}
                        </span>
                      </td>
                      <td>
                        <span className="concurrent-policy-text">
                          {job.concurrentExecutionPolicy === 0 ? 'Skip' :
                            job.concurrentExecutionPolicy === 1 ? 'Queue' : 'Unknown'}
                        </span>
                      </td>
                      <td onClick={(e) => e.stopPropagation()}>
                        <div className="table-actions">
                          <button
                            onClick={(e) => handleTrigger(job, e)}
                            className="action-btn trigger"
                            title={job.isExternal ? "External jobs cannot be triggered from Milvaion" : "Trigger now"}
                            disabled={!job.isActive || triggering || job.isExternal}
                          >
                            <Icon name="play_arrow" size={18} />
                          </button>
                          <Link
                            to={`/jobs/${job.id}/edit`}
                            className="action-btn edit"
                            title="Edit"
                            onClick={(e) => e.stopPropagation()}
                          >
                            <Icon name="edit" size={18} />
                          </Link>
                          <button
                            onClick={(e) => {
                              e.preventDefault()
                              e.stopPropagation()
                              handleDelete(job.id)
                            }}
                            className="action-btn delete"
                            title={job.isExternal ? "External jobs cannot be deleted from Milvaion" : "Delete"}
                            disabled={job.isExternal}
                          >
                            <Icon name="delete" size={18} />
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>

              {/* Pagination - inside table container */}
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
                    <option value={500}>500</option>
                    <option value={1000}>1000</option>
                  </select>
                </div>
              </div>
            </div>
          )}
        </>
      )}

      {/* Auto-refresh indicator */}
      <AutoRefreshIndicator
        enabled={autoRefreshEnabled}
        onToggle={() => {
          const newValue = !autoRefreshEnabled
          setAutoRefreshEnabled(newValue)
          localStorage.setItem('jobList_autoRefresh', newValue.toString())
        }}
        lastRefreshTime={lastRefreshTime}
        intervalSeconds={30}
      />
    </div>
  )
}

export default JobList
