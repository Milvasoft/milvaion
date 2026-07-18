import api from './api'

const wrapForUpdate = (value, isUpdated = true) => ({
  value: value ?? null,
  isUpdated
})

// The update endpoint takes every field wrapped so partial updates are possible. Callers keep passing a plain
// object; the wrapping happens here, and only the keys actually present are marked as updated.
const buildUpdatePayload = (workflowId, workflowData) => {
  const payload = { workflowId }

  const fields = [
    'name',
    'description',
    'tags',
    'isActive',
    'failureStrategy',
    'maxStepRetries',
    'timeoutSeconds',
    'cronExpression',
    'steps',
    'edges'
  ]

  for (const field of fields) {
    const supplied = Object.hasOwn(workflowData, field)
    payload[field] = wrapForUpdate(supplied ? workflowData[field] : null, supplied)
  }

  return payload
}

export const workflowService = {
  // Get all workflows
  getAll: async (params = {}) => {
    const requestBody = {
      pageNumber: 1,
      rowCount: 100000,
      ...params,
      sorting: {
        sortBy: "Id",
        type: 1
      }
    }
    return api.patch('/workflows', requestBody)
  },

  // Get workflow by ID
  getById: async (workflowId) => {
    return api.get('/workflows/workflow', { params: { workflowId } })
  },

  // Create new workflow
  create: async (workflowData) => {
    return api.post('/workflows/workflow', workflowData)
  },

  // Update workflow. Only the keys present in workflowData are changed.
  update: async (workflowId, workflowData) => {
    return api.put('/workflows/workflow', buildUpdatePayload(workflowId, workflowData))
  },

  // Pause or resume a workflow without touching its definition. Works while runs are in progress, which a full
  // definition update deliberately does not.
  setActive: async (workflowId, isActive) => {
    return api.put('/workflows/workflow', buildUpdatePayload(workflowId, { isActive }))
  },

  // Delete workflow
  delete: async (workflowId) => {
    return api.delete('/workflows/workflow', {
      params: { WorkflowId: workflowId }
    })
  },

  // Trigger workflow run
  trigger: async (workflowId, reason = 'Manual trigger', stepJobData = null) => {
    const payload = { workflowId, reason }
    if (stepJobData && Object.keys(stepJobData).length > 0)
      payload.stepJobData = stepJobData
    return api.post('/workflows/workflow/trigger', payload)
  },

  // Cancel workflow run
  cancelRun: async (workflowRunId, reason = 'Manual cancellation') => {
    return api.post('/workflows/workflow/cancel', { workflowRunId, reason })
  },

  // Get workflow runs
  getRuns: async (workflowId = null, params = {}) => {
    const requestBody = {
      pageNumber: params.pageNumber || 1,
      rowCount: params.rowCount || 20,
      workflowId,
      sorting: {
        sortBy: "Id",
        type: 1
      }
    }
    return api.patch('/workflows/runs', requestBody)
  },

  // Get workflow run detail (with step states for DAG visualization)
  getRunDetail: async (runId) => {
    return api.get('/workflows/runs/run', { params: { runId } })
  },
}

export default workflowService
