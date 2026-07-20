import { useState, useEffect, useCallback, useMemo } from 'react'
import Icon from '../../components/Icon'
import configurationService from '../../services/configurationService'
import { SkeletonCard } from '../../components/Skeleton'
import { getApiErrorMessage } from '../../utils/errorUtils'
import { buildSections, systemIdentity, offSwitches } from './configSpec'
import './Configuration.css'

/* eslint-disable react/prop-types */

/**
 * System configuration - redesign.
 *
 * Prefixed `mvc-`, stylesheet not imported globally. The page it replaces is still in the
 * tree at `pages/Configuration.jsx`; the route is one line.
 *
 * The old page was nine accordions over about ninety label/value pairs, every pair drawn
 * at the same weight. Three things follow from that, and all three are fixed here.
 *
 * 1. A PAGE OF NINETY VALUES IS A REFERENCE DOCUMENT, AND REFERENCE DOCUMENTS NEED SEARCH.
 *    To find the log collector's batch interval you had to already know it lived under
 *    "Background Services", open that section, and scroll. There is now one search box
 *    that filters every value on the page and tells you which section each hit came from.
 *    Once you can search, folders stop being navigation and start being obstacles - so the
 *    accordions are gone and the page is a continuous document with a sticky index.
 *
 * 2. THE PAGE IS READ TO ANSWER A QUESTION, USUALLY "WHY ISN'T SOMETHING RUNNING". The
 *    answer is almost always that a switch is off, and a switch that is off was previously
 *    one green-or-red chip among twenty. The switches that are off are now collected into
 *    a line at the top. If nothing is off, that line says so and takes one row.
 *
 * 3. THE MARKUP WAS THE DATA. Five hundred lines of hand-written rows, each repeating the
 *    same three spans. The layout now comes from a description of the settings
 *    (`configSpec.js`), which is what makes search, the index and the off-switch summary
 *    possible at all - none of them could have been written against the old JSX without
 *    doing it three more times.
 */

/* ── Value renderers ─────────────────────────────────────────────────────── */

/**
 * A boolean.
 *
 * A dot and a word, matching the tables, rather than a filled chip. Twenty filled green
 * chips read as decoration and the one red one does not stand out enough - which is the
 * opposite of what this page is for.
 */
function BoolValue({ value, on = 'Enabled', off = 'Disabled' }) {
  if (value == null) return <span className="mvc-empty">—</span>

  return (
    <span className={'mvc-bool ' + (value ? 'is-on' : 'is-off')}>
      <Icon name={value ? 'check_circle' : 'cancel'} size={14} />
      {value ? on : off}
    </span>
  )
}

function RowValue({ row }) {
  const { kind, value, unit, on, off } = row

  if (kind === 'bool') return <BoolValue value={value} on={on} off={off} />

  if (value == null || value === '') return <span className="mvc-empty">—</span>

  // Identifiers - hostnames, keys, queue names - are read character by character and
  // sometimes copied, so they get the monospace treatment. Numbers do not: a proportional
  // figure is easier to read at a glance and these are never copied.
  if (kind === 'code') return <code className="mvc-code">{value}</code>

  return (
    <span className="mvc-plain">
      {typeof value === 'number' ? value.toLocaleString() : value}
      {unit && <span className="mvc-unit">{unit}</span>}
    </span>
  )
}

/* ── Meter ───────────────────────────────────────────────────────────────── */

/** CPU, memory and disk, in the identity strip. Small on purpose - see below. */
function Meter({ label, percent, detail }) {
  const value = Number.isFinite(percent) ? percent : 0
  const tone = value >= 80 ? 'danger' : value >= 50 ? 'warning' : 'ok'

  return (
    <div className="mvc-meter" title={detail}>
      <div className="mvc-meter-head">
        <span>{label}</span>
        <strong>{value.toFixed(0)}%</strong>
      </div>
      <span className="mvc-meter-bar">
        <span className={`tone-${tone}`} style={{ width: `${Math.max(1.5, Math.min(100, value))}%` }} />
      </span>
    </div>
  )
}

/* ── Page ────────────────────────────────────────────────────────────────── */

function Configuration() {
  const [config, setConfig] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [query, setQuery] = useState('')
  const [activeSection, setActiveSection] = useState(null)

  const load = useCallback(async () => {
    try {
      setLoading(true)

      const response = await configurationService.getConfiguration()

      setConfig(response?.data || response)
      setError(null)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load configuration'))
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { load() }, [load])

  const sections = useMemo(() => (config ? buildSections(config) : []), [config])
  const identity = useMemo(() => (config ? systemIdentity(config) : null), [config])
  const off = useMemo(() => (config ? offSwitches(config) : []), [config])

  /*
   * Search matches the label, the rendered value and the group heading, so "batch" finds
   * "Batch size" and "rabbit" finds everything under RabbitMQ even though the word appears
   * in none of those labels. A group survives if any of its rows do; a section survives if
   * any of its groups do.
   */
  const term = query.trim().toLowerCase()

  const visible = useMemo(() => {
    if (!term) return sections

    const matches = (row, groupTitle, sectionTitle) =>
      [row.label, String(row.value ?? ''), groupTitle, sectionTitle]
        .join(' ')
        .toLowerCase()
        .includes(term)

    return sections
      .map(section => ({
        ...section,
        groups: section.groups
          .map(group => ({
            ...group,
            rows: group.rows.filter(row => matches(row, group.title || '', section.title)),
          }))
          .filter(group => group.rows.length > 0),
      }))
      .filter(section => section.groups.length > 0)
  }, [sections, term])

  const hitCount = useMemo(
    () => visible.reduce((n, s) => n + s.groups.reduce((m, g) => m + g.rows.length, 0), 0),
    [visible]
  )

  /*
   * The index highlights whichever section is under the top of the viewport. An
   * IntersectionObserver with a top-heavy margin fires as a heading crosses the upper
   * quarter, which is where the eye treats a section as "the one I am in" - watching the
   * scroll position and measuring offsets does the same job but recalculates on every
   * frame.
   */
  useEffect(() => {
    if (term) return

    const headings = Array.from(document.querySelectorAll('[data-mvc-section]'))

    if (headings.length === 0) return

    const observer = new IntersectionObserver(
      entries => {
        const onScreen = entries
          .filter(entry => entry.isIntersecting)
          .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)

        if (onScreen[0]) setActiveSection(onScreen[0].target.dataset.mvcSection)
      },
      { rootMargin: '-80px 0px -70% 0px', threshold: 0 }
    )

    headings.forEach(heading => observer.observe(heading))

    return () => observer.disconnect()
  }, [visible, term])

  const jumpTo = (id) => {
    const target = document.getElementById(`mvc-section-${id}`)

    if (target) target.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }

  if (loading) {
    return (
      <div className="page mvc-page">
        <SkeletonCard lines={6} />
        <SkeletonCard lines={10} />
      </div>
    )
  }

  if (error || !config) {
    return (
      <div className="page mvc-page">
        <div className="mvc-error">
          <Icon name="error" size={20} />
          <div>
            <strong>{error || 'Failed to load configuration'}</strong>
            <button type="button" className="mvc-retry" onClick={load}>Try again</button>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="page mvc-page">
      {/* ── Identity ───────────────────────────────────────────────────────
          "Which system am I looking at" is the first question anyone opening a config
          page has, and on the old page the answer was in the third accordion. It is now
          the first thing on the screen.

          The three meters sit here rather than in a section of their own because they
          describe the host, not the configuration - they are the only values on this page
          that change while you watch. Small, so they read as context. */}
      {identity && (
        <div className="mvc-identity">
          {/*
           * Every fact is labelled. The first draft made the hostname the headline, and a
           * container id like `e5af1cd44f73` set in 22px says nothing at all - it looked
           * like the name of the thing you were configuring rather than the machine it
           * happens to be running on. Nobody, including the person who built the system,
           * could tell what that string was.
           *
           * So: the product and its version are the title, the environment is the badge,
           * and everything else is a labelled fact in a row that spreads across the strip.
           */}
          <div className="mvc-identity-title">
            <span className={'mvc-env is-' + (identity.environment === 'Production' ? 'prod' : 'dev')}>
              {identity.environment || 'Unknown'}
            </span>
            <h1>Milvaion {identity.version || ''}</h1>
          </div>

          <dl className="mvc-facts">
            <div>
              <dt>API host</dt>
              <dd>
                <code
                  className="mvc-code"
                  title="Machine or container running the API. In a container deployment this is the container id."
                >
                  {identity.hostName || 'unknown'}
                </code>
              </dd>
            </div>
            <div>
              <dt>Uptime</dt>
              <dd>{identity.uptime}</dd>
            </div>
            <div>
              <dt>Started</dt>
              <dd>
                {identity.startupTime
                  ? new Date(identity.startupTime).toLocaleString()
                  : '—'}
              </dd>
            </div>
          </dl>

          {identity.resources && (
            <div className="mvc-meters">
              <Meter
                label="CPU"
                percent={identity.resources.cpuUsagePercent}
                detail="Process CPU usage"
              />
              <Meter
                label="Memory"
                percent={identity.resources.memoryUsagePercent}
                detail={`${identity.resources.usedMemoryMB} MB of ${identity.resources.totalMemoryMB} MB used · process ${identity.resources.processMemoryMB} MB`}
              />
              <Meter
                label="Disk"
                percent={identity.resources.diskUsagePercent}
                detail={`${identity.resources.availableDiskGB} GB free of ${identity.resources.totalDiskGB} GB`}
              />
            </div>
          )}
        </div>
      )}

      {/* ── What's off ─────────────────────────────────────────────────────
          The page is usually opened to find out why something is not happening, and the
          answer is nearly always a switch that is off. Collected here so it does not have
          to be hunted for among twenty identical chips. */}
      <div className={'mvc-switches ' + (off.length ? 'has-off' : 'all-on')}>
        <Icon name={off.length ? 'toggle_off' : 'toggle_on'} size={18} />

        {off.length === 0 ? (
          <span>Every service and integration on this page is switched on.</span>
        ) : (
          <span>
            <strong>{off.length} switched off:</strong>{' '}
            {off.map((item, index) => (
              <span key={item}>
                {index > 0 && ', '}
                <button type="button" className="mvc-off-chip" onClick={() => setQuery(item)}>
                  {item}
                </button>
              </span>
            ))}
          </span>
        )}
      </div>

      {/* ── Search ─────────────────────────────────────────────────────────
          The single change that makes the rest of the page usable. */}
      <div className="mvc-searchbar">
        <div className="mvc-search">
          <Icon name="search" size={18} />
          <input
            type="text"
            value={query}
            onChange={e => setQuery(e.target.value)}
            placeholder="Search every setting — try “batch”, “timeout”, “queue”…"
            aria-label="Search settings"
          />
          {query && (
            <button type="button" onClick={() => setQuery('')} title="Clear search" aria-label="Clear search">
              <Icon name="close" size={15} />
            </button>
          )}
        </div>

        {term && (
          <span className="mvc-hits">
            {hitCount} {hitCount === 1 ? 'setting' : 'settings'}
          </span>
        )}
      </div>

      <div className="mvc-layout">
        {/* The index is hidden while searching: results are already short, and a list of
            sections that no longer reflects what is on screen is worse than none. */}
        {!term && (
          <nav className="mvc-index" aria-label="Sections">
            {sections.map(section => (
              <button
                key={section.id}
                type="button"
                className={'mvc-index-item' + (activeSection === section.id ? ' is-active' : '')}
                onClick={() => jumpTo(section.id)}
              >
                <Icon name={section.icon} size={16} />
                <span>{section.title}</span>
              </button>
            ))}
          </nav>
        )}

        <div className="mvc-body">
          {visible.length === 0 && (
            <div className="mvc-nohits">
              <Icon name="search_off" size={30} />
              <p>Nothing matches “{query}”.</p>
            </div>
          )}

          {visible.map(section => (
            <section
              key={section.id}
              id={`mvc-section-${section.id}`}
              className="mvc-section"
              data-mvc-section={section.id}
            >
              <h2 className="mvc-section-title">
                <Icon name={section.icon} size={20} />
                {section.title}
              </h2>

              {section.note && <p className="mvc-section-note">{section.note}</p>}

              {section.groups.map((group, index) => (
                <div className="mvc-group" key={group.title || `group-${index}`}>
                  {group.title && <h3 className="mvc-group-title">{group.title}</h3>}

                  {/*
                   * A definition list, not a grid of cards. These are label/value pairs
                   * and nothing else; a card around each one adds ninety borders to the
                   * page and no information.
                   */}
                  <dl className="mvc-rows">
                    {group.rows.map(row => (
                      <div className="mvc-row" key={`${group.title || ''}-${row.label}`}>
                        <dt>
                          {row.label}
                          {row.hint && (
                            <span className="mvc-hint" title={row.hint}>
                              <Icon name="help" size={13} />
                            </span>
                          )}
                        </dt>
                        <dd><RowValue row={row} /></dd>
                      </div>
                    ))}
                  </dl>
                </div>
              ))}
            </section>
          ))}
        </div>
      </div>
    </div>
  )
}

export default Configuration
