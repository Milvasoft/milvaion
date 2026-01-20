import cronstrue from 'cronstrue'

export default function CronDisplay({ expression, showTooltip = true }) {
  if (!expression) return <span className="cron-none">One-time job</span>

  try {
    const readable = cronstrue.toString(expression)
    
    if (showTooltip) {
      return (
        <div className="cron-display-wrapper">
          <span className="cron-readable">{readable}</span>
          <span className="cron-expression" title={expression}>
            ({expression})
          </span>
        </div>
      )
    }
    
    return <span className="cron-readable">{readable}</span>
  } catch (error) {
    return (
      <span className="cron-error" title={`Invalid: ${expression}`}>
        Invalid cron expression
      </span>
    )
  }
}

// CSS can be in your global styles or inline
const styles = `
  .cron-display-wrapper {
    display: flex;
    align-items: center;
    gap: 8px;
  }

  .cron-readable {
    color: #1890ff;
    font-weight: 500;
  }

  .cron-expression {
    font-family: 'Courier New', monospace;
    font-size: 12px;
    color: #888;
    cursor: help;
  }

  .cron-none {
    color: #999;
    font-style: italic;
  }

  .cron-error {
    color: #f56c6c;
    cursor: help;
  }
`
