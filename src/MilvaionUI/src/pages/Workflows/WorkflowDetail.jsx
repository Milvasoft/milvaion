import { useState, useEffect, useCallback, useRef } from 'react'
import { useParams, Link, useNavigate } from 'react-router-dom'
import workflowService from '../../services/workflowService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import WorkflowCanvas from './WorkflowCanvas'
import CronDisplay from '../../components/CronDisplay'
import { formatDate, formatDurationMs } from '../../utils/dateUtils'
import TableActions, { ActionButton } from '../../components/TableActions'
import AutoRefreshIndicator from '../../components/AutoRefreshIndicator'
import AuditInfoCard from '../../components/AuditInfoCard'
import TriggerWorkflowModal from '../../components/TriggerWorkflowModal'
import { triggerResultModal } from '../../components/TriggerResult'
import CollapsibleSection from '../../components/CollapsibleSection'
import './WorkflowDetail.css'
import { SkeletonDetail } from '../../components/Skeleton'

const workflowStatusLabels = { 0: 'Pending', 1: 'Running', 2: 'Completed', 3: 'Failed', 4: 'Cancelled', 5: 'Partially Completed' }
const workflowStatusColors = { 0: 'pending', 1: 'running', 2: 'completed', 3: 'failed', 4: 'cancelled', 5: 'partial' }
const failureStrategyLabels = { 0: 'Stop on First Failure', 1: 'Continue on Failure' }

function WorkflowDetail() {
  const { id } = useParams()
  const navigate = useNavigate()
  const [workflow, setWorkflow] = useState(null)
  const [runs, setRuns] = useState([])
  const [loading, setLoading] = useState(true)
  const [runsLoading, setRunsLoading] = useState(false)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [lastRefreshTime, setLastRefreshTime] = useState(null)
  const [currentPage, setCurrentPage] = useState(1)
  const [totalRunsCount, setTotalRunsCount] = useState(0)
  const [showVersionHistory, setShowVersionHistory] = useState(false)
  const [expandedVersions, setExpandedVersions] = useState({})
  const runsPerPage = 20
  const { modalProps, showConfirm, showSuccess, showError, showModal } = useModal()
  const isInitialLoadRef = useRef(true)

  const [showTriggerModal, setShowTriggerModal] = useState(false)

  const toggleVersionExpand = (index) => {
    setExpandedVersions(prev => ({
      ...prev,
      [index]: !prev[index]
    }))
  }

  const loadWorkflow = useCallback(async (showLoading = false) => {
    try {
      if (showLoading) {
        setLoading(true)
      }
      const response = await workflowService.getById(id)
      if (response?.isSuccess) {
        setWorkflow(prev => {
          if (!prev) return response.data

          // Only update if data actually changed
          const hasChanges = Object.keys(response.data).some(
            key => JSON.stringify(prev[key]) !== JSON.stringify(response.data[key])
          )

          return hasChanges ? { ...prev, ...response.data } : prev
        })
      }
    } catch (err) {
      console.error('Failed to load workflow:', err)
    } finally {
      if (showLoading) {
        setLoading(false)
      }
    }
  }, [id])

  const loadRuns = useCallback(async (page = null) => {
    try {
      setRunsLoading(true)
      const targetPage = page ?? currentPage
      const response = await workflowService.getRuns(id, { pageNumber: targetPage, rowCount: runsPerPage })
      const newRuns = response?.data || []
      const newTotal = response?.totalDataCount || 0

      setRuns(prev => {
        // Only update if runs changed
        if (JSON.stringify(prev) === JSON.stringify(newRuns)) {
          return prev
        }
        return newRuns
      })

      setTotalRunsCount(newTotal)
      if (page !== null) setCurrentPage(page)
    } catch (err) {
      console.error('Failed to load runs:', err)
    } finally {
      setRunsLoading(false)
      setLastRefreshTime(new Date())
    }
  }, [id, currentPage])

  useEffect(() => {
    loadWorkflow(isInitialLoadRef.current)
    loadRuns()
    isInitialLoadRef.current = false
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  useEffect(() => {
    if (!autoRefresh) return
    const interval = setInterval(() => {
      loadWorkflow(false) // Don't show loading spinner during auto-refresh
      loadRuns()
    }, 15000)
    return () => clearInterval(interval)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoRefresh, id])

  const handleTrigger = () => setShowTriggerModal(true)

  const handleDelete = async () => {
    const confirmed = await showConfirm(
      `Are you sure you want to delete workflow "${workflow?.name}"? This action cannot be undone.`,
      'Delete Workflow',
      'Delete',
      'Cancel'
    )

    if (!confirmed) return

    try {
      const response = await workflowService.delete(id)

      if (response?.isSuccess === false) {
        const message = response.messages?.[0]?.message || 'Failed to delete workflow.'
        await showError(message)
        return
      }

      await showSuccess('Workflow deleted successfully')
      navigate('/workflows')
    } catch (err) {
      await showError('Failed to delete workflow. Please try again.')
      console.error(err)
    }
  }

  if (loading) return <SkeletonDetail />

  if (!workflow) {
    return <div className="workflow-detail-error">Workflow not found</div>
  }

  return (
    <div className="page workflow-detail-page">
      {/* Header */}
      <div className="dtl-header">
        <div className="dtl-header-left">
          <Link to="/workflows" className="dtl-back" title="Back to Workflows">
            <Icon name="arrow_back" size={24} />
          </Link>
          <div className="dtl-title">
            <h1>
              <Icon name="account_tree" size={28} />
              {workflow.name}
              <span className={`dtl-badge ${workflow.isActive ? 'dtl-badge--success' : 'dtl-badge--muted'}`}>
                {workflow.isActive ? 'Active' : 'Inactive'}
              </span>
              <button
                className="wfd-version-badge clickable"
                onClick={() => setShowVersionHistory(true)}
                title="View version history"
              >
                v{workflow.version}
              </button>
            </h1>
          </div>
        </div>
        <div className="dtl-actions">
          <AutoRefreshIndicator
            enabled={autoRefresh}
            onToggle={() => setAutoRefresh(p => !p)}
            lastRefreshTime={lastRefreshTime}
            intervalSeconds={15}
          />
          <Link to={`/workflows/${id}/builder`} className="dtl-btn dtl-btn--secondary" title="Open visual builder (experimental)">
            <Icon name="account_tree" size={18} /> Edit via Workspace
          </Link>
          <Link to={`/workflows/${id}/edit`} className="dtl-btn dtl-btn--secondary">
            <Icon name="edit" size={18} /> Edit
          </Link>
          <button className="dtl-btn dtl-btn--primary" onClick={handleTrigger} disabled={!workflow.isActive}>
            <Icon name="play_arrow" size={18} /> Run Workflow
          </button>
          <button className="dtl-btn dtl-btn--danger" onClick={handleDelete}>
            <Icon name="delete" size={18} /> Delete
          </button>
          <AuditInfoCard auditInfo={workflow.auditInfo} />
        </div>
      </div>

      {/* Configuration */}
      <CollapsibleSection
        className="workflow-config-section"
        storageKey="milvaion.workflowDetail.configOpen"
        icon="tune"
        title="Configuration"
      >
        <div className="info-card">
          <label>Description</label>
          <span title={workflow.description || 'No description'}>
            {workflow.description
              ? (workflow.description.length > 300
                ? `${workflow.description.substring(0, 300)}...`
                : workflow.description)
              : 'No description'}
          </span>
        </div>

        <div className="workflow-info-grid">
          <div className="info-card">
            <label>Failure Strategy</label>
            <span>{failureStrategyLabels[workflow.failureStrategy]}</span>
          </div>
          <div className="info-card">
            <label>Max Step Retries</label>
            <span>{workflow.maxStepRetries}</span>
          </div>
          <div className="info-card">
            <label>Timeout</label>
            <span>{workflow.timeoutSeconds ? `${workflow.timeoutSeconds}s` : 'No timeout'}</span>
          </div>
          <div className="info-card">
            <label>Steps</label>
            <span>{workflow.steps?.length || 0}</span>
          </div>
          <div className="info-card">
            <label>Schedule</label>
            <span>
              {workflow.cronExpression ? (
                <CronDisplay expression={workflow.cronExpression} showTooltip={true} />
              ) : (
                <span className="text-muted">Manual trigger only</span>
              )}
            </span>
          </div>
        </div>
      </CollapsibleSection>

      {/* DAG Visualization */}
      <CollapsibleSection
        className="workflow-dag-section"
        storageKey="milvaion.workflowDetail.dagOpen"
        icon="schema"
        title="Workflow DAG"
      >
        <WorkflowCanvas steps={workflow.steps || []} edges={workflow.edges || []} />
      </CollapsibleSection>

      {/* Steps */}
      <CollapsibleSection
        className="workflow-steps-section"
        storageKey="milvaion.workflowDetail.stepsOpen"
        icon="list"
        title={`Steps (${workflow.steps?.length || 0})`}
      >
        <div className="table-container mv-table-wrap">
          <table className="mv-table">
            <thead>
              <tr>
                <th className="mv-col-tight">#</th>
                <th>Step</th>
                <th>Job</th>
                <th>Runs after</th>
                <th className="mv-col-tight">Delay</th>
              </tr>
            </thead>
            <tbody>
              {(workflow.steps || []).map((step) => {
                const incomingEdges = (workflow.edges || []).filter(e => e.targetStepId === step.id)
                const dependencies = incomingEdges.map(e => {
                  const sourceStep = workflow.steps?.find(s => s.id === e.sourceStepId)
                  return sourceStep ? sourceStep.stepName : e.sourceStepId
                }).join(', ')

                return (
                  <tr key={step.id} className="mv-table-row">
                    <td className="mv-col-tight">{step.order}</td>
                    <td>
                      {/* Node type under the name: only condition nodes have one worth
                          saying, so a whole column would be mostly dashes. */}
                      <span className="mv-table-primary">{step.stepName}</span>
                      {step.nodeType === 1 && <span className="mv-table-muted">condition node</span>}
                    </td>
                    <td>
                      {step.jobId ? (
                        <Link to={`/jobs/${step.jobId}`} className="mv-table-primary">
                          {step.jobDisplayName || step.jobId}
                        </Link>
                      ) : (
                        <span className="mv-table-dim">Virtual node</span>
                      )}
                    </td>
                    <td>
                      {dependencies || <span className="mv-table-dim">Root</span>}
                    </td>
                    <td className="mv-col-tight">
                      {step.delaySeconds > 0
                        ? `${step.delaySeconds}s`
                        : <span className="mv-table-dim">—</span>}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </CollapsibleSection>

      {/* Runs Section */}
      <CollapsibleSection
        className="workflow-runs-section"
        storageKey="milvaion.workflowDetail.runsOpen"
        icon="history"
        title="Recent Runs"
      >
        {runsLoading ? (
          <div className="loading-text">Loading runs...</div>
        ) : runs.length === 0 ? (
          <div className="empty-runs">No runs yet. Click &quot;Run Workflow&quot; to trigger the first execution.</div>
        ) : (
          <>
            <div className="table-container mv-table-wrap">
              <table className="mv-table">
                <thead>
                  <tr>
                    <th>Run</th>
                    <th className="mv-col-tight">Status</th>
                    <th className="mv-col-tight">Started</th>
                    <th className="mv-col-tight">Duration</th>
                    <th className="mv-table-actions">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {runs.map(run => (
                    // Satırın tamamı tıklanabilir. Klavye için de erişilebilir olması gerekiyor,
                    // yoksa fareyle yapılabilen bir şey klavyeyle yapılamaz hale gelir; satır
                    // sonundaki View bağlantısı da bu yüzden duruyor.
                    <tr
                      key={run.id}
                      className="mv-table-row is-clickable"
                      onClick={() => navigate(`/workflows/${id}/runs/${run.id}`)}
                      onKeyDown={e => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault()
                          navigate(`/workflows/${id}/runs/${run.id}`)
                        }
                      }}
                      tabIndex={0}
                      role="link"
                      aria-label={`Open run ${run.id.substring(0, 8)}`}
                    >
                      <td>
                        {/* Why the run started sits under its id - it explains that run
                            rather than being something you scan a column of. */}
                        <code className="mv-table-code">{run.id.substring(0, 8)}…</code>
                        <span className="mv-table-muted">{run.triggerReason || 'no reason recorded'}</span>
                      </td>
                      <td className="mv-col-tight">
                        <span className={`status-badge status-${workflowStatusColors[run.status]}`}>
                          {workflowStatusLabels[run.status]}
                        </span>
                      </td>
                      <td className="mv-col-tight">
                        {run.startTime
                          ? <span title={formatDate(run.startTime)}>{formatDate(run.startTime)}</span>
                          : <span className="mv-table-dim">—</span>}
                      </td>
                      <td className="mv-col-tight">
                        {run.durationMs
                          ? <span className="mv-duration-text">{formatDurationMs(run.durationMs)}</span>
                          : <span className="mv-table-dim">—</span>}
                      </td>
                      {/* Satır zaten gidiyor, ama bağlantının kendisi orta tıklama ve
                          "yeni sekmede aç" için gerekli. */}
                      <td className="mv-table-actions" onClick={e => e.stopPropagation()}>
                        <TableActions>
                          <ActionButton
                            intent="primary"
                            icon="visibility"
                            title="View run"
                            to={`/workflows/${id}/runs/${run.id}`}
                          />
                        </TableActions>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {totalRunsCount > runsPerPage && (
              <div className="pagination">
                <button
                  className="dtl-btn dtl-btn--secondary dtl-btn--sm"
                  onClick={() => loadRuns(currentPage - 1)}
                  disabled={currentPage === 1 || runsLoading}
                >
                  <Icon name="chevron_left" size={16} /> Previous
                </button>
                <span className="pagination-info">
                  Page {currentPage} of {Math.ceil(totalRunsCount / runsPerPage)} ({totalRunsCount} total runs)
                </span>
                <button
                  className="dtl-btn dtl-btn--secondary dtl-btn--sm"
                  onClick={() => loadRuns(currentPage + 1)}
                  disabled={currentPage >= Math.ceil(totalRunsCount / runsPerPage) || runsLoading}
                >
                  Next <Icon name="chevron_right" size={16} />
                </button>
              </div>
            )}
          </>
        )}
      </CollapsibleSection>

      {/* Trigger Modal */}
      {showTriggerModal && (
        <TriggerWorkflowModal
          workflow={workflow}
          onClose={(errMsg) => {
            setShowTriggerModal(false)
            if (errMsg) showError(errMsg)
          }}
          onSuccess={(runId) => {
            setShowTriggerModal(false)

            /*
             * The dialog first, the refresh after.
             *
             * With the refresh in between, anything it did to the page happened before the
             * user was told what they had just started - and if it ever fails, reporting
             * the result of their action should not go down with it. The list page has
             * always done it in this order and has always worked.
             */
            showModal(triggerResultModal({
              title: 'Workflow triggered',
              label: 'Run ID',
              id: runId,
              goToLabel: 'Go to run',
              to: `/workflows/${id}/runs/${runId}`,
              navigate,
            }))

            loadRuns(1)
          }}
        />
      )}

      {/* Version History Modal */}
      {showVersionHistory && (
        <div className="modal-overlay" onClick={() => setShowVersionHistory(false)}>
          <div className="modal-content version-history-modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <h2><Icon name="history" size={22} /> Version History</h2>
              <button className="modal-close-btn" onClick={() => setShowVersionHistory(false)}>
                <Icon name="close" size={20} />
              </button>
            </div>
            <div className="modal-body">
              {!workflow.workflowVersions || workflow.workflowVersions.length === 0 ? (
                <div className="empty-state">
                  <Icon name="info" size={24} />
                  <p>No previous versions available. Version history is recorded after the first update.</p>
                </div>
              ) : (
                <div className="version-history-list">
                  {workflow.workflowVersions.map((versionJson, index) => {
                    try {
                      // Check if already parsed or needs parsing
                      const version = typeof versionJson === 'string' ? JSON.parse(versionJson) : versionJson
                      const isExpanded = expandedVersions[index] ?? false
                      return (
                        <div key={index} className="version-card">
                          <div className="version-header" onClick={() => toggleVersionExpand(index)}>
                            <div className="version-header-left">
                              <span className="version-number">v{version.version}</span>
                              <span className={`version-badge ${version.isActive ? 'version-badge-active' : 'version-badge-inactive'}`}>
                                {version.isActive ? 'Active' : 'Inactive'}
                              </span>
                            </div>
                            <div className="version-header-right">
                              <span className="version-date">{formatDate(version.creationDate)}</span>
                              <Icon name={isExpanded ? 'expand_less' : 'expand_more'} size={24} />
                            </div>
                          </div>

                          {isExpanded && (
                            <div className="version-content">
                              {/* Basic Info */}
                              <div className="version-section">
                                <h4><Icon name="info" size={16} /> Basic Information</h4>
                                <div className="version-details">
                                  <div className="version-row">
                                    <strong>Name:</strong> <span>{version.name}</span>
                                  </div>
                                  {version.description && (
                                    <div className="version-row">
                                      <strong>Description:</strong> <span>{version.description}</span>
                                    </div>
                                  )}
                                  {version.tags && (
                                    <div className="version-row">
                                      <strong>Tags:</strong>
                                      <span className="version-tags">
                                        {version.tags.split(',').map((tag, i) => (
                                          <span key={i} className="version-tag">{tag.trim()}</span>
                                        ))}
                                      </span>
                                    </div>
                                  )}
                                </div>
                              </div>

                              {/* Configuration */}
                              <div className="version-section">
                                <h4><Icon name="settings" size={16} /> Configuration</h4>
                                <div className="version-details">
                                  <div className="version-row">
                                    <strong>Failure Strategy:</strong> <span>{failureStrategyLabels[version.failureStrategy]}</span>
                                  </div>
                                  <div className="version-row">
                                    <strong>Max Step Retries:</strong> <span>{version.maxStepRetries}</span>
                                  </div>
                                  <div className="version-row">
                                    <strong>Timeout:</strong> <span>{version.timeoutSeconds ? `${version.timeoutSeconds}s` : 'No timeout'}</span>
                                  </div>
                                  {version.cronExpression && (
                                    <div className="version-row">
                                      <strong>Schedule:</strong> <span className="version-cron">{version.cronExpression}</span>
                                    </div>
                                  )}
                                  {version.lastScheduledRunAt && (
                                    <div className="version-row">
                                      <strong>Last Scheduled:</strong> <span>{formatDate(version.lastScheduledRunAt)}</span>
                                    </div>
                                  )}
                                </div>
                              </div>

                              {/* Steps */}
                              <div className="version-section">
                                <h4><Icon name="account_tree" size={16} /> Steps ({version.steps?.length || 0})</h4>
                                {version.steps && version.steps.length > 0 ? (
                                  <div className="version-steps-list">
                                    {version.steps.sort((a, b) => a.order - b.order).map((step, i) => (
                                      <div key={i} className="version-step">
                                        <div className="version-step-header">
                                          <span className="version-step-order">#{step.order}</span>
                                          <strong>{step.stepName}</strong>
                                          {step.jobName && (
                                            <span className="version-step-job">
                                              <Icon name="work" size={14} />
                                              {step.jobName}
                                              {step.jobVersion && (
                                                <span className="job-version-badge">v{step.jobVersion}</span>
                                              )}
                                            </span>
                                          )}
                                        </div>
                                        <div className="version-step-details">
                                          {step.dependsOnStepIds && (
                                            <div className="version-step-row">
                                              <Icon name="link" size={14} />
                                              <span>Depends on: {step.dependsOnStepIds.split(',').map(depId => {
                                                const depStep = version.steps.find(s => s.id === depId.trim())
                                                return depStep?.stepName || depId.trim()
                                              }).join(', ')}</span>
                                            </div>
                                          )}
                                          {step.condition && (
                                            <div className="version-step-row">
                                              <Icon name="rule" size={14} />
                                              <span>Condition: <code className="inline-code">{step.condition}</code></span>
                                            </div>
                                          )}
                                          {step.delaySeconds > 0 && (
                                            <div className="version-step-row">
                                              <Icon name="schedule" size={14} />
                                              <span>Delay: {step.delaySeconds}s</span>
                                            </div>
                                          )}
                                          {step.dataMappings && (
                                            <div className="version-step-row version-step-mappings">
                                              <Icon name="compare_arrows" size={14} />
                                              <div className="version-mappings-content">
                                                <strong>Data Mappings:</strong>
                                                <pre className="version-json">{typeof step.dataMappings === 'string'
                                                  ? JSON.stringify(JSON.parse(step.dataMappings), null, 2)
                                                  : JSON.stringify(step.dataMappings, null, 2)}</pre>
                                              </div>
                                            </div>
                                          )}
                                          {step.jobDataOverride && (
                                            <div className="version-step-row version-step-data">
                                              <Icon name="data_object" size={14} />
                                              <div className="version-data-content">
                                                <strong>Job Data Override:</strong>
                                                <pre className="version-json">{typeof step.jobDataOverride === 'string'
                                                  ? JSON.stringify(JSON.parse(step.jobDataOverride), null, 2)
                                                  : JSON.stringify(step.jobDataOverride, null, 2)}</pre>
                                              </div>
                                            </div>
                                          )}
                                        </div>
                                      </div>
                                    ))}
                                  </div>
                                ) : (
                                  <div className="version-empty">No steps</div>
                                )}
                              </div>

                              {/* Metadata */}
                              <div className="version-section version-metadata">
                                <h4><Icon name="person" size={16} /> Metadata</h4>
                                <div className="version-details">
                                  <div className="version-row">
                                    <strong>Created by:</strong> <span>{version.creatorUserName || 'Unknown'}</span>
                                  </div>
                                  {version.lastModifierUserName && (
                                    <>
                                      <div className="version-row">
                                        <strong>Last modified by:</strong> <span>{version.lastModifierUserName}</span>
                                      </div>
                                      {version.lastModificationDate && (
                                        <div className="version-row">
                                          <strong>Modified at:</strong> <span>{formatDate(version.lastModificationDate)}</span>
                                        </div>
                                      )}
                                    </>
                                  )}
                                </div>
                              </div>
                            </div>
                          )}
                        </div>
                      )
                    } catch (err) {
                      console.error(`Failed to parse version ${index + 1}:`, err)
                      console.error('Raw version JSON:', versionJson)
                      return (
                        <div key={index} className="version-card error">
                          <Icon name="error" size={18} />
                          <span>Failed to parse version {index + 1}</span>
                        </div>
                      )
                    }
                  })}
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      <Modal {...modalProps} />
    </div>
  )
}

export default WorkflowDetail
