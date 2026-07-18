import api from './api'

const wrapForUpdate = (value, isUpdated = true) => ({
  value: value ?? null,
  isUpdated
})

export const apiKeyService = {
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
    return api.patch('/apiKeys', requestBody)
  },

  getById: async (apiKeyId) => {
    return api.get('/apiKeys/apiKey', { params: { apiKeyId } })
  },

  // The generated key is present only in this response. It is not persisted and cannot be fetched again.
  create: async (apiKeyData) => {
    return api.post('/apiKeys/apiKey', apiKeyData)
  },

  update: async (id, apiKeyData, updatedFields = null) => {
    const fieldsToUpdate = updatedFields || Object.keys(apiKeyData)

    const requestBody = {
      id,
      name: wrapForUpdate(apiKeyData.name, fieldsToUpdate.includes('name')),
      description: wrapForUpdate(apiKeyData.description, fieldsToUpdate.includes('description')),
      permissionIdList: wrapForUpdate(apiKeyData.permissionIdList, fieldsToUpdate.includes('permissionIdList'))
    }

    return api.put('/apiKeys/apiKey', requestBody)
  },

  revoke: async (apiKeyId) => {
    return api.post('/apiKeys/apiKey/revoke', { apiKeyId })
  },

  delete: async (apiKeyId) => {
    return api.delete('/apiKeys/apiKey', { params: { apiKeyId } })
  }
}

export default apiKeyService
