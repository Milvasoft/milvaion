import { useState } from 'react'
import Icon from '../../components/Icon'
import './DataMappingEditor.css'

 

// Parse JSON Schema properties into a flat field descriptor list.
// Nested objects are flattened as dotted paths (e.g. complexProp.title).
function parseSchemaFields(schemaJson, prefix = '', depth = 0) {
  const schema = depth === 0
    ? (() => { try { return typeof schemaJson === 'string' ? JSON.parse(schemaJson) : schemaJson } catch { return null } })()
    : schemaJson
  const props = schema?.properties
  if (!props) return []
  const fields = []
  for (const [key, def] of Object.entries(props)) {
    const fullKey = prefix ? `${prefix}.${key}` : key
    fields.push({
      name: fullKey,
      type: def.type || 'any',
      description: def.description || '',
      format: def.format || '',
      enum: def.enum || null,
      depth,
    })
    if (def.type === 'object' && def.properties)
      fields.push(...parseSchemaFields(def, fullKey, depth + 1))
  }
  return fields
}

function FieldIcon({ type }) {
  const icons = { string: 'text_fields', integer: 'pin', number: 'calculate', boolean: 'toggle_on', array: 'list', object: 'data_object' }
  return <Icon name={icons[type] || 'data_object'} size={12} className="dm-field-type-icon" />
}

function FieldPicker({ fields, value, onChange, placeholder, allowWildcard = false }) {
  const [isOpen, setIsOpen] = useState(false)
  const [search, setSearch] = useState('')

  const selected = fields.find(f => f.name === value)
  const filtered = fields.filter(f =>
    !search || f.name.toLowerCase().includes(search.toLowerCase()) || (f.description && f.description.toLowerCase().includes(search.toLowerCase()))
  )
  // search tam olarak bir field'a denk gelmiyor ama bir şey yazılmış → custom
  const canUseCustom = search.trim() && !fields.find(f => f.name === search.trim())

  const select = (name) => { onChange(name); setIsOpen(false); setSearch('') }
  const applyCustom = () => { if (search.trim()) select(search.trim()) }

  return (
    <div className="dm-picker">
      <button
        type="button"
        className={`dm-picker-trigger${isOpen ? ' dm-picker-trigger--open' : ''}${value ? ' dm-picker-trigger--selected' : ''}`}
        onClick={() => setIsOpen(p => !p)}
      >
        {value ? (
          <>
            {selected && <FieldIcon type={selected.type} />}
            {!selected && <Icon name="edit" size={12} className="dm-field-type-icon" />}
            <span className="dm-picker-value">$.{value}</span>
            {selected?.type && <span className="dm-picker-type">{selected.type}</span>}
            {!selected && <span className="dm-picker-type dm-picker-type--custom">custom</span>}
          </>
        ) : (
          <span className="dm-picker-placeholder">{placeholder}</span>
        )}
        <Icon name={isOpen ? 'expand_less' : 'expand_more'} size={14} className="dm-picker-chevron" />
      </button>

      {isOpen && (
        <div className="dm-picker-dropdown">
          <div className="dm-picker-search">
            <Icon name="search" size={13} />
            <input
              autoFocus
              value={search}
              onChange={e => setSearch(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && (canUseCustom ? applyCustom() : filtered[0] && select(filtered[0].name))}
              placeholder="Search or type custom path…"
              className="dm-picker-search-input"
            />
            {canUseCustom && (
              <button type="button" className="dm-picker-use-custom" onClick={applyCustom} title="Use as custom path">
                <Icon name="keyboard_return" size={13} />
              </button>
            )}
          </div>
          <div className="dm-picker-list">
            {allowWildcard && (
              <button type="button" className={`dm-picker-item dm-picker-item--special${value === '*' ? ' dm-picker-item--active' : ''}`} onClick={() => select('*')}>
                <Icon name="select_all" size={13} />
                <span className="dm-picker-item-name">* entire result</span>
              </button>
            )}
            {canUseCustom && (
              <button type="button" className="dm-picker-item dm-picker-item--custom" onClick={applyCustom}>
                <div className="dm-picker-item-row">
                  <Icon name="edit" size={12} className="dm-field-type-icon" />
                  <span className="dm-picker-item-name">$.{search.trim()}</span>
                </div>
                <div className="dm-picker-item-meta">
                  <span className="dm-picker-item-type dm-picker-type--custom">custom</span>
                  <span className="dm-picker-item-desc">press Enter or click to use</span>
                </div>
              </button>
            )}
            {filtered.length === 0 && !canUseCustom && <div className="dm-picker-empty">No fields found</div>}
            {filtered.map(f => (
              <button
                key={f.name}
                type="button"
                className={`dm-picker-item${value === f.name ? ' dm-picker-item--active' : ''}`}
                style={f.depth > 0 ? { '--dm-depth': f.depth } : undefined}
                onClick={() => select(f.name)}
              >
                <div className="dm-picker-item-row" style={f.depth > 0 ? { paddingLeft: `${f.depth * 15}px` } : undefined}>
                  <FieldIcon type={f.type} />
                  <span className="dm-picker-item-name">$.{f.name}</span>
                </div>
                {(f.type || f.description) && (
                  <div className="dm-picker-item-meta" style={f.depth > 0 ? { paddingLeft: `${17 + f.depth * 15}px` } : undefined}>
                    {f.type && <span className="dm-picker-item-type">{f.format || f.type}</span>}
                    {f.description && <span className="dm-picker-item-desc dm-tooltip" data-tooltip={f.description}>{f.description}</span>}
                  </div>
                )}
              </button>
            ))}
          </div>
          {value && (
            <button type="button" className="dm-picker-clear" onClick={() => select('')}>
              <Icon name="close" size={12} /> Clear
            </button>
          )}
        </div>
      )}
    </div>
  )
}

// schemasMap: { [jobNameInWorker]: { resultFields: [], dataFields: [] } }
function DataMappingEditor({ mappings = [], onChange, steps = [], currentStepTempId, jobs = [], currentStepJobId, schemasMap = {} }) {
  const add = () => onChange([...mappings, { sourceStepTempId: '', sourcePath: '', targetPath: '' }])
  const remove = (i) => onChange(mappings.filter((_, mi) => mi !== i))
  const update = (i, field, value) => onChange(mappings.map((m, mi) => mi === i ? { ...m, [field]: value } : m))

  const getJobNameForStep = (stepTempId) => {
    if (!stepTempId) return null
    const step = steps.find(s => s.tempId === stepTempId)
    if (!step?.jobId) return null
    const job = jobs.find(j => j.id === step.jobId)
    return job?.jobType || null
  }

  const getSchemaForJobName = (jobName) => schemasMap[jobName] || { resultFields: [], dataFields: [] }

  // Current step's target fields (data schema = what it ACCEPTS as input)
  const currentJobName = currentStepJobId ? jobs.find(j => j.id === currentStepJobId)?.jobType : null
  const targetFields = getSchemaForJobName(currentJobName).dataFields

  const sourceSteps = steps.filter(s => s.tempId !== currentStepTempId && s.nodeType === 0)

  const targetCounts = mappings.reduce((acc, m) => {
    if (m.targetPath) acc[m.targetPath] = (acc[m.targetPath] || 0) + 1
    return acc
  }, {})

  return (
    <div className="dm-editor">
      <div className="dm-header">
        <span className="dm-title">
          <Icon name="swap_horiz" size={15} /> Data Mappings
        </span>
        <button type="button" className="dm-add-btn" onClick={add}>
          <Icon name="add" size={13} /> Add
        </button>
      </div>

      {mappings.length === 0 ? (
        <div className="dm-empty">
          <Icon name="swap_horiz" size={20} />
          <p>Map output fields from upstream steps into this step&apos;s job data.</p>
          <code className="dm-empty-example">$.price → $.inputAmount</code>
        </div>
      ) : (
        <div className="dm-list">
          {mappings.map((m, i) => {
            const sourceJobName = getJobNameForStep(m.sourceStepTempId)
            const sourceFields = getSchemaForJobName(sourceJobName).resultFields
            const isComplete = !!(m.sourcePath && m.targetPath)
            const isDupTarget = isComplete && (targetCounts[m.targetPath] || 0) > 1

            return (
              <div
                key={i}
                className={`dm-row${isComplete ? ' dm-row--complete' : ''}${isDupTarget ? ' dm-row--warn' : ''}`}
              >
                <div className="dm-indicator">
                  {isDupTarget
                    ? <Icon name="warning" size={12} />
                    : isComplete
                      ? <Icon name="check_circle" size={12} />
                      : <Icon name="radio_button_unchecked" size={12} />
                  }
                </div>

                {/* FROM */}
                <div className="dm-side">
                  <span className="dm-side-label dm-side-label--from">FROM</span>
                  <select
                    className="dm-select"
                    value={m.sourceStepTempId}
                    onChange={e => update(i, 'sourceStepTempId', e.target.value)}
                  >
                    <option value="">Any parent step</option>
                    {sourceSteps.map(s => {
                      const jn = getJobNameForStep(s.tempId)
                      const hasResult = jn && getSchemaForJobName(jn).resultFields.length > 0
                      return (
                        <option key={s.tempId} value={s.tempId}>
                          {s.stepName || s.tempId}{hasResult ? '' : ' (no schema)'}
                        </option>
                      )
                    })}
                  </select>
                  <FieldPicker
                    fields={sourceFields}
                    value={m.sourcePath}
                    onChange={v => update(i, 'sourcePath', v)}
                    placeholder="result field…"
                    allowWildcard
                  />
                </div>

                <Icon name="arrow_forward" size={15} className="dm-arrow" />

                {/* TO */}
                <div className="dm-side">
                  <span className="dm-side-label dm-side-label--to">TO</span>
                  <FieldPicker
                    fields={targetFields}
                    value={m.targetPath}
                    onChange={v => update(i, 'targetPath', v)}
                    placeholder="data field…"
                  />
                  {isDupTarget && <span className="dm-warn-text">Duplicate target</span>}
                </div>

                <button type="button" className="dm-remove-btn" onClick={() => remove(i)}>
                  <Icon name="close" size={13} />
                </button>
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}

export { parseSchemaFields }
export default DataMappingEditor

