import api from './api'

const dashboardService = {
  /**
   * Get dashboard statistics
   * @returns {Promise} Dashboard statistics
   */
  async getStatistics() {
    const response = await api.get('/dashboard')
    return response
  },

  /**
   * Get health check for all services (Database, Redis, RabbitMQ)
   * @returns {Promise} Health check status for all services
   */
  async getHealthChecks() {
    const response = await api.get('/healthcheck/ready')
    return response
  },
}

export default dashboardService
