import Icon from './Icon'
import { formatDateTime, formatRelativeTime } from '../utils/dateUtils'

/* eslint-disable react/prop-types */

/**
 * The pieces every list table in the app is built from.
 *
 * The styling lives in `styles/table.css`; this file exists so the markup is written once
 * too. Before it, each page assembled its own toolbar, footer and status cell, which is
 * how the same table ended up with a different search box and a different gap above it on
 * every screen - the CSS was shared but the structure was not.
 *
 * Layout of a full table:
 *
 *   <div className="mv-table-card">
 *     <TableToolbar>       search, filters
 *     <SelectionBar>       only while rows are selected
 *     <div className="mv-table-scroll"><table className="mv-table">…
 *     <TableFooter>        count, page size, pager
 *   </div>
 */

/* ── Toolbar ─────────────────────────────────────────────────────────────── */

export function TableToolbar({ children }) {
  return <div className="mv-table-toolbar">{children}</div>
}

/**
 * The search field. Quiet until focused, with a clear button once there is something to
 * clear - looking for the field's own "x" is faster than selecting the text and deleting.
 */
export function TableSearch({ value, onChange, placeholder = 'Search…' }) {
  return (
    <div className="mv-table-search">
      <Icon name="search" size={16} />
      <input
        type="text"
        value={value ?? ''}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
      />
      {value && (
        <button type="button" onClick={() => onChange('')} title="Clear search" aria-label="Clear search">
          <Icon name="close" size={14} />
        </button>
      )}
    </div>
  )
}

/**
 * A one-click filter for the dimension a page is mostly used through.
 *
 * A dropdown hides its options until opened and costs two interactions; this shows the
 * whole set. Only sensible up to about six options - past that the toolbar wraps and a
 * `<select>` is the better trade.
 *
 * @param {{value: any, label: string}[]} options
 */
export function SegmentedFilter({ options, value, onChange, label = 'Filter' }) {
  return (
    <div className="mv-segmented" role="group" aria-label={label}>
      {options.map((option) => (
        <button
          key={String(option.value)}
          type="button"
          className={'mv-segment' + (value === option.value ? ' is-active' : '')}
          onClick={() => onChange(option.value)}
          aria-pressed={value === option.value}
        >
          {option.label}
        </button>
      ))}
    </div>
  )
}

/** Pushes everything after it to the right edge of a toolbar, selection bar or footer. */
export function Spacer() {
  return <span className="mv-spacer" />
}

/* ── Selection ───────────────────────────────────────────────────────────── */

/**
 * Replaces the toolbar while rows are selected, rather than appearing next to it: at that
 * moment the only actions that matter are the ones acting on the selection.
 */
export function SelectionBar({ count, onClear, children }) {
  if (!count) return null

  return (
    <div className="mv-table-selection">
      <span>{count} selected</span>
      <button type="button" className="mv-link" onClick={onClear}>Clear</button>
      <Spacer />
      {children}
    </div>
  )
}

export function BulkDeleteButton({ onClick, label = 'Delete' }) {
  return (
    <button type="button" className="mv-bulk-danger" onClick={onClick}>
      <Icon name="delete" size={16} />
      {label}
    </button>
  )
}

/* ── Cells ───────────────────────────────────────────────────────────────── */

/**
 * A timestamp, shown relative with the exact value on hover.
 *
 * Relative because several absolute timestamps on one row nearly always fall on the same
 * day, so the eye ends up comparing long strings that differ by seconds. The exact time is
 * still there, in the tooltip, for when the difference is what matters.
 */
export function TimeCell({ value, placeholder = '—' }) {
  if (!value) return <span className="mv-table-dim">{placeholder}</span>

  return <span title={formatDateTime(value)}>{formatRelativeTime(value)}</span>
}

/**
 * Status as a dot and a label rather than a filled chip.
 *
 * The row already carries the colour on its left edge, so this only has to name the state.
 * A column of solid chips reads as a colour chart and drowns out everything else on the
 * row.
 *
 * @param {{label: string, tone: string, icon: string}} status
 * @param {boolean} spinning For states that are still changing while you look at them.
 */
export function StatusCell({ status, spinning = false }) {
  if (!status) return <span className="mv-table-dim">—</span>

  return (
    <span className={`mv-status tone-${status.tone}` + (spinning ? ' is-spinning' : '')}>
      <Icon name={status.icon} size={14} />
      {status.label}
    </span>
  )
}

/**
 * A duration with a bar scaled to the largest value on screen.
 *
 * Comparing durations as numbers means reading them one at a time; the bar makes the
 * outlier obvious without reading anything. The scale is per page rather than absolute so
 * the comparison is against what the user can actually see.
 */
export function DurationCell({ ms, text, maxMs = 0 }) {
  if (!text) return <span className="mv-table-dim">—</span>

  const ratio = maxMs > 0 ? Math.min(1, (ms ?? 0) / maxMs) : 0

  return (
    <div className="mv-duration">
      <span className="mv-duration-text">{text}</span>
      <span className="mv-duration-bar" aria-hidden="true">
        <span style={{ width: `${Math.max(2, ratio * 100)}%` }} />
      </span>
    </div>
  )
}

/** The chevron column. Hidden until the row is hovered - see `table.css`. */
export function OpenCell() {
  return (
    <td className="mv-col-open">
      <Icon name="chevron_right" size={18} />
    </td>
  )
}

/* ── Empty ───────────────────────────────────────────────────────────────── */

export function TableEmpty({ colSpan, icon = 'inbox', message }) {
  return (
    <tr>
      <td colSpan={colSpan} className="mv-table-empty">
        <Icon name={icon} size={32} />
        <p>{message}</p>
      </td>
    </tr>
  )
}

/* ── Footer ──────────────────────────────────────────────────────────────── */

/**
 * Count on the left, page controls on the right.
 *
 * `onNext`/`onPrevious` drive cursor paging, where there is no page number to show - only
 * whether more exists in either direction. Pages that page by number drop a `Pagination`
 * in as `children` instead.
 */
export function TableFooter({
  totalCount,
  noun = 'records',
  pageSize,
  onPageSizeChange,
  pageSizes = [20, 50, 100],
  hasNext,
  hasPrevious,
  onNext,
  onPrevious,
  children,
}) {
  const paged = typeof onNext === 'function' || typeof onPrevious === 'function'

  return (
    <div className="mv-table-footer">
      {totalCount != null && (
        <span className="mv-table-count">
          {totalCount.toLocaleString?.() ?? totalCount} {noun}
        </span>
      )}

      {children}

      <Spacer />

      {onPageSizeChange && (
        <label className="mv-table-pagesize">
          Rows
          <select value={pageSize} onChange={(e) => onPageSizeChange(Number(e.target.value))}>
            {pageSizes.map((size) => <option key={size} value={size}>{size}</option>)}
          </select>
        </label>
      )}

      {paged && (
        <div className="mv-table-pager">
          <button type="button" onClick={onPrevious} disabled={!hasPrevious} title="Previous page" aria-label="Previous page">
            <Icon name="chevron_left" size={18} style={{ userSelect: 'none', display: 'flex', justifyContent: 'center' }} />
          </button>
          <button type="button" onClick={onNext} disabled={!hasNext} title="Next page" aria-label="Next page">
            <Icon name="chevron_right" size={18} style={{ userSelect: 'none', display: 'flex', justifyContent: 'center' }} />
          </button>
        </div>
      )}
    </div>
  )
}
