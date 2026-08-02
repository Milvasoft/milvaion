import { useState, useEffect } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import Pagination from './Pagination'
import {
  TableToolbar,
  SegmentedFilter,
  SelectionBar,
  BulkDeleteButton,
  TimeCell,
  StatusCell,
  DurationCell,
  OpenCell,
  TableEmpty,
  TableFooter,
} from './TableParts'
import { formatDurationMs } from '../utils/dateUtils'
import { statusOf, occurrenceDurationMs, OCCURRENCE_STATUS_FILTERS } from '../utils/occurrenceStatus'

/* eslint-disable react/prop-types */

/**
 * A list of executions, for embedding in a detail page.
 *
 * Same design as the executions screen - the columns, the tone-marked rows, the relative
 * timestamps - so a run looks the same wherever it is read. What differs is that this one
 * sits inside a section that already has its own heading, and that it can page either by
 * cursor or by number depending on where it is used.
 *
 * The status filter used to be nine chips laid out above the table, one per status
 * including the ones almost nobody filters by. It is now the shared segmented control over
 * the six that get used.
 */
function OccurrenceTable({
  occurrences = [],
  flashIds,
  loading,
  totalCount,
  currentPage,
  pageSize,
  filterStatus,
  onFilterChange,
  onPageChange,
  onPageSizeChange,
  onBulkDelete,
  showJobName = false,
  useCursorPagination = false,
  hasNextPage = false,
  hasPreviousPage = false,
  onNextPage,
  onPreviousPage,
}) {
  const navigate = useNavigate()
  const [selected, setSelected] = useState([])

  // Running rows count up, so the clock has to move - but only while something is running.
  const [now, setNow] = useState(Date.now())
  const hasRunning = occurrences.some(o => o.status === 1)

  useEffect(() => {
    if (!hasRunning) return

    const timer = setInterval(() => setNow(Date.now()), 1000)

    return () => clearInterval(timer)
  }, [hasRunning])

  // A selection made on one page means nothing on the next one.
  useEffect(() => {
    setSelected([])
  }, [currentPage, filterStatus])

  // Queued and running rows cannot be deleted, so they are not selectable either.
  const selectable = occurrences.filter(o => o.status !== 0 && o.status !== 1)
  const allSelected = selectable.length > 0 && selected.length === selectable.length

  const toggleAll = () => setSelected(allSelected ? [] : selectable.map(o => o.id))

  const toggleOne = (id) =>
    setSelected(current => current.includes(id) ? current.filter(x => x !== id) : [...current, id])

  const maxMs = Math.max(0, ...occurrences.map(o => occurrenceDurationMs(o, now) ?? 0))
  const columnCount = showJobName ? 8 : 7

  if (loading) return <div className="loading">Loading executions…</div>

  return (
    <div className="mv-table-card">
      <TableToolbar>
        <SegmentedFilter
          options={OCCURRENCE_STATUS_FILTERS}
          value={filterStatus}
          onChange={onFilterChange}
          label="Filter by status"
        />
      </TableToolbar>

      {onBulkDelete && (
        <SelectionBar count={selected.length} onClear={() => setSelected([])}>
          <BulkDeleteButton onClick={() => { onBulkDelete(selected); setSelected([]) }} />
        </SelectionBar>
      )}

      <div className="mv-table-scroll">
        <table className="mv-table">
          <thead>
            <tr>
              <th className="mv-col-check">
                <input
                  type="checkbox"
                  checked={allSelected}
                  onChange={toggleAll}
                  disabled={selectable.length === 0}
                  aria-label="Select all"
                />
              </th>
              {showJobName && <th>Job</th>}
              <th className="mv-col-tight">Status</th>
              <th className="mv-col-tight mv-col-created">Created</th>
              <th className="mv-col-tight">Started</th>
              <th className="mv-col-tight mv-col-completed">Completed</th>
              <th className="mv-col-tight mv-col-duration">Duration</th>
              <th className="mv-col-open" aria-label="Open" />
            </tr>
          </thead>

          <tbody>
            {occurrences.length === 0 && (
              <TableEmpty colSpan={columnCount} message="No executions match the current filter." />
            )}

            {occurrences.map(occurrence => {
              const status = statusOf(occurrence.status)
              const ms = occurrenceDurationMs(occurrence, now)

              return (
                <tr
                  key={occurrence.id}
                  className={`mv-table-row is-clickable tone-${status.tone}${flashIds?.has(occurrence.id) ? ' is-new' : ''}`}
                  onClick={() => navigate(`/occurrences/${occurrence.id}`)}
                >
                  <td className="mv-col-check" onClick={(e) => e.stopPropagation()}>
                    <input
                      type="checkbox"
                      checked={selected.includes(occurrence.id)}
                      onChange={() => toggleOne(occurrence.id)}
                      disabled={occurrence.status === 0 || occurrence.status === 1}
                      aria-label="Select execution"
                    />
                  </td>

                  {showJobName && (
                    <td>
                      {/* The name links to the job; the row opens the execution. */}
                      {occurrence.jobId ? (
                        <Link
                          to={`/jobs/${occurrence.jobId}`}
                          className="mv-table-primary"
                          onClick={(e) => e.stopPropagation()}
                        >
                          {occurrence.jobDisplayName || occurrence.jobName || 'Unknown job'}
                        </Link>
                      ) : (
                        <span className="mv-table-primary">
                          {occurrence.jobDisplayName || occurrence.jobName || 'Unknown job'}
                        </span>
                      )}
                      <span className="mv-table-muted">{occurrence.workerId || 'no worker'}</span>
                    </td>
                  )}

                  <td className="mv-col-tight">
                    <StatusCell status={status} spinning={occurrence.status === 1} />
                    {/* With no job column there is nowhere else for the worker to go. */}
                    {!showJobName && (
                      <span className="mv-table-muted">{occurrence.workerId || 'no worker'}</span>
                    )}
                  </td>

                  <td className="mv-col-tight mv-col-created">
                    <TimeCell value={occurrence.createdAt} />
                  </td>

                  <td className="mv-col-tight">
                    <TimeCell value={occurrence.startTime} placeholder="not started" />
                  </td>

                  <td className="mv-col-tight mv-col-completed">
                    <TimeCell
                      value={occurrence.endTime}
                      placeholder={occurrence.status === 1 ? 'running' : '—'}
                    />
                  </td>

                  <td className="mv-col-tight mv-col-duration">
                    <DurationCell ms={ms} text={formatDurationMs(ms)} maxMs={maxMs} />
                  </td>

                  <OpenCell />
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>

      {useCursorPagination ? (
        <TableFooter
          totalCount={totalCount}
          noun="executions"
          pageSize={pageSize}
          onPageSizeChange={onPageSizeChange}
          hasNext={hasNextPage}
          hasPrevious={hasPreviousPage}
          onNext={onNextPage}
          onPrevious={onPreviousPage}
        />
      ) : (
        <TableFooter totalCount={totalCount} noun="executions">
          <Pagination
            page={currentPage}
            totalCount={totalCount}
            pageSize={pageSize}
            onPageChange={onPageChange}
            onPageSizeChange={onPageSizeChange}
          />
        </TableFooter>
      )}
    </div>
  )
}

export default OccurrenceTable
