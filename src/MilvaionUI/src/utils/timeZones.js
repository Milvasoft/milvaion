// Time zone helpers for the job form, job detail and Upcoming Executions.

const FALLBACK_ZONES = [
  'UTC', 'Europe/London', 'Europe/Berlin', 'Europe/Istanbul', 'Europe/Moscow', 'Asia/Dubai', 'Asia/Kolkata',
  'Asia/Singapore', 'Asia/Tokyo', 'Australia/Sydney', 'America/Sao_Paulo', 'America/New_York', 'America/Chicago',
  'America/Denver', 'America/Los_Angeles'
]

/**
 * Current UTC offset of a zone, e.g. "UTC+03:00". Empty when the browser does not know the zone.
 */
export function formatUtcOffset(timeZone, at = new Date()) {
  try {
    const parts = new Intl.DateTimeFormat('en-US', { timeZone, timeZoneName: 'longOffset' }).formatToParts(at)
    const name = parts.find(p => p.type === 'timeZoneName')?.value || ''

    // "GMT+03:00" -> "UTC+03:00"; plain "GMT" is UTC itself.
    return name === 'GMT' ? 'UTC+00:00' : name.replace('GMT', 'UTC')
  } catch {
    return ''
  }
}

let cache = null

/**
 * Every IANA zone the browser knows, UTC first, each with its current offset and a lowercase search string.
 */
export function listTimeZones() {
  if (cache) return cache

  let ids = FALLBACK_ZONES

  try {
    if (typeof Intl.supportedValuesOf === 'function') ids = Intl.supportedValuesOf('timeZone')
  } catch {
    // Older browsers: the short list above.
  }

  const unique = ['UTC', ...ids.filter(id => id !== 'UTC' && id !== 'Etc/UTC')]

  cache = unique.map(id => {
    const offset = formatUtcOffset(id)
    const shortOffset = offset.replace('UTC', '').replace(':00', '')

    return {
      id,
      offset,
      searchText: `${id} ${offset} ${shortOffset}`.toLowerCase()
    }
  })

  return cache
}

/**
 * Formats a UTC instant as wall clock time in a zone, e.g. "2026-11-14 09:00".
 */
export function formatInZone(utcValue, timeZone, withSeconds = false) {
  if (!utcValue) return '—'

  try {
    const date = typeof utcValue === 'string' && !/[zZ]|[+-]\d\d:\d\d$/.test(utcValue) ? new Date(`${utcValue}Z`) : new Date(utcValue)

    const formatted = new Intl.DateTimeFormat('sv-SE', {
      timeZone: timeZone || 'UTC',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: withSeconds ? '2-digit' : undefined,
      hour12: false
    }).format(date)

    return formatted
  } catch {
    return String(utcValue)
  }
}

export const MISSED_RUN_POLICIES = [
  { value: 0, key: 'Skip', label: 'Skip', description: 'Missed runs are not executed; one skipped occurrence is recorded and the job resumes at its next time.' },
  { value: 1, key: 'RunOnce', label: 'Run once', description: 'One catch-up run stands in for all the missed runs.' },
  { value: 2, key: 'RunAll', label: 'Run all', description: 'Each missed run is executed, one after another, up to the maximum.' }
]

/** Label for a missed-run policy, whether the API sent it as a number or a name. */
export function missedRunPolicyLabel(value) {
  const policy = MISSED_RUN_POLICIES.find(p => p.value === value || p.key === value)
  return policy ? policy.label : 'Skip'
}

export const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
