/**
 * Cron construction, parsing and occurrence preview for the visual builder.
 *
 * Everything here speaks the six-field format the scheduler actually parses -
 * `second minute hour dayOfMonth month dayOfWeek`. The server side is Cronos with
 * `CronFormat.IncludeSeconds`, which rejects a five-field expression outright, so the
 * builder never emits one.
 *
 * Occurrences are computed in UTC on purpose. The dispatcher schedules against
 * `TimeZoneInfo.Utc`, so a preview drawn in the browser's timezone would quietly disagree
 * with when the job really fires for anyone not sitting on UTC.
 */

// ── Domain values ────────────────────────────────────────────────────────────

export const FREQUENCIES = [
  { value: 'seconds', label: 'Seconds', icon: 'timer' },
  { value: 'minutes', label: 'Minutes', icon: 'timelapse' },
  { value: 'hourly', label: 'Hourly', icon: 'schedule' },
  { value: 'daily', label: 'Daily', icon: 'today' },
  { value: 'weekly', label: 'Weekly', icon: 'date_range' },
  { value: 'monthly', label: 'Monthly', icon: 'calendar_month' },
  { value: 'yearly', label: 'Yearly', icon: 'event_repeat' },
]

// Cron numbers Sunday as 0, which is also what `Date.getUTCDay()` returns.
export const WEEKDAYS = [
  { value: 0, short: 'Sun', label: 'Sunday' },
  { value: 1, short: 'Mon', label: 'Monday' },
  { value: 2, short: 'Tue', label: 'Tuesday' },
  { value: 3, short: 'Wed', label: 'Wednesday' },
  { value: 4, short: 'Thu', label: 'Thursday' },
  { value: 5, short: 'Fri', label: 'Friday' },
  { value: 6, short: 'Sat', label: 'Saturday' },
]

export const MONTHS = [
  { value: 1, label: 'January' },
  { value: 2, label: 'February' },
  { value: 3, label: 'March' },
  { value: 4, label: 'April' },
  { value: 5, label: 'May' },
  { value: 6, label: 'June' },
  { value: 7, label: 'July' },
  { value: 8, label: 'August' },
  { value: 9, label: 'September' },
  { value: 10, label: 'October' },
  { value: 11, label: 'November' },
  { value: 12, label: 'December' },
]

// `5` is "last" rather than a fifth occurrence, because a fifth Monday does not exist in
// most months and a schedule that silently skips two months out of three is never what
// anyone means by it.
export const NTH_OPTIONS = [
  { value: 1, label: 'First' },
  { value: 2, label: 'Second' },
  { value: 3, label: 'Third' },
  { value: 4, label: 'Fourth' },
  { value: 5, label: 'Last' },
]

// February gets 29 so a genuine leap-day schedule stays selectable.
const DAYS_IN_MONTH = [31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]

export const defaultBuilderState = () => ({
  frequency: 'daily',
  // interval frequencies
  secondInterval: 30,
  minuteInterval: 10,
  hourInterval: 1,
  hourMinute: 0,
  dayInterval: 1,
  monthInterval: 1,
  // time of day
  hour: 9,
  minute: 0,
  // optional windows shared by the interval frequencies
  restrictHours: false,
  hourFrom: 9,
  hourTo: 18,
  restrictWeekdays: false,
  weekdays: [1, 2, 3, 4, 5],
  // monthly / yearly
  monthlyMode: 'dayOfMonth',
  dayOfMonth: 1,
  lastDayOfMonth: false,
  nth: 1,
  nthWeekday: 1,
  month: 1,
})

// ── Field grammar ────────────────────────────────────────────────────────────

const RE_STEP = /^\*\/(\d+)$/
const RE_RANGE = /^(\d+)-(\d+)$/
const RE_RANGE_STEP = /^(\d+)-(\d+)\/(\d+)$/
const RE_NUM = /^\d+$/
const RE_NTH = /^(\d)#([1-5])$/
const RE_LAST_DOW = /^(\d)L$/

/**
 * Expand a single cron field into the sorted list of values it matches.
 *
 * Handles `*`, `n`, `a-b`, lists of those, and any of them with a `/step`. Returns null for
 * anything outside that grammar - notably `L`, `W` and `#`, which the scheduler understands
 * but which have no meaning as a flat value list.
 */
export function expandField(field, min, max) {
  const values = new Set()

  for (const term of String(field).split(',')) {
    const slash = term.indexOf('/')
    const range = slash === -1 ? term : term.slice(0, slash)
    let stepValue = 1

    if (slash !== -1) {
      stepValue = Number(term.slice(slash + 1))
      if (!Number.isInteger(stepValue) || stepValue < 1) return null
    }

    let from
    let to

    if (range === '*') {
      from = min
      to = max
    } else if (RE_RANGE.test(range)) {
      const match = range.match(RE_RANGE)
      from = Number(match[1])
      to = Number(match[2])
    } else if (RE_NUM.test(range)) {
      from = Number(range)
      // `5/10` means "from 5 onwards, every 10" - a bare `5` is just itself.
      to = slash === -1 ? from : max
    } else {
      return null
    }

    if (from < min || to > max || from > to) return null

    for (let value = from; value <= to; value += stepValue) values.add(value)
  }

  return [...values].sort((a, b) => a - b)
}

/** `*` or `* /n` as an interval; null when the field is anything else. */
function readStep(field) {
  if (field === '*') return 1
  const match = field.match(RE_STEP)
  return match ? Number(match[1]) : null
}

const asStep = (n) => (n <= 1 ? '*' : `*/${n}`)

// ── Building ─────────────────────────────────────────────────────────────────

function weekdayField(state) {
  if (!state.restrictWeekdays) return '*'
  const days = [...state.weekdays].sort((a, b) => a - b)
  if (days.length === 0 || days.length === 7) return '*'
  return days.join(',')
}

function hourWindowField(state) {
  if (!state.restrictHours) return '*'
  if (state.hourFrom === state.hourTo) return String(state.hourFrom)
  return `${state.hourFrom}-${state.hourTo}`
}

/**
 * Reasons the current selection cannot be turned into an expression, or null when it can.
 *
 * These are the cases where building anyway would produce a valid but wrong schedule - an
 * empty weekday list collapses to `*`, which reads as "every day" rather than "no days" -
 * so the caller blocks Apply instead of writing something the user did not ask for.
 */
export function validateBuilderState(state) {
  if (state.frequency === 'weekly' && state.weekdays.length === 0) {
    return 'Select at least one day of the week.'
  }

  if (state.restrictWeekdays && state.weekdays.length === 0 &&
      ['seconds', 'minutes', 'hourly'].includes(state.frequency)) {
    return 'Select at least one day of the week, or turn the day filter off.'
  }

  // Cron ranges do not wrap around midnight, so 22-06 is not an overnight window - it is
  // simply invalid, and saying so beats emitting an expression the API will reject.
  if (state.restrictHours && state.hourFrom > state.hourTo) {
    return 'The start hour must be earlier than or equal to the end hour.'
  }

  /*
   * A date that does not exist is the one mistake here with teeth. Cronos parses `0 0 9 31 2 *`
   * happily, so the API accepts the job, but it yields no occurrence ever - and the dispatcher
   * responds to an expression with no future occurrences by dropping the job. The user would
   * be left with a schedule that saved cleanly and then quietly disappeared.
   *
   * Only the yearly frequency can produce one, since it is the only one that pins a day to a
   * single month. Monthly is fine: day 31 simply skips the short months.
   */
  if (state.frequency === 'yearly' && !state.lastDayOfMonth) {
    const maxDay = DAYS_IN_MONTH[state.month - 1]
    if (state.dayOfMonth > maxDay) {
      const month = MONTHS[state.month - 1].label
      return `${month} never has ${state.dayOfMonth} days, so this job would never run.`
    }
  }

  return null
}

/**
 * Something worth saying about an otherwise valid selection, or null.
 *
 * Kept apart from validation because these do not block anything - they are the cases where
 * the schedule does what was asked but fires less often than the wording suggests.
 */
export function getBuilderWarning(state) {
  if (state.frequency === 'monthly' && state.monthlyMode === 'dayOfMonth' &&
      !state.lastDayOfMonth && state.dayOfMonth > 28) {
    return `Months with fewer than ${state.dayOfMonth} days are skipped. Use "last day" to run at the end of every month.`
  }

  if (state.frequency === 'yearly' && !state.lastDayOfMonth &&
      state.month === 2 && state.dayOfMonth === 29) {
    return 'February 29 only exists in leap years, so this runs roughly every four years.'
  }

  if (state.frequency === 'monthly' && state.monthlyMode === 'nthWeekday' && state.nth === 4) {
    return 'A month can have a fifth matching weekday; "Fourth" always picks the fourth, never the last.'
  }

  return null
}

export function buildCronExpression(state) {
  const dow = weekdayField(state)

  switch (state.frequency) {
    case 'seconds':
      return `${asStep(state.secondInterval)} * ${hourWindowField(state)} * * ${dow}`

    case 'minutes':
      return `0 ${asStep(state.minuteInterval)} ${hourWindowField(state)} * * ${dow}`

    case 'hourly': {
      const hours = state.restrictHours
        ? (state.hourInterval <= 1
          ? `${state.hourFrom}-${state.hourTo}`
          : `${state.hourFrom}-${state.hourTo}/${state.hourInterval}`)
        : asStep(state.hourInterval)
      return `0 ${state.hourMinute} ${hours} * * ${dow}`
    }

    case 'daily':
      return `0 ${state.minute} ${state.hour} ${asStep(state.dayInterval)} * *`

    case 'weekly': {
      const days = [...state.weekdays].sort((a, b) => a - b)
      return `0 ${state.minute} ${state.hour} * * ${days.join(',')}`
    }

    case 'monthly': {
      if (state.monthlyMode === 'nthWeekday') {
        const nth = state.nth === 5 ? `${state.nthWeekday}L` : `${state.nthWeekday}#${state.nth}`
        return `0 ${state.minute} ${state.hour} * ${asStep(state.monthInterval)} ${nth}`
      }
      const day = state.lastDayOfMonth ? 'L' : state.dayOfMonth
      return `0 ${state.minute} ${state.hour} ${day} ${asStep(state.monthInterval)} *`
    }

    case 'yearly': {
      const day = state.lastDayOfMonth ? 'L' : state.dayOfMonth
      return `0 ${state.minute} ${state.hour} ${day} ${state.month} *`
    }

    default:
      return ''
  }
}

// ── Parsing ──────────────────────────────────────────────────────────────────

function applyHourWindow(state, hour) {
  if (hour === '*') return true

  if (RE_RANGE.test(hour)) {
    const match = hour.match(RE_RANGE)
    state.restrictHours = true
    state.hourFrom = Number(match[1])
    state.hourTo = Number(match[2])
    return true
  }

  if (RE_NUM.test(hour)) {
    state.restrictHours = true
    state.hourFrom = Number(hour)
    state.hourTo = Number(hour)
    return true
  }

  return false
}

function applyWeekdays(state, days) {
  if (!days) return
  state.restrictWeekdays = true
  state.weekdays = days
}

/**
 * Read an expression back into builder state so reopening the dialog shows what is already
 * scheduled rather than a blank form.
 *
 * Only the shapes the builder itself produces are recognised. Anything hand-written beyond
 * them - a non-zero second on a daily schedule, a `W` modifier, a list of hours - returns
 * null, and the caller falls back to defaults. That is deliberate: guessing at a half-match
 * would show the user a form that silently disagrees with the expression in the field.
 */
export function parseCronExpression(expression) {
  const parts = String(expression || '').trim().split(/\s+/)
  if (parts.length !== 6) return null

  const [sec, min, hour, dom, mon, dow] = parts
  const state = defaultBuilderState()

  // Day-of-week first: the nth-weekday forms identify a monthly schedule on their own.
  let weekdays = null

  if (dow !== '*') {
    const nthMatch = dow.match(RE_NTH)
    const lastMatch = dow.match(RE_LAST_DOW)

    if (nthMatch || lastMatch) {
      if (dom !== '*' || !RE_NUM.test(sec) || sec !== '0') return null
      if (!RE_NUM.test(min) || !RE_NUM.test(hour)) return null

      const monthInterval = readStep(mon)
      if (monthInterval === null) return null

      state.frequency = 'monthly'
      state.monthlyMode = 'nthWeekday'
      state.nthWeekday = Number(nthMatch ? nthMatch[1] : lastMatch[1]) % 7
      state.nth = nthMatch ? Number(nthMatch[2]) : 5
      state.monthInterval = monthInterval
      state.hour = Number(hour)
      state.minute = Number(min)
      return state
    }

    const expanded = expandField(dow, 0, 7)
    if (!expanded) return null
    // Cron accepts both 0 and 7 for Sunday; the builder only ever shows 0.
    weekdays = [...new Set(expanded.map((d) => (d === 7 ? 0 : d)))].sort((a, b) => a - b)
  }

  // Sub-minute schedules.
  if (sec === '*' || RE_STEP.test(sec)) {
    if (min !== '*' || dom !== '*' || mon !== '*') return null
    state.frequency = 'seconds'
    state.secondInterval = sec === '*' ? 1 : Number(sec.match(RE_STEP)[1])
    if (!applyHourWindow(state, hour)) return null
    applyWeekdays(state, weekdays)
    return state
  }

  // Every other shape the builder emits pins the second to zero.
  if (sec !== '0') return null

  if (min === '*' || RE_STEP.test(min)) {
    if (dom !== '*' || mon !== '*') return null
    state.frequency = 'minutes'
    state.minuteInterval = min === '*' ? 1 : Number(min.match(RE_STEP)[1])
    if (!applyHourWindow(state, hour)) return null
    applyWeekdays(state, weekdays)
    return state
  }

  if (!RE_NUM.test(min)) return null
  state.minute = Number(min)

  if (hour === '*' || RE_STEP.test(hour) || RE_RANGE_STEP.test(hour) || RE_RANGE.test(hour)) {
    if (dom !== '*' || mon !== '*') return null

    state.frequency = 'hourly'
    state.hourMinute = state.minute

    if (hour === '*') {
      state.hourInterval = 1
    } else if (RE_STEP.test(hour)) {
      state.hourInterval = Number(hour.match(RE_STEP)[1])
    } else if (RE_RANGE_STEP.test(hour)) {
      const match = hour.match(RE_RANGE_STEP)
      state.restrictHours = true
      state.hourFrom = Number(match[1])
      state.hourTo = Number(match[2])
      state.hourInterval = Number(match[3])
    } else {
      const match = hour.match(RE_RANGE)
      state.restrictHours = true
      state.hourFrom = Number(match[1])
      state.hourTo = Number(match[2])
      state.hourInterval = 1
    }

    applyWeekdays(state, weekdays)
    return state
  }

  if (!RE_NUM.test(hour)) return null
  state.hour = Number(hour)

  if (weekdays) {
    if (dom !== '*' || mon !== '*') return null
    state.frequency = 'weekly'
    state.weekdays = weekdays
    return state
  }

  // A fixed month pins the schedule to one date a year.
  if (RE_NUM.test(mon)) {
    if (dom !== 'L' && !RE_NUM.test(dom)) return null
    state.frequency = 'yearly'
    state.month = Number(mon)
    if (dom === 'L') state.lastDayOfMonth = true
    else state.dayOfMonth = Number(dom)
    return state
  }

  const monthInterval = readStep(mon)
  if (monthInterval === null) return null

  if (dom === 'L' || RE_NUM.test(dom)) {
    state.frequency = 'monthly'
    state.monthlyMode = 'dayOfMonth'
    state.monthInterval = monthInterval
    if (dom === 'L') state.lastDayOfMonth = true
    else state.dayOfMonth = Number(dom)
    return state
  }

  if (monthInterval !== 1) return null

  const dayInterval = readStep(dom)
  if (dayInterval === null) return null

  state.frequency = 'daily'
  state.dayInterval = dayInterval
  return state
}

// ── Occurrence preview ───────────────────────────────────────────────────────

const DAY_MS = 86400000

const lastDateOfMonth = (date) =>
  new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + 1, 0)).getUTCDate()

/**
 * A predicate for the day-of-month field, or null when it places no restriction.
 * Undefined means the field is outside what this can evaluate.
 */
function dayOfMonthMatcher(field) {
  if (field === '*') return null
  if (field === 'L') return (date) => date.getUTCDate() === lastDateOfMonth(date)

  const values = expandField(field, 1, 31)
  if (!values) return undefined

  const set = new Set(values)
  return (date) => set.has(date.getUTCDate())
}

/** The same for day-of-week, including the `n#k` and `nL` forms. */
function dayOfWeekMatcher(field) {
  if (field === '*') return null

  const lastMatch = field.match(RE_LAST_DOW)
  if (lastMatch) {
    const weekday = Number(lastMatch[1]) % 7
    // The last one of its kind is the one with no further same weekday in the month.
    return (date) => date.getUTCDay() === weekday && date.getUTCDate() + 7 > lastDateOfMonth(date)
  }

  const nthMatch = field.match(RE_NTH)
  if (nthMatch) {
    const weekday = Number(nthMatch[1]) % 7
    const nth = Number(nthMatch[2])
    // Days 1-7 hold the first of every weekday, 8-14 the second, and so on.
    return (date) => date.getUTCDay() === weekday && Math.ceil(date.getUTCDate() / 7) === nth
  }

  const values = expandField(field, 0, 7)
  if (!values) return undefined

  const set = new Set(values.map((d) => (d === 7 ? 0 : d)))
  return (date) => set.has(date.getUTCDay())
}

/**
 * The next few times an expression fires, in UTC.
 *
 * Returns an empty list rather than throwing for anything it cannot evaluate - the `W`
 * modifier, or a field outside the grammar. The preview is a reassurance, never a gate: a
 * schedule the API accepts has to stay saveable even when this cannot draw it.
 */
export function getNextOccurrences(expression, count = 5, from = new Date()) {
  const parts = String(expression || '').trim().split(/\s+/)
  if (parts.length !== 6) return []

  const [secField, minField, hourField, domField, monField, dowField] = parts

  const seconds = expandField(secField, 0, 59)
  const minutes = expandField(minField, 0, 59)
  const hours = expandField(hourField, 0, 23)
  const months = expandField(monField, 1, 12)
  if (!seconds || !minutes || !hours || !months) return []

  const matchesDayOfMonth = dayOfMonthMatcher(domField)
  const matchesDayOfWeek = dayOfWeekMatcher(dowField)
  if (matchesDayOfMonth === undefined || matchesDayOfWeek === undefined) return []

  const monthSet = new Set(months)

  const startMs = from.getTime()
  const firstDay = Date.UTC(from.getUTCFullYear(), from.getUTCMonth(), from.getUTCDate())
  const results = []

  // Twenty years of days. The bound only bites on the rarest schedules - it takes that long
  // to collect five occurrences of "29 February" - and a day that matches nothing costs two
  // set lookups, so scanning far is cheaper than showing a short list on a leap-day job.
  for (let offset = 0; offset < 7500 && results.length < count; offset++) {
    const day = new Date(firstDay + offset * DAY_MS)
    if (!monthSet.has(day.getUTCMonth() + 1)) continue

    const dom = !matchesDayOfMonth || matchesDayOfMonth(day)
    const dow = !matchesDayOfWeek || matchesDayOfWeek(day)

    // Classic cron semantics: when both day fields are restricted they are OR-ed, and a day
    // matching either one fires. With only one restricted, that one has to match.
    const dayMatches = (!matchesDayOfMonth || !matchesDayOfWeek) ? (dom && dow) : (dom || dow)
    if (!dayMatches) continue

    for (const h of hours) {
      for (const m of minutes) {
        for (const s of seconds) {
          const at = Date.UTC(day.getUTCFullYear(), day.getUTCMonth(), day.getUTCDate(), h, m, s)
          if (at <= startMs) continue

          results.push(new Date(at))
          if (results.length === count) return results
        }
      }
    }
  }

  return results
}
