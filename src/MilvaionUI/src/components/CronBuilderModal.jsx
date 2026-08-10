import { useMemo, useState } from 'react'
import PropTypes from 'prop-types'
import cronstrue from 'cronstrue'
import moment from 'moment'
import Icon from './Icon'
import {
  FREQUENCIES,
  MONTHS,
  NTH_OPTIONS,
  WEEKDAYS,
  buildCronExpression,
  defaultBuilderState,
  getBuilderWarning,
  getNextOccurrences,
  parseCronExpression,
  validateBuilderState,
} from '../utils/cronBuilder'
// The overlay below reuses .modal-overlay and friends, which are defined here and nowhere else.
// Routes are lazily loaded and so is their CSS, so a page that never renders a Modal - the job
// form is one - would otherwise leave the overlay unpositioned and drop it into the normal flow.
// Import order does not decide which stylesheet wins here, since these end up in separate chunks;
// the rules that have to beat Modal.css are qualified in CronBuilderModal.css instead.
import './Modal.css'
import './CronBuilderModal.css'

const pad = (n) => String(n).padStart(2, '0')
const HOURS = Array.from({ length: 24 }, (_, i) => i)

// ── small controls ───────────────────────────────────────────────────────────

function Row({ label, hint, children }) {
  return (
    <div className="cb-row">
      <span className="cb-row-label">{label}</span>
      <div className="cb-row-control">{children}</div>
      {hint && <span className="cb-row-hint">{hint}</span>}
    </div>
  )
}

Row.propTypes = {
  label: PropTypes.string.isRequired,
  hint: PropTypes.string,
  children: PropTypes.node,
}

function NumberInput({ value, min, max, onChange, suffix }) {
  return (
    <span className="cb-number">
      <input
        type="number"
        className="cb-number-input"
        min={min}
        max={max}
        value={value}
        onChange={(e) => {
          const next = Number(e.target.value)
          // Clamping on every keystroke keeps the state always buildable, so the preview
          // below never has to render a half-typed value.
          if (Number.isNaN(next)) return
          onChange(Math.min(max, Math.max(min, next)))
        }}
      />
      {suffix && <span className="cb-number-suffix">{suffix}</span>}
    </span>
  )
}

NumberInput.propTypes = {
  value: PropTypes.number.isRequired,
  min: PropTypes.number.isRequired,
  max: PropTypes.number.isRequired,
  onChange: PropTypes.func.isRequired,
  suffix: PropTypes.string,
}

function HourSelect({ value, onChange }) {
  return (
    <select className="cb-select" value={value} onChange={(e) => onChange(Number(e.target.value))}>
      {HOURS.map((h) => (
        <option key={h} value={h}>{pad(h)}:00</option>
      ))}
    </select>
  )
}

HourSelect.propTypes = {
  value: PropTypes.number.isRequired,
  onChange: PropTypes.func.isRequired,
}

function TimeInput({ hour, minute, onChange }) {
  return (
    <input
      type="time"
      className="cb-time"
      value={`${pad(hour)}:${pad(minute)}`}
      onChange={(e) => {
        const [h, m] = e.target.value.split(':').map(Number)
        if (Number.isNaN(h) || Number.isNaN(m)) return
        onChange(h, m)
      }}
    />
  )
}

TimeInput.propTypes = {
  hour: PropTypes.number.isRequired,
  minute: PropTypes.number.isRequired,
  onChange: PropTypes.func.isRequired,
}

function WeekdayPicker({ selected, onChange }) {
  const toggle = (day) => onChange(
    selected.includes(day)
      ? selected.filter((d) => d !== day)
      : [...selected, day].sort((a, b) => a - b)
  )

  return (
    <div className="cb-weekdays">
      {WEEKDAYS.map((day) => (
        <button
          key={day.value}
          type="button"
          className={`cb-weekday${selected.includes(day.value) ? ' cb-weekday--on' : ''}`}
          onClick={() => toggle(day.value)}
          title={day.label}
        >
          {day.short}
        </button>
      ))}
      <button type="button" className="cb-weekday-quick" onClick={() => onChange([1, 2, 3, 4, 5])}>
        Mon-Fri
      </button>
      <button type="button" className="cb-weekday-quick" onClick={() => onChange([0, 6])}>
        Weekend
      </button>
      <button type="button" className="cb-weekday-quick" onClick={() => onChange([0, 1, 2, 3, 4, 5, 6])}>
        All
      </button>
    </div>
  )
}

WeekdayPicker.propTypes = {
  selected: PropTypes.arrayOf(PropTypes.number).isRequired,
  onChange: PropTypes.func.isRequired,
}

// ── main component ───────────────────────────────────────────────────────────

/**
 * Visual builder for the six-field cron expressions the scheduler expects.
 *
 * Opens on whatever is already in the field: `parseCronExpression` reads the expression back
 * into the form when it recognises the shape, so editing an existing schedule starts from
 * that schedule rather than from a default. Nothing is written until Apply - closing or
 * cancelling leaves the original expression untouched.
 *
 * Props:
 *   value   - the cron expression currently in the input, if any
 *   onApply - called with the built expression
 *   onClose - called when the dialog should close without applying
 */
export default function CronBuilderModal({ value, onApply, onClose }) {
  const [state, setState] = useState(() => parseCronExpression(value) || defaultBuilderState())

  const set = (patch) => setState((prev) => ({ ...prev, ...patch }))

  const error = validateBuilderState(state)
  const warning = error ? null : getBuilderWarning(state)
  const expression = error ? '' : buildCronExpression(state)

  const description = useMemo(() => {
    if (!expression) return ''
    try {
      return cronstrue.toString(expression)
    } catch {
      return ''
    }
  }, [expression])

  const nextRuns = useMemo(
    () => (expression ? getNextOccurrences(expression, 5) : []),
    [expression]
  )

  const hourWindow = (
    <>
      <label className="cb-check">
        <input
          type="checkbox"
          checked={state.restrictHours}
          onChange={(e) => set({ restrictHours: e.target.checked })}
        />
        <span>Only between these hours</span>
      </label>
      {state.restrictHours && (
        <div className="cb-inline">
          <HourSelect value={state.hourFrom} onChange={(v) => set({ hourFrom: v })} />
          <span className="cb-sep">and</span>
          <HourSelect value={state.hourTo} onChange={(v) => set({ hourTo: v })} />
          <span className="cb-row-hint">
            The end hour is included, so {pad(state.hourTo)} still runs until {pad(state.hourTo)}:59.
          </span>
        </div>
      )}
    </>
  )

  const weekdayFilter = (
    <>
      <label className="cb-check">
        <input
          type="checkbox"
          checked={state.restrictWeekdays}
          onChange={(e) => set({ restrictWeekdays: e.target.checked })}
        />
        <span>Only on these days</span>
      </label>
      {state.restrictWeekdays && (
        <WeekdayPicker selected={state.weekdays} onChange={(weekdays) => set({ weekdays })} />
      )}
    </>
  )

  const renderOptions = () => {
    switch (state.frequency) {
      case 'seconds':
        return (
          <>
            <Row label="Run every">
              <NumberInput
                value={state.secondInterval}
                min={1}
                max={59}
                suffix="seconds"
                onChange={(secondInterval) => set({ secondInterval })}
              />
            </Row>
            <Row label="Hours">{hourWindow}</Row>
            <Row label="Days">{weekdayFilter}</Row>
          </>
        )

      case 'minutes':
        return (
          <>
            <Row label="Run every">
              <NumberInput
                value={state.minuteInterval}
                min={1}
                max={59}
                suffix="minutes"
                onChange={(minuteInterval) => set({ minuteInterval })}
              />
            </Row>
            <Row label="Hours">{hourWindow}</Row>
            <Row label="Days">{weekdayFilter}</Row>
          </>
        )

      case 'hourly':
        return (
          <>
            <Row label="Run every">
              <NumberInput
                value={state.hourInterval}
                min={1}
                max={23}
                suffix="hour(s)"
                onChange={(hourInterval) => set({ hourInterval })}
              />
            </Row>
            <Row label="At minute">
              <NumberInput
                value={state.hourMinute}
                min={0}
                max={59}
                suffix="past the hour"
                onChange={(hourMinute) => set({ hourMinute })}
              />
            </Row>
            <Row label="Hours">{hourWindow}</Row>
            <Row label="Days">{weekdayFilter}</Row>
          </>
        )

      case 'daily':
        return (
          <>
            <Row
              label="Run every"
              hint={state.dayInterval > 1
                ? 'Cron counts days from the 1st of each month, so the interval restarts at the start of every month.'
                : undefined}
            >
              <NumberInput
                value={state.dayInterval}
                min={1}
                max={31}
                suffix="day(s)"
                onChange={(dayInterval) => set({ dayInterval })}
              />
            </Row>
            <Row label="At">
              <TimeInput hour={state.hour} minute={state.minute} onChange={(hour, minute) => set({ hour, minute })} />
            </Row>
          </>
        )

      case 'weekly':
        return (
          <>
            <Row label="On">
              <WeekdayPicker selected={state.weekdays} onChange={(weekdays) => set({ weekdays })} />
            </Row>
            <Row label="At">
              <TimeInput hour={state.hour} minute={state.minute} onChange={(hour, minute) => set({ hour, minute })} />
            </Row>
          </>
        )

      case 'monthly':
        return (
          <>
            <Row label="Run every">
              <NumberInput
                value={state.monthInterval}
                min={1}
                max={12}
                suffix="month(s)"
                onChange={(monthInterval) => set({ monthInterval })}
              />
            </Row>
            <Row label="On">
              <div className="cb-modes">
                <label className="cb-check">
                  <input
                    type="radio"
                    checked={state.monthlyMode === 'dayOfMonth'}
                    onChange={() => set({ monthlyMode: 'dayOfMonth' })}
                  />
                  <span>A day of the month</span>
                </label>
                {state.monthlyMode === 'dayOfMonth' && (
                  <div className="cb-inline cb-indent">
                    <NumberInput
                      value={state.dayOfMonth}
                      min={1}
                      max={31}
                      onChange={(dayOfMonth) => set({ dayOfMonth })}
                    />
                    <label className="cb-check">
                      <input
                        type="checkbox"
                        checked={state.lastDayOfMonth}
                        onChange={(e) => set({ lastDayOfMonth: e.target.checked })}
                      />
                      <span>Last day instead</span>
                    </label>
                  </div>
                )}

                <label className="cb-check">
                  <input
                    type="radio"
                    checked={state.monthlyMode === 'nthWeekday'}
                    onChange={() => set({ monthlyMode: 'nthWeekday' })}
                  />
                  <span>A weekday of the month</span>
                </label>
                {state.monthlyMode === 'nthWeekday' && (
                  <div className="cb-inline cb-indent">
                    <select
                      className="cb-select"
                      value={state.nth}
                      onChange={(e) => set({ nth: Number(e.target.value) })}
                    >
                      {NTH_OPTIONS.map((o) => (
                        <option key={o.value} value={o.value}>{o.label}</option>
                      ))}
                    </select>
                    <select
                      className="cb-select"
                      value={state.nthWeekday}
                      onChange={(e) => set({ nthWeekday: Number(e.target.value) })}
                    >
                      {WEEKDAYS.map((d) => (
                        <option key={d.value} value={d.value}>{d.label}</option>
                      ))}
                    </select>
                  </div>
                )}
              </div>
            </Row>
            <Row label="At">
              <TimeInput hour={state.hour} minute={state.minute} onChange={(hour, minute) => set({ hour, minute })} />
            </Row>
          </>
        )

      case 'yearly':
        return (
          <>
            <Row label="On">
              <div className="cb-inline">
                <select
                  className="cb-select"
                  value={state.month}
                  onChange={(e) => set({ month: Number(e.target.value) })}
                >
                  {MONTHS.map((m) => (
                    <option key={m.value} value={m.value}>{m.label}</option>
                  ))}
                </select>
                {!state.lastDayOfMonth && (
                  <NumberInput
                    value={state.dayOfMonth}
                    min={1}
                    max={31}
                    onChange={(dayOfMonth) => set({ dayOfMonth })}
                  />
                )}
                <label className="cb-check">
                  <input
                    type="checkbox"
                    checked={state.lastDayOfMonth}
                    onChange={(e) => set({ lastDayOfMonth: e.target.checked })}
                  />
                  <span>Last day of the month</span>
                </label>
              </div>
            </Row>
            <Row label="At">
              <TimeInput hour={state.hour} minute={state.minute} onChange={(hour, minute) => set({ hour, minute })} />
            </Row>
          </>
        )

      default:
        return null
    }
  }

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-content cb-modal" onClick={(e) => e.stopPropagation()}>

        <div className="modal-header">
          <h2 className="cb-title"><Icon name="edit_calendar" size={22} /> Cron Builder</h2>
          <button className="modal-close-btn" onClick={onClose} title="Close">
            <Icon name="close" size={20} />
          </button>
        </div>

        <div className="modal-body cb-body">
          <div className="cb-frequencies">
            {FREQUENCIES.map((f) => (
              <button
                key={f.value}
                type="button"
                className={`cb-frequency${state.frequency === f.value ? ' cb-frequency--on' : ''}`}
                onClick={() => set({ frequency: f.value })}
              >
                <Icon name={f.icon} size={18} />
                <span>{f.label}</span>
              </button>
            ))}
          </div>

          <div className="cb-options">{renderOptions()}</div>

          <div className="cb-preview">
            {error ? (
              <div className="cb-preview-error">
                <Icon name="error" size={16} />
                {error}
              </div>
            ) : (
              <>
                <code className="cb-expression">{expression}</code>
                <div className="cb-fields">second minute hour day month day-of-week</div>
                {description && (
                  <div className="cb-description">
                    <Icon name="check_circle" size={15} />
                    {description}
                  </div>
                )}
                {warning && (
                  <div className="cb-preview-warning">
                    <Icon name="warning" size={15} />
                    {warning}
                  </div>
                )}
                {nextRuns.length > 0 && (
                  <div className="cb-next">
                    <span className="cb-next-label">
                      Next runs
                      {/* The dispatcher evaluates cron against TimeZoneInfo.Utc, so showing
                          browser-local times here would mislead anyone off UTC. */}
                      <span className="cb-next-tz">UTC</span>
                    </span>
                    <ul className="cb-next-list">
                      {nextRuns.map((run) => (
                        <li key={run.toISOString()}>{moment.utc(run).format('ddd, DD MMM YYYY HH:mm:ss')}</li>
                      ))}
                    </ul>
                  </div>
                )}
              </>
            )}
          </div>
        </div>

        <div className="modal-footer">
          <button type="button" className="modal-btn modal-btn-cancel" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="modal-btn modal-btn-confirm"
            onClick={() => onApply(expression)}
            disabled={!expression}
          >
            Apply
          </button>
        </div>
      </div>
    </div>
  )
}

CronBuilderModal.propTypes = {
  value: PropTypes.string,
  onApply: PropTypes.func.isRequired,
  onClose: PropTypes.func.isRequired,
}
