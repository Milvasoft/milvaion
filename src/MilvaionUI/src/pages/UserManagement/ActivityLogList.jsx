import { useState, useEffect, useCallback } from 'react'
import activityLogService from '../../services/activityLogService'
import Icon from '../../components/Icon'
import { SkeletonTable } from '../../components/Skeleton'
import { getApiErrorMessage } from '../../utils/errorUtils'
import './ActivityLogList.css'

const activityLabels = {
  0: 'Create User',
  1: 'Update User',
  2: 'Delete User',
  3: 'Create Role',
  4: 'Update Role',
  5: 'Delete Role',
  6: 'Create Namespace',
  7: 'Update Namespace',
  8: 'Delete Namespace',
  9: 'Create Resource Group',
  10: 'Update Resource Group',
  11: 'Delete Resource Group',
  12: 'Create Content',
  13: 'Update Content',
  14: 'Delete Content',
  15: 'Update Languages',
  16: 'Create Scheduled Job',
  17: 'Update Scheduled Job',
  18: 'Delete Scheduled Job',
  19: 'Update Failed Occurrence',
  20: 'Delete Failed Occurrence',
  21: 'Delete Job Occurrence',
}

const activityIcons = {
  0: 'person_add', 1: 'edit', 2: 'person_remove',
  3: 'add_circle', 4: 'edit', 5: 'remove_circle',
  6: 'create_new_folder', 7: 'edit', 8: 'folder_delete',
  9: 'library_add', 10: 'edit', 11: 'delete',
  12: 'note_add', 13: 'edit_note', 14: 'delete',
  15: 'translate', 16: 'schedule', 17: 'edit', 18: 'delete',
  19: 'edit', 20: 'delete', 21: 'delete',
}

const getActivityColor = (activity) => {
  const label = activityLabels[activity] || ''
  if (label.startsWith('Create')) return 'activity-create'
  if (label.startsWith('Update')) return 'activity-update'
  if (label.startsWith('Delete')) return 'activity-delete'
  return ''
}

function ActivityLogList() {
  const [logs, setLogs] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [debouncedSearchTerm, setDebouncedSearchTerm] = useState('')
  const [currentPage, setCurrentPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [totalCount, setTotalCount] = useState(0)

  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearchTerm(searchTerm)
      setCurrentPage(1)
    }, 500)
    return () => clearTimeout(timer)
  }, [searchTerm])

  const loadLogs = useCallback(async (showLoading = false) => {
    try {
      if (showLoading) setLoading(true)
      setError(null)

      const requestBody = {
        pageNumber: currentPage,
        rowCount: pageSize
      }

      if (debouncedSearchTerm) {
        requestBody.filtering = {
          criterias: [
            {
              filterBy: 'UserName',
              value: debouncedSearchTerm,
              type: 1 // Contains
            }
          ]
        }
      }

      const response = await activityLogService.getAll(requestBody)
      const data = response?.data?.data || response?.data || []
      const total = response?.data?.totalDataCount || response?.totalDataCount || 0

      setLogs(data)
      setTotalCount(total)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load activity logs'))
      console.error(err)
    } finally {
      if (showLoading) setLoading(false)
    }
  }, [currentPage, pageSize, debouncedSearchTerm])

  useEffect(() => {
    loadLogs(true)
  }, [loadLogs])

  const formatDate = (dateStr) => {
    if (!dateStr) return '—'
    const date = new Date(dateStr)
    return date.toLocaleString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
      hour: '2-digit', minute: '2-digit', second: '2-digit'
    })
  }

  const totalPages = Math.ceil(totalCount / pageSize)

  const handlePageChange = (newPage) => {
    if (newPage >= 1 && newPage <= totalPages) {
      setCurrentPage(newPage)
    }
  }

  if (loading) return <SkeletonTable rows={pageSize} columns={4} />
  if (error) return <div className="error">{error}</div>

  return (
    <div className="page activity-log-list">
      <div className="page-header">
        <h1>
          <Icon name="history" size={28} />
          <span>Activity Logs</span>
          <span>({totalCount})</span>
        </h1>
      </div>

      <div className="search-section">
        <div className="search-box">
          <input
            type="text"
            placeholder="Search by username..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="search-input"
          />
          {searchTerm && (
            <button onClick={() => setSearchTerm('')} className="clear-search-btn" title="Clear search">
              <Icon name="close" size={16} />
            </button>
          )}
        </div>
      </div>

      {logs.length === 0 ? (
        <div className="empty-state-card">
          <div className="empty-icon">
            <Icon name="history" size={64} />
          </div>
          <h3>No Activity Logs</h3>
          <p>{searchTerm ? 'No logs match your search.' : 'No user activity has been recorded yet.'}</p>
        </div>
      ) : (
        <div className="table-container">
          <table className="data-table">
            <thead>
              <tr>
                <th>ID</th>
                <th>User</th>
                <th>Activity</th>
                <th>Date</th>
              </tr>
            </thead>
            <tbody>
              {logs.map(log => (
                <tr key={log.id}>
                  <td className="id-col">{log.id}</td>
                  <td>
                    <div className="user-cell">
                      <Icon name="person" size={16} />
                      <span>{log.userName}</span>
                    </div>
                  </td>
                  <td>
                    <span className={`activity-badge ${getActivityColor(log.activity)}`}>
                      <Icon name={activityIcons[log.activity] || 'info'} size={14} />
                      <span>{log.activityDescription || activityLabels[log.activity] || `Activity ${log.activity}`}</span>
                    </span>
                  </td>
                  <td className="date-col">{formatDate(log.activityDate)}</td>
                </tr>
              ))}
            </tbody>
          </table>

          {/* Pagination */}
          <div className="pagination">
            <div className="pagination-info">
              Showing {(currentPage - 1) * pageSize + 1}-{Math.min(currentPage * pageSize, totalCount)} of {totalCount}
            </div>
            <div className="pagination-controls">
              <button onClick={() => handlePageChange(1)} disabled={currentPage <= 1} className="page-btn">
                <Icon name="first_page" size={18} />
              </button>
              <button onClick={() => handlePageChange(currentPage - 1)} disabled={currentPage <= 1} className="page-btn">
                <Icon name="chevron_left" size={18} />
              </button>
              <span className="page-indicator">Page {currentPage} of {totalPages || 1}</span>
              <button onClick={() => handlePageChange(currentPage + 1)} disabled={currentPage >= totalPages} className="page-btn">
                <Icon name="chevron_right" size={18} />
              </button>
              <button onClick={() => handlePageChange(totalPages)} disabled={currentPage >= totalPages} className="page-btn">
                <Icon name="last_page" size={18} />
              </button>
            </div>
            <div className="page-size-selector">
              <select value={pageSize} onChange={(e) => { setPageSize(Number(e.target.value)); setCurrentPage(1) }}>
                <option value={10}>10 / page</option>
                <option value={20}>20 / page</option>
                <option value={50}>50 / page</option>
                <option value={100}>100 / page</option>
              </select>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

export default ActivityLogList
