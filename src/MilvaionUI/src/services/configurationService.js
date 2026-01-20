import api from './api'

const configurationService = {
  /**
   * Get system configuration
   * @returns {Promise} System configuration
   */
  async getConfiguration() {
    const response = await api.get('/admin/configuration')
    return response.data
  },
}

export default configurationService
