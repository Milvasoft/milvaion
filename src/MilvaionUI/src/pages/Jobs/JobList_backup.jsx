import { useState, useEffect, useCallback } from 'react'
import { Link, useLocation } from 'react-router-dom'
import jobService from '../../services/jobService'
import { formatDateTime } from '../../utils/dateUtils'
import CronDisplay from '../../components/CronDisplay'
import Modal from '../../components/Modal'
import Icon from '../../components/Icon'
import JsonEditor from '../../components/JsonEditor'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { useModal } from '../../hooks/useModal'
import { useTriggerJob } from '../../hooks/useTriggerJob'
import './JobList.css'

function JobList() {
const location = useLocation()
const [jobs, setJobs] = useState([])
const [loading, setLoading] = useState(true)
const [error, setError] = useState(null)
const [filterTag, setFilterTag] = useState(location.state?.filterByTag || null)
const [searchTerm, setSearchTerm] = useState('')
const [debouncedSearchTerm, setDebouncedSearchTerm] = useState('')
const [currentPage, setCurrentPage] = useState(1)
const [pageSize, setPageSize] = useState(20)
const [totalCount, setTotalCount] = useState(0)
const [autoRefreshEnabled, setAutoRefreshEnabled] = useState(true)
const [lastRefreshTime, setLastRefreshTime] = useState(null)

// Trigger modal state
const [showTriggerModal, setShowTriggerModal] = useState(false)
const [triggerJobData, setTriggerJobData] = useState('')
const [useCustomData, setUseCustomData] = useState(false)
const [selectedJobForTrigger, setSelectedJobForTrigger] = useState(null)

const { modalProps: deleteModalProps, showConfirm, showSuccess, showError } = useModal()
const { triggerJob, triggering, modalProps: triggerModalProps } = useTriggerJob()

const [isInitialLoad, setIsInitialLoad] = useState(true)

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

      // Add search term if provided
      if (debouncedSearchTerm) {
        requestBody.searchTerm = debouncedSearchTerm
      }

      // Add tag filtering if filterTag is set
      if (filterTag) {
        requestBody.filtering = {
          criterias: [
            {
              filterBy: "Tags",
              value: filterTag,
              type: 1 // Contains
            }
          ]
        }
      }

      const response = await jobService.getAll(requestBody)

      // Handle paginated response
      const data = response?.data?.data || response?.data || []
      const total = response?.data?.totalDataCount || response?.totalDataCount || 0

      setJobs(data)
      setTotalCount(total)
      setLastRefreshTime(new Date())
    } catch (err) {
      setError('Failed to load jobs')
      console.error(err)
    } finally {
      if (showLoading) {
        setLoading(false)
        setIsInitialLoad(false)
      }
    }
  }, [filterTag, currentPage, pageSize, debouncedSearchTerm])

  useEffect(() => {
    loadJobs(isInitialLoad)

    // Auto-refresh every 30 seconds (seamless data refresh)
    const refreshInterval = setInterval(() => {
      if (autoRefreshEnabled) {
        loadJobs(false)
      }
    }, 30000) // 30 seconds

    return () => clearInterval(refreshInterval)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterTag, currentPage, pageSize, debouncedSearchTerm, autoRefreshEnabled])

  const handleDelete = async (id) => {
    const confirmed = await showConfirm(
      'Are you sure you want to delete this job? This action cannot be undone.',
      'Delete Job',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    try {
      await jobService.delete(id)
      await loadJobs()
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
      loadJobs()
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

  const getLatestStatusBadge = (status) => {
    if (status === null || status === undefined) return null

    const statusMap = {
      0: { icon: 'schedule', label: 'Pending', className: 'status-pending' },
      1: { icon: 'sync', label: 'Running', className: 'status-running' },
      2: { icon: 'check_circle', label: 'Success', className: 'status-success' },
      3: { icon: 'cancel', label: 'Failed', className: 'status-failed' },
      4: { icon: 'warning', label: 'Cancelled', className: 'status-cancelled' },
    }

    const statusInfo = statusMap[status] || { icon: 'help', label: `Status ${status}`, className: 'status-unknown' }
    return (      <div className={`latest-status ${statusInfo.className}`}>
        <Icon name={statusInfo.icon} size={16} />
      </div>
    )
  }

  if (loading) return <div className="loading">Loading jobs...</div>
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

      {/* Page Header */}
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
          <Link to="/jobs/new" className="create-job-btn">
            <Icon name="add" size={20} />
            <span>Create New Job</span>
          </Link>
        </div>
      </div>

      {/* Filter Tag Display - Separate from header */}
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

      {jobs.length === 0 ? (
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
          <div className="jobs-grid">
            {jobs.map((job) => (
              <div
                key={job.id}
                className={`job-card ${job.isActive ? 'active' : 'inactive'}`}
                onClick={() => window.location.href = `/jobs/${job.id}`}
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
                    </Link>
                  </div>

                  <div className="job-actions" onClick={(e) => e.stopPropagation()}>
                    <button
                      onClick={(e) => handleTrigger(job, e)}
                      className="action-btn trigger"
                      title="Trigger now"
                      disabled={!job.isActive || triggering}
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
                      title="Delete"
                    >
                      <Icon name="delete" size={18} />
                    </button>
                  </div>
                </div>

                <div className="job-card-body">
                  {job.latestRun ? (
                    <div className="job-info-row">
                      <div style={{ display: 'flex', alignItems: 'center', gap: '5px' }}>
                        {getLatestStatusBadge(job.latestStatus)}
                        <span className="info-label">Latest Run</span>
                      </div>

                      <div className="info-value latest-run">
                        <span className="run-date">{formatDateTime(job.latestRun)}</span>

                      </div>
                    </div>
                  ) : (
                    <div className="job-info-row">
                      <span className="info-label">Latest Run</span>
                      <span className="info-value never-run">Never run</span>
                    </div>
                  )}

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
                      {job.concurrentExecutionPolicy === 0 ? 'Skip' : job.concurrentExecutionPolicy === 1 ? 'Queue' : 'Unknown'}
                    </span>
                  </div>
                </div>
              </div>
            ))}
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
                <option value={500}>500</option>
                <option value={1000}>1000</option>
              </select>
            </div>
          </div>
        </>
      )}

      {/* Auto-refresh indicator */}
      <AutoRefreshIndicator
        enabled={autoRefreshEnabled}
        onToggle={() => setAutoRefreshEnabled(!autoRefreshEnabled)}
        lastRefreshTime={lastRefreshTime}
        intervalSeconds={30}
      />
    </div>
  )
}

export default JobList
