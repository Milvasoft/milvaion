import { useState, useEffect, useCallback, useRef, useMemo } from 'react'
import { useNavigate, useParams, Link } from 'react-router-dom'
import workflowService from '../../services/workflowService'
import jobService from '../../services/jobService'
import workerService from '../../services/workerService'
import JobSelect from '../../components/JobSelect'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import CronExpressionInput from '../../components/CronExpressionInput'
import DataMappingEditor, { parseSchemaFields } from './DataMappingEditor'
import './WorkflowForm.css'
import { SkeletonForm } from '../../components/Skeleton'

const failureStrategies = [
  { value: 0, label: 'Stop on First Failure' },
  { value: 1, label: 'Continue on Failure' },
  { value: 2, label: 'Retry then Stop' },
  { value: 3, label: 'Retry then Continue' },
]

const nodeTypes = [
  { value: 0, label: 'Task', icon: 'work' },
  { value: 1, label: 'Condition', icon: 'fork_right' },
  { value: 2, label: 'Merge', icon: 'merge' },
]

function WorkflowForm() {
  const tempIdCounter = useRef(1)
  const { id } = useParams()
  const navigate = useNavigate()
  const isEditMode = !!id
  const { modalProps, showSuccess, showError } = useModal()

  const [jobs, setJobs] = useState([])
  const [workers, setWorkers] = useState([])
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [tagInput, setTagInput] = useState('')

  const [form, setForm] = useState({
    name: '',
    description: '',
    tags: [],
    isActive: true,
    failureStrategy: 0,
    maxStepRetries: 0,
    timeoutSeconds: '',
    cronExpression: '',
  })

  const [steps, setSteps] = useState([])
  const [edges, setEdges] = useState([])

  /*
   * A first page only. This used to request every job in the system to fill one dropdown;
   * the picker now searches the server, and jobs it returns are merged into this array so
   * the lookups elsewhere on the page keep resolving.
   */
  const loadJobs = useCallback(async () => {
    try {
      const [jobsRes, workersRes] = await Promise.all([
        jobService.getAll({ pageNumber: 1, rowCount: 50 }),
        workerService.getAll(),
      ])
      setJobs(jobsRes?.data?.data || jobsRes?.data || [])
      setWorkers(workersRes?.data || [])
    } catch {
      // ignore
    }
  }, [])

  // Keeps a job the picker returned available to the rest of the page - node labels and
  // schema previews look their job up in this array.
  const rememberJob = useCallback((job) => {
    if (!job) return

    setJobs(current => current.some(j => j.id === job.id) ? current : [...current, job])
  }, [])

  const schemasMap = useMemo(() => {
    const map = {}
    for (const worker of workers) {
      for (const jobName of (worker.jobNames || [])) {
        map[jobName] = {
          dataFields: parseSchemaFields(worker.jobDataDefinitions?.[jobName]),
          resultFields: parseSchemaFields(worker.jobResultDefinitions?.[jobName]),
        }
      }
    }
    return map
  }, [workers])

  // Load workflow data for edit mode
  const loadWorkflow = useCallback(async () => {
    try {
      setLoading(true)
      const response = await workflowService.getById(id)
      const data = response.data

      setForm({
        name: data.name || '',
        description: data.description || '',
        tags: data.tags ? data.tags.split(',').map(t => t.trim()).filter(t => t) : [],
        isActive: data.isActive,
        failureStrategy: data.failureStrategy ?? 0,
        maxStepRetries: data.maxStepRetries || 0,
        timeoutSeconds: data.timeoutSeconds || '',
        cronExpression: data.cronExpression || '',
      })

      // Load steps
      if (data.steps && data.steps.length > 0) {

        setSteps(data.steps.map(s => ({
          tempId: s.id ? s.id.toString() : `step-${tempIdCounter.current++}`,
          nodeType: s.nodeType ?? 0,
          jobId: s.jobId || null,
          stepName: s.stepName || '',
          order: s.order || 0,
          nodeConfigJson: s.nodeConfigJson || '',
          delaySeconds: s.delaySeconds || 0,
          jobDataOverride: s.jobDataOverride || '',
          dataMappings: s.dataMappings ? deserializeMappings(s.dataMappings) : [],
          positionX: s.positionX,
          positionY: s.positionY,
        })))
      }

      // Load edges
      if (data.edges && data.edges.length > 0) {
        setEdges(data.edges.map(e => ({
          tempId: e.id ? e.id.toString() : `edge-${tempIdCounter.current++}`,
          sourceTempId: e.sourceStepId.toString(),
          targetTempId: e.targetStepId.toString(),
          sourcePort: e.sourcePort || '',
          targetPort: e.targetPort || '',
          label: e.label || '',
          order: e.order || 0,
        })))
      }
    } catch (err) {
      showError('Failed to load workflow')
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [id, showError])

  useEffect(() => {
    const initializeForm = async () => {
      await loadJobs()
      if (isEditMode) {
        await loadWorkflow()
      }
    }
    initializeForm()
  }, [isEditMode, loadJobs, loadWorkflow])

  const handleFormChange = (field, value) => {
    setForm(prev => ({ ...prev, [field]: value }))
  }

  const handleAddTag = (e) => {
    if (e.type === 'keydown' && e.key !== 'Enter') return
    e.preventDefault()
    const tag = tagInput.trim()
    if (tag && !form.tags.includes(tag)) {
      setForm(prev => ({ ...prev, tags: [...prev.tags, tag] }))
    }
    setTagInput('')
  }

  const handleRemoveTag = (tagToRemove) => {
    setForm(prev => ({ ...prev, tags: prev.tags.filter(t => t !== tagToRemove) }))
  }

  // Step management
  const addStep = (nodeType = 0) => {
    setSteps(prev => [
      ...prev,
      {
        tempId: `step-${tempIdCounter.current++}`,
        nodeType,
        jobId: null,
        stepName: '',
        order: prev.length + 1,
        nodeConfigJson: '',
        delaySeconds: 0,
        jobDataOverride: '',
        dataMappings: [],
        positionX: null,
        positionY: null,
      }
    ])
  }

  const updateStep = (index, field, value) => {
    setSteps(prev => prev.map((s, i) => i === index ? { ...s, [field]: value } : s))
  }

  const removeStep = (index) => {
    const removedTempId = steps[index].tempId
    setSteps(prev => {
      const updated = prev.filter((_, i) => i !== index)
      return updated.map((s, i) => ({ ...s, order: i + 1 }))
    })
    setEdges(prev => prev.filter(e => e.sourceTempId !== removedTempId && e.targetTempId !== removedTempId))
  }

  // Edge management
  const addEdge = () => {
    setEdges(prev => [
      ...prev,
      {
        tempId: `edge-${tempIdCounter.current++}`,
        sourceTempId: '',
        targetTempId: '',
        sourcePort: '',
        targetPort: '',
        label: '',
        order: prev.length + 1,
      }
    ])
  }

  const updateEdge = (index, field, value) => {
    setEdges(prev => prev.map((e, i) => i === index ? { ...e, [field]: value } : e))
  }

  const removeEdge = (index) => {
    setEdges(prev => {
      const updated = prev.filter((_, i) => i !== index)
      return updated.map((e, i) => ({ ...e, order: i + 1 }))
    })
  }

  const handleMappingsChange = (stepIndex, newMappings) => {
    setSteps(prev => prev.map((s, i) => i === stepIndex ? { ...s, dataMappings: newMappings } : s))
  }

  // Deserialize dataMappings from backend JSON format to array for editing
  const deserializeMappings = (dataMappingsJson) => {
    if (!dataMappingsJson) return []
    try {
      const obj = typeof dataMappingsJson === 'string' ? JSON.parse(dataMappingsJson) : dataMappingsJson
      return Object.entries(obj).map(([sourceKey, targetPath]) => {
        const [sourceStepTempId, sourcePath] = sourceKey.includes(':')
          ? sourceKey.split(':', 2)
          : ['', sourceKey]
        return { sourceStepTempId, sourcePath, targetPath }
      })
    } catch {
      return []
    }
  }

  // Serialize dataMappings array into the JSON format the backend expects:
  // { "stepTempId:sourcePath": "targetPath" } or { "sourcePath": "targetPath" }
  const serializeMappings = (mappings) => {
    if (!mappings || mappings.length === 0) return null
    const obj = {}
    for (const m of mappings) {
      if (!m.sourcePath || !m.targetPath) continue
      const sourceKey = m.sourceStepTempId ? `${m.sourceStepTempId}:${m.sourcePath}` : m.sourcePath
      obj[sourceKey] = m.targetPath
    }
    return Object.keys(obj).length > 0 ? JSON.stringify(obj) : null
  }

  const handleSubmit = async (e) => {
    e.preventDefault()

    if (!form.name.trim()) {
      showError('Workflow name is required.')
      return
    }

    if (steps.length === 0) {
      showError('Add at least one step.')
      return
    }

    for (const step of steps) {
      if (step.nodeType === 0 && !step.jobId) {
        showError(`Task step "${step.stepName || step.order}" has no job selected.`)
        return
      }
      if (!step.stepName.trim()) {
        showError(`Step #${step.order} needs a name.`)
        return
      }
    }

    for (const edge of edges) {
      if (!edge.sourceTempId || !edge.targetTempId) {
        showError(`Edge #${edge.order} is missing source or target step.`)
        return
      }
    }

    try {
      setSaving(true)

      const payload = {
        name: form.name.trim(),
        description: form.description.trim(),
        tags: form.tags.join(','),
        isActive: form.isActive,
        failureStrategy: form.failureStrategy,
        maxStepRetries: form.maxStepRetries,
        timeoutSeconds: form.timeoutSeconds ? parseInt(form.timeoutSeconds) : null,
        cronExpression: form.cronExpression?.trim() || null,
        steps: steps.map(s => ({
          tempId: s.tempId,
          nodeType: s.nodeType,
          jobId: s.jobId,
          stepName: s.stepName,
          order: s.order,
          nodeConfigJson: s.nodeConfigJson || null,
          dataMappings: serializeMappings(s.dataMappings),
          delaySeconds: s.delaySeconds || 0,
          jobDataOverride: s.jobDataOverride || null,
          positionX: s.positionX ?? null,
          positionY: s.positionY ?? null,
        })),
        edges: edges.map(e => ({
          tempId: e.tempId,
          sourceTempId: e.sourceTempId,
          targetTempId: e.targetTempId,
          sourcePort: e.sourcePort || null,
          targetPort: e.targetPort || null,
          label: e.label || null,
          order: e.order,
          edgeConfigJson: e.edgeConfigJson || null,
        })),
      }

      let result
      if (isEditMode) {
        result = await workflowService.update(id, payload)
      } else {
        result = await workflowService.create(payload)
      }

      if (result?.isSuccess) {
        showSuccess(isEditMode ? 'Workflow updated successfully!' : 'Workflow created successfully!')
        setTimeout(() => navigate(`/workflows/${isEditMode ? id : result.data}`), 1000)
      } else {
        showError(result?.message || `Failed to ${isEditMode ? 'update' : 'create'} workflow.`)
      }
    } catch (err) {
      showError(err?.response?.data?.message || `Failed to ${isEditMode ? 'update' : 'create'} workflow.`)
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return (
      <div className="page workflow-form-page">
        <div className="page-header">
          <h1><Icon name="account_tree" size={28} /> Workflow</h1>
        </div>
        <SkeletonForm fields={6} />
      </div>
    )
  }

  return (
    <div className="page workflow-form-page">
      <div className="wf-form-header">
        <div className="wf-form-header-left">
          <Link to={isEditMode ? `/workflows/${id}` : "/workflows"} className="wf-back-btn" title="Back">
            <Icon name="arrow_back" size={24} />
          </Link>
          <div className="wf-form-header-content">
            <h1>{isEditMode ? 'Edit Workflow' : 'Create Workflow'}</h1>
            <p className="wf-form-subtitle">{isEditMode ? 'Modify workflow configuration and steps' : 'Define a workflow by chaining jobs into a DAG pipeline'}</p>
          </div>
        </div>
        <div className="wf-form-actions">
          <Link to={isEditMode ? `/workflows/${id}` : "/workflows"} className="wf-cancel-btn">Cancel</Link>
          <button type="submit" form="wf-form" className="wf-submit-btn" disabled={saving}>
            {saving ? (
              <><Icon name="hourglass_empty" size={18} /> {isEditMode ? 'Updating...' : 'Creating...'}</>
            ) : (
              <><Icon name="check" size={18} /> {isEditMode ? 'Update Workflow' : 'Create Workflow'}</>
            )}
          </button>
        </div>
      </div>

      <form id="wf-form" onSubmit={handleSubmit} className="workflow-form">
        <div className="wf-top-row">
          {/* General Info */}
          <div className="form-section">
            <h2><Icon name="info" size={20} /> General</h2>
            <div className="wf-general-fields">
              <div className="form-group">
                <label htmlFor="wf-name">Workflow Name *</label>
                <input
                  id="wf-name"
                  type="text"
                  value={form.name}
                  onChange={e => handleFormChange('name', e.target.value)}
                  placeholder="e.g. Daily Report Pipeline"
                  required
                />
              </div>
              <div className="form-group">
                <label htmlFor="wf-desc">Description</label>
                <textarea
                  id="wf-desc"
                  value={form.description}
                  onChange={e => handleFormChange('description', e.target.value)}
                  placeholder="Describe what this workflow does..."
                  rows={3}
                />
              </div>
              <div className="form-group">
                <label htmlFor="wf-tags">Tags</label>
                <div className="wf-tags-input-container">
                  <div className="wf-tag-input-wrapper">
                    <input
                      type="text"
                      id="wf-tags"
                      value={tagInput}
                      onChange={e => setTagInput(e.target.value)}
                      onKeyDown={handleAddTag}
                      placeholder="Type and press Enter to add tag"
                    />
                    <button type="button" onClick={handleAddTag} className="wf-tag-add-btn">Add</button>
                  </div>
                  {form.tags.length > 0 && (
                    <div className="wf-tags-list">
                      {form.tags.map((tag, i) => (
                        <span key={i} className="wf-tag">
                          {tag}
                          <button type="button" onClick={() => handleRemoveTag(tag)} className="wf-tag-remove" title="Remove tag">×</button>
                        </span>
                      ))}
                    </div>
                  )}
                </div>
              </div>
              <div className="form-group">
                <label className="wf-switch-label">
                  <span className="wf-switch-label-text">Active Status</span>
                  <div className="wf-switch-container">
                    <input
                      type="checkbox"
                      checked={form.isActive}
                      onChange={e => handleFormChange('isActive', e.target.checked)}
                      id="wf-isActive"
                      className="wf-switch-input"
                    />
                    <label htmlFor="wf-isActive" className="wf-switch">
                      <span className="wf-switch-slider"></span>
                    </label>
                    <span className="wf-switch-status">
                      {form.isActive ? 'Active' : 'Inactive'}
                    </span>
                  </div>
                </label>
              </div>
            </div>
          </div>

          {/* Settings */}
          <div className="form-section">
            <h2><Icon name="tune" size={20} /> Settings</h2>
            <div className="wf-settings-fields">
              <div className="form-group">
                <label htmlFor="wf-strategy">Failure Strategy</label>
                <select
                  id="wf-strategy"
                  value={form.failureStrategy}
                  onChange={e => handleFormChange('failureStrategy', parseInt(e.target.value))}
                >
                  {failureStrategies.map(s => (
                    <option key={s.value} value={s.value}>{s.label}</option>
                  ))}
                </select>
              </div>
              <div className="form-group">
                <label htmlFor="wf-retries">Max Step Retries</label>
                <input
                  id="wf-retries"
                  type="number"
                  min="0"
                  value={form.maxStepRetries}
                  onChange={e => handleFormChange('maxStepRetries', parseInt(e.target.value) || 0)}
                />
              </div>
              <div className="form-group">
                <label htmlFor="wf-timeout">Timeout (seconds)</label>
                <input
                  id="wf-timeout"
                  type="number"
                  min="0"
                  value={form.timeoutSeconds}
                  onChange={e => handleFormChange('timeoutSeconds', e.target.value)}
                  placeholder="No timeout"
                />
              </div>
              <div className="form-group wf-cron-field">
                <label htmlFor="wf-cron">Schedule (Cron Expression)</label>
                <CronExpressionInput
                  value={form.cronExpression}
                  onChange={(e) => handleFormChange('cronExpression', e.target.value)}
                />
                <small className="wf-form-hint">Leave empty for manual-only trigger</small>
              </div>

            </div>
          </div>
        </div>

        {/* Steps */}
        <div className="form-section">
          <div className="section-header">
            <h2><Icon name="layers" size={20} /> Steps ({steps.length})</h2>
            <div className="wf-add-step-buttons">
              {nodeTypes.map(nt => (
                <button
                  key={nt.value}
                  type="button"
                  className="wf-add-step-btn"
                  onClick={() => addStep(nt.value)}
                  title={`Add ${nt.label} node`}
                >
                  <Icon name={nt.icon} size={16} /> {nt.label}
                </button>
              ))}
            </div>
          </div>

          {steps.length === 0 ? (
            <div className="empty-steps">
              <Icon name="layers" size={40} />
              <p>No steps yet. Click &quot;Add Step&quot; to build your workflow.</p>
            </div>
          ) : (
            <div className="steps-list">
              {steps.map((step, index) => {
                const nodeTypeInfo = nodeTypes.find(nt => nt.value === step.nodeType) || nodeTypes[0]
                const isTaskNode = step.nodeType === 0
                const isConditionNode = step.nodeType === 1

                return (
                  <div key={step.tempId} className={`step-card step-card-${nodeTypeInfo.label.toLowerCase()}`}>
                    <div className="step-card-header">
                      <div className="step-card-header-left">
                        <Icon name={nodeTypeInfo.icon} size={20} />
                        <span className="step-type-badge">{nodeTypeInfo.label}</span>
                        <span className="step-number">#{step.order}</span>
                        <span className="step-temp-id">{step.tempId}</span>
                      </div>
                      <button
                        type="button"
                        className="wf-remove-step-btn"
                        onClick={() => removeStep(index)}
                        title="Remove step"
                      >
                        <Icon name="close" size={18} />
                      </button>
                    </div>

                    <div className="step-form-grid">
                      <div className="form-group">
                        <label>Step Name *</label>
                        <input
                          type="text"
                          value={step.stepName}
                          onChange={e => updateStep(index, 'stepName', e.target.value)}
                          placeholder={`e.g. ${nodeTypeInfo.label} Step`}
                        />
                      </div>

                      {isTaskNode && (
                        <>
                          <div className="form-group">
                            <label>Job *</label>
                            <JobSelect
                              value={step.jobId || ''}
                              knownJobs={jobs}
                              onChange={(jobId, job) => {
                                rememberJob(job)
                                updateStep(index, 'jobId', jobId)
                              }}
                            />
                          </div>
                          <div className="form-group">
                            <label>Delay (seconds)</label>
                            <input
                              type="number"
                              min="0"
                              value={step.delaySeconds}
                              onChange={e => updateStep(index, 'delaySeconds', parseInt(e.target.value) || 0)}
                            />
                          </div>
                        </>
                      )}

                      {isConditionNode && (
                        <div className="form-group form-group-full">
                          <label>Condition Expression</label>
                          <input
                            type="text"
                            value={(() => {
                              try {
                                return step.nodeConfigJson ? (JSON.parse(step.nodeConfigJson).expression || '') : ''
                              } catch {
                                return ''
                              }
                            })()}
                            onChange={e => updateStep(index, 'nodeConfigJson', JSON.stringify({ expression: e.target.value }))}
                            placeholder='e.g. @status == "Completed" || $.price > 100'
                          />
                          <small className="wf-form-hint">Supports: @status checks, $.field comparisons, && (AND), || (OR)</small>
                        </div>
                      )}

                      {isTaskNode && (
                        <>
                          <div className="form-group form-group-full">
                            <label>Job Data Override (JSON)</label>
                            <textarea
                              value={step.jobDataOverride}
                              onChange={e => updateStep(index, 'jobDataOverride', e.target.value)}
                              placeholder='{"key": "value"}'
                              rows={2}
                            />
                          </div>
                        </>
                      )}
                    </div>

                    {/* Data Mappings for Task nodes */}
                    {isTaskNode && (
                      <div className="step-data-mappings">
                        <DataMappingEditor
                          mappings={step.dataMappings}
                          onChange={(newMappings) => handleMappingsChange(index, newMappings)}
                          steps={steps}
                          currentStepTempId={step.tempId}
                          jobs={jobs}
                          currentStepJobId={step.jobId}
                          schemasMap={schemasMap}
                        />
                      </div>
                    )}
                  </div>
                )
              })}
            </div>
          )}
        </div>

        {/* Edges */}
        <div className="form-section">
          <div className="section-header">
            <h2><Icon name="fork_right" size={20} /> Connections ({edges.length})</h2>
            <button type="button" className="wf-add-step-btn" onClick={addEdge}>
              <Icon name="add" size={16} /> Add Connection
            </button>
          </div>

          {edges.length === 0 ? (
            <div className="empty-steps">
              <Icon name="fork_right" size={40} />
              <p>No connections yet. Add edges to define workflow flow.</p>
            </div>
          ) : (
            <div className="edges-list">
              {edges.map((edge, index) => (
                <div key={edge.tempId} className="edge-card">
                  <div className="edge-card-header">
                    <span className="edge-number">#{edge.order}</span>
                    <span className="edge-temp-id">{edge.tempId}</span>
                    <button
                      type="button"
                      className="wf-remove-step-btn"
                      onClick={() => removeEdge(index)}
                      title="Remove edge"
                    >
                      <Icon name="close" size={18} />
                    </button>
                  </div>

                  <div className="edge-form-grid">
                    <div className="form-group">
                      <label>Source Step *</label>
                      <select
                        value={edge.sourceTempId}
                        onChange={e => updateEdge(index, 'sourceTempId', e.target.value)}
                      >
                        <option value="">Select source...</option>
                        {steps.map(s => (
                          <option key={s.tempId} value={s.tempId}>
                            {s.stepName || `#${s.order}`} ({nodeTypes.find(nt => nt.value === s.nodeType)?.label})
                          </option>
                        ))}
                      </select>
                    </div>

                    <div className="form-group">
                      <label>Target Step *</label>
                      <select
                        value={edge.targetTempId}
                        onChange={e => updateEdge(index, 'targetTempId', e.target.value)}
                      >
                        <option value="">Select target...</option>
                        {steps.map(s => (
                          <option key={s.tempId} value={s.tempId}>
                            {s.stepName || `#${s.order}`} ({nodeTypes.find(nt => nt.value === s.nodeType)?.label})
                          </option>
                        ))}
                      </select>
                    </div>

                    <div className="form-group">
                      <label>Source Port</label>
                      <select
                        value={edge.sourcePort}
                        onChange={e => updateEdge(index, 'sourcePort', e.target.value)}
                      >
                        <option value="">None</option>
                        <option value="true">true</option>
                        <option value="false">false</option>
                      </select>
                      <small className="wf-form-hint">For condition nodes</small>
                    </div>

                    <div className="form-group">
                      <label>Label</label>
                      <input
                        type="text"
                        value={edge.label}
                        onChange={e => updateEdge(index, 'label', e.target.value)}
                        placeholder="Optional label"
                      />
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        </form>

      <Modal {...modalProps} />
    </div>
  )
}

export default WorkflowForm
