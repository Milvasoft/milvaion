import { useState, useEffect, useCallback } from 'react'
import activityLogService from '../../services/activityLogService'
import Icon from '../../components/Icon'
import { SkeletonTable } from '../../components/Skeleton'
import { getApiErrorMessage } from '../../utils/errorUtils'
import Pagination from '../../components/Pagination'
import { TableToolbar, TableSearch, TableFooter, TimeCell } from '../../components/TableParts'
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

      {logs.length === 0 ? (
        <div className="empty-state-card">
          <div className="empty-icon">
            <Icon name="history" size={64} />
          </div>
          <h3>No Activity Logs</h3>
          <p>{searchTerm ? 'No logs match your search.' : 'No user activity has been recorded yet.'}</p>
        </div>
      ) : (
        <div className="mv-table-card">
          <TableToolbar>
            <TableSearch value={searchTerm} onChange={setSearchTerm} placeholder="Search by username…" />
          </TableToolbar>

          <div className="mv-table-scroll">
            <table className="mv-table">
              <thead>
                <tr>
                  <th>User</th>
                  <th>Activity</th>
                  <th className="mv-col-tight">When</th>
                </tr>
              </thead>
              <tbody>
                {logs.map(log => (
                  <tr key={log.id} className="mv-table-row">
                    <td>
                      <span className="mv-table-primary">{log.userName}</span>
                      <span className="mv-table-muted">#{log.id}</span>
                    </td>
                    <td>
                      <span className={`activity-badge ${getActivityColor(log.activity)}`}>
                        <Icon name={activityIcons[log.activity] || 'info'} size={14} />
                        <span>{log.activityDescription || activityLabels[log.activity] || `Activity ${log.activity}`}</span>
                      </span>
                    </td>
                    <td className="mv-col-tight">
                      <TimeCell value={log.activityDate} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <TableFooter totalCount={totalCount} noun="activity records">
            <Pagination
              page={currentPage}
              pageSize={pageSize}
              totalCount={totalCount}
              onPageChange={handlePageChange}
              onPageSizeChange={(size) => { setPageSize(size); setCurrentPage(1) }}
            />
          </TableFooter>
        </div>
      )}
    </div>
  )
}

export default ActivityLogList
