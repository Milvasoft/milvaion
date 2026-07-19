import { useState, useMemo } from 'react'
import Icon from '../../../components/Icon'
import DataMappingEditor from '../DataMappingEditor'
import ConditionBuilder from './ConditionBuilder'

/* eslint-disable react/prop-types */

/**
 * Node tipleri. Açıklamalar motorun gerçek davranışını anlatıyor: Condition bir iş
 * çalıştırmıyor, yalnızca true/false portlarını belirliyor; Merge de hiçbir şey
 * çalıştırmıyor, gelen dalların hepsi bitene kadar bekleyip geçiyor. Bu ikisi
 * dropdown'da yan yana yazınca ayırt edilemiyordu.
 */
const nodeTypeOptions = [
  {
    value: 0,
    icon: 'work',
    label: 'Task',
    description: 'Runs a scheduled job.',
  },
  {
    value: 1,
    icon: 'alt_route',
    label: 'Condition',
    description: 'Runs no job. Checks the steps feeding into it and sends the flow out of its true or false port.',
  },
  {
    value: 2,
    icon: 'call_merge',
    label: 'Merge',
    description: 'Runs no job. Waits for every incoming branch to finish, then continues as one.',
  },
]

function SchemaSection({ label, icon, fields, color }) {
  const [open, setOpen] = useState(false)
  if (!fields?.length) return null
  return (
    <div className="wfb-schema-section" style={{ '--schema-color': color }}>
      <button type="button" className="wfb-schema-toggle" onClick={() => setOpen(p => !p)}>
        <Icon name={icon} size={13} />
        <span>{label}</span>
        <span className="wfb-schema-count">{fields.length} field{fields.length !== 1 ? 's' : ''}</span>
        <Icon name={open ? 'expand_less' : 'expand_more'} size={14} className="wfb-schema-chevron" />
      </button>
      {open && (
        <div className="wfb-schema-fields">
          {fields.map(f => (
            <div key={f.name} className="wfb-schema-field" style={f.depth > 0 ? { paddingLeft: `${f.depth * 12 + 8}px` } : undefined}>
              <span className="wfb-schema-field-name">$.{f.name}</span>
              <span className={`wfb-schema-field-type wfb-schema-type--${f.type}`}>{f.format || f.type}</span>
              {f.description && <span className="wfb-schema-field-desc" title={f.description}>{f.description}</span>}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function StepConfigPanel({ step, jobs, allSteps, edges = [], schemasMap = {}, onChange, onClose }) {
  // Bu adıma bağlanan adımlar. Condition kuralları ve Merge açıklaması ikisi de
  // bunu kullanıyor - "hangi adımı kontrol ediyorum" sorusunun cevabı burası.
  const parentSteps = useMemo(() => {
    if (!step) return []

    const sourceIds = new Set(edges.filter(e => e.target === step.tempId).map(e => e.source))

    return allSteps.filter(s => sourceIds.has(s.tempId))
  }, [edges, allSteps, step])

  if (!step) return null

  const update = (field, value) => onChange({ ...step, [field]: value })

  const selectedJob = step.jobId ? jobs.find(j => j.id === step.jobId) : null
  const jobSchema = selectedJob ? (schemasMap[selectedJob.jobType] || { dataFields: [], resultFields: [] }) : null

  const isTask = step.nodeType === 0 || step.nodeType === undefined

  let conditionExpression = ''

  try {
    conditionExpression = JSON.parse(step.nodeConfigJson || '{}').expression || ''
  } catch {
    // Bozuk config: ifadeyi boş kabul edip editörün yeniden yazmasına izin veriyoruz.
  }

  return (
    <div className="wfb-config-panel">
      <div className="wfb-config-header">
        <h3><Icon name="tune" size={18} /> Step Config</h3>
        <button className="wfb-config-close" onClick={onClose} title="Close">
          <Icon name="chevron_right" size={20} />
        </button>
      </div>

      <div className="wfb-config-body">
        {/* Node Type */}
        <div className="wfb-field">
          <label>Node Type</label>
          <div className="wfb-nodetype-options">
            {nodeTypeOptions.map(option => {
              const active = (step.nodeType ?? 0) === option.value

              return (
                <button
                  key={option.value}
                  type="button"
                  className={`wfb-nodetype-option${active ? ' is-active' : ''}`}
                  onClick={() => update('nodeType', option.value)}
                >
                  <span className="wfb-nodetype-head">
                    <Icon name={option.icon} size={14} />
                    {option.label}
                  </span>
                  <span className="wfb-nodetype-desc">{option.description}</span>
                </button>
              )
            })}
          </div>
        </div>

        {/* Merge nodes have nothing to configure, so the panel would otherwise be empty
            and give no clue about what the node is waiting for. */}
        {step.nodeType === 2 && (
          <div className="wfb-note">
            <Icon name="call_merge" size={15} />
            <div>
              <strong>
                {parentSteps.length === 0
                  ? 'Nothing connects into this merge yet.'
                  : `Waiting on ${parentSteps.length} incoming ${parentSteps.length === 1 ? 'branch' : 'branches'}.`}
              </strong>
              {parentSteps.length > 0 && (
                <span className="wfb-note-list">
                  {parentSteps.map(s => s.stepName || 'Unnamed step').join(', ')}
                </span>
              )}
              <span>
                Whatever follows this node starts once all of them finish. A merge runs no job and
                needs no configuration.
              </span>
            </div>
          </div>
        )}

        {/* Step Name */}
        <div className="wfb-field">
          <label>Step Name</label>
          <input
            className="wfb-input"
            value={step.stepName}
            onChange={e => update('stepName', e.target.value)}
            placeholder="e.g. Extract Data"
          />
        </div>

        {/* Job (only for Task nodes) */}
        {isTask && (
          <div className="wfb-field">
            <label>Job</label>
            <select className="wfb-select" value={step.jobId} onChange={e => update('jobId', e.target.value)}>
              <option value="">— Select Job —</option>
              {jobs.map(j => (
                <option key={j.id} value={j.id}>{j.displayName || j.jobNameInWorker}</option>
              ))}
            </select>
          </div>
        )}

        {/* Job Schema Preview */}
        {jobSchema && (jobSchema.dataFields?.length > 0 || jobSchema.resultFields?.length > 0) && (
          <div className="wfb-schema-preview">
            <SchemaSection
              label="Input Schema"
              icon="input"
              fields={jobSchema.dataFields}
              color="var(--accent-color)"
            />
            <SchemaSection
              label="Output Schema"
              icon="output"
              fields={jobSchema.resultFields}
              color="#10b981"
            />
          </div>
        )}

        {/* Delay (only for Task nodes — virtual nodes don't support delay yet) */}
        {isTask && (
          <div className="wfb-field">
            <label>Delay (seconds)</label>
            <input
              className="wfb-input"
              type="number"
              min={0}
              value={step.delaySeconds}
              onChange={e => update('delaySeconds', Number(e.target.value) || 0)}
            />
          </div>
        )}

        {/* Node Config (for Condition nodes) */}
        {step.nodeType === 1 && (
          <div className="wfb-field">
            <label>Condition</label>

            {parentSteps.length === 0 && (
              <div className="wfb-note wfb-note--warn">
                <Icon name="warning" size={15} />
                <div>
                  <strong>Nothing connects into this condition yet.</strong>
                  <span>
                    A condition checks the steps feeding into it, so connect at least one before
                    writing rules.
                  </span>
                </div>
              </div>
            )}

            <ConditionBuilder
              expression={conditionExpression}
              parentSteps={parentSteps}
              schemasMap={schemasMap}
              jobs={jobs}
              onChange={value => update('nodeConfigJson', value ? JSON.stringify({ expression: value }) : null)}
            />

            <span className="wfb-hint">
              Matching sends the flow out of the <strong>true</strong> port, otherwise the{' '}
              <strong>false</strong> port. With no rules it always takes true.
            </span>
          </div>
        )}

        {/* Job Data Override (only for Task nodes) */}
        {isTask && (
          <div className="wfb-field">
            <div className="wfb-field-header">
              <label>Job Data Override</label>
              {step.dataMappings?.length > 0 && (
                <span className="wfb-badge wfb-badge--warn">
                  <Icon name="swap_horiz" size={11} /> Overridden by mappings
                </span>
              )}
            </div>
            <textarea
              className="wfb-textarea"
              rows={4}
              value={step.dataMappings?.length > 0 ? '' : (step.jobDataOverride || '')}
              disabled={step.dataMappings?.length > 0}
              onChange={e => update('jobDataOverride', e.target.value)}
              placeholder={step.dataMappings?.length > 0 ? 'Disabled — data mappings are active' : '{"key": "value"}'}
            />
          </div>
        )}

        {/* Data Mappings (only for Task nodes) */}
        {isTask && (
          <div className="wfb-field">
            <DataMappingEditor
              mappings={step.dataMappings || []}
              onChange={(newMappings) => {
                const hadMappings = (step.dataMappings?.length || 0) > 0
                const hasMappings = newMappings.length > 0
                update('dataMappings', newMappings)
                if (!hadMappings && hasMappings && step.jobDataOverride)
                  onChange({ ...step, dataMappings: newMappings, jobDataOverride: '' })
              }}
              steps={allSteps}
              currentStepTempId={step.tempId}
              jobs={jobs}
              currentStepJobId={step.jobId}
              schemasMap={schemasMap}
            />
          </div>
        )}
      </div>
    </div>
  )
}

export default StepConfigPanel
