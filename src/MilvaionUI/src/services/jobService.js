import api from './api'

// Helper function to wrap value with isUpdated flag
const wrapForUpdate = (value, isUpdated = true) => ({
  value: value ?? null,
  isUpdated
})

export const jobService = {
  // Get all jobs
  getAll: async (params = {}) => {
    const requestBody = {
      pageNumber: 1,
      rowCount: 100000,
      ...params,
      sorting: {
        sortBy: "Id",
        type: 1 // 1 = Descending
      }
    }
    return api.patch('/jobs', requestBody)
  },

  // Get job by ID
  getById: async (jobId) => {
    return api.get('/jobs/job', { params: { jobId } })
  },

  // Create new job
  create: async (jobData) => {
    return api.post('/jobs/job', jobData)
  },

  // Update job - wraps each field with { value, isUpdated } structure
  update: async (id, jobData, updatedFields = null) => {
    // If updatedFields is provided, only mark those as updated
    // Otherwise, mark all provided fields as updated
    const fieldsToUpdate = updatedFields || Object.keys(jobData)

    // Check if cronExpression is provided (for recurring jobs)
    const hasCronExpression = 'cronExpression' in jobData

    const requestBody = {
      id,
      displayName: wrapForUpdate(jobData.displayName, fieldsToUpdate.includes('displayName')),
      description: wrapForUpdate(jobData.description, fieldsToUpdate.includes('description')),
      tags: wrapForUpdate(jobData.tags, fieldsToUpdate.includes('tags')),
      jobType: wrapForUpdate(jobData.selectedJobName || jobData.jobType, fieldsToUpdate.includes('selectedJobName') || fieldsToUpdate.includes('jobType')),
      jobData: wrapForUpdate(jobData.jobData, fieldsToUpdate.includes('jobData')),
      cronExpression: wrapForUpdate(jobData.cronExpression || null, hasCronExpression),
      isActive: wrapForUpdate(jobData.isActive, fieldsToUpdate.includes('isActive')),
      concurrentExecutionPolicy: wrapForUpdate(jobData.concurrentExecutionPolicy, fieldsToUpdate.includes('concurrentExecutionPolicy')),
      zombieTimeoutMinutes: wrapForUpdate(jobData.zombieTimeoutMinutes, fieldsToUpdate.includes('zombieTimeoutMinutes')),
      executionTimeoutSeconds: wrapForUpdate(jobData.executionTimeoutSeconds, fieldsToUpdate.includes('executionTimeoutSeconds')),
      autoDisableSettings: wrapForUpdate(jobData.autoDisableSettings, fieldsToUpdate.includes('autoDisableSettings') || 'autoDisableSettings' in jobData),
    }

    return api.put('/jobs/job', requestBody)
  },

  // Delete job - single ID via query parameter
  delete: async (jobId) => {
    return api.delete('/jobs/job', {
      params: { jobId }
    })
  },

  // Trigger job manually
  trigger: async (jobId, reason = 'Manual trigger by user', force = false, jobData = null) => {
    const payload = { jobId, reason, force }
    if (jobData !== null && jobData !== undefined && jobData !== '') {
      payload.jobData = jobData
    }
    return api.post('/jobs/job/trigger', payload)
  },

  // Get job occurrences with filtering by jobId
  getOccurrences: async (jobId, params = {}) => {
    const requestBody = {
      pageNumber: params.pageNumber || 1,
      rowCount: params.rowCount || 10,
      sorting: {
        sortBy: "CreatedAt",
        type: 1 // 1 = Descending
      },
      filtering: {
        criterias: [
          {
            filterBy: "JobId",
            value: jobId,
            type: 5 // Equals
          }
        ]
      }
    }

    // Add status filtering if provided
    if (params.status !== undefined && params.status !== null) {
      requestBody.filtering.criterias.push({
        filterBy: "Status",
        value: params.status,
        type: 5 // Equals
      })
    }

    // Add search term if provided
    if (params.searchTerm) {
      requestBody.searchTerm = params.searchTerm
    }

    return api.patch('/jobs/occurrences', requestBody)
  },

  // Pause/Resume job (endpoint might not exist yet, keeping for future)
  toggleStatus: async (id, isActive) => {
    return api.patch(`/jobs/${id}/status`, { isActive })
  },
}

export default jobService
