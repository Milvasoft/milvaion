import { useState, useEffect } from 'react'
import PropTypes from 'prop-types'
import cronstrue from 'cronstrue'
import Icon from './Icon'
import CronBuilderModal from './CronBuilderModal'

export default function CronExpressionInput({ value, onChange, required = false, disabled = false }) {
  const [cronInput, setCronInput] = useState(value || '')
  const [humanReadable, setHumanReadable] = useState('')
  const [error, setError] = useState('')
  const [builderOpen, setBuilderOpen] = useState(false)

  // Sync local state with prop value
  useEffect(() => {
    setCronInput(value || '')
    if (value) {
      try {
        setHumanReadable(cronstrue.toString(value))
        setError('')
      } catch (err) {
        setHumanReadable('')
        setError('Invalid cron expression')
      }
    } else {
      setHumanReadable('')
      setError('')
    }
  }, [value])

  /**
   * Single path for every way the expression can change - typing, a preset, the builder.
   *
   * The parent is handed a synthetic event carrying the `cronExpression` name because the
   * form handlers on the other side key off `target.name`, and a bare value would land on
   * the wrong field.
   */
  const commit = (newValue) => {
    setCronInput(newValue)

    const syntheticEvent = {
      target: {
        name: 'cronExpression',
        value: newValue
      }
    }
    onChange(syntheticEvent)

    if (newValue) {
      try {
        setHumanReadable(cronstrue.toString(newValue))
        setError('')
      } catch (err) {
        setHumanReadable('')
        setError('Invalid cron expression')
      }
    } else {
      setHumanReadable('')
      setError('')
    }
  }

  const handleChange = (e) => commit(e.target.value)

  // Preset cron templates (6-part format: second minute hour day month dayOfWeek)
  const presets = [
    { label: 'Every minute', value: '0 * * * * *' },
    { label: 'Every 5 minutes', value: '0 */5 * * * *' },
    { label: 'Every 10 minutes', value: '0 */10 * * * *' },
    { label: 'Every hour', value: '0 0 * * * *' },
    { label: 'Every day at midnight', value: '0 0 0 * * *' },
    { label: 'Every day at 9 AM', value: '0 0 9 * * *' },
    { label: 'Every day at 6 PM', value: '0 0 18 * * *' },
    { label: 'Every Monday at 9 AM', value: '0 0 9 * * 1' },
    { label: 'Every 1st of month', value: '0 0 0 1 * *' },
  ]

  const handlePresetClick = (presetValue) => commit(presetValue)

  return (
    <div className="cron-expression-container">
      <div className="cron-input-wrapper">
        <div className="cron-input-row">
          <input
            type="text"
            name="cronExpression"
            value={cronInput}
            onChange={handleChange}
            placeholder="e.g., 0 0 9 * * * (every day at 9 AM)"
            required={required}
            disabled={disabled}
            className={error ? 'cron-input error' : 'cron-input'}
          />

          <button
            type="button"
            className="btn-cron-builder"
            onClick={() => setBuilderOpen(true)}
            disabled={disabled}
            title="Generate the expression with a visual editor"
          >
            <Icon name="edit_calendar" size={16} />
            <span>Generate</span>
          </button>
        </div>

        {humanReadable && !error && (
          <div className="cron-readable success">
            <Icon name="check" size={16} />
            {humanReadable}
          </div>
        )}

        {error && (
          <div className="cron-readable error">
            <Icon name="close" size={16} />
            {error}
          </div>
        )}
      </div>

      <small className="cron-hint">
        Format: second minute hour day month dayOfWeek
      </small>

      <div className="cron-presets">
        <label className="presets-label">Quick presets:</label>
        <div className="preset-buttons">
          {presets.map((preset) => (
            <button
              key={preset.value}
              type="button"
              className="btn-preset"
              onClick={() => handlePresetClick(preset.value)}
              disabled={disabled}
              title={preset.value}
            >
              {preset.label}
            </button>
          ))}
        </div>
      </div>

      {builderOpen && (
        <CronBuilderModal
          value={cronInput}
          onApply={(expression) => {
            commit(expression)
            setBuilderOpen(false)
          }}
          onClose={() => setBuilderOpen(false)}
        />
      )}

      <style jsx>{`
        .cron-expression-container {
          width: 100%;
        }

        .cron-input-wrapper {
          margin-bottom: 8px;
        }

        .cron-input-row {
          display: flex;
          align-items: stretch;
          gap: 8px;
        }

        .cron-input {
          flex: 1;
          min-width: 0;
          padding: 8px 12px;
          border: 1px solid #ddd;
          border-radius: 4px;
          font-family: 'Courier New', monospace;
          font-size: 14px;
        }

        .cron-input.error {
          border-color: #f56c6c;
        }

        .cron-input:disabled {
          opacity: 0.5;
          cursor: not-allowed;
        }

        .btn-cron-builder {
          display: inline-flex;
          align-items: center;
          gap: 6px;
          padding: 6px 12px;
          white-space: nowrap;
          background: var(--bg-secondary);
          border: 1px solid var(--border-color);
          color: var(--text-primary);
          border-radius: 4px;
          font-family: inherit;
          font-size: 13px;
          font-weight: 500;
          cursor: pointer;
          transition: all 0.2s;
        }

        .btn-cron-builder:hover:not(:disabled) {
          background: var(--bg-hover);
          border-color: var(--accent-color);
          color: var(--accent-color);
        }

        .btn-cron-builder:disabled {
          opacity: 0.5;
          cursor: not-allowed;
        }

        .cron-readable {
          margin-top: 8px;
          padding: 8px 12px;
          border-radius: 4px;
          font-size: 13px;
        }

        .cron-readable.success {
          background: #f0f9ff;
          color: #1890ff;
          border: 1px solid #d1e9ff;
        }

        .cron-readable.error {
          background: #fff2f0;
          color: #f56c6c;
          border: 1px solid #ffccc7;
        }

        .cron-hint {
          display: block;
          color: #888;
          font-size: 12px;
          margin-bottom: 12px;
        }

        .cron-presets {
          margin-top: 12px;
        }

        .presets-label {
          color: #333;
        }

        .preset-buttons {
          display: flex;
          flex-wrap: wrap;
          gap: 8px;

        }

       .btn-preset {
          padding: 6px 12px;
          font-size: 12px;
          background: var(--bg-secondary);
          border: 1px solid var(--border-color);
          color: var(--text-primary);
          border-radius: 4px;
          cursor: pointer;
          transition: all 0.2s;
        }

        .btn-preset:hover {
          background: var(--bg-hover);
          border-color: var(--accent-color);
          color: var(--accent-color);
        }

        .btn-preset:active {
          transform: scale(0.98);
        }

        .btn-preset:disabled {
          opacity: 0.5;
          cursor: not-allowed;
        }
      `}</style>
    </div>
  )
}

CronExpressionInput.propTypes = {
  value: PropTypes.string,
  onChange: PropTypes.func.isRequired,
  required: PropTypes.bool,
  disabled: PropTypes.bool
}
