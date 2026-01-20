import { useState, useEffect } from 'react'
import cronstrue from 'cronstrue'
import Icon from './Icon'

export default function CronExpressionInput({ value, onChange, required = false }) {
  const [cronInput, setCronInput] = useState(value || '')
  const [humanReadable, setHumanReadable] = useState('')
  const [error, setError] = useState('')

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
    }
  }, [value])

  const handleChange = (e) => {
    const newValue = e.target.value
    setCronInput(newValue)

    // Create event with correct name attribute
    const syntheticEvent = {
      target: {
        name: 'cronExpression',
        value: newValue
      }
    }
    onChange(syntheticEvent) // Pass through to parent with correct name

    // Parse cron expression
    if (newValue) {
      try {
        const readable = cronstrue.toString(newValue)
        setHumanReadable(readable)
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

  const handlePresetClick = (presetValue) => {
    setCronInput(presetValue)

    // Create event with correct name attribute
    const syntheticEvent = {
      target: {
        name: 'cronExpression',
        value: presetValue
      }
    }
    onChange(syntheticEvent)

    try {
      setHumanReadable(cronstrue.toString(presetValue))
      setError('')
    } catch (err) {
      setError('Invalid cron expression')
    }
  }

  return (
    <div className="cron-expression-container">
      <div className="cron-input-wrapper">
        <input
          type="text"
          name="cronExpression"
          value={cronInput}
          onChange={handleChange}
          placeholder="e.g., 0 0 9 * * * (every day at 9 AM)"
          required={required}
          className={error ? 'cron-input error' : 'cron-input'}
        />

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
              title={preset.value}
            >
              {preset.label}
            </button>
          ))}
        </div>
      </div>

      <style jsx>{`
        .cron-expression-container {
          width: 100%;
        }

        .cron-input-wrapper {
          margin-bottom: 8px;
        }

        .cron-input {
          width: 100%;
          padding: 8px 12px;
          border: 1px solid #ddd;
          border-radius: 4px;
          font-family: 'Courier New', monospace;
          font-size: 14px;
        }

        .cron-input.error {
          border-color: #f56c6c;
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
      `}</style>
    </div>
  )
}
