import api from './api'

export const occurrenceService = {
  // Get all occurrences with pagination
  getAll: async (params = {}) => {
    const requestBody = {
      pageNumber: params.pageNumber || 1,
      rowCount: params.rowCount || 10,
      sorting: {
        sortBy: params.sortBy || 'Id',
        type: params.sortType || 1, // 1 = Descending
      },
    }

    // Add search term if provided
    if (params.searchTerm) {
      requestBody.searchTerm = params.searchTerm
    }

    // Add filtering if provided
    if (params.filtering) {
      requestBody.filtering = params.filtering
    } else if (params.jobId) {
      // Legacy: Add filtering if jobId is provided
      requestBody.filtering = {
        criterias: [
          {
            filterBy: 'JobId',
            value: params.jobId,
            type: 5, // Equals
          },
        ],
      }
    }

    return api.patch('/jobs/occurrences', requestBody)
  },

  // Get occurrences by job ID with pagination
  getByJobId: async (jobId, params = {}) => {
    const requestBody = {
      pageNumber: params.pageNumber || 1,
      rowCount: params.rowCount || 10,
      sorting: {
        sortBy: 'Id',
        type: 1, // 1 = Descending
      },
      filtering: {
        criterias: [
          {
            filterBy: 'JobId',
            value: jobId,
            type: 5, // Equals
          },
        ],
      },
    }
    return api.patch('/jobs/occurrences', requestBody)
  },

  // Get occurrence by ID
  getById: async (occurrenceId) => {
    return api.get('/jobs/occurrences/occurrence', { params: { occurrenceId } })
  },

  // Get occurrence logs
  getLogs: async (occurrenceId) => {
    return api.get('/jobs/occurrences/occurrence', { params: { occurrenceId } })
  },

  // Cancel running occurrence
  cancel: async (occurrenceId, reason) => {
    return api.post('/jobs/occurrences/cancel', { occurrenceId, reason })
  },

  // Delete occurrence (only completed/failed/cancelled) - supports bulk delete
  delete: async (occurrenceIdOrIds) => {
    const occurrenceIdList = Array.isArray(occurrenceIdOrIds) ? occurrenceIdOrIds : [occurrenceIdOrIds]
    return api.delete('/jobs/occurrences/occurrence', {
      data: { occurrenceIdList }
    })
  },
}

export default occurrenceService
