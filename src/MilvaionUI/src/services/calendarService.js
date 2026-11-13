import api from './api'

// Exclusion calendars: named sets of dates, date ranges and weekdays on which the jobs that reference them are
// skipped. Update replaces the whole calendar, so always send every field.
export const calendarService = {
  getAll: async (params = {}) => {
    const requestBody = {
      pageNumber: params.pageNumber || 1,
      rowCount: params.rowCount || 100,
      sorting: {
        sortBy: params.sortBy || 'Name',
        type: params.sortType ?? 0
      },
      ...params
    }
    return api.patch('/exclusionCalendars', requestBody)
  },

  getById: async (calendarId) => {
    return api.get('/exclusionCalendars/calendar', { params: { calendarId } })
  },

  create: async (calendar) => {
    return api.post('/exclusionCalendars/calendar', calendar)
  },

  update: async (id, calendar) => {
    return api.put('/exclusionCalendars/calendar', { id, ...calendar })
  },

  delete: async (calendarId) => {
    return api.delete('/exclusionCalendars/calendar', { params: { calendarId } })
  }
}

export default calendarService
