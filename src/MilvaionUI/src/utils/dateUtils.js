import moment from 'moment'

/**
 * Format date from API (ISO 8601) to human readable format
 * @param {string|Date} date - Date string or Date object
 * @param {string} format - Moment.js format string (default: 'LLL')
 * @returns {string} Formatted date string
 */
export const formatDate = (date, format = 'LLL') => {
  if (!date) return '-'
  
  const momentDate = moment(date)
  
  if (!momentDate.isValid()) {
    console.warn('Invalid date:', date)
    return '-'
  }
  
  return momentDate.format(format)
}

/**
 * Format date to relative time (e.g., "2 hours ago")
 * @param {string|Date} date - Date string or Date object
 * @returns {string} Relative time string
 */
export const formatRelativeTime = (date) => {
  if (!date) return '-'
  
  const momentDate = moment(date)
  
  if (!momentDate.isValid()) {
    console.warn('Invalid date:', date)
    return '-'
  }
  
  return momentDate.fromNow()
}

/**
 * Calculate duration between two dates
 * @param {string|Date} startDate - Start date
 * @param {string|Date} endDate - End date (optional, defaults to now)
 * @returns {string} Duration string (e.g., "2h 30m 15s")
 */
export const formatDuration = (startDate, endDate = null) => {
  if (!startDate) return '-'
  
  const start = moment(startDate)
  const end = endDate ? moment(endDate) : moment()
  
  if (!start.isValid()) {
    console.warn('Invalid start date:', startDate)
    return '-'
  }
  
  if (endDate && !end.isValid()) {
    console.warn('Invalid end date:', endDate)
    return '-'
  }
  
  const duration = moment.duration(end.diff(start))
  
  const days = Math.floor(duration.asDays())
  const hours = duration.hours()
  const minutes = duration.minutes()
  const seconds = duration.seconds()
  const milliseconds = duration.milliseconds()
  
  const parts = []
  if (days > 0) parts.push(`${days}d`)
  if (hours > 0) parts.push(`${hours}h`)
  if (minutes > 0) parts.push(`${minutes}m`)
  if (seconds > 0) parts.push(`${seconds}s`)
  
  // Show milliseconds if total duration is less than 1 second
  if (parts.length === 0) {
    parts.push(`${milliseconds}ms`)
  }
  
  return parts.join(' ')
}

/**
 * Format date to short format (e.g., "Dec 20, 2025")
 * @param {string|Date} date - Date string or Date object
 * @returns {string} Short date string
 */
export const formatDateShort = (date) => {
  return formatDate(date, 'MMM D, YYYY')
}

/**
 * Format date to time only (e.g., "5:58 PM")
 * @param {string|Date} date - Date string or Date object
 * @returns {string} Time string
 */
export const formatTime = (date) => {
  return formatDate(date, 'LT')
}

/**
 * Format date to date and time (e.g., "Dec 20, 2025 5:58 PM")
 * @param {string|Date} date - Date string or Date object
 * @returns {string} Date and time string
 */
export const formatDateTime = (date) => {
  return formatDate(date, 'lll')
}

/**
 * Check if date is valid and not DateTime.MinValue (0001-01-01)
 * @param {string|Date} date - Date string or Date object
 * @returns {boolean} True if date is valid and not MinValue
 */
export const isValidDate = (date) => {
  if (!date) return false
  
  const momentDate = moment(date)
  
  if (!momentDate.isValid()) return false
  
  // Check for DateTime.MinValue (year 1) or very old dates
  if (momentDate.year() < 1900) return false
  
  return true
}

/**
 * Format time since date with MinValue check (e.g., "2h ago", "Never")
 * @param {string|Date} date - Date string or Date object
 * @returns {string} Time ago string or "Never"
 */
export const formatTimeSince = (date) => {
  if (!isValidDate(date)) return 'No Heartbeat'
  
  const momentDate = moment(date)
  const seconds = moment().diff(momentDate, 'seconds')
  
  if (seconds < 0) return 'Just now'
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}
