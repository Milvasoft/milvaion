import { useState, useEffect, useCallback, useRef, useMemo } from 'react'
import { useLocation } from 'react-router-dom'
import logService from '../../services/logService'
import Icon from '../../components/Icon'
import JobSelect from '../../components/JobSelect'
import { SkeletonTable } from '../../components/Skeleton'
import { TableToolbar, TableSearch, SegmentedFilter, Spacer } from '../../components/TableParts'
import { formatTime, formatDateTime, formatRelativeTime } from '../../utils/dateUtils'
import { getApiErrorMessage } from '../../utils/errorUtils'
import './LogList.css'

/*
 * A live tail of worker execution logs.
 *
 * There is no websocket for logs, so "realtime" here is HTTP polling: every few seconds the
 * newest page is fetched and merged into what is already on screen, deduped by id. Newest
 * lines sit at the top, so new activity appears without the reader having to scroll. Paging
 * older is a separate, explicit action - a tail that keeps growing on its own would fight
 * the reader trying to look at something further up.
 */

const POLL_MS = 3000
const PAGE_SIZE = 100
const HARD_CAP = 2000

const LEVEL_OPTIONS = [
  { value: '', label: 'All' },
  { value: 'Information', label: 'Info' },
  { value: 'Warning', label: 'Warning' },
  { value: 'Error', label: 'Error' },
]

/** Maps a worker-reported level onto one of the shared status tones. */
const levelTone = (level) => {
  const l = (level || '').toLowerCase()
  if (l.startsWith('err') || l.startsWith('crit') || l.startsWith('fatal') || l.startsWith('fail')) return 'danger'
  if (l.startsWith('warn')) return 'warning'
  if (l.startsWith('info')) return 'info'
  return 'idle'
}

const levelRank = (level) => {
  const t = levelTone(level)
  return t === 'danger' ? 3 : t === 'warning' ? 2 : t === 'info' ? 1 : 0
}

const shortId = (id) => (id ? String(id).slice(0, 8) : '')

const byTimestampDesc = (a, b) => {
  const d = new Date(b.timestamp) - new Date(a.timestamp)
  return d !== 0 ? d : (String(a.id) < String(b.id) ? 1 : -1)
}

/** Adds only unseen lines, keeps the list newest-first, and caps how much is held. */
const mergeDesc = (existing, incoming) => {
  const map = new Map(existing.map((l) => [l.id, l]))
  let added = 0
  for (const line of incoming) {
    if (!map.has(line.id)) { map.set(line.id, line); added += 1 }
  }
  if (!added) return existing
  return Array.from(map.values()).sort(byTimestampDesc).slice(0, HARD_CAP)
}

function LogList() {
  const location = useLocation()

  // Filters. A job id can arrive from another page (e.g. "view logs for this job").
  const [jobId, setJobId] = useState(location.state?.jobId || '')
  const [level, setLevel] = useState('')
  const [searchTerm, setSearchTerm] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  const [groupByOccurrence, setGroupByOccurrence] = useState(false)
  const [includeData, setIncludeData] = useState(false)
  const [live, setLive] = useState(true)

  // Data.
  const [logs, setLogs] = useState([])
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [loadingOlder, setLoadingOlder] = useState(false)
  const [hasMore, setHasMore] = useState(false)
  const [error, setError] = useState(null)
  const [lastRefresh, setLastRefresh] = useState(null)

  const [expanded, setExpanded] = useState(() => new Set())
  const [collapsedGroups, setCollapsedGroups] = useState(() => new Set())

  const olderPageRef = useRef(1)

  // Debounce the free-text box so a poll does not fire on every keystroke.
  useEffect(() => {
    const t = setTimeout(() => setDebouncedSearch(searchTerm.trim()), 400)
    return () => clearTimeout(t)
  }, [searchTerm])

  const baseParams = useCallback((extra = {}) => ({
    jobId: jobId || undefined,
    level: level || undefined,
    searchTerm: debouncedSearch || undefined,
    includeData: includeData || undefined,
    rowCount: PAGE_SIZE,
    ...extra,
  }), [jobId, level, debouncedSearch, includeData])

  // Full reload whenever the filter set changes.
  const reload = useCallback(async () => {
    setLoading(true)
    setError(null)
    olderPageRef.current = 1
    try {
      const res = await logService.search(baseParams({ pageNumber: 1 }))
      const dto = res?.data || {}
      setLogs([...(dto.logs || [])].sort(byTimestampDesc))
      setHasMore(!!dto.hasMore)
      setLastRefresh(new Date())
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load logs'))
      setLogs([])
      setHasMore(false)
    } finally {
      setLoading(false)
    }
  }, [baseParams])

  useEffect(() => { reload() }, [reload])

  // The poll body reads current filters through a ref so the interval never goes stale and
  // never needs tearing down on every filter change.
  const pollRef = useRef(null)
  pollRef.current = async () => {
    setRefreshing(true)
    try {
      const res = await logService.search(baseParams({ pageNumber: 1 }))
      const dto = res?.data || {}
      setLogs((prev) => mergeDesc(prev, dto.logs || []))
      setLastRefresh(new Date())
      setError(null)
    } catch (err) {
      // A dropped poll is not worth interrupting the view over; the next one recovers.
      console.debug('Log poll failed:', err)
    } finally {
      setRefreshing(false)
    }
  }

  useEffect(() => {
    if (!live || loading) return undefined
    const id = setInterval(() => { pollRef.current?.() }, POLL_MS)
    return () => clearInterval(id)
  }, [live, loading])

  const loadOlder = useCallback(async () => {
    if (loadingOlder || !hasMore) return
    setLoadingOlder(true)
    try {
      const next = olderPageRef.current + 1
      const res = await logService.search(baseParams({ pageNumber: next }))
      const dto = res?.data || {}
      olderPageRef.current = next
      setLogs((prev) => mergeDesc(prev, dto.logs || []))
      setHasMore(!!dto.hasMore)
    } catch (err) {
      console.error(err)
    } finally {
      setLoadingOlder(false)
    }
  }, [baseParams, hasMore, loadingOlder])

  const toggleExpand = (id) => setExpanded((prev) => {
    const next = new Set(prev)
    if (next.has(id)) next.delete(id); else next.add(id)
    return next
  })

  const toggleGroup = (id) => setCollapsedGroups((prev) => {
    const next = new Set(prev)
    if (next.has(id)) next.delete(id); else next.add(id)
    return next
  })

  // Group the loaded lines by occurrence, newest occurrence first. `logs` is already sorted
  // newest-first, so first-seen insertion order gives exactly that.
  const groups = useMemo(() => {
    if (!groupByOccurrence) return null
    const map = new Map()
    for (const line of logs) {
      let group = map.get(line.occurrenceId)
      if (!group) {
        group = { occurrenceId: line.occurrenceId, jobId: line.jobId, jobName: line.jobName, lines: [], latest: line.timestamp, worst: 0 }
        map.set(line.occurrenceId, group)
      }
      group.lines.push(line)
      if (new Date(line.timestamp) > new Date(group.latest)) group.latest = line.timestamp
      const rank = levelRank(line.level)
      if (rank > group.worst) group.worst = rank
    }
    return Array.from(map.values())
  }, [logs, groupByOccurrence])

  const rankTone = (rank) => (rank === 3 ? 'danger' : rank === 2 ? 'warning' : rank === 1 ? 'info' : 'idle')

  const hasActiveFilters = !!(jobId || level || debouncedSearch)

  const renderValue = (line, key) => {
    if (!line.data || !(key in line.data)) return includeData ? 'null' : '·'
    const value = line.data[key]
    return typeof value === 'object' ? JSON.stringify(value) : String(value)
  }

  const renderLine = (line) => {
    const tone = levelTone(line.level)
    const keys = line.dataKeys || []
    const isOpen = expanded.has(line.id)
    return (
      <div className={`log-row tone-${tone}`} key={line.id}>
        <button
          type="button"
          className="log-line"
          onClick={() => keys.length > 0 && toggleExpand(line.id)}
        >
          <span className="log-time" title={formatDateTime(line.timestamp)}>{formatTime(line.timestamp)}</span>
          <span className={`log-level tone-${tone}`}>
            <span className="log-dot" />
            {line.level || 'Log'}
          </span>
          {!groupByOccurrence && line.jobName && (
            <span className="log-job" title={line.jobName}>{line.jobName}</span>
          )}
          {line.category && <span className="log-cat">{line.category}</span>}
          <span className="log-msg">{line.message}</span>
          {line.exceptionType && <span className="log-exc" title={line.exceptionType}>{line.exceptionType}</span>}
          {keys.length > 0 && (
            <span className="log-fields">
              <Icon name={isOpen ? 'expand_less' : 'expand_more'} size={14} />
              {keys.length}
            </span>
          )}
        </button>
        {isOpen && keys.length > 0 && (
          <div className="log-data">
            {keys.map((key) => (
              <div className="log-data-row" key={key}>
                <span className="log-data-key">{key}</span>
                <span className="log-data-val">{renderValue(line, key)}</span>
              </div>
            ))}
            {!includeData && (
              <p className="log-data-hint">Turn on “Show data” to load field values.</p>
            )}
          </div>
        )}
      </div>
    )
  }

  return (
    <div className="page log-list">
      <div className="page-header log-header">
        <h1>
          <Icon name="terminal" size={28} />
          <span>Logs</span>
          {logs.length > 0 && <span className="log-count">({logs.length}{hasMore ? '+' : ''})</span>}
        </h1>
        <div className="log-header-actions">
          {lastRefresh && (
            <span className="log-updated" title={formatDateTime(lastRefresh)}>
              {refreshing ? 'Updating…' : `Updated ${formatRelativeTime(lastRefresh)}`}
            </span>
          )}
          <button
            type="button"
            className={`log-live ${live ? 'is-live' : ''}`}
            onClick={() => setLive((v) => !v)}
            title={live ? 'Pause live updates' : 'Resume live updates'}
          >
            <span className="log-live-dot" />
            {live ? 'Live' : 'Paused'}
          </button>
        </div>
      </div>

      <TableToolbar>
        <div className="log-jobfilter">
          <JobSelect value={jobId} onChange={(id) => setJobId(id)} placeholder="All jobs" />
        </div>
        <SegmentedFilter label="Level" options={LEVEL_OPTIONS} value={level} onChange={setLevel} />
        <TableSearch value={searchTerm} onChange={setSearchTerm} placeholder="Search message…" />
        <Spacer />
        <button
          type="button"
          className={`log-toggle ${groupByOccurrence ? 'is-active' : ''}`}
          onClick={() => setGroupByOccurrence((v) => !v)}
          title="Group lines by execution"
        >
          <Icon name="workspaces" size={16} />
          <span>Group by occurrence</span>
        </button>
        <button
          type="button"
          className={`log-toggle ${includeData ? 'is-active' : ''}`}
          onClick={() => setIncludeData((v) => !v)}
          title="Include the values of structured fields"
        >
          <Icon name="data_object" size={16} />
          <span>Show data</span>
        </button>
      </TableToolbar>

      {loading ? (
        <SkeletonTable rows={12} columns={4} />
      ) : error && logs.length === 0 ? (
        <div className="empty-state-card">
          <div className="empty-icon"><Icon name="error" size={64} /></div>
          <h3>Couldn’t load logs</h3>
          <p>{error}</p>
          <button type="button" className="empty-action-btn" onClick={reload}>Try again</button>
        </div>
      ) : logs.length === 0 ? (
        <div className="empty-state-card">
          <div className="empty-icon"><Icon name="terminal" size={64} /></div>
          <h3>No log lines</h3>
          <p>{hasActiveFilters ? 'No logs match the current filters.' : 'Nothing has been logged yet. New lines will appear here as jobs run.'}</p>
        </div>
      ) : (
        <div className="log-console">
          {groupByOccurrence && groups ? (
            groups.map((group) => {
              const tone = rankTone(group.worst)
              const collapsed = collapsedGroups.has(group.occurrenceId)
              return (
                <div className={`log-group tone-${tone}`} key={group.occurrenceId}>
                  <button type="button" className="log-group-head" onClick={() => toggleGroup(group.occurrenceId)}>
                    <Icon name={collapsed ? 'chevron_right' : 'expand_more'} size={18} />
                    <span className="log-group-job">{group.jobName || 'Unknown job'}</span>
                    <span className="log-group-occ" title={group.occurrenceId}>#{shortId(group.occurrenceId)}</span>
                    <span className={`log-level tone-${tone}`}>
                      <span className="log-dot" />
                      {group.lines.length} {group.lines.length === 1 ? 'line' : 'lines'}
                    </span>
                    <Spacer />
                    <span className="log-group-time" title={formatDateTime(group.latest)}>{formatRelativeTime(group.latest)}</span>
                  </button>
                  {!collapsed && <div className="log-group-body">{group.lines.map(renderLine)}</div>}
                </div>
              )
            })
          ) : (
            logs.map(renderLine)
          )}

          {hasMore ? (
            <div className="log-more">
              <button type="button" className="log-more-btn" onClick={loadOlder} disabled={loadingOlder}>
                {loadingOlder ? 'Loading…' : 'Load older logs'}
              </button>
            </div>
          ) : (
            <div className="log-end">— end of logs —</div>
          )}
        </div>
      )}
    </div>
  )
}

export default LogList
