import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import Icon from '../../components/Icon'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { SkeletonTable } from '../../components/Skeleton'
import { TableToolbar, TableSearch, TableEmpty } from '../../components/TableParts'
import jobService from '../../services/jobService'
import { formatDateTime } from '../../utils/dateUtils'
import { getApiErrorMessage } from '../../utils/errorUtils'
import './UpcomingExecutions.css'

/**
 * Enums arrive as either a name or an ordinal depending on serializer settings,
 * so both are accepted rather than assuming one.
 */
const HEALTH = {
  0: 'Scheduled',
  1: 'Projected',
  2: 'NotScheduled',
  3: 'InvalidSchedule',
}

const KIND = {
  0: 'Job',
  1: 'Workflow',
}

const normalize = (map, value) => (typeof value === 'number' ? map[value] : value)

/* `tone` is the shared vocabulary from `table.css` - it colours the status label and the
   row's left edge together, so a problem is visible before the column is read. */
const HEALTH_META = {
  Scheduled: {
    tone: 'success',
    icon: 'check_circle',
    label: 'Scheduled',
    title: 'The dispatcher is holding this time. It will fire unless something changes.',
  },
  Projected: {
    tone: 'info',
    icon: 'calculate',
    label: 'Projected',
    title: 'Derived from the cron expression. The workflow engine works the time out on each poll rather than storing it.',
  },
  NotScheduled: {
    tone: 'danger',
    icon: 'error',
    label: 'Not scheduled',
    title: 'Active and recurring, but the dispatcher holds no run time for it, so it will not run.',
  },
  InvalidSchedule: {
    tone: 'danger',
    icon: 'report',
    label: 'Invalid cron',
    title: 'The cron expression could not be parsed, so no next run can be worked out.',
  },
}

const WINDOWS = [
  { hours: 1, label: '1 hour' },
  { hours: 6, label: '6 hours' },
  { hours: 24, label: '24 hours' },
  { hours: 168, label: '7 days' },
]

function UpcomingExecutions() {
  const navigate = useNavigate()

  const [data, setData] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)

  const [withinHours, setWithinHours] = useState(24)
  const [kind, setKind] = useState('')
  const [searchTerm, setSearchTerm] = useState('')
  const [onlyProblems, setOnlyProblems] = useState(false)

  const [autoRefreshEnabled, setAutoRefreshEnabled] = useState(() => {
    const saved = localStorage.getItem('upcoming_autoRefresh')
    return saved !== null ? saved === 'true' : true
  })
  const [lastRefreshTime, setLastRefreshTime] = useState(null)

  // Ticks once a second so the countdown column stays live between refreshes.
  const [, forceTick] = useState(0)

  // Typing should not fire a request per keystroke - the search path costs a database
  // query plus a pipelined Redis lookup.
  const [debouncedSearch, setDebouncedSearch] = useState('')

  useEffect(() => {
    const timeout = setTimeout(() => setDebouncedSearch(searchTerm.trim()), 400)

    return () => clearTimeout(timeout)
  }, [searchTerm])

  const load = useCallback(async () => {
    try {
      setError(null)

      const response = await jobService.getUpcoming({
        withinHours,
        limit: 200,
        searchTerm: debouncedSearch || undefined,
        kind: kind === '' ? undefined : Number(kind),
        onlyProblems,
      })

      setData(response?.data?.data ?? response?.data ?? null)
      setLastRefreshTime(new Date())
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load upcoming executions'))
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [withinHours, kind, debouncedSearch, onlyProblems])

  useEffect(() => {
    load()
  }, [load])

  useEffect(() => {
    if (!autoRefreshEnabled) return

    const interval = setInterval(load, 30000)

    return () => clearInterval(interval)
  }, [load, autoRefreshEnabled])

  useEffect(() => {
    const interval = setInterval(() => forceTick(t => t + 1), 1000)

    return () => clearInterval(interval)
  }, [])

  // AutoRefreshIndicator wires onToggle straight to onClick, so this receives the click
  // event rather than the new value - the flip has to happen here.
  const handleAutoRefreshToggle = () => {
    setAutoRefreshEnabled(current => {
      const next = !current

      localStorage.setItem('upcoming_autoRefresh', String(next))

      return next
    })
  }

  /*
   * Countdowns are measured against the server clock, not the browser's. A workstation
   * a couple of minutes fast would otherwise show runs as overdue while the dispatcher
   * still considers them early - and this page exists to be believed on that point.
   */
  const clockOffsetMs = useMemo(() => {
    if (!data?.asOfUtc) return 0

    const asOf = new Date(data.asOfUtc).getTime()

    return Number.isNaN(asOf) ? 0 : Date.now() - asOf
  }, [data?.asOfUtc])

  const serverNow = Date.now() - clockOffsetMs

  const formatCountdown = (scheduledAt) => {
    if (!scheduledAt) return '—'

    const target = new Date(scheduledAt).getTime()

    if (Number.isNaN(target)) return '—'

    const diffSeconds = Math.round((target - serverNow) / 1000)

    if (diffSeconds <= 0) return 'due now'

    if (diffSeconds < 60) return `in ${diffSeconds}s`

    const minutes = Math.floor(diffSeconds / 60)

    if (minutes < 60) return `in ${minutes}m`

    const hours = Math.floor(minutes / 60)

    if (hours < 24) return `in ${hours}h ${minutes % 60}m`

    return `in ${Math.floor(hours / 24)}d ${hours % 24}h`
  }

  const isOverdue = (item) => {
    if (!item.scheduledAt) return false

    return new Date(item.scheduledAt).getTime() <= serverNow
  }

  const openItem = (item) => {
    const itemKind = normalize(KIND, item.kind)

    navigate(itemKind === 'Workflow' ? `/workflows/${item.id}` : `/jobs/${item.id}`)
  }

  const items = data?.items ?? []

  const header = (
    <div className="page-header">
      <h1>
        <Icon name="schedule_send" size={28} />
        Upcoming Executions
      </h1>
      <p className="page-subtitle">Next run for each scheduled job and workflow</p>
      <div className="page-header-actions">
        <AutoRefreshIndicator
          enabled={autoRefreshEnabled}
          onToggle={handleAutoRefreshToggle}
          lastRefreshTime={lastRefreshTime}
          intervalSeconds={30}
        />
      </div>
    </div>
  )

  if (loading) {
    return (
      <div className="page upcoming-page">
        {header}
        <SkeletonTable rows={8} columns={6} />
      </div>
    )
  }

  if (error) return <div className="error">{error}</div>

  return (
    <div className="page upcoming-page">
      {header}

      {data && !data.schedulerReachable && (
        <div className="upcoming-banner critical">
          <Icon name="cloud_off" size={20} />
          <div>
            <strong>The scheduler cannot be reached.</strong>
            <p>
              The Redis circuit breaker is open, so job run times could not be read. This
              timeline is incomplete - it is not saying that nothing is scheduled.
            </p>
          </div>
        </div>
      )}

      {data?.notScheduledCount > 0 && !onlyProblems && (
        <div className="upcoming-banner warning">
          <Icon name="warning" size={20} />
          <div>
            <strong>
              {data.notScheduledCount} recurring {data.notScheduledCount === 1 ? 'job has' : 'jobs have'} no run time.
            </strong>
            <p>
              They are active and have a cron expression, but the dispatcher holds nothing for
              them, so they will not run. They are listed at the bottom.
            </p>
          </div>
          <button className="btn-secondary" onClick={() => setOnlyProblems(true)}>
            Show only these
          </button>
        </div>
      )}

      {data?.healthScanTruncated && (
        <div className="upcoming-banner info">
          <Icon name="info" size={20} />
          <div>
            <p>
              There are more recurring jobs than the health check inspects, so the count above
              is a floor rather than a total.
            </p>
          </div>
        </div>
      )}

      <div className="mv-table-card">
        <TableToolbar>
          <TableSearch value={searchTerm} onChange={setSearchTerm} placeholder="Search by name…" />

          <select value={withinHours} onChange={(e) => setWithinHours(Number(e.target.value))} aria-label="Time window">
            {WINDOWS.map(w => (
              <option key={w.hours} value={w.hours}>Next {w.label}</option>
            ))}
          </select>

          <select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Filter by type">
            <option value="">Jobs and workflows</option>
            <option value="0">Jobs only</option>
            <option value="1">Workflows only</option>
          </select>

          <label className="upcoming-toggle">
            <input
              type="checkbox"
              checked={onlyProblems}
              onChange={(e) => setOnlyProblems(e.target.checked)}
            />
            <span>Only problems</span>
          </label>
        </TableToolbar>

        <div className="mv-table-scroll">
          <table className="mv-table">
            <thead>
              <tr>
                <th className="mv-col-tight">When</th>
                <th>Name</th>
                <th className="mv-col-tight">Type</th>
                <th>Schedule</th>
                <th>Target</th>
                <th className="mv-col-tight">Status</th>
              </tr>
            </thead>
            <tbody>
              {items.length === 0 && (
                <TableEmpty
                  colSpan={6}
                  icon="event_available"
                  message={onlyProblems
                    ? 'Every recurring job has a run time.'
                    : 'Nothing scheduled in this window. Try widening it, or check that the jobs you expect are active.'}
                />
              )}

              {items.map((item, index) => {
                const health = normalize(HEALTH, item.health)
                const meta = HEALTH_META[health] ?? HEALTH_META.Scheduled
                const itemKind = normalize(KIND, item.kind)
                const overdue = isOverdue(item)

                return (
                  <tr
                    key={`${item.id}-${index}`}
                    /* A run the dispatcher is not holding is the reason to read this page,
                       so it is marked down the row edge rather than only in the last
                       column. */
                    className={`mv-table-row is-clickable tone-${meta.tone}`}
                    onClick={() => openItem(item)}
                  >
                    <td className="mv-col-tight">
                      {item.scheduledAt ? (
                        <>
                          <span
                            className={'mv-table-primary' + (overdue ? ' upcoming-overdue' : '')}
                            title={formatDateTime(item.scheduledAt)}
                          >
                            {formatCountdown(item.scheduledAt)}
                          </span>
                          <span className="mv-table-muted">{formatDateTime(item.scheduledAt)}</span>
                        </>
                      ) : (
                        <span className="mv-table-dim">never</span>
                      )}
                    </td>

                    <td>
                      <span className="mv-table-primary">{item.displayName}</span>
                      {item.tags && <span className="mv-table-muted">{item.tags}</span>}
                    </td>

                    <td className="mv-col-tight">
                      <span className="mv-status tone-idle">
                        <Icon name={itemKind === 'Workflow' ? 'account_tree' : 'work'} size={14} />
                        {itemKind}
                      </span>
                    </td>

                    <td>
                      {item.cronExpression
                        ? <code className="mv-table-code">{item.cronExpression}</code>
                        : <span className="mv-table-dim">one-time</span>}
                    </td>

                    <td>
                      {itemKind === 'Workflow'
                        ? <span className="mv-table-dim">—</span>
                        : (
                          <>
                            <span className="mv-table-primary">{item.workerId || '—'}</span>
                            {item.jobNameInWorker && (
                              <span className="mv-table-muted">{item.jobNameInWorker}</span>
                            )}
                          </>
                        )}
                    </td>

                    <td className="mv-col-tight">
                      <span className={`mv-status tone-${meta.tone}`} title={meta.title}>
                        <Icon name={meta.icon} size={14} />
                        {meta.label}
                      </span>
                      {item.isExternal && (
                        <span className="mv-table-muted" title="Runs on an external scheduler. Milvaion only records what it ran.">
                          external
                        </span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>

        {data?.hasMore && (
          <div className="mv-table-footer">
            More runs fall inside this window than are shown. Narrow the window or search to see the rest.
          </div>
        )}
      </div>
    </div>
  )
}

export default UpcomingExecutions
