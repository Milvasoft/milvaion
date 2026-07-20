import { useState, useEffect, useRef, useCallback } from 'react'
import Icon from './Icon'
import jobService from '../services/jobService'
import useInfiniteScroll from '../hooks/useInfiniteScroll'
import './JobSelect.css'

/* eslint-disable react/prop-types */

/** How many jobs one page of the dropdown holds. */
const _pageSize = 50

/**
 * Picks one job, searching on the server.
 *
 * The workflow screens used to render a plain `<select>` over every job in the system,
 * which meant downloading the whole catalogue to fill a dropdown. Here the list is a
 * bounded page and typing narrows it server side, so the cost no longer grows with the
 * number of jobs.
 *
 * The selected job is reported alongside its id. Callers keep their own `jobs` array for
 * node labels and schema lookups, and merging the picked job into it keeps those working
 * without every job being present.
 *
 * @param {string} value Selected job id, or empty.
 * @param {(jobId: string, job: object|null) => void} onChange
 * @param {object[]} knownJobs Jobs the caller already has, used to label the current value.
 * @param {boolean} disabled
 */
function JobSelect({ value, onChange, knownJobs = [], disabled = false, placeholder = 'Select a job...' }) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  const [results, setResults] = useState([])
  const [loading, setLoading] = useState(false)

  // Pages accumulate as the dropdown is scrolled. A workflow author looking for a job
  // among thousands should not have to guess the right search term to make it appear.
  const [page, setPage] = useState(1)
  const [hasMore, setHasMore] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)

  // Resolved separately from `results`: the selected job is often not on the current page
  // of search results, and the closed control still has to show its name.
  const [selectedJob, setSelectedJob] = useState(null)

  const containerRef = useRef(null)
  const resultsRef = useRef(null)

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search.trim()), 350)

    return () => clearTimeout(timer)
  }, [search])

  // A new query is a new list, so paging restarts.
  useEffect(() => {
    setPage(1)
  }, [debouncedSearch])

  // Fetch a page whenever the dropdown is open, the query settles, or the user scrolls
  // far enough to ask for more.
  useEffect(() => {
    if (!open) return

    let cancelled = false

    const load = async () => {
      try {
        if (page === 1) setLoading(true)
        else setLoadingMore(true)

        const response = await jobService.getAll({
          pageNumber: page,
          rowCount: _pageSize,
          ...(debouncedSearch ? { searchTerm: debouncedSearch } : {}),
        })

        if (cancelled) return

        const items = response?.data?.data || response?.data || []
        const total = response?.data?.totalDataCount ?? response?.totalDataCount ?? 0

        setResults(prev => page === 1 ? items : [...prev, ...items])
        setHasMore(page * _pageSize < total)
      } catch (err) {
        if (!cancelled && page === 1) setResults([])
        console.debug('Job search failed:', err)
      } finally {
        if (!cancelled) {
          setLoading(false)
          setLoadingMore(false)
        }
      }
    }

    load()

    return () => { cancelled = true }
  }, [open, debouncedSearch, page])

  // Scrolling happens inside the dropdown, not the page, so the observer needs that
  // element as its root.
  const sentinelRef = useInfiniteScroll({
    hasMore,
    loading: loading || loadingMore,
    onLoadMore: () => setPage(p => p + 1),
    enabled: open,
    getRoot: () => resultsRef.current,
    rootMargin: '80px',
  })

  // Label for the current value. Prefer what the caller already knows; only ask the server
  // when the job is genuinely unknown, which happens when editing a workflow whose steps
  // point at jobs outside the first page.
  useEffect(() => {
    if (!value) {
      setSelectedJob(null)
      return
    }

    const known = knownJobs.find(j => j.id === value) || results.find(j => j.id === value)

    if (known) {
      setSelectedJob(known)
      return
    }

    let cancelled = false

    jobService.getById(value)
      .then(r => {
        if (cancelled) return

        const job = r?.data?.data || r?.data

        if (job) setSelectedJob(job)
      })
      .catch(() => { /* falls back to showing the raw id */ })

    return () => { cancelled = true }
  }, [value, knownJobs, results])

  // Close when the click lands outside.
  useEffect(() => {
    if (!open) return

    const onDocClick = (e) => {
      if (containerRef.current && !containerRef.current.contains(e.target)) setOpen(false)
    }

    document.addEventListener('mousedown', onDocClick)

    return () => document.removeEventListener('mousedown', onDocClick)
  }, [open])

  const select = useCallback((job) => {
    onChange(job.id, job)
    setOpen(false)
    setSearch('')
  }, [onChange])

  const clear = useCallback((e) => {
    e.stopPropagation()
    onChange('', null)
    setSelectedJob(null)
  }, [onChange])

  const label = selectedJob
    ? (selectedJob.displayName || selectedJob.jobNameInWorker || selectedJob.jobType)
    : (value ? value : placeholder)

  return (
    <div className={'mv-jobselect' + (disabled ? ' is-disabled' : '')} ref={containerRef}>
      <button
        type="button"
        className={'mv-jobselect-control' + (value ? '' : ' is-empty')}
        onClick={() => !disabled && setOpen(o => !o)}
        disabled={disabled}
      >
        <span className="mv-jobselect-label">{label}</span>

        {value && !disabled && (
          <span className="mv-jobselect-clear" onClick={clear} title="Clear selection">
            <Icon name="close" size={16} />
          </span>
        )}

        <Icon name={open ? 'expand_less' : 'expand_more'} size={18} />
      </button>

      {open && (
        <div className="mv-jobselect-menu">
          <div className="mv-jobselect-search">
            <Icon name="search" size={16} />
            <input
              type="text"
              value={search}
              onChange={e => setSearch(e.target.value)}
              placeholder="Search jobs..."
              autoFocus
            />
          </div>

          <div className="mv-jobselect-results" ref={resultsRef}>
            {loading && <div className="mv-jobselect-hint">Searching…</div>}

            {!loading && results.length === 0 && (
              <div className="mv-jobselect-hint">
                {debouncedSearch ? 'No jobs match that search.' : 'No jobs found.'}
              </div>
            )}

            {!loading && results.map(job => (
              <button
                type="button"
                key={job.id}
                className={'mv-jobselect-option' + (job.id === value ? ' is-selected' : '')}
                onClick={() => select(job)}
              >
                <span className="mv-jobselect-option-name">
                  {job.displayName || job.jobNameInWorker}
                </span>
                <span className="mv-jobselect-option-meta">
                  {job.jobType || job.jobNameInWorker}
                  {job.workerId ? ` · ${job.workerId}` : ''}
                </span>
              </button>
            ))}

            {/* Sentinel plus status. Without the closing line someone scrolling a long
                list has no way to tell "that is all of them" from "still loading". */}
            {!loading && hasMore && <div ref={sentinelRef} className="mv-jobselect-sentinel" />}

            {loadingMore && <div className="mv-jobselect-hint">Loading more…</div>}

            {!loading && !hasMore && results.length > 0 && (
              <div className="mv-jobselect-hint">End of list</div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}

export default JobSelect
