import { useState, useEffect, useCallback } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import workflowService from '../../services/workflowService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import CronDisplay from '../../components/CronDisplay'
import TriggerWorkflowModal from '../../components/TriggerWorkflowModal'
import './WorkflowList.css'
import { SkeletonGrid } from '../../components/Skeleton'

const failureStrategyLabels = {
  0: 'Stop on First Failure',
  1: 'Continue on Failure',
  2: 'Retry then Stop',
  3: 'Retry then Continue'
}

function WorkflowList() {
  const [workflows, setWorkflows] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [triggerTarget, setTriggerTarget] = useState(null) // workflow to trigger
  const navigate = useNavigate()
  const { modalProps, showConfirm, showSuccess, showError } = useModal()

  const loadWorkflows = useCallback(async () => {
    try {
      setLoading(true)
      const response = await workflowService.getAll()
      setWorkflows(response?.data || [])
    } catch (err) {
      setError('Failed to load workflows')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    loadWorkflows()
  }, [loadWorkflows])

  const handleTrigger = (workflow) => setTriggerTarget(workflow)

  const handleDelete = async (workflow) => {
    const confirmed = await showConfirm(
      `Are you sure you want to delete workflow "${workflow.name}"? This action cannot be undone.`,
      'Delete Workflow',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    try {
      const response = await workflowService.delete(workflow.id)

      if (response?.isSuccess === false) {
        const message = response.messages?.[0]?.message || 'Failed to delete workflow.'
        await showError(message)
        return
      }

      await loadWorkflows()
      await showSuccess('Workflow deleted successfully')
    } catch (err) {
      await showError('Failed to delete workflow. Please try again.')
      console.error(err)
    }
  }

  if (loading) {
    return (
      <div className="page workflow-list-page">
        <div className="page-header">
          <div className="page-title">
            <Icon name="account_tree" size={28} />
            <h1>Workflows</h1>
          </div>
        </div>
        <SkeletonGrid cards={6} lines={3} />
      </div>
    )
  }

  return (
    <div className="page workflow-list-page">
      <div className="page-header">
        <div className="page-title">
          <Icon name="account_tree" size={28} />
          <h1>Workflows ({workflows.length})</h1>
        </div>
        <div className="page-header-actions">
          <Link to="/workflows/new/builder" className="create-workflow-btn create-workflow-btn--builder" title="Create with visual builder (experimental)">
            <Icon name="account_tree" size={18} />
            Create via Workspace
          </Link>
          <Link to="/workflows/new" className="create-workflow-btn">
            <Icon name="add" size={18} />
            Create Workflow
          </Link>
        </div>
      </div>

      {error && <div className="error-banner">{error}</div>}

      <div className="workflow-grid">
        {workflows.length === 0 ? (
          <div className="empty-state">
            <Icon name="account_tree" size={48} />
            <h3>No workflows yet</h3>
            <p>Create your first workflow to chain jobs together.</p>
            <Link to="/workflows/new" className="create-workflow-btn">Create Workflow</Link>
          </div>
        ) : (
          workflows.map(workflow => (
            <div key={workflow.id} className="workflow-card" onClick={() => navigate(`/workflows/${workflow.id}`)}>
              <div className="workflow-card-header">
                <Link to={`/workflows/${workflow.id}`} className="workflow-name">
                  <Icon name="account_tree" size={20} />
                  {workflow.name}
                </Link>
                <span className={`badge ${workflow.isActive ? 'badge-success' : 'badge-muted'}`}>
                  {workflow.isActive ? 'Active' : 'Inactive'}
                </span>
              </div>
              <p className="workflow-description">{workflow.description || 'No description'}</p>
              <div className="workflow-meta">
                <span><Icon name="layers" size={14} /> {workflow.stepCount} steps</span>
                <span><Icon name="shield" size={14} /> {failureStrategyLabels[workflow.failureStrategy]}</span>
                <span><Icon name="tag" size={14} /> v{workflow.version}</span>
                {workflow.cronExpression && (
                  <span className="workflow-cron-badge" title={workflow.cronExpression}>
                    <Icon name="schedule" size={14} /> <CronDisplay expression={workflow.cronExpression} showTooltip={false} />
                  </span>
                )}
              </div>
              {workflow.tags && (
                <div className="workflow-tags">
                  {workflow.tags.split(',').map((tag, i) => (
                    <span key={i} className="tag-chip">{tag.trim()}</span>
                  ))}
                </div>
              )}
              <div className="workflow-actions">
                <button className="wf-action-btn wf-action-primary" onClick={(e) => { e.stopPropagation(); handleTrigger(workflow) }} disabled={!workflow.isActive}>
                  <Icon name="play_arrow" size={16} /> Run
                </button>
                <Link to={`/workflows/${workflow.id}`} className="wf-action-btn wf-action-secondary" onClick={(e) => e.stopPropagation()}>
                  <Icon name="visibility" size={16} /> View
                </Link>
                <button className="wf-action-btn wf-action-danger" onClick={(e) => { e.stopPropagation(); handleDelete(workflow) }}>
                  <Icon name="delete" size={16} />
                </button>
              </div>
            </div>
          ))
        )}
      </div>

      {triggerTarget && (
        <TriggerWorkflowModal
          workflowId={triggerTarget.id}
          onClose={(errMsg) => {
            setTriggerTarget(null)
            if (errMsg) showError(errMsg)
          }}
          onSuccess={(runId) => {
            setTriggerTarget(null)
            showSuccess('Workflow triggered! Run ID: ' + runId)
            navigate(`/workflows/${triggerTarget.id}/runs/${runId}`)
          }}
        />
      )}

      <Modal {...modalProps} />
    </div>
  )
}

export default WorkflowList
