import api from './api'
import authService from './authService'

const notificationService = {
  /**
   * Fetches account notifications.
   * PATCH /account/notifications
   */
  async getNotifications({ pageIndex = 0, itemCount = 20 } = {}) {
    const user = authService.getCurrentUser()
    const response = await api.patch('/account/notifications', {
      userId: user?.id,
      pageIndex,
      itemCount,
      sorting: {
        sortBy: "Id",
        type: 1 // 1 = Descending
      }
    })
    return response
  },

  /**
   * Marks notifications as seen.
   * PUT /account/notifications/seen
   */
  async markAsSeen({ notificationIdList = [], markAll = false } = {}) {
    const response = await api.put('/account/notifications/seen', {
      notificationIdList,
      markAll,
    })
    return response
  },

  /**
   * Deletes notifications.
   * DELETE /account/notifications
   */
  async deleteNotifications({ notificationIdList = [], deleteAll = false } = {}) {
    const response = await api.delete('/account/notifications', {
      data: {
        notificationIdList,
        deleteAll,
      },
    })
    return response
  },
}

export default notificationService
