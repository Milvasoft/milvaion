import api from './api'

export const workerService = {
  // Get all workers
  getAll: async (params = {}) => {
    const requestBody = {
      pageNumber: 1,
      rowCount: 100000,
      ...params
    }
    return api.patch('/workers', requestBody)
  },

  // Get worker by ID
  getById: async (workerId) => {
    return api.get('/workers/worker', { params: { workerId } })
  },

  // Delete worker
  delete: async (workerId) => {
    return api.delete('/workers/worker', { params: { workerId } })
  }
}

export default workerService
