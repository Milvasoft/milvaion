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
