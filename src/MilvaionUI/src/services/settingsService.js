import api from './api'

/**
 * Application settings. The public endpoint is anonymous (login page + tab title need it
 * before auth); get/update require a Manager.
 */
const settingsService = {
  /** Non-secret branding subset, safe to call before authentication. */
  async getPublicSettings() {
    const res = await api.get('/settings/public')
    return res.data?.data ?? res.data
  },

  /** Full settings document (admin). */
  async getSettings() {
    const res = await api.get('/settings')
    return res.data?.data ?? res.data
  },

  /**
   * Update settings. Sectioned; a null section is left unchanged.
   * @param {{ branding?: { title?: string, subtitle?: string } }} payload
   */
  async updateSettings(payload) {
    const res = await api.put('/settings', payload)
    return res.data?.data ?? res.data
  },
}

export default settingsService
