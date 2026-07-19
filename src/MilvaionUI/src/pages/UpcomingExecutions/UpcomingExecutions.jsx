import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import Icon from '../../components/Icon'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { SkeletonTable } from '../../components/Skeleton'
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

const HEALTH_META = {
  Scheduled: {
    className: 'ok',
    icon: 'check_circle',
    label: 'Scheduled',
    title: 'The dispatcher is holding this time. It will fire unless something changes.',
  },
  Projected: {
    className: 'projected',
    icon: 'calculate',
    label: 'Projected',
    title: 'Derived from the cron expression. The workflow engine works the time out on each poll rather than storing it.',
  },
  NotScheduled: {
    className: 'problem',
    icon: 'error',
    label: 'Not scheduled',
    title: 'Active and recurring, but the dispatcher holds no run time for it, so it will not run.',
  },
  InvalidSchedule: {
    className: 'problem',
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

      <div className="upcoming-filters">
        <div className="upcoming-search">
          <Icon name="search" size={18} />
          <input
            type="text"
            placeholder="Search by name..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
          />
        </div>

        <select value={withinHours} onChange={(e) => setWithinHours(Number(e.target.value))}>
          {WINDOWS.map(w => (
            <option key={w.hours} value={w.hours}>Next {w.label}</option>
          ))}
        </select>

        <select value={kind} onChange={(e) => setKind(e.target.value)}>
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
      </div>

      {items.length === 0 ? (
        <div className="upcoming-empty">
          <Icon name="event_available" size={48} />
          <h3>Nothing scheduled in this window</h3>
          <p>
            {onlyProblems
              ? 'Every recurring job has a run time.'
              : 'Try widening the window, or check that the jobs you expect are active.'}
          </p>
        </div>
      ) : (
        <div className="upcoming-table-wrapper">
          <table className="upcoming-table">
            <thead>
              <tr>
                <th>When</th>
                <th>Name</th>
                <th>Type</th>
                <th>Schedule</th>
                <th>Target</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item, index) => {
                const health = normalize(HEALTH, item.health)
                const meta = HEALTH_META[health] ?? HEALTH_META.Scheduled
                const itemKind = normalize(KIND, item.kind)
                const overdue = isOverdue(item)

                return (
                  <tr
                    key={`${item.id}-${index}`}
                    className={`upcoming-row ${meta.className}`}
                    onClick={() => openItem(item)}
                  >
                    <td className="upcoming-when">
                      {item.scheduledAt ? (
                        <>
                          <span className={`upcoming-countdown ${overdue ? 'overdue' : ''}`}>
                            {formatCountdown(item.scheduledAt)}
                          </span>
                          <span className="upcoming-absolute">{formatDateTime(item.scheduledAt)}</span>
                        </>
                      ) : (
                        <span className="upcoming-countdown none">never</span>
                      )}
                    </td>

                    <td className="upcoming-name">
                      <span>{item.displayName}</span>
                      {item.tags && <span className="upcoming-tags">{item.tags}</span>}
                    </td>

                    <td>
                      <span className={`upcoming-kind ${itemKind === 'Workflow' ? 'workflow' : 'job'}`}>
                        <Icon name={itemKind === 'Workflow' ? 'account_tree' : 'work'} size={15} />
                        {itemKind}
                      </span>
                    </td>

                    <td>
                      {item.cronExpression
                        ? <code className="upcoming-cron">{item.cronExpression}</code>
                        : <span className="upcoming-muted">one-time</span>}
                    </td>

                    <td className="upcoming-target">
                      {itemKind === 'Workflow'
                        ? <span className="upcoming-muted">—</span>
                        : (
                          <>
                            <span>{item.workerId || '—'}</span>
                            {item.jobNameInWorker && (
                              <span className="upcoming-muted">{item.jobNameInWorker}</span>
                            )}
                          </>
                        )}
                    </td>

                    <td>
                      <span className={`upcoming-health ${meta.className}`} title={meta.title}>
                        <Icon name={meta.icon} size={15} />
                        {meta.label}
                      </span>
                      {item.isExternal && (
                        <span className="upcoming-external" title="Runs on an external scheduler. Milvaion only records what it ran.">
                          external
                        </span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>

          {data?.hasMore && (
            <div className="upcoming-more">
              More runs fall inside this window than are shown. Narrow the window or search to see the rest.
            </div>
          )}
        </div>
      )}
    </div>
  )
}

export default UpcomingExecutions
