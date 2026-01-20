import { useState, useEffect, useCallback } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import jobService from '../../services/jobService'
import workerService from '../../services/workerService'
import Icon from '../../components/Icon'
import CronExpressionInput from '../../components/CronExpressionInput'
import JsonStringConverter from '../../components/JsonStringConverter'
import JsonEditor from '../../components/JsonEditor'
import './JobForm.css'

// Helper function to generate example JSON from JSON Schema
function generateExampleFromSchema(schema) {
  if (typeof schema === 'string') {
    try {
      schema = JSON.parse(schema)
    } catch {
      return {}
    }
  }

  if (!schema || !schema.properties) return {}

  const example = {}
  const properties = schema.properties

  for (const [key, prop] of Object.entries(properties)) {
    if (prop.default !== undefined) {
      example[key] = prop.default
    } else if (prop.enum && prop.enum.length > 0) {
      example[key] = prop.enum[0]
    } else {
      switch (prop.type) {
        case 'string':
          if (prop.format === 'date-time') {
            example[key] = new Date().toISOString()
          } else if (prop.format === 'date') {
            example[key] = new Date().toISOString().split('T')[0]
          } else if (prop.format === 'email') {
            example[key] = 'example@email.com'
          } else if (prop.format === 'uri') {
            example[key] = 'https://example.com'
          } else {
            example[key] = prop.description ? `<${key}>` : ''
          }
          break
        case 'integer':
        case 'number':
          example[key] = 0
          break
        case 'boolean':
          example[key] = false
          break
        case 'array':
          example[key] = []
          break
        case 'object':
          example[key] = prop.properties ? generateExampleFromSchema(prop) : {}
          break
        default:
          example[key] = null
      }
    }
  }

  return example
}

// Schema Viewer Component
function SchemaViewer({ schema }) {
  const [expanded, setExpanded] = useState(false)

  let parsedSchema = schema
  if (typeof schema === 'string') {
    try {
      parsedSchema = JSON.parse(schema)
    } catch {
      return <div className="schema-error">Invalid schema format</div>
    }
  }

  if (!parsedSchema || !parsedSchema.properties) {
    return <div className="schema-empty">No schema defined</div>
  }

  const properties = parsedSchema.properties
  const required = parsedSchema.required || []

  return (
    <div className="schema-viewer">
      <div className="schema-properties">
        {Object.entries(properties).map(([key, prop]) => (
          <div key={key} className="schema-property">
            <div className="property-header">
              <span className="property-name">{key}</span>
              <span className={`property-type type-${prop.type}`}>{prop.type}</span>
              {required.includes(key) && <span className="property-required">required</span>}
              {prop.enum && <span className="property-enum">enum</span>}
            </div>
            {prop.description && (
              <div className="property-description">{prop.description}</div>
            )}
            {prop.enum && (
              <div className="property-enum-values">
                Values: {prop.enum.join(' | ')}
              </div>
            )}
            {prop.default !== undefined && (
              <div className="property-default">
                Default: <code>{JSON.stringify(prop.default)}</code>
              </div>
            )}
          </div>
        ))}
      </div>
      <button
        type="button"
        className="schema-toggle"
        onClick={() => setExpanded(!expanded)}
      >
        {expanded ? 'Hide Raw Schema' : 'Show Raw Schema'}
      </button>
      {expanded && (
        <pre className="schema-raw">{JSON.stringify(parsedSchema, null, 2)}</pre>
      )}
    </div>
  )
}

function JobForm() {
  const { id } = useParams()
  const navigate = useNavigate()
  const isEditMode = !!id

  const [formData, setFormData] = useState({
    displayName: '',
    workerId: '',
    selectedJobName: '',
    cronExpression: '',
    executeAt: '',
    description: '',
    jobData: '{}',
    isActive: true,
    concurrentExecutionPolicy: 0, // 0=Skip, 1=Queue
    tags: [], // Tag array
    zombieTimeoutMinutes: '',
    executionTimeoutSeconds: '',
    // Auto-disable settings
    autoDisableSettings: {
      enabled: true,
      threshold: ''
    }
  })

  const [scheduleType, setScheduleType] = useState('cron') // 'cron' or 'once'
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(null)
  const [workers, setWorkers] = useState([])           // ← YENİ
  const [selectedWorker, setSelectedWorker] = useState(null) // ← YENİ
  const [tagInput, setTagInput] = useState('') // Input for new tag

  const loadWorkers = useCallback(async () => {
    try {
      const response = await workerService.getAll()

      let workerData = []
      if (Array.isArray(response.data)) {
        workerData = response.data
      } else if (response.data && response.data.isSuccess && response.data.data) {
        workerData = response.data.data
      }

      const activeWorkers = workerData.filter(w => w.status === 'Active')
      setWorkers(activeWorkers)
      return activeWorkers
    } catch (err) {
      console.error('Failed to load workers:', err)
      setError('Failed to load workers. Check console for details.')
      return []
    }
  }, [])

  const loadJob = useCallback(async () => {
    try {
      setLoading(true)
      const response = await jobService.getById(id)
      const data = response.data

      setFormData({
        displayName: data.displayName || '',
        workerId: data.workerId || '',
        selectedJobName: data.jobType || '',
        cronExpression: data.cronExpression || '',
        executeAt: data.executeAt ? new Date(data.executeAt).toISOString().slice(0, 16) : '',
        description: data.description || '',
        jobData: data.jobData ? JSON.stringify(JSON.parse(data.jobData), null, 2) : '{}',
        isActive: data.isActive,
        concurrentExecutionPolicy: data.concurrentExecutionPolicy ?? 0,
        tags: data.tags ? data.tags.split(',').map(t => t.trim()).filter(t => t) : [], // Split by comma
        zombieTimeoutMinutes: data.zombieTimeoutMinutes || '',
        executionTimeoutSeconds: data.executionTimeoutSeconds || '',
        autoDisableSettings: {
          enabled: data.autoDisableSettings?.enabled ?? true,
          threshold: data.autoDisableSettings?.threshold || ''
        }
      })

      setScheduleType(data.cronExpression ? 'cron' : 'once')
    } catch (err) {
      setError('Failed to load job')
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [id])

  useEffect(() => {
    const initializeForm = async () => {
      await loadWorkers()
      if (isEditMode) {
        await loadJob()
      }
    }
    initializeForm()
  }, [isEditMode, loadWorkers, loadJob])

  const handleWorkerChange = (e) => {
    const workerId = e.target.value
    const worker = workers.find(w => w.workerId === workerId)
    setSelectedWorker(worker)
    setFormData(prev => ({
      ...prev,
      workerId,
      selectedJobName: '' // Reset job selection
    }))

    // Auto-select if only one job type
    if (worker && worker.jobNames && worker.jobNames.length === 1) {
      setFormData(prev => ({
        ...prev,
        selectedJobName: worker.jobNames[0]
      }))
    }
  }

  // When workers are loaded and we have a workerId in formData, set the selected worker
  useEffect(() => {
    if (workers.length > 0 && formData.workerId && !selectedWorker) {
      const worker = workers.find(w => w.workerId === formData.workerId)
      if (worker) {
        setSelectedWorker(worker)
      }
    }
  }, [workers, formData.workerId, selectedWorker])

  const handleChange = (e) => {
    const { name, value, type, checked } = e.target
    setFormData((prev) => ({
      ...prev,
      [name]: type === 'checkbox' ? checked : value,
    }))
  }

  const handleAutoDisableChange = (e) => {
    const { name, value, type, checked } = e.target
    setFormData((prev) => ({
      ...prev,
      autoDisableSettings: {
        ...prev.autoDisableSettings,
        [name]: type === 'checkbox' ? checked : value
      }
    }))
  }

  const handleAddTag = (e) => {
    if (e.key === 'Enter' || e.type === 'click') {
      e.preventDefault()
      const tag = tagInput.trim()
      if (tag && !formData.tags.includes(tag)) {
        setFormData(prev => ({
          ...prev,
          tags: [...prev.tags, tag]
        }))
        setTagInput('')
      }
    }
  }

  const handleRemoveTag = (tagToRemove) => {
    setFormData(prev => ({
      ...prev,
      tags: prev.tags.filter(tag => tag !== tagToRemove)
    }))
  }

  const handleSubmit = async (e) => {
    e.preventDefault()
    setError(null)

    // Validate JSON
    try {
      JSON.parse(formData.jobData)
    } catch {
      setError('Invalid JSON in Job Data field')
      return
    }

    try {
      setLoading(true)

      const payload = {
        displayName: formData.displayName,
        workerId: formData.workerId,
        selectedJobName: formData.selectedJobName,
        description: formData.description,
        jobData: formData.jobData,
        isActive: formData.isActive,
        concurrentExecutionPolicy: parseInt(formData.concurrentExecutionPolicy),
        tags: formData.tags.join(','), // Join tags with comma
        zombieTimeoutMinutes: formData.zombieTimeoutMinutes ? parseInt(formData.zombieTimeoutMinutes) : null,
        executionTimeoutSeconds: formData.executionTimeoutSeconds ? parseInt(formData.executionTimeoutSeconds) : null,
        autoDisableSettings: {
          enabled: formData.autoDisableSettings.enabled,
          threshold: formData.autoDisableSettings.threshold ? parseInt(formData.autoDisableSettings.threshold) : null
        }
      }

      if (scheduleType === 'cron') {
        payload.cronExpression = formData.cronExpression
      } else {
        payload.executeAt = new Date(formData.executeAt).toISOString()
      }

      if (isEditMode) {
        await jobService.update(id, payload)
      } else {
        await jobService.create(payload)
      }

      navigate('/jobs')
    } catch (err) {
      setError(err.response?.data?.message || 'Failed to save job')
      console.error(err)
    } finally {
      setLoading(false)
    }
  }

  const handleCancel = () => {
    navigate(isEditMode ? `/jobs/${id}` : '/jobs')
  }

  if (loading && isEditMode) {
    return <div className="loading">Loading job...</div>
  }

  return (
    <div className="job-form-container">
      <div className="form-header">
        <div className="form-header-left">
          <Link to={isEditMode ? `/jobs/${id}` : '/jobs'} className="back-icon-btn" title={isEditMode ? 'Back to Job Detail' : 'Back to Jobs'}>
            <Icon name="arrow_back" size={24} />
          </Link>
          <div className="form-header-content">
            <h1>
              {/*<Icon name={isEditMode ? 'edit' : 'add'} size={28} />*/}
              <span style={{ margin: '0 0 0 0.25rem' }}>  {isEditMode ? 'Edit Job' : 'Create New Job'}</span>

            </h1>
            <p className="form-subtitle">
              {isEditMode
                ? 'Update the configuration for this scheduled job'
                : 'Configure a new scheduled job to run on your workers'
              }
            </p>
          </div>
        </div>

        {/* Form Actions - moved to header */}
        <div className="form-actions">
          <button type="button" onClick={handleCancel} className="btn btn-secondary">
            <Icon name="close" size={18} />
            Cancel
          </button>
          <button type="submit" disabled={loading} className="btn btn-primary" form="job-form">
            {loading ? (
              <>
                <Icon name="schedule" size={18} />
                Saving...
              </>
            ) : isEditMode ? (
              <>
                <Icon name="save" size={18} />
                Update Job
              </>
            ) : (
              <>
                <Icon name="add" size={18} />
                Create Job
              </>
            )}
          </button>
        </div>
      </div>

      {error && <div className="error-message">{error}</div>}

      <form onSubmit={handleSubmit} className="job-form" id="job-form">
        {/* Main Form Section */}
        <div className="main-form-section">
          {/* Basic Information Card */}
          <div className="form-card">
            <div className="form-section">
              <h3 className="form-section-title">Basic Information</h3>

              <div className="form-group">
                <label htmlFor="displayName">
                  Display Name <span className="required">*</span>
                </label>
                <input
                  type="text"
                  id="displayName"
                  name="displayName"
                  value={formData.displayName}
                  onChange={handleChange}
                  required
                  placeholder="e.g., Daily Report Generator"
                />
                <small>A human-readable name for this job</small>
              </div>

              <div className="form-group">
                <label htmlFor="description">Description</label>
                <textarea
                  id="description"
                  name="description"
                  value={formData.description}
                  onChange={handleChange}
                  rows="3"
                  placeholder="Optional description of what this job does"
                />
              </div>

              <div className="form-group">
                <label htmlFor="tags">Tags</label>
                <div className="tags-input-container">

                  <div className="tag-input-wrapper">
                    <input
                      type="text"
                      id="tags"
                      value={tagInput}
                      onChange={(e) => setTagInput(e.target.value)}
                      onKeyDown={handleAddTag}
                      placeholder="Type and press Enter to add tag"
                    />
                    <button
                      type="button"
                      onClick={handleAddTag}
                      style={{ maxWidth: '100px' }}
                      className="btn btn-sm btn-secondary"
                    >
                      Add
                    </button>
                  </div>
                  <div className="tags-list">
                    {formData.tags.map((tag, index) => (
                      <span key={index} className="tag">
                        {tag}
                        <button
                          type="button"
                          onClick={() => handleRemoveTag(tag)}
                          className="tag-remove"
                          title="Remove tag"
                        >
                          ×
                        </button>
                      </span>
                    ))}
                  </div>
                </div>
                <small>Tags help categorize and filter jobs (e.g., reports, email, daily)</small>
              </div>
            </div>
          </div>

          {/* Worker & Job Type Card */}
          <div className="form-card">
            <div className="form-section">
              <h3 className="form-section-title">Worker Configuration</h3>

              <div className="form-row">
                <div className="form-group">
                  <label htmlFor="workerId">
                    Worker <span className="required">*</span>
                  </label>
                  <select
                    id="workerId"
                    name="workerId"
                    value={formData.workerId}
                    onChange={handleWorkerChange}
                    required
                  >
                    <option value="">Select a worker...</option>
                    {workers.map(worker => (
                      <option key={worker.workerId} value={worker.workerId}>
                        {worker.displayName} {'Capacity:'} ({worker.currentJobs}/{worker.maxParallelJobsPerWorker || '∞'})
                      </option>
                    ))}
                  </select>
                  <small>The worker that will execute this job</small>
                </div>

                <div className="form-group">
                  <label htmlFor="selectedJobName">
                    Job Type <span className="required">*</span>
                  </label>
                  <select
                    id="selectedJobName"
                    name="selectedJobName"
                    value={formData.selectedJobName}
                    onChange={handleChange}
                    required
                    disabled={!selectedWorker}
                  >
                    <option value="">
                      {!selectedWorker ? 'Select a worker first...' : 'Select job type...'}
                    </option>
                    {selectedWorker?.jobNames?.map(jobName => (
                      <option key={jobName} value={jobName}>
                        {jobName}
                      </option>
                    ))}
                  </select>
                  <small>
                    {selectedWorker
                      ? `Available job types for ${selectedWorker.displayName}`
                      : 'Job types will load after selecting a worker'}
                  </small>
                </div>
              </div>
            </div>
          </div>

          {/* Schedule Card */}
          <div className="form-card">
            <div className="form-section">
              <h3 className="form-section-title">Schedule</h3>

              <div className="form-group">
                <label>
                  Schedule Type <span className="required">*</span>
                </label>
                <div className="radio-group">
                  <label className="radio-option">
                    <input
                      type="radio"
                      value="cron"
                      checked={scheduleType === 'cron'}
                      onChange={(e) => setScheduleType(e.target.value)}
                    />
                    <Icon name="refresh" size={18} />
                    <span>Recurring (Cron)</span>
                  </label>
                  <label className="radio-option">
                    <input
                      type="radio"
                      value="once"
                      checked={scheduleType === 'once'}
                      onChange={(e) => setScheduleType(e.target.value)}
                    />
                    <Icon name="event" size={18} />
                    <span>One-time</span>
                  </label>
                </div>
              </div>

              {scheduleType === 'cron' ? (
                <div className="form-group">
                  <label htmlFor="cronExpression">
                    Cron Expression <span className="required">*</span>
                  </label>
                  <CronExpressionInput
                    value={formData.cronExpression}
                    onChange={handleChange}
                    required={scheduleType === 'cron'}
                  />
                </div>
              ) : (
                <div className="form-group">
                  <label htmlFor="executeAt">
                    Execute At <span className="required">*</span>
                  </label>
                  <input
                    type="datetime-local"
                    id="executeAt"
                    name="executeAt"
                    value={formData.executeAt}
                    onChange={handleChange}
                    required={scheduleType === 'once'}
                  />
                </div>
              )}
            </div>
          </div>

          {/* Job Data Card */}
          <div className="form-card">
            <div className="form-section">
              <h3 className="form-section-title">Job Data (JSON)</h3>

              <div className="form-group">
                <JsonEditor
                  name="jobData"
                  value={formData.jobData}
                  onChange={handleChange}
                  rows={10}
                  placeholder='{"key": "value"}'
                  hint="JSON configuration data that will be passed to the job"
                />
              </div>
              {/* Show Job Data Schema if available */}
              {selectedWorker && formData.selectedJobName && selectedWorker.jobDataDefinitions?.[formData.selectedJobName] && (
                <div className="job-data-schema">
                  <div className="schema-header">
                    <Icon name="info" size={18} />
                    <span>Expected Data Schema for <strong>{formData.selectedJobName}</strong></span>
                    <button
                      type="button"
                      className="btn btn-sm btn-secondary"
                      onClick={() => {
                        try {
                          const schema = JSON.parse(selectedWorker.jobDataDefinitions[formData.selectedJobName])
                          // Generate example from schema
                          const example = generateExampleFromSchema(schema)
                          setFormData(prev => ({
                            ...prev,
                            jobData: JSON.stringify(example, null, 2)
                          }))
                        } catch (e) {
                          console.error('Failed to generate example:', e)
                        }
                      }}
                      title="Generate example JSON from schema"
                    >
                      <Icon name="auto_fix_high" size={16} />
                      Generate Example
                    </button>
                  </div>
                  <div className="schema-content">
                    <SchemaViewer schema={selectedWorker.jobDataDefinitions[formData.selectedJobName]} />
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>

        {/* Sidebar */}
        <div className="form-sidebar">
          {/* Settings Card */}
          <div className="sidebar-card">
            <h4 className="sidebar-card-title">Settings</h4>

            <div className="form-group">
              <label htmlFor="executionTimeoutSeconds">
                Execution Timeout (seconds)
              </label>
              <input
                type="number"
                id="executionTimeoutSeconds"
                name="executionTimeoutSeconds"
                value={formData.executionTimeoutSeconds}
                onChange={handleChange}
                min="1"
                max="86400"
                placeholder="Default: 3600 (1 hour - from worker, if not changed)"
              />
              <small>Max execution time before job is cancelled (default: 1 hour)</small>
            </div>

            <div className="form-group">
              <label htmlFor="zombieTimeoutMinutes">
                Zombie Marker Timeout (minutes)
              </label>
              <input
                type="number"
                id="zombieTimeoutMinutes"
                name="zombieTimeoutMinutes"
                value={formData.zombieTimeoutMinutes}
                onChange={handleChange}
                min="1"
                max="1440"
                placeholder="Default: 10 (if not changed)"
              />
              <small>Max time in Queued status before marked as failed(zombie) (default: 10 min)</small>
            </div>

            <div className="form-group">
              <label htmlFor="concurrentExecutionPolicy">
                Concurrent Policy
              </label>
              <select
                id="concurrentExecutionPolicy"
                name="concurrentExecutionPolicy"
                value={formData.concurrentExecutionPolicy}
                onChange={handleChange}
              >
                <option value={0}>
                  🚫 Skip
                </option>
                <option value={1}>
                  ⏳ Queue
                </option>
              </select>
              <small>What happens if job is triggered while already running</small>
            </div>

            <div className="form-group">
              <label className="switch-label">
                <span className="switch-label-text">Active Status</span>
                <div className="switch-container">
                  <input
                    type="checkbox"
                    name="isActive"
                    checked={formData.isActive}
                    onChange={handleChange}
                    id="isActive"
                    className="switch-input"
                  />
                  <label htmlFor="isActive" className="switch">
                    <span className="switch-slider"></span>
                  </label>
                  <span className="switch-status">
                    {formData.isActive ? 'Active' : 'Inactive'}
                  </span>
                </div>
              </label>
              <small>Job will run on schedule when active</small>
            </div>
          </div>

          {/* Auto-Disable Settings Card */}
          <div className="sidebar-card">
            <h4 className="sidebar-card-title">
              <Icon name="power_off" size={18} />
              Auto-Disable (Circuit Breaker)
            </h4>

            <div className="form-group">
              <label className="switch-label">
                <span className="switch-label-text">Enable Auto-Disable</span>
                <div className="switch-container">
                  <input
                    type="checkbox"
                    name="enabled"
                    checked={formData.autoDisableSettings.enabled}
                    onChange={handleAutoDisableChange}
                    id="autoDisableEnabled"
                    className="switch-input"
                  />
                  <label htmlFor="autoDisableEnabled" className="switch">
                    <span className="switch-slider"></span>
                  </label>
                  <span className="switch-status">
                    {formData.autoDisableSettings.enabled ? 'Enabled' : 'Disabled'}
                  </span>
                </div>
              </label>
              <small>Automatically disable job after consecutive failures</small>
            </div>

            {formData.autoDisableSettings.enabled && (
              <div className="form-group">
                <label htmlFor="threshold">
                  Failure Threshold
                </label>
                <input
                  type="number"
                  id="threshold"
                  name="threshold"
                  value={formData.autoDisableSettings.threshold}
                  onChange={handleAutoDisableChange}
                  min="1"
                  max="100"
                  placeholder="Default: 5 (if not changed)"
                />
                <small>Number of consecutive failures before auto-disable (default: 5)</small>
              </div>
            )}
          </div>

          {/* Help Card */}
          <div className="sidebar-card">
            <h4 className="sidebar-card-title">Tips</h4>
            <p><strong>Execution Timeout:</strong> Max time a job can run before being cancelled. Leave empty to use worker's default (1 hour).</p>
            <p><strong>Zombie Timeout:</strong> Max time in Queued status before marked as failed. Leave empty for default (10 min).</p>
            <p><strong>Concurrent Policies:</strong></p>
            <ul>
              <li><strong>Skip:</strong> Don't create execution if already executing</li>
              <li><strong>Queue:</strong> Create execution and wait for previous execution to complete</li>
            </ul>
            <p><strong>Auto-Disable:</strong> Automatically disables job after consecutive failures to prevent resource waste. Admin notification will be sent.</p>
            <p><strong>Tags:</strong> Use tags to organize and filter jobs in the dashboard</p>
          </div>

          {/* JSON String Converter Card */}
          <JsonStringConverter />
        </div>

        {/* Form Actions - moved to header */}
      </form>
    </div>
  )
}

export default JobForm
