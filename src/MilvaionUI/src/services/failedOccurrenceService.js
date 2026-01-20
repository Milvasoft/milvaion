import api from './api'

// Helper function to wrap value with isUpdated flag
const wrapForUpdate = (value, isUpdated = true) => ({
  value: value ?? null,
  isUpdated
})

export const failedOccurrenceService = {
  // Get all failed occurrences
  getAll: async (params = {}) => {
    const requestBody = {
      pageNumber: params.pageNumber || 1,
      rowCount: params.rowCount || 20,
      ...params,
      sorting: {
        sortBy: "Id",
        type: 1 // 1 = Descending
      }
    }
    return api.patch('/jobs/occurrences/failed', requestBody)
  },

  // Get failed occurrence by ID
  getById: async (failedOccurrenceId) => {
    return api.get('/jobs/occurrences/occurrence/failed', { params: { failedOccurrenceId } })
  },

  // Update failed occurrence (single or bulk)
  update: async (idOrIds, updateData, updatedFields = null) => {
    const idList = Array.isArray(idOrIds) ? idOrIds : [idOrIds]
    const fieldsToUpdate = updatedFields || Object.keys(updateData)

    const requestBody = {
      idList,
      resolved: wrapForUpdate(updateData.resolved, fieldsToUpdate.includes('resolved')),
      resolvedBy: wrapForUpdate(updateData.resolvedBy, fieldsToUpdate.includes('resolvedBy')),
      resolutionNote: wrapForUpdate(updateData.resolutionNote, fieldsToUpdate.includes('resolutionNote')),
      resolutionAction: wrapForUpdate(updateData.resolutionAction, fieldsToUpdate.includes('resolutionAction')),
    }

    return api.put('/jobs/occurrences/occurrence/failed', requestBody)
  },

  // Mark as resolved (single or bulk)
  markAsResolved: async (idOrIds, resolutionNote, resolutionAction = 'Manually resolved') => {
    return failedOccurrenceService.update(idOrIds, {
      resolved: true,
      resolutionNote,
      resolutionAction
    }, ['resolved', 'resolutionNote', 'resolutionAction'])
  },

  // Delete failed occurrence - supports bulk delete
  delete: async (failedOccurrenceIdOrIds) => {
    const failedOccurrenceIdList = Array.isArray(failedOccurrenceIdOrIds) ? failedOccurrenceIdOrIds : [failedOccurrenceIdOrIds]
    return api.delete('/jobs/occurrences/occurrence/failed', { 
      data: { failedOccurrenceIdList } 
    })
  },

  // Get failure type display info
  getFailureTypeInfo: (failureType) => {
    const failureTypeMap = {
      0: { icon: 'help', label: 'Unknown', className: 'failure-unknown', color: '#757575' },
      1: { icon: 'replay', label: 'Max Retries Exceeded', className: 'failure-retries', color: '#ff9800' },
      2: { icon: 'timer_off', label: 'Timeout', className: 'failure-timeout', color: '#ff5722' },
      3: { icon: 'computer', label: 'Worker Crash', className: 'failure-crash', color: '#f44336' },
      4: { icon: 'error', label: 'Invalid Job Data', className: 'failure-data', color: '#9c27b0' },
      5: { icon: 'cloud_off', label: 'External Dependency Failure', className: 'failure-external', color: '#e91e63' },
      6: { icon: 'bug_report', label: 'Unhandled Exception', className: 'failure-exception', color: '#d32f2f' },
      7: { icon: 'cancel', label: 'Cancelled', className: 'failure-cancelled', color: '#607d8b' },
      8: { icon: 'warning', label: 'Zombie Detection', className: 'failure-zombie', color: '#795548' },
    }

    return failureTypeMap[failureType] || failureTypeMap[0]
  }
}

// Backwards compatibility alias
export const failedJobService = failedOccurrenceService

export default failedOccurrenceService
