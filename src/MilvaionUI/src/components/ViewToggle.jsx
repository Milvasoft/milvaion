import Icon from './Icon'
import './ViewToggle.css'

/* eslint-disable react/prop-types */

/**
 * Switches a list between presentations - cards and a table, typically.
 *
 * The job list and the workflow list each grew their own version of this control with
 * different padding, colours and active states, so the same switch looked like two
 * different controls depending on which page you were on.
 *
 * @param {string} value Current mode.
 * @param {(mode: string) => void} onChange
 * @param {{value: string, icon: string, label: string}[]} options
 */
function ViewToggle({ value, onChange, options }) {
  return (
    <div className="mv-view-toggle" role="group" aria-label="View mode">
      {options.map(option => (
        <button
          key={option.value}
          type="button"
          className={'mv-view-btn' + (value === option.value ? ' is-active' : '')}
          onClick={() => onChange(option.value)}
          title={option.label}
          aria-pressed={value === option.value}
        >
          <Icon name={option.icon} size={18} />
          {/* The label carries its own class rather than being styled as "the span in
              here": Icon renders a span too, so a bare `span` selector hides the icon
              along with the text and leaves an empty button. */}
          <span className="mv-view-label">{option.label}</span>
        </button>
      ))}
    </div>
  )
}

export default ViewToggle
