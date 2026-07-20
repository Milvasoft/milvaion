import { useState, useEffect, useCallback } from 'react'
import api from '../../services/api'
import Icon from '../../components/Icon'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import DatabaseStatistics from '../../components/DatabaseStatistics/DatabaseStatistics'
import ServiceMemoryStats from '../../components/ServiceMemoryStats/ServiceMemoryStats'
import CollapsibleSection from '../../components/CollapsibleSection'
import { SkeletonDashboard } from '../../components/Skeleton'
import './Monitoring.css'

/* eslint-disable react/prop-types */

/**
 * System monitoring - redesign.
 *
 * Everything here is prefixed `mvm-` and the stylesheet is not imported globally, so this
 * can be kept or dropped without touching anything else. `AdminDashboard.jsx` is left in
 * place; swapping the route back is one line.
 *
 * The page it replaces laid out five equal-weight cards in a grid. That is the wrong shape
 * for a monitoring screen, because a monitoring screen is read to answer one question -
 * "is anything wrong right now?" - and only then, if the answer is yes, a second one:
 * "where?". A grid of equals makes both answers cost the same amount of reading.
 *
 * Four things changed.
 *
 * 1. THE VERDICT IS ALWAYS ON SCREEN. The old page rendered its health banner only when
 *    something was already wrong, so the most important line on the page was the one you
 *    never saw while things were fine - and when it did appear, the card underneath
 *    repeated it. Now there is one strip, always present, and it is the only place the
 *    overall verdict is stated.
 *
 * 2. THE NUMBERS THAT MOVE ARE SEPARATED FROM THE NUMBERS THAT DON'T. Queue depth and
 *    Redis success rate change minute to minute; the job catalogue - how many jobs exist,
 *    how many are recurring - does not. The old page polled both every few seconds and
 *    drew them at the same size. Here the moving values are tiles at the top and the
 *    catalogue is one quiet line at the bottom, fetched once.
 *
 * 3. THE PAGE REMEMBERS. It was already polling every five seconds and discarding every
 *    previous sample. Keeping the last sixty costs nothing and turns "1,240 messages" into
 *    "1,240 and climbing", which is the question someone actually has.
 *
 * 4. THE DESTRUCTIVE CONTROL LOOKS DESTRUCTIVE. Emergency Stop sat inside a card between
 *    two statistics, the same size and shape as a refresh button. It now lives in the
 *    verdict strip, styled as the one dangerous thing on the page, behind a dialog that
 *    asks for a reason.
 */

/* ── Health vocabulary ────────────────────────────────────────────────────────
   The API sends ordinals; these are the four states and how each is drawn. `tone` is the
   same vocabulary the tables use, so a warning looks like a warning everywhere. */

const HEALTH = {
  Healthy: { tone: 'success', icon: 'check_circle', label: 'Healthy' },
  Warning: { tone: 'warning', icon: 'warning', label: 'Warning' },
  Degraded: { tone: 'warning', icon: 'error', label: 'Degraded' },
  Critical: { tone: 'danger', icon: 'error', label: 'Critical' },
  Unknown: { tone: 'idle', icon: 'help', label: 'Unknown' },
}

const HEALTH_BY_ORDINAL = { 0: 'Healthy', 1: 'Warning', 2: 'Critical', 3: 'Degraded' }

const healthOf = (name) => HEALTH[name] ?? HEALTH.Unknown

/** Messages on one queue at which it is considered full. Mirrors the old page. */
const QUEUE_CAPACITY = 10000

/* ── Sparkline ────────────────────────────────────────────────────────────────
 *
 * Deliberately tiny and unlabelled. It is not a chart - there are no axes and no values
 * to read off it. Its whole job is to answer "is this number where it has been?", which
 * a single figure cannot answer no matter how large it is drawn.
 *
 * Scaled to its own min and max rather than to zero: at zero-baseline a queue sitting
 * between 1,200 and 1,240 draws as a flat line, and that 40-message drift is exactly what
 * someone is looking for.
 */
function Sparkline({ values = [], tone = 'accent' }) {
  if (values.length < 2) {
    return <span className="mvm-spark-empty" aria-hidden="true" />
  }

  const width = 100
  const height = 24
  const min = Math.min(...values)
  const max = Math.max(...values)
  const span = max - min || 1

  const points = values.map((value, index) => {
    const x = (index / (values.length - 1)) * width
    const y = height - ((value - min) / span) * (height - 4) - 2

    return `${x.toFixed(2)},${y.toFixed(2)}`
  })

  // The filled area under the line reads as volume; the line alone is easy to lose at
  // this size against a card background.
  const area = `0,${height} ${points.join(' ')} ${width},${height}`

  return (
    <svg
      className={`mvm-spark tone-${tone}`}
      viewBox={`0 0 ${width} ${height}`}
      preserveAspectRatio="none"
      aria-hidden="true"
    >
      <polygon points={area} className="mvm-spark-area" />
      <polyline points={points.join(' ')} className="mvm-spark-line" />
    </svg>
  )
}

/**
 * One moving number.
 *
 * The delta is against the oldest sample held, not the previous one: at a five second
 * poll the sample-to-sample difference is noise, and a number that flickers between +3
 * and -2 teaches nothing.
 */
function Signal({ label, value, unit = '', tone = 'accent', history = [], hint }) {
  const first = history[0]
  const last = history[history.length - 1]
  const delta = history.length > 3 && typeof first === 'number' ? last - first : null
  const direction = delta === null || Math.abs(delta) < 0.005 ? null : delta > 0 ? 'up' : 'down'

  return (
    <div className={`mvm-signal tone-${tone}`} title={hint}>
      <span className="mvm-signal-label">{label}</span>

      <span className="mvm-signal-value">
        {value}
        {unit && <span className="mvm-signal-unit">{unit}</span>}
      </span>

      <div className="mvm-signal-foot">
        <Sparkline values={history} tone={tone} />

        {direction && (
          <span className={`mvm-delta is-${direction}`}>
            <Icon name={direction === 'up' ? 'trending_up' : 'trending_down'} size={13} />
            {Math.abs(delta) >= 1 ? Math.round(Math.abs(delta)).toLocaleString() : Math.abs(delta).toFixed(2)}
          </span>
        )}
      </div>
    </div>
  )
}

/** Keeps the last `limit` samples of a series. */
function pushSample(series, value, limit = 60) {
  if (typeof value !== 'number' || Number.isNaN(value)) return series

  const next = [...series, value]

  return next.length > limit ? next.slice(next.length - limit) : next
}

function Monitoring() {
  const [health, setHealth] = useState(null)
  const [breaker, setBreaker] = useState(null)
  const [catalogue, setCatalogue] = useState(null)
  const [loading, setLoading] = useState(true)
  const [lastRefresh, setLastRefresh] = useState(null)
  const [stopOpen, setStopOpen] = useState(false)
  const [stopReason, setStopReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [actionError, setActionError] = useState(null)

  const [autoRefresh, setAutoRefresh] = useState(() => {
    const saved = localStorage.getItem('monitoring_autoRefresh')

    return saved !== null ? saved === 'true' : true
  })

  /*
   * The retained samples. Appended through the functional form of `setSeries` rather
   * than by reading `series` inside the poll callbacks: those callbacks are memoised
   * with an empty dependency list, so they would otherwise keep appending to whatever
   * the series was on first render and the history would never grow past one entry.
   */
  const [series, setSeries] = useState({ queue: [], active: [], success: [] })

  const loadHealth = useCallback(async () => {
    try {
      const response = await api.get('/admin/system-health')
      const data = response?.data?.data || response?.data || response

      if (!data) return

      const mapped = {
        ...data,
        overallHealth: HEALTH_BY_ORDINAL[data.overallHealth] ?? data.overallHealth ?? 'Unknown',
        queueStats: (data.queueStats || []).map(queue => ({
          ...queue,
          healthStatus: HEALTH_BY_ORDINAL[queue.healthStatus] ?? queue.healthStatus ?? 'Unknown',
        })),
      }

      const queueDepth = mapped.queueStats.reduce((sum, q) => sum + (q.messageCount || 0), 0)

      setHealth(mapped)
      setSeries(current => ({
        ...current,
        queue: pushSample(current.queue, queueDepth),
        active: pushSample(current.active, mapped.totalActiveJobs || 0),
      }))
      setLastRefresh(new Date())
    } catch (err) {
      console.error('Failed to load system health:', err)
    }
  }, [])

  const loadBreaker = useCallback(async () => {
    try {
      const response = await api.get('/admin/redis-circuit-breaker')
      const data = response?.data?.data || response?.data || response

      if (!data) return

      setBreaker(data)
      setSeries(current => ({
        ...current,
        success: pushSample(current.success, data.successRatePercentage ?? null),
      }))
      setLastRefresh(new Date())
    } catch (err) {
      console.error('Failed to load circuit breaker stats:', err)
    }
  }, [])

  // The job catalogue is inventory, not health: it changes when someone creates a job,
  // not on its own. Fetched once, and again only when the page is refreshed by hand.
  const loadCatalogue = useCallback(async () => {
    try {
      const response = await api.get('/admin/job-stats')

      setCatalogue(response?.data?.data || response?.data || response)
    } catch (err) {
      console.error('Failed to load job stats:', err)
    }
  }, [])

  const loadAll = useCallback(async () => {
    await Promise.all([loadHealth(), loadBreaker(), loadCatalogue()])
  }, [loadHealth, loadBreaker, loadCatalogue])

  useEffect(() => {
    let cancelled = false

    const first = async () => {
      await loadAll()

      if (!cancelled) setLoading(false)
    }

    first()

    return () => { cancelled = true }
  }, [loadAll])

  useEffect(() => {
    if (!autoRefresh) return

    const timer = setInterval(() => {
      loadHealth()
      loadBreaker()
    }, 5000)

    return () => clearInterval(timer)
  }, [autoRefresh, loadHealth, loadBreaker])

  const runControl = async (fn) => {
    setBusy(true)
    setActionError(null)

    try {
      await fn()
      await loadHealth()
      setStopOpen(false)
      setStopReason('')
    } catch (err) {
      // Reported in place rather than through `alert`, which loses the page behind a
      // modal the user cannot copy from.
      setActionError(err?.response?.data?.message || err?.message || 'The request failed.')
      console.error('Dispatcher control failed:', err)
    } finally {
      setBusy(false)
    }
  }

  const emergencyStop = () => runControl(() =>
    api.post(`/admin/jobdispatcher/stop?reason=${encodeURIComponent(stopReason || 'Manual emergency stop')}`)
  )

  const resume = () => runControl(() => api.post('/admin/jobdispatcher/resume'))

  if (loading) return <SkeletonDashboard />

  const overall = healthOf(health?.overallHealth)
  const dispatcherOn = !!health?.dispatcherEnabled
  const queues = health?.queueStats || []
  const queueDepth = queues.reduce((sum, q) => sum + (q.messageCount || 0), 0)
  const starvedQueues = queues.filter(q => q.messageCount > 0 && q.consumerCount === 0)
  const criticalQueues = queues.filter(q => q.healthStatus === 'Critical')

  /*
   * The strip states the problem, not the label. "Critical" tells someone the severity but
   * not what to do; "2 queues at capacity" is the same length and is actionable. The
   * reasons are ordered by how much they demand attention, and only the first is shown -
   * a list of problems in a banner is a wall, and the detail is right below anyway.
   */
  const reason = !dispatcherOn
    ? 'The dispatcher is stopped. No new work is being handed out.'
    : criticalQueues.length > 0
      ? `${criticalQueues.length} ${criticalQueues.length === 1 ? 'queue is' : 'queues are'} at capacity.`
      : breaker?.healthStatus === 'Critical'
        ? 'Redis is failing often enough to put the circuit breaker at risk.'
        : starvedQueues.length > 0
          ? `${starvedQueues.length} ${starvedQueues.length === 1 ? 'queue has' : 'queues have'} messages but no consumer.`
          : null

  return (
    <div className="page mvm-page">
      <div className="page-header">
        <h1>
          <Icon name="monitor_heart" size={28} />
          <span>Monitoring</span>
        </h1>
        <div className="page-header-actions">
          <button type="button" className="mvm-btn-quiet" onClick={loadAll}>
            <Icon name="refresh" size={18} />
            Refresh
          </button>
        </div>
      </div>

      {/* ── Verdict ────────────────────────────────────────────────────────
          One line, always present, and the only place the overall state is stated. */}
      <div className={`mvm-verdict tone-${overall.tone}`}>
        <span className="mvm-verdict-icon">
          <Icon name={overall.icon} size={26} />
        </span>

        <div className="mvm-verdict-text">
          <strong>
            {overall.label === 'Healthy' && !reason ? 'All systems healthy' : `System ${overall.label.toLowerCase()}`}
          </strong>
          <p>{reason || 'Dispatcher running, queues within capacity, Redis responding.'}</p>
        </div>

        <div className="mvm-verdict-actions">
          {dispatcherOn ? (
            <button type="button" className="mvm-btn-danger" onClick={() => setStopOpen(true)}>
              <Icon name="stop_circle" size={18} />
              Emergency stop
            </button>
          ) : (
            <button type="button" className="mvm-btn-primary" onClick={resume} disabled={busy}>
              <Icon name="play_arrow" size={18} />
              {busy ? 'Resuming…' : 'Resume dispatching'}
            </button>
          )}
        </div>
      </div>

      {actionError && (
        <div className="mvm-inline-error">
          <Icon name="error" size={16} />
          <span>{actionError}</span>
        </div>
      )}

      {/* ── Signals ────────────────────────────────────────────────────────
          The four values that move. Everything else on the page explains one of them. */}
      <div className="mvm-signals">
        <Signal
          label="Queue depth"
          value={queueDepth.toLocaleString()}
          tone={criticalQueues.length ? 'danger' : queueDepth > 1000 ? 'warning' : 'accent'}
          history={series.queue}
          hint="Total messages waiting across all queues."
        />
        <Signal
          label="Active jobs"
          value={(health?.totalActiveJobs ?? 0).toLocaleString()}
          tone="accent"
          history={series.active}
          hint="Jobs currently eligible to be dispatched."
        />
        <Signal
          label="Redis success"
          value={(breaker?.successRatePercentage ?? 0).toFixed(1)}
          unit="%"
          tone={healthOf(breaker?.healthStatus).tone === 'success' ? 'success' : healthOf(breaker?.healthStatus).tone}
          history={series.success}
          hint="Share of Redis operations that succeeded in the last hour."
        />
        <Signal
          label="Consumers"
          value={queues.reduce((sum, q) => sum + (q.consumerCount || 0), 0)}
          tone={starvedQueues.length ? 'warning' : 'accent'}
          hint="Workers attached across all queues."
        />
      </div>

      {/* ── Queues ─────────────────────────────────────────────────────────
          A list of records, so it uses the same table as every other list in the app.
          The old page built this out of divs and had to hand-write its own mobile
          behaviour for it. */}
      <div className="mv-table-card">
        <div className="mv-table-toolbar">
          <span className="mvm-toolbar-title">
            <Icon name="storage" size={18} />
            Queues
          </span>
          <span className="mv-spacer" />
          <span className="mvm-toolbar-note">{queues.length} total</span>
        </div>

        <div className="mv-table-scroll">
          <table className="mv-table">
            <thead>
              <tr>
                <th>Queue</th>
                <th className="mv-col-tight">Status</th>
                <th>Depth</th>
                <th className="mv-col-tight">Consumers</th>
              </tr>
            </thead>
            <tbody>
              {queues.length === 0 && (
                <tr>
                  <td colSpan={4} className="mv-table-empty">
                    <Icon name="storage" size={32} />
                    <p>No queues reported.</p>
                  </td>
                </tr>
              )}

              {queues.map(queue => {
                const status = healthOf(queue.healthStatus)
                const fill = Math.min((queue.messageCount / QUEUE_CAPACITY) * 100, 100)
                const starved = queue.messageCount > 0 && queue.consumerCount === 0

                return (
                  <tr key={queue.queueName} className={`mv-table-row tone-${status.tone}`}>
                    <td>
                      <span className="mv-table-primary">{queue.queueName}</span>
                      {/* A queue with work and nobody to do it is not "unhealthy" by
                          depth, so the status column will not say it. Said here. */}
                      {starved && <span className="mv-table-muted">no consumer attached</span>}
                    </td>

                    <td className="mv-col-tight">
                      <span className={`mv-status tone-${status.tone}`}>
                        <Icon name={status.icon} size={14} />
                        {status.label}
                      </span>
                    </td>

                    <td>
                      {/* Depth as a number and a bar against capacity. The number alone
                          means nothing without knowing what "a lot" is. */}
                      <div className="mvm-depth">
                        <span className="mvm-depth-value">{queue.messageCount.toLocaleString()}</span>
                        <span className="mvm-depth-bar">
                          <span className={`tone-${status.tone}`} style={{ width: `${Math.max(1.5, fill)}%` }} />
                        </span>
                      </div>
                    </td>

                    <td className="mv-col-tight">
                      <span className={starved ? 'mvm-starved' : 'mv-table-dim'}>
                        {queue.consumerCount}
                      </span>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </div>

      {/* ── Redis ──────────────────────────────────────────────────────────
          The breaker had a card of four stat boxes. Three of them - operations, failures,
          time since last failure - are only ever read to explain the success rate, which
          is already a signal above. So this is a sentence with the numbers folded in. */}
      <div className={`mvm-panel tone-${healthOf(breaker?.healthStatus).tone}`}>
        <div className="mvm-panel-head">
          <Icon name="bolt" size={18} />
          <h2>Redis circuit breaker</h2>
          <span className={`mv-status tone-${healthOf(breaker?.healthStatus).tone}`}>
            <Icon name={healthOf(breaker?.healthStatus).icon} size={14} />
            {breaker?.state || 'Unknown'}
          </span>
        </div>

        <p className="mvm-panel-line">
          {breaker?.healthMessage || 'No report yet.'}
        </p>

        <dl className="mvm-facts">
          <div>
            <dt>Operations</dt>
            <dd>{breaker?.totalOperations?.toLocaleString() ?? '—'}</dd>
          </div>
          <div>
            <dt>Failures</dt>
            <dd className={breaker?.totalFailures > 0 ? 'is-bad' : undefined}>
              {breaker?.totalFailures?.toLocaleString() ?? '—'}
            </dd>
          </div>
          <div>
            <dt>Last failure</dt>
            <dd>{breaker?.timeSinceLastFailure || 'none recorded'}</dd>
          </div>
        </dl>

        {breaker?.recommendation && breaker?.healthStatus !== 'Healthy' && (
          <p className="mvm-panel-advice">
            <Icon name="lightbulb" size={15} />
            {breaker.recommendation}
          </p>
        )}
      </div>

      {/* ── Inventory ──────────────────────────────────────────────────────
          Not health. One line, at the bottom, off the polling loop. */}
      {catalogue && (
        <div className="mvm-inventory">
          <Icon name="inventory_2" size={16} />
          <span><strong>{catalogue.totalJobs?.toLocaleString() ?? 0}</strong> jobs</span>
          <span><strong>{catalogue.activeJobs?.toLocaleString() ?? 0}</strong> active</span>
          <span><strong>{catalogue.inactiveJobs?.toLocaleString() ?? 0}</strong> inactive</span>
          <span><strong>{catalogue.recurringJobs?.toLocaleString() ?? 0}</strong> recurring</span>
          <span><strong>{catalogue.oneTimeJobs?.toLocaleString() ?? 0}</strong> one-time</span>
        </div>
      )}

      {/* Infrastructure detail. Closed by default - it is read when investigating, not
          when checking. */}
      <CollapsibleSection
        storageKey="milvaion.monitoring.databaseOpen"
        icon="database"
        title="Database"
        defaultOpen={false}
      >
        <DatabaseStatistics />
      </CollapsibleSection>

      <CollapsibleSection
        storageKey="milvaion.monitoring.memoryOpen"
        icon="memory"
        title="Background service memory"
        defaultOpen={false}
      >
        <ServiceMemoryStats />
      </CollapsibleSection>

      {/* ── Emergency stop ─────────────────────────────────────────────────
          A dialog rather than a confirm, because the reason is recorded and whoever reads
          the log later needs it to say something. */}
      {stopOpen && (
        <div className="mvm-overlay" onClick={() => !busy && setStopOpen(false)}>
          <div className="mvm-dialog" onClick={e => e.stopPropagation()} role="dialog" aria-modal="true">
            <div className="mvm-dialog-head">
              <Icon name="stop_circle" size={22} />
              <h2>Stop the dispatcher</h2>
            </div>

            <p className="mvm-dialog-body">
              Nothing new will be dispatched until someone resumes it. Work already handed
              to a worker keeps running.
            </p>

            <label className="mvm-field">
              <span>Reason</span>
              <textarea
                value={stopReason}
                onChange={e => setStopReason(e.target.value)}
                placeholder="Queue backing up, deploying a fix, …"
                rows={3}
                autoFocus
              />
            </label>

            {actionError && (
              <div className="mvm-inline-error">
                <Icon name="error" size={16} />
                <span>{actionError}</span>
              </div>
            )}

            <div className="mvm-dialog-actions">
              <button type="button" className="mvm-btn-quiet" onClick={() => setStopOpen(false)} disabled={busy}>
                Cancel
              </button>
              <button type="button" className="mvm-btn-danger" onClick={emergencyStop} disabled={busy}>
                {busy ? 'Stopping…' : 'Stop dispatching'}
              </button>
            </div>
          </div>
        </div>
      )}

      <AutoRefreshIndicator
        enabled={autoRefresh}
        onToggle={() => {
          const next = !autoRefresh

          setAutoRefresh(next)
          localStorage.setItem('monitoring_autoRefresh', String(next))
        }}
        lastRefreshTime={lastRefresh}
        intervalSeconds={5}
      />
    </div>
  )
}

export default Monitoring
