import { memo } from 'react'
import { Handle, Position } from 'reactflow'
import Icon from '../../../components/Icon'
import { RunStatusBadge, RunMetaRow } from './NodeRunParts'

/* eslint-disable react/prop-types */
function MergeNode({ data, selected }) {
  const { step, onDelete, readOnly, run, dimmed, incomingCount } = data

  const classNames = [
    'wfb-node',
    'wfb-merge-node',
    selected && 'wfb-node--selected',
    readOnly && 'wfb-node--readonly',
    dimmed && 'wfb-node--dimmed',
    run && `wfb-node--status-${run.status}`,
  ].filter(Boolean).join(' ')

  return (
    <div className={classNames}>
      {/* İki giriş de solda, Task ve Condition ile aynı akış yönünde. React Flow
          aynı noktaya birden fazla kenar bağlamaya izin veriyor, ama iki ayrı nokta
          birleşmenin ne olduğunu grafikte doğrudan gösteriyor. */}
      <Handle
        type="target"
        position={Position.Left}
        id="input-1"
        style={{ top: '28%' }}
        isConnectable={!readOnly}
      />
      <Handle
        type="target"
        position={Position.Left}
        id="input-2"
        style={{ top: '72%' }}
        isConnectable={!readOnly}
      />

      <div className="wfb-node-header">
        <span className="wfb-node-kind" title="Merge">
          <Icon name="call_merge" size={12} />
        </span>

        <span className="wfb-node-title" title={step.stepName}>
          {step.stepName || <em>Merge</em>}
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
        <span className="wfb-node-placeholder">
          {incomingCount > 0
            ? `Waits for ${incomingCount} branch${incomingCount === 1 ? '' : 'es'}`
            : 'Waits for all branches'}
        </span>
      </div>

      <RunMetaRow run={run} />

      <Handle type="source" position={Position.Right} id="output" isConnectable={!readOnly} />
    </div>
  )
}

export default memo(MergeNode)
