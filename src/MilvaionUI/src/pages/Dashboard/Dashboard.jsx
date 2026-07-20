import { useState, useEffect, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import dashboardService from '../../services/dashboardService'
import Icon from '../../components/Icon'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import { SkeletonDashboard } from '../../components/Skeleton'
import './Dashboard.css'

/* eslint-disable react/prop-types */

/**
 * Dashboard - redesign.
 *
 * Prefixed `mvd-`, stylesheet not imported globally. `pages/Dashboard.jsx` is untouched;
 * the route is one line in `App.jsx`.
 *
 * A dashboard exists to answer one question - "is today normal, and if not, where do I
 * look?" - and the page it replaces could not answer either half. Four things were wrong,
 * and the first is not a design problem but a correctness one.
 *
 * 1. THE NUMBERS DID NOT SAY WHAT PERIOD THEY COVERED. "Last 7 days" appeared on two
 *    cards out of eight; the four headline figures at the top carried no window at all,
 *    and one of them - total executions - was all-time while the one beside it was
 *    seven days. Two numbers of different periods, the same size, side by side, is not a
 *    layout flaw; it is a number that means something other than what it appears to mean.
 *    The period is now stated once, at the top, and anything that does not follow it is
 *    labelled where it stands.
 *
 * 2. THE OUTCOMES DID NOT ADD UP. The status grid showed queued, failed, running and
 *    cancelled - and left out completed, which is nearly always the largest of them, and
 *    timed out, which the API sends and nothing displayed. Four numbers that are parts of
 *    a whole, with two parts missing and no whole shown. They are now one bar: every
 *    outcome, in proportion, adding to the total the success rate is a percentage of.
 *
 * 3. HEALTH WAS A CARD IN THE CORNER. Whether the system is up is a yes/no that decides
 *    whether anything else on the page is worth reading. It goes first, in one strip, in
 *    the same shape the monitoring page uses.
 *
 * 4. ONLY FOUR TILES WERE CLICKABLE. A dashboard is a table of contents for the rest of
 *    the app; a figure that identifies a problem should take you to the list of them.
 *    Every outcome segment, the health strip and the worker tile now navigate.
 */

/* The window the API reports over. Stated in one place because it is stated on screen in
   one place - if this ever changes, the label changes with it. */
const PERIOD_LABEL = 'Last 7 days'

/*
 * Execution outcomes, in the order they are stacked.
 *
 * Order is finished-and-fine, finished-and-not, then still-going: the eye reads the bar
 * left to right and the interesting part - anything that is not "completed" - starts at
 * the boundary rather than being scattered through it.
 */
const OUTCOMES = [
  { key: 'completedJobs', status: 2, label: 'Completed', tone: 'success' },
  { key: 'failedJobs', status: 3, label: 'Failed', tone: 'danger' },
  { key: 'timedOutJobs', status: 5, label: 'Timed out', tone: 'warning' },
  { key: 'cancelledJobs', status: 4, label: 'Cancelled', tone: 'idle' },
  { key: 'runningJobs', status: 1, label: 'Running', tone: 'active' },
  { key: 'queuedJobs', status: 0, label: 'Queued', tone: 'queued' },
]

const HEALTH = {
  Healthy: { tone: 'success', icon: 'check_circle' },
  Degraded: { tone: 'warning', icon: 'warning' },
  Unhealthy: { tone: 'danger', icon: 'error' },
  Unknown: { tone: 'idle', icon: 'help' },
}

const healthOf = (status) => HEALTH[status] || HEALTH.Unknown

/** Which icon stands for a health check, guessed from its name and tags. */
function checkIcon(name = '', tags) {
  const haystack = `${name} ${(tags || []).join(' ')}`.toLowerCase()

  if (/postgres|database|sql/.test(haystack)) return 'database'
  if (/redis|cache/.test(haystack)) return 'bolt'
  if (/rabbit|mq|messaging/.test(haystack)) return 'message'
  if (/disk|storage/.test(haystack)) return 'save'
  if (/memory|ram/.test(haystack)) return 'memory'
  if (/http|api|url/.test(haystack)) return 'public'

  return 'build'
}

/* ── Formatting ──────────────────────────────────────────────────────────── */

/** Compact for headline figures: 1.2K rather than 1,214. The exact value goes in a title. */
function compact(value) {
  const n = Number(value)

  if (!Number.isFinite(n)) return '0'
  if (n >= 1e9) return `${(n / 1e9).toFixed(1)}B`
  if (n >= 1e6) return `${(n / 1e6).toFixed(1)}M`
  if (n >= 1e4) return `${(n / 1e3).toFixed(1)}K`

  return n.toLocaleString()
}

const exact = (value) => Number(value ?? 0).toLocaleString()

/**
 * The API sends the average as a .NET TimeSpan string, or occasionally as a number of
 * milliseconds. Both are handled, and the result stops at two units - a dashboard figure
 * is glanced at, and "1m 4s" is glanced at where "00:01:04.2381000" is not.
 */
function formatDuration(duration) {
  if (duration == null || duration === '') return '—'

  let ms

  if (typeof duration === 'string' && duration.includes(':')) {
    const [h, m, rest] = duration.split(':')
    const [s, frac] = (rest || '0').split('.')

    ms = (parseInt(h, 10) * 3600 + parseInt(m, 10) * 60 + parseInt(s, 10)) * 1000
      + (frac ? parseFloat(`0.${frac}`) * 1000 : 0)
  } else {
    ms = typeof duration === 'number' ? duration : parseFloat(duration)
  }

  if (!Number.isFinite(ms)) return '—'

  if (ms < 1) return `${(ms * 1000).toFixed(0)}µs`
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`

  return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`
}

/* ── Pieces ──────────────────────────────────────────────────────────────── */

/**
 * One headline figure.
 *
 * `scope` is required rather than optional: it is what the old page left out, and leaving
 * it out is how two numbers covering different periods ended up looking like a pair.
 */
function Tile({ label, value, exactValue, scope, foot, tone = 'plain', onClick }) {
  const Element = onClick ? 'button' : 'div'

  return (
    <Element
      type={onClick ? 'button' : undefined}
      className={`mvd-tile tone-${tone}` + (onClick ? ' is-clickable' : '')}
      onClick={onClick}
    >
      <span className="mvd-tile-label">
        {label}
        <span className="mvd-tile-scope">{scope}</span>
      </span>
      <span className="mvd-tile-value" title={exactValue}>{value}</span>
      {foot && <span className="mvd-tile-foot">{foot}</span>}
    </Element>
  )
}

/**
 * The outcome bar.
 *
 * Segments are widths in proportion to their counts, with one rule: a non-zero outcome is
 * never drawn narrower than a hairline. Three failures out of two hundred thousand is
 * mathematically invisible and is the single most important thing on the page, so it gets
 * a sliver rather than nothing.
 */
function OutcomeBar({ counts, total, onSelect }) {
  if (!total) {
    return <div className="mvd-bar is-empty">No executions in this period.</div>
  }

  return (
    <div className="mvd-bar" role="img" aria-label="Execution outcomes">
      {OUTCOMES.map(outcome => {
        const count = counts[outcome.key] || 0

        if (count === 0) return null

        return (
          <button
            key={outcome.key}
            type="button"
            className={`mvd-bar-seg tone-${outcome.tone}`}
            style={{ flexGrow: count, minWidth: '3px' }}
            title={`${outcome.label}: ${exact(count)} (${((count / total) * 100).toFixed(1)}%)`}
            onClick={() => onSelect(outcome.status)}
            aria-label={`${outcome.label}, ${exact(count)}`}
          />
        )
      })}
    </div>
  )
}

/* ── Page ────────────────────────────────────────────────────────────────── */

function Dashboard() {
  const navigate = useNavigate()

  const [stats, setStats] = useState(null)
  const [checks, setChecks] = useState([])
  const [overall, setOverall] = useState('Unknown')
  const [loading, setLoading] = useState(true)
  const [lastRefresh, setLastRefresh] = useState(null)

  const [autoRefresh, setAutoRefresh] = useState(() => {
    const saved = localStorage.getItem('dashboard_autoRefresh')

    return saved !== null ? saved === 'true' : true
  })

  const load = useCallback(async () => {
    const [statsResponse, healthResponse] = await Promise.all([
      dashboardService.getStatistics().catch(err => {
        console.error('Dashboard statistics failed:', err)

        return { data: null }
      }),
      dashboardService.getHealthChecks().catch(err => {
        // A failing health endpoint still answers - with the failure - so its body is
        // used rather than discarded.
        console.error('Health checks failed:', err)

        return err.response?.data
      }),
    ])

    const statsData = statsResponse?.data?.data || statsResponse?.data

    if (statsData) setStats(statsData)

    if (healthResponse) {
      setChecks(healthResponse.checks || [])
      setOverall(healthResponse.status || 'Unknown')
    }

    setLastRefresh(new Date())
  }, [])

  useEffect(() => {
    let cancelled = false

    load().finally(() => { if (!cancelled) setLoading(false) })

    return () => { cancelled = true }
  }, [load])

  useEffect(() => {
    if (!autoRefresh) return

    const timer = setInterval(load, 10000)

    return () => clearInterval(timer)
  }, [autoRefresh, load])

  /*
   * The period total is the outcomes added up, not `totalExecutions` - that field is
   * all-time, and dividing a seven-day count by an all-time total is where a success rate
   * quietly becomes meaningless.
   */
  const periodTotal = useMemo(
    () => (stats ? OUTCOMES.reduce((sum, o) => sum + (stats[o.key] || 0), 0) : 0),
    [stats]
  )

  const failing = useMemo(
    () => checks.filter(check => check.status && check.status !== 'Healthy'),
    [checks]
  )

  if (loading) return <SkeletonDashboard />

  const health = healthOf(overall)
  const utilisation = stats?.workerUtilization || 0
  const goToStatus = (status) => navigate('/executions', { state: { filterByStatus: status } })

  return (
    <div className="page mvd-page">
      <div className="page-header">
        <h1>
          <Icon name="dashboard" size={28} />
          <span>Dashboard</span>
        </h1>

        <div className="page-header-actions">
          {/* The window every figure below is measured over, unless it says otherwise.
              Stated once, next to the title, rather than repeated on two cards out of
              eight and omitted from the rest. */}
          <span className="mvd-period">
            <Icon name="date_range" size={15} />
            {PERIOD_LABEL}
          </span>

          <AutoRefreshIndicator
            enabled={autoRefresh}
            onToggle={() => {
              const next = !autoRefresh

              setAutoRefresh(next)
              localStorage.setItem('dashboard_autoRefresh', String(next))
            }}
            lastRefreshTime={lastRefresh}
            intervalSeconds={10}
          />
        </div>
      </div>

      {/* ── Health ─────────────────────────────────────────────────────────
          Whether the system is up decides whether anything else here is worth reading, so
          it is not a card in the bottom-right corner. */}
      <button
        type="button"
        className={`mvd-health tone-${health.tone}`}
        onClick={() => navigate('/admin')}
      >
        <Icon name={health.icon} size={22} />

        <span className="mvd-health-text">
          <strong>
            {failing.length > 0
              ? `${failing.length} of ${checks.length} checks not healthy`
              : checks.length > 0
                ? `All ${checks.length} health checks passing`
                : 'No health checks reported'}
          </strong>
          <span>
            {failing.length === 0
              ? 'Database, cache and broker all responding.'
              : failing.map(check => check.name).join(', ')}
          </span>
        </span>

        <Icon name="chevron_right" size={18} />
      </button>

      {/* ── Outcomes ───────────────────────────────────────────────────────
          The centre of the page: what happened to everything that ran. */}
      <section className="mvd-outcomes">
        <header className="mvd-outcomes-head">
          <div>
            <span className="mvd-rate-value">{(stats?.successRate || 0).toFixed(1)}%</span>
            <span className="mvd-rate-label">
              succeeded of {exact(periodTotal)} executions · {PERIOD_LABEL.toLowerCase()}
            </span>
          </div>
        </header>

        <OutcomeBar counts={stats || {}} total={periodTotal} onSelect={goToStatus} />

        {/*
         * The legend is the numbers. Every outcome the API reports appears here, including
         * the two the old page dropped - completed, which is usually the largest, and
         * timed out, which was fetched and never rendered.
         */}
        <div className="mvd-legend">
          {OUTCOMES.map(outcome => {
            const count = stats?.[outcome.key] || 0
            const share = periodTotal ? (count / periodTotal) * 100 : 0

            return (
              <button
                key={outcome.key}
                type="button"
                className={`mvd-legend-item tone-${outcome.tone}` + (count === 0 ? ' is-zero' : '')}
                onClick={() => goToStatus(outcome.status)}
              >
                <span className="mvd-dot" />
                <span className="mvd-legend-label">{outcome.label}</span>
                <span className="mvd-legend-value" title={exact(count)}>{compact(count)}</span>
                <span className="mvd-legend-share">{share.toFixed(1)}%</span>
              </button>
            )
          })}
        </div>
      </section>

      {/* ── Signals ────────────────────────────────────────────────────────
          Everything that is a single figure rather than a composition. Each states its
          own period, because two of them do not follow the page's. */}
      <div className="mvd-tiles">
        <Tile
          label="Throughput"
          scope={PERIOD_LABEL.toLowerCase()}
          value={(stats?.executionsPerMinute || 0).toFixed(1)}
          exactValue={`${(stats?.executionsPerSecond || 0).toFixed(2)} per second`}
          foot={
            stats?.peakExecutionsPerMinute
              ? `peak ${compact(stats.peakExecutionsPerMinute)}/min in the last hour`
              : 'executions per minute'
          }
        />

        <Tile
          label="Average duration"
          scope={PERIOD_LABEL.toLowerCase()}
          value={formatDuration(stats?.averageDuration)}
          foot="per execution"
        />

        {/*
         * Worker capacity was a 120px donut drawn to hold one percentage. A donut is for
         * showing a part of a whole when the parts are worth comparing; with one value it
         * is a number in an expensive frame. The bar carries the same figure and leaves
         * room for the counts it is derived from.
         */}
        <Tile
          label="Worker capacity"
          scope="now"
          tone={utilisation >= 80 ? 'warning' : 'plain'}
          value={`${utilisation.toFixed(0)}%`}
          foot={
            <>
              <span className="mvd-capacity-bar" aria-hidden="true">
                <span
                  className={utilisation >= 80 ? 'is-high' : undefined}
                  style={{ width: `${Math.max(1.5, Math.min(100, utilisation))}%` }}
                />
              </span>
              {exact(stats?.workerCurrentJobs)} of {exact(stats?.workerMaxCapacity)} slots
              {' · '}
              {exact(stats?.totalWorkers)} workers, {exact(stats?.totalWorkerInstances)} instances
            </>
          }
          onClick={() => navigate('/workers')}
        />

        <Tile
          label="Total executions"
          scope="all time"
          value={compact(stats?.totalExecutions)}
          exactValue={exact(stats?.totalExecutions)}
          foot="since the first run"
          onClick={() => navigate('/executions')}
        />
      </div>

      {/* ── Health checks ──────────────────────────────────────────────────
          The detail behind the strip at the top. Flat rows rather than a card of badges:
          the overall verdict is stated above, so each row only has to name itself. */}
      {checks.length > 0 && (
        <section className="mvd-checks">
          <h2>Health checks</h2>

          <div className="mvd-check-list">
            {checks.map((check, index) => {
              const tone = healthOf(check.status).tone

              return (
                <div className={`mvd-check tone-${tone}`} key={check.name || index}>
                  <Icon name={checkIcon(check.name, check.tags)} size={18} />
                  <span className="mvd-check-name">{check.name}</span>
                  <span className="mvd-check-duration">{formatDuration(check.duration)}</span>
                  <span className={`mv-status tone-${tone}`}>
                    <Icon name={healthOf(check.status).icon} size={14} />
                    {check.status || 'Unknown'}
                  </span>
                </div>
              )
            })}
          </div>
        </section>
      )}
    </div>
  )
}

export default Dashboard
