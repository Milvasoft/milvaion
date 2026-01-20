import './Icon.css'

function Icon({ name, className = '', size = 24, filled = false, style = {} }) {
  const iconClass = filled
    ? 'material-symbols-outlined filled'
    : 'material-symbols-outlined'

  return (
    <span
      className={`icon ${iconClass} ${className}`}
      style={{ fontSize: `${size}px`, ...style }}
    >
      {name}
    </span>
  )
}

export default Icon
