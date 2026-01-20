import { useState, useEffect } from 'react'
import PropTypes from 'prop-types'
import Icon from './Icon'
import './AutoRefreshIndicator.css'

function AutoRefreshIndicator({
  enabled,
  onToggle,
  lastRefreshTime,
  intervalSeconds = 10
}) {
  const [timeAgo, setTimeAgo] = useState('')

  useEffect(() => {
    const updateTimeAgo = () => {
      if (!lastRefreshTime) {
        setTimeAgo('Never')
        return
      }

      const now = new Date()
      const lastRefresh = new Date(lastRefreshTime)
      const diffInSeconds = Math.floor((now - lastRefresh) / 1000)

      if (diffInSeconds < 3) {
        setTimeAgo('Just now')
      } else if (diffInSeconds < 60) {
        setTimeAgo(`${diffInSeconds}s ago`)
      } else if (diffInSeconds < 3600) {
        const minutes = Math.floor(diffInSeconds / 60)
        setTimeAgo(`${minutes}m ago`)
      } else {
        const hours = Math.floor(diffInSeconds / 3600)
        setTimeAgo(`${hours}h ago`)
      }
    }

    updateTimeAgo()
    const interval = setInterval(updateTimeAgo, 1000)

    return () => clearInterval(interval)
  }, [lastRefreshTime])

  return (
    <div className="auto-refresh-indicator">
      <div className="refresh-info">
        <div className="refresh-time">
          <Icon name="schedule" size={14} />
          <span className="time-text">{timeAgo}</span>
        </div>
        {enabled && (
          <div className="next-refresh">
            {intervalSeconds}s
          </div>
        )}
      </div>

      <button
        className={`refresh-toggle ${enabled ? 'active' : 'inactive'}`}
        onClick={onToggle}
        title={enabled ? 'Disable auto-refresh' : 'Enable auto-refresh'}
        aria-label={enabled ? 'Disable auto-refresh' : 'Enable auto-refresh'}
      >
        <Icon name={enabled ? 'sync' : 'sync_disabled'} size={14} />
        <span className="toggle-label">
          Auto-refresh {enabled ? 'ON' : 'OFF'}
        </span>
      </button>
    </div>
  )
}

AutoRefreshIndicator.propTypes = {
  enabled: PropTypes.bool.isRequired,
  onToggle: PropTypes.func.isRequired,
  lastRefreshTime: PropTypes.oneOfType([
    PropTypes.string,
    PropTypes.instanceOf(Date)
  ]),
  intervalSeconds: PropTypes.number
}

export default AutoRefreshIndicator
