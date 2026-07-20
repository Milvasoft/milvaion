import { useState, useEffect } from 'react'
import PropTypes from 'prop-types'
import Icon from './Icon'
import workerService from '../services/workerService'
import workflowService from '../services/workflowService'
import './TriggerWorkflowModal.css'

// ── helpers ──────────────────────────────────────────────────────────────────

function generateExampleFromSchema(schema) {
  if (typeof schema === 'string') {
    try { schema = JSON.parse(schema) } catch { return {} }
  }
  if (!schema?.properties) return {}
  const example = {}
  for (const [key, prop] of Object.entries(schema.properties)) {
    if (prop.default !== undefined) { example[key] = prop.default; continue }
    if (prop.enum?.length) { example[key] = prop.enum[0]; continue }
    switch (prop.type) {
      case 'string':
        example[key] = prop.format === 'date-time' ? new Date().toISOString()
          : prop.format === 'date' ? new Date().toISOString().split('T')[0]
          : prop.format === 'email' ? 'example@email.com'
          : prop.format === 'uri' ? 'https://example.com'
          : `<${key}>`
        break
      case 'integer': case 'number': example[key] = 0; break
      case 'boolean': example[key] = false; break
      case 'array': example[key] = []; break
      case 'object': example[key] = {}; break
      default: example[key] = null
    }
  }
  return example
}

function SchemaViewer({ schema, onGenerate }) {
  const [expanded, setExpanded] = useState(false)
  let parsed = schema
  if (typeof schema === 'string') {
    try { parsed = JSON.parse(schema) } catch { return <div className="twm-schema-error">Invalid schema</div> }
  }
  if (!parsed?.properties) return null
  const required = parsed.required || []
  return (
    <div className="twm-schema">
      <div className="twm-schema-header">
        <Icon name="schema" size={14} />
        <span>Expected Schema</span>
        <button type="button" className="twm-schema-gen-btn" onClick={onGenerate} title="Fill textarea with example JSON">
          <Icon name="auto_fix_high" size={14} /> Generate Example
        </button>
      </div>
      <div className="twm-schema-props">
        {Object.entries(parsed.properties).map(([key, prop]) => (
          <div key={key} className="twm-schema-prop">
            <span className="twm-prop-name">{key}</span>
            <span className={`twm-prop-type twm-type-${prop.type}`}>{prop.type}</span>
            {required.includes(key) && <span className="twm-prop-required">required</span>}
            {prop.enum && <span className="twm-prop-enum">enum: {prop.enum.join(' | ')}</span>}
            {prop.description && <span className="twm-prop-desc">{prop.description}</span>}
          </div>
        ))}
      </div>
      <button type="button" className="twm-schema-toggle" onClick={() => setExpanded(p => !p)}>
        {expanded ? 'Hide Raw Schema' : 'Show Raw Schema'}
      </button>
      {expanded && <pre className="twm-schema-raw">{JSON.stringify(parsed, null, 2)}</pre>}
    </div>
  )
}

SchemaViewer.propTypes = {
  schema: PropTypes.oneOfType([PropTypes.string, PropTypes.object]).isRequired,
  onGenerate: PropTypes.func.isRequired,
}

// ── main component ────────────────────────────────────────────────────────────

/**
 * Modal for triggering a workflow run with optional per-step job data.
 *
 * Props:
 *   workflowId – workflow ID to trigger
 *   onClose    – called when modal should close (optionally with error message)
 *   onSuccess  – called with (runId) after successful trigger
 */
export default function TriggerWorkflowModal({ workflowId, workflow: workflowProp, onClose, onSuccess }) {
  const [reason, setReason] = useState('')
  const [stepData, setStepData] = useState({})
  const [stepErrors, setStepErrors] = useState({})
  const [loading, setLoading] = useState(false)
  const [fetchLoading, setFetchLoading] = useState(!workflowProp)
  const [workers, setWorkers] = useState([])
  const [workflow, setWorkflow] = useState(workflowProp ?? null)

  const taskSteps = (workflow?.steps || [])
    .filter(s => s.nodeType === 0 || s.nodeType == null)
    .sort((a, b) => a.order - b.order)

  useEffect(() => {
    const id = workflowProp?.id ?? workflowId
    if (!id) return

    const fetchWorkers = workerService.getAll().then(r => setWorkers(r?.data || [])).catch(() => {})

    if (workflowProp) {
      fetchWorkers
      return
    }

    setFetchLoading(true)
    Promise.all([
      workflowService.getById(id),
      workerService.getAll(),
    ]).then(([wfRes, workerRes]) => {
      setWorkflow(wfRes?.data ?? null)
      setWorkers(workerRes?.data || [])
    }).catch(() => {}).finally(() => setFetchLoading(false))
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const getSchema = (step) => {
    if (!step.workerId || !step.jobNameInWorker) return null
    const worker = workers.find(w => w.workerId === step.workerId)
    return worker?.jobDataDefinitions?.[step.jobNameInWorker] ?? null
  }

  const handleDataChange = (stepId, value) => {
    setStepData(prev => ({ ...prev, [stepId]: value }))
    if (!value.trim()) {
      setStepErrors(prev => { const n = { ...prev }; delete n[stepId]; return n })
      return
    }
    try {
      JSON.parse(value)
      setStepErrors(prev => { const n = { ...prev }; delete n[stepId]; return n })
    } catch {
      setStepErrors(prev => ({ ...prev, [stepId]: 'Invalid JSON' }))
    }
  }

  const handleGenerateExample = (step) => {
    const schema = getSchema(step)
    if (!schema) return
    const example = generateExampleFromSchema(schema)
    handleDataChange(step.id, JSON.stringify(example, null, 2))
  }

  const hasErrors = Object.keys(stepErrors).length > 0

  const handleSubmit = async () => {
    if (hasErrors) return
    const stepJobData = {}
    Object.entries(stepData).forEach(([id, val]) => {
      if (val.trim()) stepJobData[id] = val.trim()
    })
    setLoading(true)

    let runId

    try {
      const result = await workflowService.trigger(workflow.id, reason || 'Manual trigger', stepJobData)

      if (!result?.isSuccess) {
        onClose(result?.message || 'Failed to trigger workflow')
        return
      }

      /*
       * The trigger command returns `Response<Guid>` and the interceptor unwraps the
       * envelope, so `data` is the run id itself - not an object with an `id` on it. This
       * read `result.data.id`, which is undefined, so the run id never reached the caller.
       */
      runId = typeof result.data === 'string' ? result.data : result.data?.id
    } catch (err) {
      console.error('Workflow trigger failed:', err)
      onClose('Failed to trigger workflow')
      return
    } finally {
      setLoading(false)
    }

    /*
     * Outside the request's try, deliberately, and with its own handling.
     *
     * `onSuccess` used to sit inside that try, so anything the parent's handler threw was
     * caught there and reported as "Failed to trigger workflow" - a request that succeeded,
     * announced as a failure, with the real error swallowed. The request and the callback
     * are separate concerns.
     *
     * If the callback does fail, the run was still started, so the message says so rather
     * than blaming the trigger. The error itself goes to the console named for what it is.
     */
    try {
      onSuccess(runId)
    } catch (err) {
      console.error('Workflow was triggered, but the page failed to handle the result:', err)
      onClose('The workflow was triggered, but this page could not show the result. Check the runs list.')
    }
  }

  return (
    <div className="modal-overlay" onClick={() => !loading && onClose()}>
      <div className="modal-content twm-modal" onClick={e => e.stopPropagation()}>

        {/* Header */}
        <div className="modal-header">
          <h2><Icon name="play_arrow" size={22} /> Run Workflow</h2>
          <button className="modal-close-btn" onClick={() => !loading && onClose()} disabled={loading}>
            <Icon name="close" size={20} />
          </button>
        </div>

        {/* Body */}
        <div className="modal-body twm-body">
          {fetchLoading ? (
            <div className="twm-fetch-loading"><Icon name="hourglass_empty" size={20} /> Loading...</div>
          ) : (<>
          <div className="twm-field">
            <label className="twm-label">Trigger Reason</label>
            <input
              type="text"
              className="form-input"
              placeholder="Manual trigger"
              value={reason}
              onChange={e => setReason(e.target.value)}
              disabled={loading}
            />
          </div>

          {/* Per-step data */}
          {taskSteps.length > 0 && (
            <div className="twm-steps">
              <label className="twm-label twm-steps-label">
                <Icon name="data_object" size={15} />
                Step Job Data
                <span className="twm-hint">Leave empty to use each step&apos;s default job data</span>
              </label>

              {taskSteps.map(step => {
                const schema = getSchema(step)
                return (
                  <div key={step.id} className="twm-step">
                    <div className="twm-step-header">
                      <span className="twm-step-order">#{step.order}</span>
                      <strong className="twm-step-name">{step.stepName}</strong>
                      {step.jobDisplayName && (
                        <span className="twm-step-job">
                          <Icon name="work" size={13} />
                          {step.jobDisplayName}
                        </span>
                      )}
                    </div>

                    {schema && (
                      <SchemaViewer
                        schema={schema}
                        onGenerate={() => handleGenerateExample(step)}
                      />
                    )}

                    <textarea
                      className={`twm-textarea${stepErrors[step.id] ? ' twm-textarea--error' : ''}`}
                      placeholder="{}"
                      value={stepData[step.id] || ''}
                      onChange={e => handleDataChange(step.id, e.target.value)}
                      rows={3}
                      disabled={loading}
                      spellCheck={false}
                    />
                    {stepErrors[step.id] && (
                      <span className="twm-error">{stepErrors[step.id]}</span>
                    )}
                  </div>
                )
              })}
            </div>
          )}
          </>)}
        </div>

        {/* Footer */}
        <div className="modal-footer">
          <button className="wfd-btn" onClick={() => onClose()} disabled={loading}>Cancel</button>
          <button
            className="wfd-btn wfd-btn-primary"
            onClick={handleSubmit}
            disabled={loading || hasErrors}
          >
            {loading
              ? <><Icon name="hourglass_empty" size={16} /> Triggering...</>
              : <><Icon name="play_arrow" size={16} /> Run</>}
          </button>
        </div>
      </div>
    </div>
  )
}

TriggerWorkflowModal.propTypes = {
  workflowId: PropTypes.string,
  workflow: PropTypes.object,
  onClose: PropTypes.func.isRequired,
  onSuccess: PropTypes.func.isRequired,
}
