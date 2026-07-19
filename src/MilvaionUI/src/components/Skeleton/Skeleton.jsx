import PropTypes from 'prop-types'
import './Skeleton.css'

function Skeleton({
  variant = 'text',
  width,
  height,
  count = 1,
  className = '',
  animation = 'pulse'
}) {
  const style = {
    ...(width && { width: typeof width === 'number' ? `${width}px` : width }),
    ...(height && { height: typeof height === 'number' ? `${height}px` : height }),
  }

  const items = Array.from({ length: count }, (_, i) => (
    <div
      key={i}
      className={`skeleton skeleton-${variant} skeleton-${animation} ${className}`}
      style={style}
      aria-hidden="true"
    />
  ))

  return count === 1 ? items[0] : <>{items}</>
}

Skeleton.propTypes = {
  variant: PropTypes.oneOf(['text', 'circular', 'rectangular', 'card', 'table-row', 'stat-card']),
  width: PropTypes.oneOfType([PropTypes.number, PropTypes.string]),
  height: PropTypes.oneOfType([PropTypes.number, PropTypes.string]),
  count: PropTypes.number,
  className: PropTypes.string,
  animation: PropTypes.oneOf(['pulse', 'wave', 'none'])
}

export function SkeletonStatCard() {
  return (
    <div className="skeleton-stat-card">
      <div className="skeleton-stat-icon">
        <Skeleton variant="circular" width={32} height={32} />
      </div>
      <div className="skeleton-stat-content">
        <Skeleton variant="text" width={60} height={24} />
        <Skeleton variant="text" width={80} height={14} />
      </div>
    </div>
  )
}

export function SkeletonTableRow({ columns = 5 }) {
  return (
    <tr className="skeleton-table-row">
      {Array.from({ length: columns }, (_, i) => (
        <td key={i}>
          <Skeleton variant="text" width={i === 0 ? '80%' : '60%'} height={16} />
        </td>
      ))}
    </tr>
  )
}

SkeletonTableRow.propTypes = {
  columns: PropTypes.number
}

export function SkeletonTable({ rows = 5, columns = 5 }) {
  return (
    <div className="skeleton-table-container">
      <table className="skeleton-table">
        <thead>
          <tr>
            {Array.from({ length: columns }, (_, i) => (
              <th key={i}>
                <Skeleton variant="text" width="70%" height={14} />
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {Array.from({ length: rows }, (_, i) => (
            <SkeletonTableRow key={i} columns={columns} />
          ))}
        </tbody>
      </table>
    </div>
  )
}

SkeletonTable.propTypes = {
  rows: PropTypes.number,
  columns: PropTypes.number
}

export function SkeletonCard({ lines = 3 }) {
  return (
    <div className="skeleton-card">
      <div className="skeleton-card-header">
        <Skeleton variant="text" width="40%" height={20} />
      </div>
      <div className="skeleton-card-body">
        {Array.from({ length: lines }, (_, i) => (
          <Skeleton
            key={i}
            variant="text"
            width={i === lines - 1 ? '60%' : '100%'}
            height={14}
          />
        ))}
      </div>
    </div>
  )
}

SkeletonCard.propTypes = {
  lines: PropTypes.number
}

export function SkeletonDashboard() {
  return (
    <div className="skeleton-dashboard">
      {/* Quick Stats */}
      <div className="skeleton-quick-stats">
        <SkeletonStatCard />
        <SkeletonStatCard />
        <SkeletonStatCard />
        <SkeletonStatCard />
      </div>

      {/* Main Grid */}
      <div className="skeleton-dashboard-grid">
        <SkeletonCard lines={4} />
        <SkeletonCard lines={4} />
        <SkeletonCard lines={6} />
      </div>
    </div>
  )
}

export function SkeletonJobList({ rows = 10 }) {
  return (
    <div className="skeleton-job-list">
      <div className="skeleton-toolbar">
        <Skeleton variant="rectangular" width={200} height={36} />
        <Skeleton variant="rectangular" width={120} height={36} />
      </div>

      <SkeletonTable rows={rows} columns={6} />
    </div>
  )
}

SkeletonJobList.propTypes = {
  rows: PropTypes.number
}

export function SkeletonDetail() {
  return (
    <div className="skeleton-detail">
      <div className="skeleton-detail-header">
        <Skeleton variant="text" width="30%" height={28} />
        <Skeleton variant="text" width="20%" height={20} />
      </div>
      <div className="skeleton-detail-body">
        <SkeletonCard lines={5} />
        <SkeletonCard lines={3} />
      </div>
    </div>
  )
}

export default Skeleton

/**
 * Kart ızgarası. Workflows, Tags, Workers ve Reports gibi tablo yerine kart
 * listeleyen sayfalar için.
 */
export function SkeletonGrid({ cards = 6, lines = 3 }) {
  return (
    <div className="skeleton-grid">
      {Array.from({ length: cards }, (_, i) => (
        <SkeletonCard key={i} lines={lines} />
      ))}
    </div>
  )
}

SkeletonGrid.propTypes = {
  cards: PropTypes.number,
  lines: PropTypes.number
}

/**
 * Grafik alanı.
 *
 * Çubuklar rastgele değil sabit bir dizi: her yüklemede farklı yükseklikler
 * çizmek, veri geliyormuş izlenimi veriyor ve bakışı gereksiz meşgul ediyor.
 */
export function SkeletonChart({ bars = 12, height = 260 }) {
  const heights = [45, 70, 55, 85, 60, 40, 75, 50, 90, 65, 35, 80]

  return (
    <div className="skeleton-chart" style={{ height }}>
      {Array.from({ length: bars }, (_, i) => (
        <div
          key={i}
          className="skeleton-chart-bar skeleton-pulse"
          style={{ height: `${heights[i % heights.length]}%` }}
        />
      ))}
    </div>
  )
}

SkeletonChart.propTypes = {
  bars: PropTypes.number,
  height: PropTypes.number
}

/**
 * Form iskeleti: etiket ve alan çiftleri.
 */
export function SkeletonForm({ fields = 6 }) {
  return (
    <div className="skeleton-form">
      {Array.from({ length: fields }, (_, i) => (
        <div key={i} className="skeleton-form-field">
          <Skeleton variant="text" width="25%" height={12} />
          <Skeleton variant="rectangular" width="100%" height={38} />
        </div>
      ))}
    </div>
  )
}

SkeletonForm.propTypes = {
  fields: PropTypes.number
}
