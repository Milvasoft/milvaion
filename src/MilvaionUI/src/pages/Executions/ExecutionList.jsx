import { useState, useEffect, useCallback, useRef } from 'react'
import { useLocation } from 'react-router-dom'
import occurrenceService from '../../services/occurrenceService'
import signalRService from '../../services/signalRService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { SkeletonTable } from '../../components/Skeleton'
import { getApiErrorMessage } from '../../utils/errorUtils'
import './ExecutionList.css'
import ExecutionsTable from './ExecutionsTable'

function ExecutionList() {
  const location = useLocation()
  const [occurrences, setOccurrences] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [debouncedSearchTerm, setDebouncedSearchTerm] = useState('')
  const isFirstRender = useRef(true)
  const [totalCount, setTotalCount] = useState(0)
  const [filterStatus, setFilterStatus] = useState(
    location.state?.filterByStatus !== undefined ? location.state.filterByStatus : null
  )
  const [pageSize, setPageSize] = useState(20)
  const [paginationState, setPaginationState] = useState({
    cursor: null,
    cursorHistory: [],
    hasNextPage: false,
  })

  const { modalProps, showConfirm, showSuccess, showError } = useModal()

  useEffect(() => {
    if (isFirstRender.current) {
      isFirstRender.current = false
      return
    }
    const timer = setTimeout(() => {
      setDebouncedSearchTerm(searchTerm)
      setPaginationState({ cursor: null, cursorHistory: [], hasNextPage: false })
    }, 500)

    return () => clearTimeout(timer)
  }, [searchTerm])

  const loadOccurrences = useCallback(async (showLoading = false) => {
    try {
      if (showLoading) {
        setLoading(true)
      }
      setError(null)

      const params = {
        cursor: paginationState.cursor || undefined,
        rowCount: pageSize,
        searchTerm: debouncedSearchTerm || undefined,
      }

      if (filterStatus !== null) {
        params.filtering = {
          criterias: [
            {
              filterBy: 'Status',
              value: filterStatus,
              type: 5
            }
          ]
        }
      }

      const response = await occurrenceService.getAllCursor(params)

      const data = response?.data || []
      const total = response?.totalDataCount ?? 0
      const nextCursorVal = response?.nextCursor ?? null
      const hasNext = response?.hasNextPage ?? false

      setOccurrences(data)
      setTotalCount(total)
      setPaginationState(prev => ({ ...prev, hasNextPage: hasNext, nextCursor: nextCursorVal }))
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load executions'))
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [paginationState.cursor, pageSize, debouncedSearchTerm, filterStatus])

  useEffect(() => {
    loadOccurrences(true)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paginationState.cursor, pageSize, debouncedSearchTerm, filterStatus])

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
      if (paginationState.cursorHistory.length === 0 && (filterStatus === null || newOccurrence.status === filterStatus)) {
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
      } else {
        setTotalCount(prev => prev + 1)
      }
    }

    const unsubscribeOccurrenceUpdated = signalRService.on('OccurrenceUpdated', handleOccurrenceUpdated)
    const unsubscribeOccurrenceCreated = signalRService.on('OccurrenceCreated', handleOccurrenceCreated)

    return () => {
      unsubscribeOccurrenceUpdated()
      unsubscribeOccurrenceCreated()
    }
  }, [paginationState.cursorHistory.length, pageSize, filterStatus])

  const handleNextPage = () => {
    setPaginationState(prev => {
      if (!prev.nextCursor) return prev
      return {
        cursor: prev.nextCursor,
        cursorHistory: [...prev.cursorHistory, prev.cursor],
        hasNextPage: false,
        nextCursor: null,
      }
    })
  }

  const handlePreviousPage = () => {
    setPaginationState(prev => {
      if (prev.cursorHistory.length === 0) return prev
      const newHistory = [...prev.cursorHistory]
      const previousCursor = newHistory.pop() ?? null
      return {
        cursor: previousCursor,
        cursorHistory: newHistory,
        hasNextPage: false,
        nextCursor: null,
      }
    })
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

  if (loading) return <SkeletonTable rows={pageSize} columns={7} />
  if (error) return <div className="error">{error}</div>

  return (
    <div className="page execution-list">
      <Modal {...modalProps} />

      <div className="page-header">
        <h1>
          <Icon name="play_circle" size={28} />
          <span>Job Executions</span>
          <span>({totalCount})</span>
        </h1>
      </div>

      <ExecutionsTable
        occurrences={occurrences}
        loading={loading}
        totalCount={totalCount}
        pageSize={pageSize}
        filterStatus={filterStatus}
        searchTerm={searchTerm}
        onSearchChange={setSearchTerm}
        onFilterChange={(status) => {
          setFilterStatus(status)
          setPaginationState({ cursor: null, cursorHistory: [], hasNextPage: false, nextCursor: null })
        }}
        onPageSizeChange={(newSize) => {
          setPageSize(newSize)
          setPaginationState({ cursor: null, cursorHistory: [], hasNextPage: false, nextCursor: null })
        }}
        onBulkDelete={handleBulkDelete}
        hasNextPage={paginationState.hasNextPage}
        hasPreviousPage={paginationState.cursorHistory.length > 0}
        onNextPage={handleNextPage}
        onPreviousPage={handlePreviousPage}
      />
    </div>
  )
}

export default ExecutionList
