import { Link } from 'react-router-dom'
import Icon from './Icon'

/* eslint-disable react/prop-types */

/**
 * A row action button.
 *
 * Row actions were written six different ways across the app - bordered here, borderless
 * there, text-only somewhere else - because each page styled its own `.action-btn`, and
 * six stylesheets declared that class. One component, one shape, intent carried by colour.
 *
 * Renders a `Link` when `to` is given and a `button` otherwise, so navigation actions stay
 * real links: middle click and "open in new tab" keep working, which they do not when a
 * button calls `navigate`.
 *
 * Clicks are stopped from reaching the row. Nearly every table here makes the row itself
 * clickable, and without this an action would also open the record it acts on.
 *
 * @param {'default'|'primary'|'edit'|'danger'|'success'} intent
 * @param {string} icon Material symbol name.
 * @param {string} title Tooltip and accessible name. Required - these buttons are icon only.
 * @param {string} [label] Optional visible text next to the icon.
 * @param {string} [to] Route to link to, instead of an onClick.
 */
export function ActionButton({
  intent = 'default',
  icon,
  title,
  label = null,
  to = null,
  onClick = null,
  disabled = false,
}) {
  const className = `mv-action-btn intent-${intent}` + (disabled ? ' is-disabled' : '')

  const content = (
    <>
      <Icon name={icon} size={16} />
      {label && <span>{label}</span>}
    </>
  )

  // The row underneath is usually clickable; an action must not trigger it as well.
  const stop = (e) => {
    e.stopPropagation()

    if (disabled) {
      e.preventDefault()
      return
    }

    onClick?.(e)
  }

  if (to && !disabled) {
    return (
      <Link to={to} className={className} title={title} aria-label={title} onClick={stop}>
        {content}
      </Link>
    )
  }

  return (
    <button
      type="button"
      className={className}
      title={title}
      aria-label={title}
      onClick={stop}
      disabled={disabled}
    >
      {content}
    </button>
  )
}

/**
 * Wraps the action buttons of one row.
 *
 * Put it on the `td` so the buttons align to the right edge of the column and the cell
 * itself absorbs clicks that land between them.
 */
export function TableActions({ children }) {
  return <div className="mv-table-actions-inner">{children}</div>
}

export default TableActions
