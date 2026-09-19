import api from './api'

/**
 * Worker execution logs.
 *
 * Two read endpoints sit under the jobs controller:
 *  - `GET /jobs/logs`         one page of raw log lines, newest first, with rich filters.
 *  - `GET /jobs/logs/summary` counts over a time window, for the header strip.
 *
 * Both take their filters as query parameters. Empty values are dropped so the URL only
 * carries the filters the user actually set - the API binds by name and ignores the rest,
 * but a clean query is easier to read in the network tab when something looks wrong.
 */
const clean = (params = {}) => {
  const out = {}
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') out[key] = value
  }
  return out
}

export const logService = {
  // A page of log lines. Returns { logs, totalCount, pageNumber, hasMore, dataIncluded }.
  search: (params = {}) => api.get('/jobs/logs', { params: clean(params) }),

  // Aggregated counts over a window. Returns the summary DTO.
  summary: (params = {}) => api.get('/jobs/logs/summary', { params: clean(params) }),
}

export default logService
