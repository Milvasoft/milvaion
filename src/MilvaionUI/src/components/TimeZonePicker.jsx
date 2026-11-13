import { useEffect, useMemo, useRef, useState } from 'react'
import PropTypes from 'prop-types'
import Icon from './Icon'
import { formatUtcOffset, listTimeZones } from '../utils/timeZones'
import './TimeZonePicker.css'

/**
 * Searchable IANA time zone picker.
 *
 * The list comes from the browser (Intl), so it matches what the server accepts: IANA ids such as
 * "Europe/Istanbul". Typing filters by id, city and current UTC offset ("+03", "istanbul", "new york").
 */
function TimeZonePicker({ id, value, onChange, disabled }) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [highlight, setHighlight] = useState(0)
  const rootRef = useRef(null)
  const searchRef = useRef(null)

  const zones = useMemo(() => listTimeZones(), [])

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase().replace(/\s+/g, '_')
    if (!q) return zones

    return zones.filter(z => z.searchText.includes(q))
  }, [zones, query])

  useEffect(() => {
    if (!open) return

    const close = (event) => {
      if (rootRef.current && !rootRef.current.contains(event.target)) setOpen(false)
    }

    document.addEventListener('mousedown', close)
    setTimeout(() => searchRef.current?.focus(), 0)

    return () => document.removeEventListener('mousedown', close)
  }, [open])

  useEffect(() => setHighlight(0), [query])

  const choose = (zoneId) => {
    onChange(zoneId)
    setOpen(false)
    setQuery('')
  }

  const onKeyDown = (event) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      setHighlight(h => Math.min(h + 1, filtered.length - 1))
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      setHighlight(h => Math.max(h - 1, 0))
    } else if (event.key === 'Enter') {
      event.preventDefault()
      if (filtered[highlight]) choose(filtered[highlight].id)
    } else if (event.key === 'Escape') {
      setOpen(false)
    }
  }

  const current = value || 'UTC'

  return (
    <div className="mv-tzpicker" ref={rootRef}>
      <button
        id={id}
        type="button"
        className="mv-tzpicker-control"
        onClick={() => setOpen(o => !o)}
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
      >
        <Icon name="public" size={16} />
        <span className="mv-tzpicker-label">{current}</span>
        <span className="mv-tzpicker-offset">{formatUtcOffset(current)}</span>
        <Icon name={open ? 'expand_less' : 'expand_more'} size={18} />
      </button>

      {open && (
        <div className="mv-tzpicker-panel">
          <input
            ref={searchRef}
            type="text"
            className="mv-tzpicker-search"
            placeholder="Search by city, region or offset (e.g. Istanbul, +03)"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={onKeyDown}
            aria-label="Search time zones"
          />
          <ul className="mv-tzpicker-list" role="listbox">
            {filtered.length === 0 && <li className="mv-tzpicker-empty">No time zone matches “{query}”</li>}
            {filtered.slice(0, 300).map((zone, index) => (
              <li
                key={zone.id}
                role="option"
                aria-selected={zone.id === current}
                className={'mv-tzpicker-option' + (index === highlight ? ' is-highlighted' : '') + (zone.id === current ? ' is-selected' : '')}
                onMouseEnter={() => setHighlight(index)}
                onMouseDown={(e) => { e.preventDefault(); choose(zone.id) }}
              >
                <span>{zone.id}</span>
                <span className="mv-tzpicker-offset">{zone.offset}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}

TimeZonePicker.propTypes = {
  id: PropTypes.string,
  value: PropTypes.string,
  onChange: PropTypes.func.isRequired,
  disabled: PropTypes.bool
}

export default TimeZonePicker
