import { memo } from 'react'
import { Handle, Position } from 'reactflow'
import Icon from '../../../components/Icon'
import { RunStatusBadge, RunMetaRow, BranchLabels } from './NodeRunParts'

/* eslint-disable react/prop-types */
function StepNode({ data, selected }) {
  const { step, jobsMap, onDelete, readOnly, run, dimmed, branches } = data

  // Builder tüm işleri zaten yüklediği için haritadan okuyor. Detay ve run ekranları
  // yalnızca ilgili adımların işlerini biliyor, orada isim adımın üzerinden geliyor.
  const jobName = jobsMap?.[step.jobId]?.displayName ?? step.jobDisplayName

  const classNames = [
    'wfb-node',
    'wfb-step-node',
    selected && 'wfb-node--selected',
    !step.jobId && !readOnly && 'wfb-node--invalid',
    readOnly && 'wfb-node--readonly',
    dimmed && 'wfb-node--dimmed',
    run && `wfb-node--status-${run.status}`,
  ].filter(Boolean).join(' ')

  return (
    <div className={classNames}>
      <Handle type="target" position={Position.Left} isConnectable={!readOnly} />

      <div className="wfb-node-header">
        <span className="wfb-node-kind" title="Task">
          <Icon name="work" size={12} />
        </span>

        <span className="wfb-node-title" title={step.stepName}>
          {step.stepName || <em>Unnamed Step</em>}
        </span>

        {readOnly
          ? <RunStatusBadge run={run} />
          : (
            <button
              className="wfb-node-delete"
              onClick={e => { e.stopPropagation(); onDelete(step.tempId) }}
              title="Delete step"
            >
              <Icon name="close" size={13} />
            </button>
          )}
      </div>

      <div className="wfb-node-body">
        {jobName
          ? <span className="wfb-node-job" title={jobName}>{jobName}</span>
          : <span className="wfb-node-placeholder wfb-node-placeholder--danger">No job selected</span>}
      </div>

      <BranchLabels branches={branches} />

      {run
        ? <RunMetaRow run={run} />
        : step.delaySeconds > 0 && (
          <div className="wfb-node-meta">
            <span className="wfb-node-tag">
              <Icon name="schedule" size={11} /> {step.delaySeconds}s delay
            </span>
          </div>
        )}

      <Handle type="source" position={Position.Right} isConnectable={!readOnly} />
    </div>
  )
}

export default memo(StepNode)
