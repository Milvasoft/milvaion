import api from './api'

const wrapForUpdate = (value, isUpdated = true) => ({
  value: value ?? null,
  isUpdated
})

export const userService = {
  getAll: async (params = {}) => {
    const requestBody = {
      pageNumber: params.pageNumber || 1,
      rowCount: params.rowCount || 20,
      sorting: {
        sortBy: params.sortBy || 'Id',
        type: params.sortType || 1
      },
      ...params
    }
    return api.patch('/users', requestBody)
  },

  getById: async (userId) => {
    return api.get('/users/user', { params: { userId } })
  },

  create: async (userData) => {
    return api.post('/users/user', userData)
  },

  update: async (id, userData, updatedFields = null) => {
    const fieldsToUpdate = updatedFields || Object.keys(userData)

    const requestBody = {
      id,
      name: wrapForUpdate(userData.name, fieldsToUpdate.includes('name')),
      surname: wrapForUpdate(userData.surname, fieldsToUpdate.includes('surname')),
      lockout: wrapForUpdate(userData.lockout ?? false, fieldsToUpdate.includes('lockout')),
      newPassword: wrapForUpdate(userData.newPassword, fieldsToUpdate.includes('newPassword')),
      roleIdList: wrapForUpdate(userData.roleIdList, fieldsToUpdate.includes('roleIdList')),
      allowedNotifications: wrapForUpdate(userData.allowedNotifications, fieldsToUpdate.includes('allowedNotifications'))
    }

    return api.put('/users/user', requestBody)
  },

  delete: async (userId) => {
    return api.delete('/users/user', { params: { userId } })
  }
}

export default userService
