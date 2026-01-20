import { useState, useEffect } from 'react'
import { Link } from 'react-router-dom'
import Icon from './Icon'
import { formatDateTime, formatDuration } from '../utils/dateUtils'
import './OccurrenceTable.css'

function OccurrenceTable({
  occurrences,
  loading,
  totalCount,
  currentPage,
  pageSize,
  filterStatus,
  onFilterChange,
  onPageChange,
  onPageSizeChange,
  onBulkDelete,
  showJobName = false
}) {
  const [currentTime, setCurrentTime] = useState(Date.now())
  const [selectedOccurrences, setSelectedOccurrences] = useState([])

  // Update current time every second for running occurrences
  useEffect(() => {
    const hasRunning = occurrences.some(occ => occ.status === 1)
    if (!hasRunning) return

    const interval = setInterval(() => {
      setCurrentTime(Date.now())
    }, 1000)

    return () => clearInterval(interval)
  }, [occurrences])

  // Clear selection when page changes or filter changes
  useEffect(() => {
    setSelectedOccurrences([])
  }, [currentPage, filterStatus])

  const calculateDuration = (occurrence) => {

    // Queued jobs don't have duration yet
    if (!occurrence?.startTime || occurrence.status === 0) return ''

    if (occurrence.durationMs !== null && occurrence.durationMs !== undefined) {
      const ms = occurrence.durationMs
      if (ms < 0) return '' // Invalid duration
      if (ms < 1000) {
        return `${Math.round(ms)}ms`
      }
      const seconds = Math.floor(ms / 1000)
      const minutes = Math.floor(seconds / 60)
      const hours = Math.floor(minutes / 60)
      if (hours > 0) return `${hours}h ${minutes % 60}m ${seconds % 60}s`
      if (minutes > 0) return `${minutes}m ${seconds % 60}s`
      return `${seconds}s`
    }

    if (occurrence.status === 1 && occurrence.startTime) {
      // Running - calculate from start time to now
      const start = new Date(occurrence.startTime).getTime()
      const durationMs = currentTime - start
      if (durationMs < 0) return '' // Invalid duration (future start time)
      if (durationMs < 1000) {
        return `${Math.round(durationMs)}ms`
      }
      return `${Math.floor(durationMs / 1000)}s`
    }

    // Use formatDuration for completed/failed/cancelled
    return formatDuration(occurrence.startTime, occurrence.endTime)
  }

  const getStatusBadge = (status) => {
    const statusMap = {
      0: { icon: 'schedule', label: 'Queued', className: 'status-queued' },
      1: { icon: 'sync', label: 'Running', className: 'status-running' },
      2: { icon: 'check_circle', label: 'Completed', className: 'status-success' },
      3: { icon: 'cancel', label: 'Failed', className: 'status-failed' },
      4: { icon: 'block', label: 'Cancelled', className: 'status-cancelled' },
      5: { icon: 'schedule', label: 'Timed Out', className: 'status-timeout' },
      6: { icon: 'help_outline', label: 'Unknown', className: 'status-unknown' },
    }

    const statusInfo = statusMap[status] || { icon: 'help', label: `Status ${status}`, className: 'status-unknown' }
    return (
      <span className={`occurrence-status ${statusInfo.className}`}>
        <Icon name={statusInfo.icon} size={16} />
        {statusInfo.label}
      </span>
    )
  }

  const totalPages = Math.ceil(totalCount / pageSize)

  const renderPagination = () => {
    if (totalPages <= 1 && totalCount <= pageSize) return null

    const maxVisiblePages = 5
    let startPage = Math.max(1, currentPage - Math.floor(maxVisiblePages / 2))
    let endPage = Math.min(totalPages, startPage + maxVisiblePages - 1)

    if (endPage - startPage + 1 < maxVisiblePages) {
      startPage = Math.max(1, endPage - maxVisiblePages + 1)
    }

    return (
      <div className="pagination-container">
        <div className="pagination">
          <button
            className="btn btn-sm"
            onClick={() => onPageChange(1)}
            disabled={currentPage === 1}
          >
            <Icon name="first_page" size={18} />
          </button>
          <button
            className="btn btn-sm"
            onClick={() => onPageChange(currentPage - 1)}
            disabled={currentPage === 1}
          >
            <Icon name="chevron_left" size={18} />
          </button>

          {startPage > 1 && <span className="page-ellipsis">...</span>}

          {Array.from({ length: endPage - startPage + 1 }, (_, i) => startPage + i).map(page => (
            <button
              key={page}
              className={'btn btn-sm' + (page === currentPage ? ' btn-primary' : '')}
              onClick={() => onPageChange(page)}
            >
              {page}
            </button>
          ))}

          {endPage < totalPages && <span className="page-ellipsis">...</span>}

          <button
            className="btn btn-sm"
            onClick={() => onPageChange(currentPage + 1)}
            disabled={currentPage === totalPages}
          >
            <Icon name="chevron_right" size={18} />
          </button>
          <button
            className="btn btn-sm"
            onClick={() => onPageChange(totalPages)}
            disabled={currentPage === totalPages}
          >
            <Icon name="last_page" size={18} />
          </button>

          <span className="page-info">
            Page {currentPage} of {totalPages} ({totalCount} total)
          </span>
        </div>

        <div className="page-size-selector">
          <label htmlFor="pageSize">Rows per page:</label>
          <select
            id="pageSize"
            value={pageSize}
            onChange={(e) => onPageSizeChange(parseInt(e.target.value))}
            className="page-size-select"
          >
            <option value={10}>10</option>
            <option value={20}>20</option>
            <option value={50}>50</option>
            <option value={100}>100</option>
            <option value={500}>500</option>
            <option value={1000}>1000</option>
          </select>
        </div>
      </div>
    )
  }

  const handleSelectAll = (e) => {
    if (e.target.checked) {
      // Only select occurrences that can be deleted (not running or queued)
      const selectableOccurrences = occurrences
        .filter(occ => occ.status !== 0 && occ.status !== 1)
        .map(occ => occ.id)
      setSelectedOccurrences(selectableOccurrences)
    } else {
      setSelectedOccurrences([])
    }
  }

  const handleSelectOccurrence = (occurrenceId) => {
    setSelectedOccurrences(prev =>
      prev.includes(occurrenceId)
        ? prev.filter(id => id !== occurrenceId)
        : [...prev, occurrenceId]
    )
  }

  const isOccurrenceSelectable = (occurrence) => {
    // Can only delete completed, failed, cancelled, timed out occurrences
    return occurrence.status !== 0 && occurrence.status !== 1
  }

  const handleBulkDelete = () => {
    if (onBulkDelete && selectedOccurrences.length > 0) {
      onBulkDelete(selectedOccurrences)
      setSelectedOccurrences([])
    }
  }

  if (loading) return <div className="loading">Loading occurrences...</div>

  return (
    <div className="occurrence-table-container">
      {/* Status Filter Chips */}
      <div className="status-filters-header">
        <div className="status-filters">
          <button
            className={`status-chip ${filterStatus === null ? 'active' : ''}`}
            onClick={() => onFilterChange(null)}
          >
            All
          </button>
          <button
            className={`status-chip queued ${filterStatus === 0 ? 'active' : ''}`}
            onClick={() => onFilterChange(filterStatus === 0 ? null : 0)}
          >
            <Icon name="schedule" size={16} />
            Queued
          </button>
          <button
            className={`status-chip running ${filterStatus === 1 ? 'active' : ''}`}
            onClick={() => onFilterChange(filterStatus === 1 ? null : 1)}
          >
            <Icon name="sync" size={16} />
            Running
          </button>
          <button
            className={`status-chip success ${filterStatus === 2 ? 'active' : ''}`}
            onClick={() => onFilterChange(filterStatus === 2 ? null : 2)}
          >
            <Icon name="check_circle" size={16} />
            Completed
          </button>
          <button
            className={`status-chip failed ${filterStatus === 3 ? 'active' : ''}`}
            onClick={() => onFilterChange(filterStatus === 3 ? null : 3)}
          >
            <Icon name="cancel" size={16} />
            Failed
          </button>
          <button
            className={`status-chip cancelled ${filterStatus === 4 ? 'active' : ''}`}
            onClick={() => onFilterChange(filterStatus === 4 ? null : 4)}
          >
            <Icon name="block" size={16} />
            Cancelled
          </button>
          <button
            className={`status-chip timeout ${filterStatus === 5 ? 'active' : ''}`}
            onClick={() => onFilterChange(filterStatus === 5 ? null : 5)}
          >
            <Icon name="schedule" size={16} />
            Timed Out
          </button>
          <button
            className={`status-chip unknown ${filterStatus === 6 ? 'active' : ''}`}
            onClick={() => onFilterChange(filterStatus === 6 ? null : 6)}
          >
            <Icon name="help_outline" size={16} />
            Unknown
          </button>
        </div>

        {selectedOccurrences.length > 0 && onBulkDelete && (
          <button
            className="bulk-delete-btn"
            onClick={handleBulkDelete}
          >
            <Icon name="delete" size={20} />
            Delete Selected ({selectedOccurrences.length})
          </button>
        )}
      </div>

      {occurrences.length === 0 ? (
        <div className="empty-state">
          <p>No occurrences found</p>
        </div>
      ) : (
        <>

          <table className="occurrence-table">
            <thead>
              <tr>
                <th className="checkbox-column">
                  <input
                    type="checkbox"
                    checked={selectedOccurrences.length > 0 && selectedOccurrences.length === occurrences.filter(isOccurrenceSelectable).length}
                    onChange={handleSelectAll}
                    disabled={occurrences.filter(isOccurrenceSelectable).length === 0}
                  />
                </th>
                {showJobName && <th>Job Name</th>}
                <th>Created At</th>
                <th>Started At</th>
                <th>Completed At</th>
                <th>Duration</th>
                <th>Worker</th>
                <th>Status</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {occurrences.map((occurrence) => (
                <tr
                  key={occurrence.id}
                  className={occurrence.status === 1 ? 'occurrence-running' : ''}
                  onClick={() => window.location.href = `/occurrences/${occurrence.id}`}
                  style={{ cursor: 'pointer' }}
                >
                  <td className="checkbox-column" onClick={(e) => e.stopPropagation()}>
                    <input
                      type="checkbox"
                      checked={selectedOccurrences.includes(occurrence.id)}
                      onChange={() => handleSelectOccurrence(occurrence.id)}
                      disabled={!isOccurrenceSelectable(occurrence)}
                    />
                  </td>
                  {showJobName && (
                    <td onClick={(e) => e.stopPropagation()}>
                      {occurrence.jobId ? (
                        <Link to={`/jobs/${occurrence.jobId}`} className="job-link">
                          {occurrence.jobDisplayName || occurrence.jobName || 'Unknown Job'}
                        </Link>
                      ) : (
                        <span>{occurrence.jobDisplayName || occurrence.jobName || 'Unknown Job'}</span>
                      )}
                    </td>
                  )}
                  <td>{occurrence.createdAt ? formatDateTime(occurrence.createdAt) : '-'}</td>
                  <td>
                    {occurrence.startTime ? formatDateTime(occurrence.startTime) : '-'}
                  </td>
                  <td>{occurrence.endTime ? formatDateTime(occurrence.endTime) : '-'}</td>
                  <td>{calculateDuration(occurrence)}</td>
                  <td>
                    <span className="worker-badge">{occurrence.workerId || '-'}</span>
                  </td>
                  <td>{getStatusBadge(occurrence.status)}</td>
                  <td onClick={(e) => e.stopPropagation()}>
                    <Link
                      to={`/occurrences/${occurrence.id}`}
                      className="btn btn-sm"
                    >
                      <Icon name="visibility" size={16} />
                      View Details
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {renderPagination()}
        </>
      )}
    </div>
  )
}

export default OccurrenceTable
