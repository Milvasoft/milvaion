import { useState, useEffect, useCallback } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import jobService from '../../services/jobService'
import workerService from '../../services/workerService'
import useInfiniteScroll from '../../hooks/useInfiniteScroll'
import ViewToggle from '../../components/ViewToggle'
import CronDisplay from '../../components/CronDisplay'
import Modal from '../../components/Modal'
import Icon from '../../components/Icon'
import Pagination from '../../components/Pagination'
import { ActionButton } from '../../components/TableActions'
import { TableFooter } from '../../components/TableParts'
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

  /*
   * Column filters. null means "no opinion" rather than false, so an unset boolean filter
   * is distinguishable from one deliberately set to false - otherwise "Inactive only" and
   * "don't care" would send the same request.
   */
  const [filterIsActive, setFilterIsActive] = useState(null)
  const [filterIsExternal, setFilterIsExternal] = useState(null)
  const [filterJobType, setFilterJobType] = useState(null)
  const [filterWorkerId, setFilterWorkerId] = useState(null)
  const [filterIsRecurring, setFilterIsRecurring] = useState(null)

  /*
   * Options come from the worker registry rather than from the jobs on screen: the jobs on
   * screen are already filtered, so deriving the choices from them would make an option
   * disappear the moment you used it.
   */
  const [workerOptions, setWorkerOptions] = useState([])
  const [jobTypeOptions, setJobTypeOptions] = useState([])
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

    const criterias = []

    // Tags is a comma separated column, so a tag is matched as a substring rather than
    // by equality - filtering "billing" must also find a job tagged "billing,nightly".
    if (filterTag) criterias.push({ filterBy: 'Tags', value: filterTag, type: 1 })

    // filterBy names the entity property, not the DTO field: filtering runs against
    // ScheduledJob before the list projection. The type column is JobNameInWorker there,
    // even though the grid labels it Type.
    if (filterIsActive !== null) criterias.push({ filterBy: 'IsActive', value: filterIsActive, type: 5 })
    if (filterIsExternal !== null) criterias.push({ filterBy: 'IsExternal', value: filterIsExternal, type: 5 })
    if (filterJobType) criterias.push({ filterBy: 'JobNameInWorker', value: filterJobType, type: 5 })
    if (filterWorkerId) criterias.push({ filterBy: 'WorkerId', value: filterWorkerId, type: 5 })

    // A job is one-time exactly when it has no cron expression, so the schedule kind is a
    // null check on that column - no value to send, the operator is the whole condition.
    // The NullOrWhiteSpace pair rather than plain IsNull/IsNotNull, so a job saved with an
    // empty cron string is still counted as one-time instead of falling between the two.
    if (filterIsRecurring !== null) {
      criterias.push({ filterBy: 'CronExpression', type: filterIsRecurring ? 16 : 15 })
    }

    if (criterias.length > 0) requestBody.filtering = { criterias }

    return requestBody
  }, [debouncedSearchTerm, filterTag, filterIsActive, filterIsExternal, filterJobType, filterWorkerId, filterIsRecurring])

  const activeFilterCount = [filterTag, filterIsActive, filterIsExternal, filterJobType, filterWorkerId, filterIsRecurring]
    .filter(f => f !== null && f !== undefined).length

  const clearFilters = () => {
    setFilterTag(null)
    setFilterIsActive(null)
    setFilterIsExternal(null)
    setFilterJobType(null)
    setFilterWorkerId(null)
    setFilterIsRecurring(null)
    setCurrentPage(1)
  }

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

  // Filter choices, loaded once. Workers know both their own id and the job types they can
  // run, which is the full set a job could legitimately be filtered by.
  useEffect(() => {
    let cancelled = false

    const loadFilterOptions = async () => {
      try {
        const response = await workerService.getAll()
        const workers = response?.data?.data ?? response?.data ?? []

        if (cancelled) return

        setWorkerOptions([...new Set(workers.map(w => w.workerId).filter(Boolean))].sort())
        setJobTypeOptions([...new Set(workers.flatMap(w => w.jobNames ?? []).filter(Boolean))].sort())
      } catch (err) {
        // The page works without these - the selects just have nothing to offer. Not worth
        // failing the whole job list over.
        console.debug('Failed to load filter options:', err)
      }
    }

    loadFilterOptions()

    return () => { cancelled = true }
  }, [])

  // Any filter change invalidates the current page: page 4 of the old result set is
  // meaningless against the new one, and often empty.
  useEffect(() => {
    setCurrentPage(1)
  }, [filterTag, filterIsActive, filterIsExternal, filterJobType, filterWorkerId, filterIsRecurring])

  // Reset card state when search/filter changes
  useEffect(() => {
    setCardPage(1)
    setCardJobs([])
  }, [debouncedSearchTerm, filterTag, filterIsActive, filterIsExternal, filterJobType, filterWorkerId, filterIsRecurring])

  // Table view effect (pagination)
  useEffect(() => {
    if (viewMode !== 'table') return
    loadJobs(true)

    const refreshInterval = setInterval(() => {
      if (autoRefreshEnabled) loadJobs(false)
    }, 30000)

    return () => clearInterval(refreshInterval)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [viewMode, filterTag, filterIsActive, filterIsExternal, filterJobType, filterWorkerId, filterIsRecurring, currentPage, pageSize, debouncedSearchTerm, autoRefreshEnabled])

  // Card view effect (infinite scroll)
  useEffect(() => {
    if (viewMode !== 'list') return
    loadCardJobs(cardPage, cardPage > 1)

    const refreshInterval = setInterval(() => {
      if (autoRefreshEnabled) loadCardJobsRefresh(cardPage)
    }, 30000)

    return () => clearInterval(refreshInterval)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [viewMode, cardPage, debouncedSearchTerm, filterTag, filterIsActive, filterIsExternal, filterJobType, filterWorkerId, filterIsRecurring, autoRefreshEnabled])

  // Infinite scroll for the card view. The observer logic lives in the hook, shared with
  // the workflow list and the job picker.
  const sentinelRef = useInfiniteScroll({
    hasMore: cardHasMore,
    loading: loadingMore,
    onLoadMore: () => setCardPage(prev => prev + 1),
    enabled: viewMode === 'list',
  })

  /*
   * Which row is being deleted, not just "a delete is running".
   *
   * Removing a job takes down every occurrence and log line it ever produced, so on a job
   * with a short schedule the request runs for seconds. Holding the id means only the row
   * that was clicked shows the spinner - a page-wide flag would freeze every delete button
   * in the list and leave the user unable to tell which one they had actually hit.
   */
  const [deletingId, setDeletingId] = useState(null)

  const handleDelete = async (id) => {
    const confirmed = await showConfirm(
      'Are you sure you want to delete this job? This action cannot be undone.',
      'Delete Job',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    setDeletingId(id)

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
    } finally {
      setDeletingId(null)
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
    <div className="page job-list">
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
            <span>Scheduled Jobs</span>
            <span >({totalCount})</span>
          </h1>
        </div>
        <div className="header-actions">
          <ViewToggle
            value={viewMode}
            onChange={(mode) => { if (mode !== viewMode) toggleViewMode() }}
            options={[
              { value: 'list', icon: 'view_list', label: 'Cards' },
              { value: 'table', icon: 'table_rows', label: 'Table' },
            ]}
          />
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

      <div className="job-filters">
        <div className="job-filter">
          <label htmlFor="filterIsActive">Status</label>
          <select
            id="filterIsActive"
            value={filterIsActive === null ? '' : String(filterIsActive)}
            onChange={(e) => setFilterIsActive(e.target.value === '' ? null : e.target.value === 'true')}
          >
            <option value="">All</option>
            <option value="true">Active</option>
            <option value="false">Inactive</option>
          </select>
        </div>

        <div className="job-filter">
          <label htmlFor="filterJobType">Type</label>
          <select
            id="filterJobType"
            value={filterJobType ?? ''}
            onChange={(e) => setFilterJobType(e.target.value || null)}
          >
            <option value="">All types</option>
            {jobTypeOptions.map(type => (
              <option key={type} value={type}>{type}</option>
            ))}
          </select>
        </div>

        <div className="job-filter">
          <label htmlFor="filterIsRecurring">Schedule</label>
          <select
            id="filterIsRecurring"
            value={filterIsRecurring === null ? '' : String(filterIsRecurring)}
            onChange={(e) => setFilterIsRecurring(e.target.value === '' ? null : e.target.value === 'true')}
          >
            <option value="">All schedules</option>
            <option value="true">Recurring</option>
            <option value="false">One-time</option>
          </select>
        </div>

        <div className="job-filter">
          <label htmlFor="filterWorkerId">Worker</label>
          <select
            id="filterWorkerId"
            value={filterWorkerId ?? ''}
            onChange={(e) => setFilterWorkerId(e.target.value || null)}
          >
            <option value="">All workers</option>
            {workerOptions.map(worker => (
              <option key={worker} value={worker}>{worker}</option>
            ))}
          </select>
        </div>

        <div className="job-filter">
          <label htmlFor="filterIsExternal">Source</label>
          <select
            id="filterIsExternal"
            value={filterIsExternal === null ? '' : String(filterIsExternal)}
            onChange={(e) => setFilterIsExternal(e.target.value === '' ? null : e.target.value === 'true')}
          >
            <option value="">All sources</option>
            <option value="false">Milvaion</option>
            <option value="true">External</option>
          </select>
        </div>

        {filterTag && (
          <div className="job-filter-tag">
            <Icon name="filter_alt" size={16} />
            <span className="tag-chip" title={filterTag}>{filterTag}</span>
            <button onClick={() => setFilterTag(null)} className="clear-filter-btn" title="Clear tag filter">
              <Icon name="close" size={14} />
            </button>
          </div>
        )}

        {activeFilterCount > 0 && (
          <button className="job-filters-clear" onClick={clearFilters}>
            <Icon name="filter_alt_off" size={16} />
            Clear {activeFilterCount === 1 ? 'filter' : `${activeFilterCount} filters`}
          </button>
        )}
      </div>

      {displayJobs.length === 0 && !loading ? (
        <div className="empty-state-card">
          <div className="empty-icon">
            <Icon name="assignment" size={64} />
          </div>
          <h3>No Jobs Found</h3>
          <p>
            {/* Offering "create your first job" to someone who has simply filtered
                everything out is misleading - the jobs exist, the filters hid them. */}
            {activeFilterCount > 0 || debouncedSearchTerm
              ? 'No jobs match the current filters. Try widening or clearing them.'
              : 'Get started by creating your first scheduled job.'}
          </p>
          {activeFilterCount > 0 && (
            <button className="empty-action-btn" onClick={clearFilters}>
              <Icon name="filter_alt_off" size={20} />
              Clear filters
            </button>
          )}
          {activeFilterCount === 0 && !debouncedSearchTerm && (
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
                        {job.completedAt && <span className="completed-badge" title="One-time job that has already run. It will not be scheduled again.">Completed</span>}
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
                        disabled={job.isExternal || deletingId === job.id}
                      >
                        <Icon
                          name={deletingId === job.id ? 'progress_activity' : 'delete'}
                          size={18}
                          className={deletingId === job.id ? 'mv-spin' : ''}
                        />
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
            <div className="mv-table-card">
              <div className="mv-table-scroll">
              <table className="mv-table">
                <thead>
                  <tr>
                    <th className="mv-col-tight">Status</th>
                    <th>Job</th>
                    <th>Schedule</th>
                    <th>Description</th>
                    <th className="mv-col-tight">Concurrency</th>
                    <th className="mv-table-actions">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {jobs.map((job) => (
                    <tr
                      key={job.id}
                      /* A job that is switched off, or a one-time job that has already
                         run, is marked down the row edge - it is the thing someone is
                         usually looking for when a job "isn't running". */
                      className={'mv-table-row is-clickable' + (job.isActive && !job.completedAt ? '' : ' tone-idle')}
                      onClick={() => navigate(`/jobs/${job.id}`)}
                    >
                      <td className="mv-col-tight">
                        <span className={'mv-status tone-' + (job.isActive ? 'success' : 'idle')}>
                          <Icon name={job.isActive ? 'check_circle' : 'cancel'} size={14} />
                          {job.isActive ? 'Active' : 'Inactive'}
                        </span>
                      </td>
                      <td>
                        {/* Type sits under the name rather than in a column of its own:
                            it describes the job, it is not a fact you scan a list by. */}
                        <Link
                          to={`/jobs/${job.id}`}
                          className="mv-table-primary"
                          onClick={(e) => e.stopPropagation()}
                        >
                          {job.displayName || job.name}
                          {job.isExternal && <span className="external-badge" title="External job (Quartz/Hangfire)">External</span>}
                          {job.completedAt && <span className="completed-badge" title="One-time job that has already run. It will not be scheduled again.">Completed</span>}
                        </Link>
                        <span className="mv-table-muted">{job.jobType}</span>
                      </td>
                      <td>
                        <CronDisplay expression={job.cronExpression} showTooltip={false} />
                      </td>
                      <td>
                        <span className="job-description-cell" title={job.description || 'No description'}>
                          {job.description ? truncateText(job.description, 20) : <span className="mv-table-dim">—</span>}
                        </span>
                      </td>
                      <td className="mv-col-tight">
                        <span className="mv-table-dim">
                          {job.concurrentExecutionPolicy === 0 ? 'Skip' :
                            job.concurrentExecutionPolicy === 1 ? 'Queue' : 'Unknown'}
                        </span>
                      </td>
                      <td className="mv-table-actions" onClick={(e) => e.stopPropagation()}>
                        <div className="mv-table-actions-inner">
                          <ActionButton
                            intent="primary"
                            icon="play_arrow"
                            title={job.isExternal ? 'External jobs cannot be triggered from Milvaion' : 'Trigger now'}
                            onClick={(e) => handleTrigger(job, e)}
                            disabled={!job.isActive || triggering || job.isExternal}
                          />
                          <ActionButton
                            intent="edit"
                            icon="edit"
                            title="Edit"
                            to={`/jobs/${job.id}/edit`}
                          />
                          <ActionButton
                            intent="danger"
                            icon={deletingId === job.id ? 'progress_activity' : 'delete'}
                            spinning={deletingId === job.id}
                            title={deletingId === job.id
                              ? 'Deleting…'
                              : job.isExternal ? 'External jobs cannot be deleted from Milvaion' : 'Delete'}
                            onClick={() => handleDelete(job.id)}
                            disabled={job.isExternal || deletingId === job.id}
                          />
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
              </div>

              <TableFooter totalCount={totalCount} noun="jobs">
                <Pagination
                  page={currentPage}
                  pageSize={pageSize}
                  totalCount={totalCount}
                  onPageChange={setCurrentPage}
                  onPageSizeChange={(size) => { setPageSize(size); setCurrentPage(1) }}
                />
              </TableFooter>
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
