import { memo } from 'react'
import { Handle, Position } from 'reactflow'
import Icon from '../../../components/Icon'
import { RunStatusBadge, RunMetaRow } from './NodeRunParts'

/* eslint-disable react/prop-types */
function ConditionNode({ data, selected }) {
  const { step, onDelete, readOnly, run, dimmed } = data

  let expression = ''

  try {
    expression = JSON.parse(step.nodeConfigJson || '{}').expression || ''
  } catch {
    // Ignore parse errors
  }

  const classNames = [
    'wfb-node',
    'wfb-condition-node',
    selected && 'wfb-node--selected',
    readOnly && 'wfb-node--readonly',
    dimmed && 'wfb-node--dimmed',
    run && `wfb-node--status-${run.status}`,
  ].filter(Boolean).join(' ')

  return (
    <div className={classNames}>
      <Handle type="target" position={Position.Left} id="input" isConnectable={!readOnly} />

      <div className="wfb-node-header">
        <span className="wfb-node-kind" title="Condition">
          <Icon name="alt_route" size={12} />
        </span>

        <span className="wfb-node-title" title={step.stepName}>
          {step.stepName || <em>Condition</em>}
        </span>

        {readOnly
          ? <RunStatusBadge run={run} />
          : (
            <button
              className="wfb-node-delete"
              onClick={e => { e.stopPropagation(); onDelete(step.tempId) }}
              title="Delete node"
            >
              <Icon name="close" size={13} />
            </button>
          )}
      </div>

      <div className="wfb-node-body">
        {expression
          ? <code className="wfb-condition-expr" title={expression}>{expression}</code>
          : <span className="wfb-node-placeholder">No rules — always true</span>}
      </div>

      <RunMetaRow run={run} />

      {/* Her iki çıkış da sağda: soldan sağa akış üç node tipinde de aynı olsun diye.
          Etiketler kartın içinde, çünkü dışarı taşan mutlak konumlu etiketler
          uzaklaştırıldığında komşu kartların üstüne biniyordu. */}
      <div className="wfb-node-ports">
        <span className="wfb-node-port wfb-node-port--true">true</span>
        <span className="wfb-node-port wfb-node-port--false">false</span>
      </div>

      {/* Etiketlerle hizalı. Aradaki mesafe, genişletilmiş tıklama alanlarının
          çakışmayacağı kadar açık tutuluyor. */}
      <Handle
        type="source"
        position={Position.Right}
        id="true"
        className="wfb-handle--true"
        style={{ top: 'calc(100% - 36px)' }}
        isConnectable={!readOnly}
      />
      <Handle
        type="source"
        position={Position.Right}
        id="false"
        className="wfb-handle--false"
        style={{ top: 'calc(100% - 13px)' }}
        isConnectable={!readOnly}
      />
    </div>
  )
}

export default memo(ConditionNode)
