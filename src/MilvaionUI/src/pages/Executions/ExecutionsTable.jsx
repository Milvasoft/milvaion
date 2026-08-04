import { useState, useEffect } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import {
  TableToolbar,
  TableSearch,
  SegmentedFilter,
  SelectionBar,
  BulkDeleteButton,
  TimeCell,
  StatusCell,
  DurationCell,
  OpenCell,
  TableEmpty,
  TableFooter,
} from '../../components/TableParts'
import { formatDurationMs } from '../../utils/dateUtils'
import { statusOf, occurrenceDurationMs, OCCURRENCE_STATUS_FILTERS } from '../../utils/occurrenceStatus'

/* eslint-disable react/prop-types */

/**
 * The executions list.
 *
 * This is where the shared table design was worked out, so it is the fullest example of
 * it: toolbar, segmented status filter, selection bar, tone-marked rows, relative
 * timestamps, a scaled duration bar and cursor paging in the footer.
 *
 * The columns are:
 *
 *   selection · what ran (job + worker) · how it went · created · started · completed ·
 *   how long · open
 *
 * Worker sits under the job name because it qualifies the job rather than standing on its
 * own - that is the rule the `mv-table-primary` / `mv-table-muted` pair exists for, and
 * it is what keeps the table from growing a column per attribute.
 *
 * Separate from `OccurrenceTable`, which the job detail page renders inside a card that
 * already has its own header and filters.
 */

function ExecutionsTable({
  occurrences = [],
  flashIds,
  loading,
  totalCount,
  filterStatus,
  onFilterChange,
  searchTerm,
  onSearchChange,
  pageSize,
  onPageSizeChange,
  hasNextPage,
  hasPreviousPage,
  onNextPage,
  onPreviousPage,
  onBulkDelete,
}) {
  const navigate = useNavigate()
  const [selected, setSelected] = useState([])

  // Running rows show a duration counting up, so the clock has to move. Only while
  // something is actually running - otherwise this would re-render the whole table once a
  // second for nothing.
  const [now, setNow] = useState(Date.now())
  const hasRunning = occurrences.some(o => o.status === 1)

  useEffect(() => {
    if (!hasRunning) return

    const timer = setInterval(() => setNow(Date.now()), 1000)

    return () => clearInterval(timer)
  }, [hasRunning])

  // Queued and running rows cannot be deleted, so they are not selectable either.
  const selectable = occurrences.filter(o => o.status !== 0 && o.status !== 1)
  const allSelected = selectable.length > 0 && selected.length === selectable.length

  const toggleAll = () => setSelected(allSelected ? [] : selectable.map(o => o.id))

  const toggleOne = (id) =>
    setSelected(current => current.includes(id) ? current.filter(x => x !== id) : [...current, id])

  // Scale for the duration bars, recomputed per page so they compare what is on screen
  // rather than against an absolute the user cannot see.
  const maxMs = Math.max(0, ...occurrences.map(o => occurrenceDurationMs(o, now) ?? 0))

  return (
    <div className="mv-table-card">
      <TableToolbar>
        <TableSearch
          value={searchTerm}
          onChange={onSearchChange}
          placeholder="Search by id or name…"
        />
        <SegmentedFilter
          options={OCCURRENCE_STATUS_FILTERS}
          value={filterStatus}
          onChange={onFilterChange}
          label="Filter by status"
        />
      </TableToolbar>

      <SelectionBar count={selected.length} onClear={() => setSelected([])}>
        <BulkDeleteButton onClick={() => { onBulkDelete?.(selected); setSelected([]) }} />
      </SelectionBar>

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
              <th>Job</th>
              <th className="mv-col-tight">Status</th>
              <th className="mv-col-tight mv-col-created">Created</th>
              <th className="mv-col-tight">Started</th>
              <th className="mv-col-tight mv-col-completed">Completed</th>
              <th className="mv-col-tight mv-col-duration">Duration</th>
              <th className="mv-col-open" aria-label="Open" />
            </tr>
          </thead>

          <tbody>
            {occurrences.length === 0 && !loading && (
              <TableEmpty colSpan={8} message="No executions match the current filters." />
            )}

            {occurrences.map(occurrence => {
              const status = statusOf(occurrence.status)
              const ms = occurrenceDurationMs(occurrence, now)
              const name = occurrence.jobDisplayName || occurrence.jobName || 'Unknown job'
              // Second line: the technical job name (when it differs from the title shown above)
              // and the worker, both muted, e.g. "SendEmailJob / worker-01".
              const worker = occurrence.workerId || 'no worker'
              const jobTech = occurrence.jobName && occurrence.jobName !== name ? occurrence.jobName : null
              const subline = jobTech ? `${jobTech} / ${worker}` : worker

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

                  <td>
                    {/* The name links to the job; the row opens the execution. Two
                        destinations, so the inner one stops the row handler. */}
                    {occurrence.jobId ? (
                      <Link
                        to={`/jobs/${occurrence.jobId}`}
                        className="mv-table-primary"
                        onClick={(e) => e.stopPropagation()}
                      >
                        {name}
                      </Link>
                    ) : (
                      <span className="mv-table-primary">{name}</span>
                    )}
                    <span className="mv-table-muted">{subline}</span>
                  </td>

                  <td className="mv-col-tight">
                    <StatusCell status={status} spinning={occurrence.status === 1} />
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
    </div>
  )
}

export default ExecutionsTable
