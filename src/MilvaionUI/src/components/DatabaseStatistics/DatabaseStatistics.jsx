import { useState, useEffect } from 'react'
import api from '../../services/api'
import Icon from '../../components/Icon'
import './DatabaseStatistics.css'

function DatabaseStatistics() {
const [tableSizes, setTableSizes] = useState(null)
const [indexEfficiency, setIndexEfficiency] = useState(null)
const [cacheHitRatio, setCacheHitRatio] = useState(null)
const [tableBloat, setTableBloat] = useState(null)
const [totalDbSize, setTotalDbSize] = useState({ bytes: 0, formatted: 'N/A' })

const [loading, setLoading] = useState({
  tables: true,
  indexes: true,
  cache: true,
  bloat: true
})

const [errors, setErrors] = useState({
  tables: null,
  indexes: null,
  cache: null,
  bloat: null
})

useEffect(() => {
  loadAllStatistics()
}, [])

const loadAllStatistics = () => {
  loadTableSizes()
  loadIndexEfficiency()
  loadCacheHitRatio()
  loadTableBloat()
}

const loadTableSizes = async () => {
  try {
    setLoading(prev => ({ ...prev, tables: true }))
    setErrors(prev => ({ ...prev, tables: null }))
    const response = await api.get('/admin/database-statistics/tables')
    const data = response?.data || response
    setTableSizes(data)

    // Calculate total size
    if (data && data.length > 0) {
      const totalBytes = data.reduce((sum, t) => sum + t.sizeBytes, 0)
      setTotalDbSize({ bytes: totalBytes, formatted: formatBytes(totalBytes) })
    }
  } catch (err) {
    console.error('Failed to load table sizes:', err)
    setErrors(prev => ({ ...prev, tables: 'Failed to load table sizes' }))
  } finally {
    setLoading(prev => ({ ...prev, tables: false }))
  }
}

const loadIndexEfficiency = async () => {
  try {
    setLoading(prev => ({ ...prev, indexes: true }))
    setErrors(prev => ({ ...prev, indexes: null }))
    const response = await api.get('/admin/database-statistics/indexes')
    const data = response?.data || response
    setIndexEfficiency(data)
  } catch (err) {
    console.error('Failed to load index efficiency:', err)
    setErrors(prev => ({ ...prev, indexes: 'Failed to load index efficiency' }))
  } finally {
    setLoading(prev => ({ ...prev, indexes: false }))
  }
}

const loadCacheHitRatio = async () => {
  try {
    setLoading(prev => ({ ...prev, cache: true }))
    setErrors(prev => ({ ...prev, cache: null }))
    const response = await api.get('/admin/database-statistics/cache')
    const data = response?.data || response
    setCacheHitRatio(data)
  } catch (err) {
    console.error('Failed to load cache hit ratio:', err)
    setErrors(prev => ({ ...prev, cache: 'Failed to load cache hit ratio' }))
  } finally {
    setLoading(prev => ({ ...prev, cache: false }))
  }
}

const loadTableBloat = async () => {
  try {
    setLoading(prev => ({ ...prev, bloat: true }))
    setErrors(prev => ({ ...prev, bloat: null }))
    const response = await api.get('/admin/database-statistics/bloat')
    const data = response?.data || response
    setTableBloat(data)
  } catch (err) {
    console.error('Failed to load table bloat:', err)
    setErrors(prev => ({ ...prev, bloat: 'Failed to load table bloat' }))
  } finally {
    setLoading(prev => ({ ...prev, bloat: false }))
  }
}

  const formatBytes = (bytes) => {
    if (!bytes) return '0 B'
    const suffixes = ['B', 'KB', 'MB', 'GB', 'TB']
    const k = 1024
    const i = Math.floor(Math.log(bytes) / Math.log(k))
    return `${(bytes / Math.pow(k, i)).toFixed(2)} ${suffixes[i]}`
  }

  const getHealthBadgeClass = (status) => {
    switch (status?.toLowerCase()) {
      case 'excellent': return 'badge-success'
      case 'good': return 'badge-info'
      case 'normal': return 'badge-secondary'
      case 'warning': return 'badge-warning'
      case 'poor': return 'badge-warning'
      case 'critical': return 'badge-error'
      case 'unused': return 'badge-error'
      case 'rarely used': return 'badge-warning'
      default: return 'badge-secondary'
    }
  }

  const isLoading = loading.tables || loading.indexes || loading.cache || loading.bloat
  const hasAnyError = errors.tables || errors.indexes || errors.cache || errors.bloat

  if (isLoading) {
    return (
      <div className="stats-body">
                <div className="card-content">
          <div className="loading-spinner">Loading...</div>
        </div>
      </div>
    )
  }

  return (
    <div className="stats-body">
      {/* Total Database Size */}
      <div className="db-stat-summary">
        <div className="stat-item-large">
          <Icon name="database" size={32} className="stat-icon-large" />
          <div>
            <div className="stat-value-large">{totalDbSize.formatted}</div>
            <div className="stat-label">Total Database Size</div>
          </div>
        </div>
      </div>

      <div className="card-content">

        {/* Cache Hit Ratio */}
        {loading.cache ? (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="speed" size={18} />
              Cache Hit Ratio
            </h4>
            <div className="loading-spinner">Loading cache statistics...</div>
          </div>
        ) : errors.cache ? (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="speed" size={18} />
              Cache Hit Ratio
            </h4>
            <div className="error-message">{errors.cache}</div>
          </div>
        ) : cacheHitRatio && (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="speed" size={18} />
              Cache Hit Ratio
              <span className={`badge ${getHealthBadgeClass(cacheHitRatio.status)}`}>
                {cacheHitRatio.status}
              </span>
            </h4>
            <div className="cache-stats">
              <div className="cache-metric-row">
                <div className="cache-metric">
                  <div className="metric-label">Overall Cache Hit</div>
                  <div className="metric-value">{cacheHitRatio.hitRatioPercentage.toFixed(2)}%</div>
                  <div className="progress-bar">
                    <div
                      className="progress-fill progress-success"
                      style={{ width: `${cacheHitRatio.hitRatioPercentage}%` }}
                    />
                  </div>
                </div>
                <div className="cache-metric">
                  <div className="metric-label">Index Cache Hit</div>
                  <div className="metric-value">{cacheHitRatio.indexHitRatioPercentage.toFixed(2)}%</div>
                  <div className="progress-bar">
                    <div
                      className="progress-fill progress-info"
                      style={{ width: `${cacheHitRatio.indexHitRatioPercentage}%` }}
                    />
                  </div>
                </div>
                <div className="cache-metric">
                  <div className="metric-label">Table Cache Hit</div>
                  <div className="metric-value">{cacheHitRatio.tableHitRatioPercentage.toFixed(2)}%</div>
                  <div className="progress-bar">
                    <div
                      className="progress-fill progress-warning"
                      style={{ width: `${cacheHitRatio.tableHitRatioPercentage}%` }}
                    />
                  </div>
                </div>
              </div>
              <div className="cache-details">
                <div className="detail-item">
                  <Icon name="check_circle" size={16} />
                  <span>Cache Reads: {cacheHitRatio.cacheReads.toLocaleString()}</span>
                </div>
                <div className="detail-item">
                  <Icon name="storage" size={16} />
                  <span>Disk Reads: {cacheHitRatio.diskReads.toLocaleString()}</span>
                </div>
              </div>
              {cacheHitRatio.recommendation && (
                <div className="recommendation-box">
                  <Icon name="lightbulb" size={16} />
                  {cacheHitRatio.recommendation}
                </div>
              )}
            </div>
          </div>
        )}

        {/* Index Efficiency */}
        {loading.indexes ? (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="segment" size={18} />
              Index Efficiency
            </h4>
            <div className="loading-spinner">Loading index statistics...</div>
          </div>
        ) : errors.indexes ? (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="segment" size={18} />
              Index Efficiency
            </h4>
            <div className="error-message">{errors.indexes}</div>
          </div>
        ) : indexEfficiency && indexEfficiency.indexes?.length > 0 && (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="segment" size={18} />
              Index Efficiency
              {indexEfficiency.totalWastedBytes > 0 && (
                <span className="badge badge-warning">
                  {indexEfficiency.totalWastedSpace} wasted
                </span>
              )}
            </h4>
            <div className="index-list">
              {indexEfficiency.indexes.map((index, i) => (
                <div key={i} className="index-item">
                  <div className="index-header">
                    <span className="index-name" title={index.indexName}>
                      {index.tableName.split('.')[1] || index.tableName}
                      <span className="index-name-small">/{index.indexName}</span>
                    </span>
                    <span className={`badge ${getHealthBadgeClass(index.status)}`}>
                      {index.status}
                    </span>
                  </div>
                  <div className="index-stats-row">
                    <div className="index-stat">
                      <Icon name="storage" size={14} />
                      <span>{index.size}</span>
                    </div>
                    <div className="index-stat">
                      <Icon name="search" size={14} />
                      <span>{index.scans.toLocaleString()} scans</span>
                    </div>
                    <div className="index-stat">
                      <Icon name="analytics" size={14} />
                      <span>{index.efficiencyScore.toFixed(0)}% efficient</span>
                    </div>
                  </div>
                </div>
              ))}
            </div>
            {indexEfficiency.recommendation && (
              <div className="recommendation-box">
                <Icon name="lightbulb" size={16} />
                {indexEfficiency.recommendation}
              </div>
            )}
          </div>
        )}

        {/* Table Bloat */}
        {loading.bloat ? (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="warning" size={18} />
              Table Bloat Detection
            </h4>
            <div className="loading-spinner">Loading bloat statistics...</div>
          </div>
        ) : errors.bloat ? (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="warning" size={18} />
              Table Bloat Detection
            </h4>
            <div className="error-message">{errors.bloat}</div>
          </div>
        ) : tableBloat && tableBloat.bloatedTables?.length > 0 && (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="warning" size={18} />
              Table Bloat Detection
              {tableBloat.totalWastedBytes > 0 && (
                <span className="badge badge-error">
                  {tableBloat.totalWastedSpace} wasted
                </span>
              )}
            </h4>
            <div className="bloat-list">
              {tableBloat.bloatedTables.map((table, i) => (
                <div key={i} className={`bloat-item bloat-${table.status.toLowerCase()}`}>
                  <div className="bloat-header">
                    <span className="bloat-table-name">{table.tableName.split('.')[1] || table.tableName}</span>
                    <span className={`badge ${getHealthBadgeClass(table.status)}`}>
                      {table.status}
                    </span>
                  </div>
                  <div className="bloat-stats-row">
                    <div className="bloat-stat">
                      <span className="label">Bloat:</span>
                      <span className="value">{table.bloatPercentage.toFixed(1)}%</span>
                    </div>
                    <div className="bloat-stat">
                      <span className="label">Wasted:</span>
                      <span className="value">{table.wastedSpace}</span>
                    </div>
                    <div className="bloat-stat">
                      <span className="label">Dead Tuples:</span>
                      <span className="value">{table.deadTuples.toLocaleString()}</span>
                    </div>
                  </div>
                  <div className="bloat-meta">
                    {table.lastVacuum && (
                      <span className="meta-item">
                        <Icon name="cleaning_services" size={14} />
                        Last VACUUM: {new Date(table.lastVacuum).toLocaleString()}
                      </span>
                    )}
                    {table.lastAnalyze && (
                      <span className="meta-item">
                        <Icon name="analytics" size={14} />
                        Last ANALYZE: {new Date(table.lastAnalyze).toLocaleString()}
                      </span>
                    )}
                  </div>
                </div>
              ))}
            </div>
            {tableBloat.recommendation && (
              <div className="recommendation-box recommendation-urgent">
                <Icon name="priority_high" size={16} />
                {tableBloat.recommendation}
              </div>
            )}
          </div>
        )}

        {/* Top Tables */}
        {loading.tables ? (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="table_chart" size={18} />
              Top Tables by Size
            </h4>
            <div className="loading-spinner">Loading table sizes...</div>
          </div>
        ) : errors.tables ? (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="table_chart" size={18} />
              Top Tables by Size
            </h4>
            <div className="error-message">{errors.tables}</div>
          </div>
        ) : tableSizes && (
          <div className="db-section">
            <h4 className="section-title">
              <Icon name="table_chart" size={18} />
              Top Tables by Size
            </h4>
            <div className="table-list">
              {tableSizes.slice(0, 5).map((table, index) => (
                <div key={index} className="table-item">
                  <div className="table-info">
                    <span className="table-name">{table.tableName}</span>
                    <span className="table-size">{table.size}</span>
                  </div>
                  <div className="progress-bar">
                    <div
                      className="progress-fill"
                      style={{ width: `${table.percentage}%` }}
                      title={`${table.percentage.toFixed(1)}% of total`}
                    />
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

export default DatabaseStatistics
