import { useMemo, useState } from 'react'
import Icon from '../../../components/Icon'
import {
  parseExpression,
  buildExpression,
  countRules,
  makeRule,
  makeGroup,
  ANY_PARENT,
} from './conditionExpression'
import './ConditionBuilder.css'

/* eslint-disable react/prop-types */

/**
 * Condition node'ların ifadesini iç içe kural gruplarıyla kuran editör.
 *
 * Dilbilgisi ve ayrıştırma conditionExpression.js içinde, motordaki
 * ConditionExpression.cs ile aynı. Buradaki tek iş onu ekranda düzenlenebilir hale
 * getirmek.
 *
 * Editörün var olma sebebi: ifadeyi elle yazmak sessizce yanlış sonuç veriyordu.
 * Tanınmayan bir clause motorda hataya düşmüyor, o clause hiçbir şeyle eşleşmiyor
 * ve koşul sabit bir değere dönüşüyor. Her zaman aynı dala giden bir condition,
 * o dalın önemli olduğu güne kadar çalışıyormuş gibi görünüyor.
 */

const STATUS_VALUES = ['Completed', 'Failed', 'Skipped', 'Cancelled', 'Running', 'Pending', 'Delayed']

const STATUS_OPERATORS = [
  { value: '==', label: 'is' },
  { value: '!=', label: 'is not' },
]

const FIELD_OPERATORS = [
  { value: '==', label: 'equals' },
  { value: '!=', label: 'not equals' },
  { value: '>', label: 'greater than' },
  { value: '<', label: 'less than' },
  { value: '>=', label: 'at least' },
  { value: '<=', label: 'at most' },
]

// ─── Ağaç düzenleme ──────────────────────────────────────────────────────────
// Düğümler yol dizisiyle adresleniyor: [0, 2] → children[0].children[2].

function updateAt(node, path, updater) {
  if (path.length === 0) return updater(node)

  const [head, ...rest] = path

  return {
    ...node,
    children: node.children.map((child, index) => index === head ? updateAt(child, rest, updater) : child),
  }
}

/**
 * Bir çocuğu ve onu komşusuna bağlayan operatörü birlikte siler.
 *
 * Operatörler çocukların arasında duruyor, yani n çocuk için n-1 operatör var.
 * İlk çocuk silindiğinde kendinden sonraki bağlantı, aksi halde kendinden önceki
 * bağlantı düşüyor.
 */
function removeAt(node, path) {
  const parentPath = path.slice(0, -1)
  const index = path[path.length - 1]

  return updateAt(node, parentPath, parent => ({
    ...parent,
    children: parent.children.filter((_, i) => i !== index),
    ops: parent.ops.filter((_, i) => i !== (index === 0 ? 0 : index - 1)),
  }))
}

function appendAt(node, path, child, op = '&&') {
  return updateAt(node, path, group => ({
    ...group,
    children: [...group.children, child],
    ops: group.children.length > 0 ? [...group.ops, op] : group.ops,
  }))
}

function setOpAt(node, path, opIndex, op) {
  return updateAt(node, path, group => ({
    ...group,
    ops: group.ops.map((current, i) => i === opIndex ? op : current),
  }))
}

function ConditionBuilder({ expression, parentSteps = [], schemasMap = {}, jobs = [], onChange }) {
  const parsed = useMemo(() => parseExpression(expression), [expression])

  // İfade bu editörle temsil edilemiyorsa ham moda düşüyoruz. Elle yazılmış ya da
  // MCP üzerinden gelmiş bir ifadede olabilecek bir durum.
  const [rawMode, setRawMode] = useState(() => parsed === null)

  const tree = parsed ?? makeGroup([], [], false)
  const totalRules = countRules(tree)

  const emit = (nextTree) => onChange(buildExpression(nextTree))

  // Seçili kaynak adımın çıktı şeması - alan adını elle yazmak yerine seçtiriyoruz.
  const fieldOptionsFor = (stepId) => {
    const candidates = stepId ? parentSteps.filter(s => s.tempId === stepId) : parentSteps
    const names = new Set()

    for (const step of candidates) {
      const job = jobs.find(j => j.id === step.jobId)

      for (const field of (job ? schemasMap[job.jobType] : null)?.resultFields ?? [])
        names.add(field.name)
    }

    return [...names]
  }

  // ── Kural satırı ───────────────────────────────────────────────────────────

  const renderRule = (rule, path) => {
    const fieldOptions = fieldOptionsFor(rule.stepId)

    const update = (patch) => emit(updateAt(tree, path, current => {
      const merged = { ...current, ...patch }

      // Tür değişince operatör ve değer o türe uymuyor olabilir; varsayılana çekiyoruz.
      if (patch.kind && patch.kind !== current.kind) {
        merged.operator = '=='
        merged.value = patch.kind === 'status' ? 'Completed' : ''
        merged.field = ''
      }

      return merged
    }))

    return (
      <div className="wfb-cond-rule-body">
        <select
          className="wfb-select wfb-cond-select"
          value={rule.stepId}
          onChange={e => update({ stepId: e.target.value })}
          title="Which incoming step to check"
        >
          <option value={ANY_PARENT}>Every incoming step</option>
          {parentSteps.map(step => (
            <option key={step.tempId} value={step.tempId}>
              {step.stepName || 'Unnamed step'}
            </option>
          ))}
        </select>

        <select
          className="wfb-select wfb-cond-select wfb-cond-select--kind"
          value={rule.kind}
          onChange={e => update({ kind: e.target.value })}
        >
          <option value="status">status</option>
          <option value="field">output field</option>
        </select>

        {rule.kind === 'field' && (
          fieldOptions.length > 0 ? (
            <select
              className="wfb-select wfb-cond-select"
              value={rule.field}
              onChange={e => update({ field: e.target.value })}
            >
              <option value="">— field —</option>
              {fieldOptions.map(name => <option key={name} value={name}>{name}</option>)}
              {rule.field && !fieldOptions.includes(rule.field) && (
                <option value={rule.field}>{rule.field}</option>
              )}
            </select>
          ) : (
            <input
              className="wfb-input wfb-cond-input"
              value={rule.field}
              onChange={e => update({ field: e.target.value })}
              placeholder="field"
            />
          )
        )}

        <select
          className="wfb-select wfb-cond-select wfb-cond-select--op"
          value={rule.operator}
          onChange={e => update({ operator: e.target.value })}
        >
          {(rule.kind === 'status' ? STATUS_OPERATORS : FIELD_OPERATORS).map(op => (
            <option key={op.value} value={op.value}>{op.label}</option>
          ))}
        </select>

        {rule.kind === 'status' ? (
          <select
            className="wfb-select wfb-cond-select"
            value={rule.value}
            onChange={e => update({ value: e.target.value })}
          >
            {STATUS_VALUES.map(status => <option key={status} value={status}>{status}</option>)}
          </select>
        ) : (
          <input
            className="wfb-input wfb-cond-input"
            value={rule.value}
            onChange={e => update({ value: e.target.value })}
            placeholder="value"
          />
        )}

        <button
          type="button"
          className="wfb-cond-remove"
          onClick={() => emit(removeAt(tree, path))}
          title="Remove rule"
        >
          <Icon name="close" size={14} />
        </button>
      </div>
    )
  }

  // ── Bağlantı operatörü ─────────────────────────────────────────────────────

  const renderJoin = (group, path, opIndex) => {
    const op = group.ops[opIndex] ?? '&&'

    return (
      <div className={`wfb-cond-join wfb-cond-join--${op === '&&' ? 'and' : 'or'}`}>
        <span className="wfb-cond-join-line" />

        <div className="wfb-cond-op">
          <button
            type="button"
            className={`wfb-cond-op-btn${op === '&&' ? ' is-active' : ''}`}
            onClick={() => emit(setOpAt(tree, path, opIndex, '&&'))}
          >
            AND
          </button>
          <button
            type="button"
            className={`wfb-cond-op-btn${op === '||' ? ' is-active' : ''}`}
            onClick={() => emit(setOpAt(tree, path, opIndex, '||'))}
          >
            OR
          </button>
        </div>

        <span className="wfb-cond-join-line" />
      </div>
    )
  }

  // ── Grup ───────────────────────────────────────────────────────────────────

  const renderGroup = (group, path) => {
    const isRoot = path.length === 0

    // AND önceliği yüksek olduğu için karışık bir grup göründüğü gibi okunmuyor:
    // `A && B || C` aslında `(A && B) || C`. Kullanıcı bunu bilmezse kurduğu koşul
    // beklediğinden farklı çalışır ve fark yalnızca belirli bir durumda ortaya çıkar.
    const isMixed = group.ops.includes('&&') && group.ops.includes('||')

    return (
      <div className={`wfb-cond-group${isRoot ? ' is-root' : ''}`}>
        {!isRoot && (
          <button
            type="button"
            className="wfb-cond-group-remove"
            onClick={() => emit(removeAt(tree, path))}
            title="Remove group"
          >
            <Icon name="close" size={13} />
          </button>
        )}

        <div className="wfb-cond-children">
          {group.children.map((child, index) => (
            <div key={index} className="wfb-cond-child">
              {index > 0 && renderJoin(group, path, index - 1)}

              {child.type === 'rule'
                ? renderRule(child, [...path, index])
                : renderGroup(child, [...path, index])}
            </div>
          ))}
        </div>

        {isMixed && (
          <p className="wfb-cond-precedence">
            <Icon name="info" size={12} />
            <span>
              AND is applied before OR. Add a group if you need the OR evaluated first.
            </span>
          </p>
        )}

        <div className="wfb-cond-actions">
          <button
            type="button"
            className="wfb-cond-add"
            onClick={() => emit(appendAt(tree, path, makeRule()))}
          >
            <Icon name="add" size={14} /> Rule
          </button>

          <button
            type="button"
            className="wfb-cond-add"
            onClick={() => emit(appendAt(tree, path, makeGroup([makeRule()], [], true)))}
          >
            <Icon name="add" size={14} /> Group
          </button>
        </div>
      </div>
    )
  }

  // ── Ham mod ────────────────────────────────────────────────────────────────

  if (rawMode) {
    return (
      <div className="wfb-cond">
        <div className="wfb-cond-toolbar">
          <span className="wfb-cond-mode-label">
            <Icon name="code" size={13} /> Raw expression
          </span>
          <button
            type="button"
            className="wfb-cond-mode-btn"
            onClick={() => {
              // Ham metin ağaca çevrilemiyorsa builder onu gösteremez, temizliyoruz.
              if (parseExpression(expression) === null) onChange('')
              setRawMode(false)
            }}
          >
            <Icon name="tune" size={13} /> Use builder
          </button>
        </div>

        <textarea
          className="wfb-textarea wfb-cond-raw"
          rows={3}
          value={expression || ''}
          onChange={e => onChange(e.target.value)}
          placeholder="(@status == 'Completed' || @status == 'Skipped') && $.count > 0"
        />

        {parsed === null && expression?.trim() && (
          <p className="wfb-cond-warning">
            <Icon name="info" size={13} />
            The builder cannot read this expression. Switching to the builder will clear it.
          </p>
        )}

        <p className="wfb-cond-hint">
          Combine clauses with <code>&amp;&amp;</code>, <code>||</code> and brackets.{' '}
          <code>&amp;&amp;</code> binds tighter, so <code>A &amp;&amp; B || C</code> means{' '}
          <code>(A and B) or C</code>.
        </p>
      </div>
    )
  }

  // ── Builder ────────────────────────────────────────────────────────────────

  return (
    <div className="wfb-cond">
      <div className="wfb-cond-toolbar">
        <span className="wfb-cond-mode-label">Run the true branch when…</span>

        <button type="button" className="wfb-cond-mode-btn" onClick={() => setRawMode(true)}>
          <Icon name="code" size={13} /> Raw
        </button>
      </div>

      {totalRules === 0 && (
        <p className="wfb-cond-empty">
          No rules yet — this condition always takes the <strong>true</strong> branch.
        </p>
      )}

      {renderGroup(tree, [])}

      {expression?.trim() && (
        <div className="wfb-cond-preview">
          <span className="wfb-cond-preview-label">Expression</span>
          <code>{expression}</code>
        </div>
      )}
    </div>
  )
}

export default ConditionBuilder
