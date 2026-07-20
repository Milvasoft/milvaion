/**
 * Execution statuses, as the tables render them.
 *
 * The same eight states were written out in `OccurrenceTable`, `ExecutionList` and
 * `FailedOccurrenceList`, each with its own icon and its own idea of which colour meant
 * what - so the same run could be amber on one screen and red on another. One map.
 *
 * `tone` is the shared vocabulary from `table.css`: it colours both the status label and
 * the row's left edge, so the two can never disagree.
 */
export const OCCURRENCE_STATUS = {
  0: { label: 'Queued', tone: 'idle', icon: 'schedule' },
  1: { label: 'Running', tone: 'active', icon: 'sync' },
  2: { label: 'Completed', tone: 'success', icon: 'check_circle' },
  3: { label: 'Failed', tone: 'danger', icon: 'error' },
  4: { label: 'Cancelled', tone: 'idle', icon: 'block' },
  5: { label: 'Timed Out', tone: 'warning', icon: 'timer_off' },
  6: { label: 'Unknown', tone: 'idle', icon: 'help' },
  7: { label: 'Skipped', tone: 'idle', icon: 'skip_next' },
}

/** Falls back to "Unknown" rather than rendering an empty cell for a status we don't know. */
export const statusOf = (status) => OCCURRENCE_STATUS[status] ?? OCCURRENCE_STATUS[6]

/**
 * The statuses worth putting in a segmented filter, in the order people reach for them.
 *
 * Not all eight: cancelled, unknown and skipped are rare enough that including them would
 * push the control past the width where one glance takes it in. They remain reachable
 * through search and the status column.
 */
export const OCCURRENCE_STATUS_FILTERS = [
  { value: null, label: 'All' },
  { value: 1, label: 'Running' },
  { value: 2, label: 'Completed' },
  { value: 3, label: 'Failed' },
  { value: 0, label: 'Queued' },
  { value: 5, label: 'Timed Out' },
]

/**
 * How long a run took, in milliseconds.
 *
 * Running rows count up from their start, which is why this takes a `now` - the caller
 * ticks a timer and passes it in, so the cell re-renders each second instead of freezing
 * at whatever the duration was when the page loaded.
 */
export const occurrenceDurationMs = (occurrence, now = Date.now()) => {
  if (!occurrence?.startTime || occurrence.status === 0) return null

  if (occurrence.durationMs != null) return occurrence.durationMs < 0 ? null : occurrence.durationMs

  if (occurrence.status === 1) return Math.max(0, now - new Date(occurrence.startTime).getTime())

  if (!occurrence.endTime) return null

  const ms = new Date(occurrence.endTime) - new Date(occurrence.startTime)

  return ms < 0 ? null : ms
}
