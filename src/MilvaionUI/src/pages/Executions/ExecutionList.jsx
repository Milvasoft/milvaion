import { useState, useEffect, useRef, useCallback } from 'react'
import { useLocation } from 'react-router-dom'
import occurrenceService from '../../services/occurrenceService'
import signalRService from '../../services/signalRService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import './ExecutionList.css'
import OccurrenceTable from '../../components/OccurrenceTable'

function ExecutionList() {
  const location = useLocation()
  const [occurrences, setOccurrences] = useState([])
  const [loading, setLoading] = useState(true)
  const [isInitialLoad, setIsInitialLoad] = useState(true)
  const [error, setError] = useState(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [debouncedSearchTerm, setDebouncedSearchTerm] = useState('')
  const [currentPage, setCurrentPage] = useState(1)
  const [totalCount, setTotalCount] = useState(0)
  const [filterStatus, setFilterStatus] = useState(location.state?.filterByStatus || null)
  const [pageSize, setPageSize] = useState(20)

  const { modalProps, showConfirm, showSuccess, showError } = useModal()

  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearchTerm(searchTerm)
      setCurrentPage(1)
    }, 500)

    return () => clearTimeout(timer)
  }, [searchTerm])

  const loadOccurrences = useCallback(async (showLoading = false) => {
    try {
      if (showLoading) {
        setLoading(true)
      }
      setError(null)

      const requestBody = {
        pageNumber: currentPage,
        rowCount: pageSize,
        searchTerm: debouncedSearchTerm || undefined
      }

      if (filterStatus !== null) {
        requestBody.filtering = {
          criterias: [
            {
              filterBy: "Status",
              value: filterStatus,
              type: 5
            }
          ]
        }
      }

      const response = await occurrenceService.getAll(requestBody)

      const data = response?.data?.data || response?.data || []
      const total = response?.data?.totalDataCount || response?.totalDataCount || 0

      setOccurrences(data)
      setTotalCount(total)
    } catch (err) {
      setError('Failed to load executions')
      console.error(err)
    } finally {
      if (showLoading) {
        setLoading(false)
        setIsInitialLoad(false)
      }
    }
  }, [currentPage, pageSize, debouncedSearchTerm, filterStatus])

  useEffect(() => {
    loadOccurrences(isInitialLoad)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentPage, pageSize, debouncedSearchTerm, filterStatus])

  useEffect(() => {

    // Connect to SignalR
    const connectSignalR = async () => {
      await signalRService.connect()
    }

    connectSignalR()

    const handleOccurrenceUpdated = (updatedOccurrence) => {
      setOccurrences(prev => {
        const occId = updatedOccurrence.id || updatedOccurrence.occurrenceId
        const exists = prev.some(occ => occ.id === occId)
        if (exists) {
          return prev.map(occ =>
            occ.id === occId ? { ...occ, ...updatedOccurrence } : occ
          )
        }
        return prev
      })
    }

    const handleOccurrenceCreated = (newOccurrence) => {
      if (currentPage === 1 && (filterStatus === null || newOccurrence.status === filterStatus)) {
        setOccurrences(prev => {
          const occId = newOccurrence.id || newOccurrence.occurrenceId
          const exists = prev.some(occ => occ.id === occId)
          if (!exists) {
            const updated = [newOccurrence, ...prev]
            return updated.slice(0, pageSize)
          }
          return prev
        })
        setTotalCount(prev => prev + 1)
      } else if (currentPage !== 1) {
        setTotalCount(prev => prev + 1)
      }
    }

    const unsubscribeOccurrenceUpdated = signalRService.on('OccurrenceUpdated', handleOccurrenceUpdated)
    const unsubscribeOccurrenceCreated = signalRService.on('OccurrenceCreated', handleOccurrenceCreated)

    return () => {
      unsubscribeOccurrenceUpdated()
      unsubscribeOccurrenceCreated()
    }
  }, [currentPage, pageSize, filterStatus])

  const handlePageChange = (newPage) => {
    const totalPages = Math.ceil(totalCount / pageSize)
    if (newPage >= 1 && newPage <= totalPages) {
      setCurrentPage(newPage)
    }
  }

  const handleBulkDelete = async (occurrenceIds) => {
    const confirmed = await showConfirm(
      `Are you sure you want to delete ${occurrenceIds.length} execution${occurrenceIds.length > 1 ? 's' : ''}? This action cannot be undone.`,
      'Delete Executions',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    try {
      await occurrenceService.delete(occurrenceIds)
      await loadOccurrences()
      await showSuccess(`${occurrenceIds.length} execution${occurrenceIds.length > 1 ? 's' : ''} deleted successfully`)
    } catch (err) {
      await showError('Failed to delete execution(s). Please try again.')
      console.error(err)
    }
  }

  if (loading && isInitialLoad) return <div className="loading">Loading executions...</div>
  if (error) return <div className="error">{error}</div>

  return (
    <div className="execution-list">
      <Modal {...modalProps} />

      <div className="page-header">
        <h1>
          <Icon name="play_circle" size={28} />
          <span style={{ margin: '0 0 0 1rem' }}>Job Executions</span>
          <span>({totalCount})</span>
        </h1>
      </div>

      <div className="search-section">
        <div className="search-box">
          <input
            type="text"
            placeholder="Search by name or tags..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="search-input"
          />
          {searchTerm && (
            <button
              type="button"
              onClick={() => setSearchTerm('')}
              className="clear-search-btn"
              title="Clear search"
            >
              <Icon name="close" size={16} />
            </button>
          )}
        </div>
      </div>

      <OccurrenceTable
        occurrences={occurrences}
        loading={loading}
        totalCount={totalCount}
        currentPage={currentPage}
        pageSize={pageSize}
        filterStatus={filterStatus}
        onFilterChange={(status) => {
          setFilterStatus(status)
          setCurrentPage(1)
        }}
        onPageChange={handlePageChange}
        onPageSizeChange={(newSize) => {
          setPageSize(newSize)
          setCurrentPage(1)
        }}
        onBulkDelete={handleBulkDelete}
        showJobName={true}
      />
    </div>
  )
}

export default ExecutionList
