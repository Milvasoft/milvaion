import { useState, useEffect, useCallback, useRef } from 'react'
import { useParams, Link } from 'react-router-dom'
import workflowService from '../../services/workflowService'
import occurrenceService from '../../services/occurrenceService'
import signalRService from '../../services/signalRService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import WorkflowCanvas from './WorkflowCanvas'
import { useModal } from '../../hooks/useModal'
import CollapsibleSection from '../../components/CollapsibleSection'
import { formatDate } from '../../utils/dateUtils'
import './WorkflowRunDetail.css'
import { SkeletonDetail } from '../../components/Skeleton'

const workflowStatusLabels = { 0: 'Pending', 1: 'Running', 2: 'Completed', 3: 'Failed', 4: 'Cancelled', 5: 'Partially Completed' }
const workflowStatusColors = { 0: 'pending', 1: 'running', 2: 'completed', 3: 'failed', 4: 'cancelled', 5: 'partial' }
const stepStatusLabels = { 0: 'Pending', 1: 'Running', 2: 'Completed', 3: 'Failed', 4: 'Skipped', 5: 'Cancelled', 6: 'Delayed' }
const stepStatusColors = { 0: 'pending', 1: 'running', 2: 'completed', 3: 'failed', 4: 'skipped', 5: 'cancelled', 6: 'delayed' }

const FINAL_RUN_STATUS = new Set([2, 3, 4, 5])   // Completed, Failed, Cancelled, PartiallyCompleted
const FINAL_STEP_STATUS = new Set([2, 3, 4, 5])  // Completed, Failed, Skipped, Cancelled

function WorkflowRunDetail() {
  const { id, runId } = useParams()
  const [run, setRun] = useState(null)
  const [loading, setLoading] = useState(true)
  const [signalRActive, setSignalRActive] = useState(false)

  // DAG'da seçili adım ve o adımın logları. Bir adım patladığında logu görmek için
  // ayrı bir sayfaya gitmek gerekiyordu; asıl aranan şey oysa bu gereksiz bir sıçrama.
  const [selectedStep, setSelectedStep] = useState(null)
  const [stepLogs, setStepLogs] = useState({ loading: false, lines: [], error: null })

  const subscribedRef = useRef(new Set())
  const pollingRef = useRef(null)
  const runStatusRef = useRef(null)
  const { modalProps, showConfirm, showSuccess, showError } = useModal()

  const handleCancelRun = async () => {
    const confirmed = await showConfirm(
      'Are you sure you want to cancel this workflow run? Running steps will be cancelled and pending steps will be skipped.',
      'Cancel Workflow Run',
      'Cancel Run',
      'Keep Running'
    )

    if (!confirmed) return

    try {
      const response = await workflowService.cancelRun(runId, 'Manual cancellation from UI')

      if (response?.isSuccess) {
        await showSuccess('Workflow run cancelled successfully')
        loadRun(false)
      } else {
        await showError(response?.message || 'Failed to cancel workflow run')
      }
    } catch (err) {
      await showError('Failed to cancel workflow run. Please try again.')
      console.error(err)
    }
  }

  const handleStepClick = useCallback(async (step, stepRun) => {
    setSelectedStep({ step, run: stepRun })

    if (!stepRun?.occurrenceId) {
      setStepLogs({ loading: false, lines: [], error: null })
      return
    }

    setStepLogs({ loading: true, lines: [], error: null })

    try {
      const response = await occurrenceService.getById(stepRun.occurrenceId)

      setStepLogs({
        loading: false,
        lines: response?.data?.logs ?? [],
        error: response?.isSuccess ? null : 'Failed to load logs',
      })
    } catch {
      setStepLogs({ loading: false, lines: [], error: 'Failed to load logs' })
    }
  }, [])

  const loadRun = useCallback(async (showSpinner = false) => {
    try {
      if (showSpinner) setLoading(true)
      const response = await workflowService.getRunDetail(runId)
      if (response?.isSuccess) {
        setRun(response.data)
        runStatusRef.current = response.data?.status
      }
    } catch (err) {
      console.error('Failed to load workflow run:', err)
    } finally {
      if (showSpinner) setLoading(false)
    }
  }, [runId])

  // Initial load
  useEffect(() => {
    loadRun(true)
  }, [loadRun])

  // SignalR setup + polling â€” keyed on run ID so it only runs once per run
  useEffect(() => {
    if (!run) return
    if (FINAL_RUN_STATUS.has(run.status)) return

    let cancelled = false

    const setupSignalR = async () => {
      await signalRService.connect()
      if (cancelled) return

      setSignalRActive(signalRService.isConnected())

      for (const step of run.stepRuns ?? []) {
        const occId = step.occurrenceId
        if (occId && !subscribedRef.current.has(occId)) {
          await signalRService.subscribeToOccurrence(occId)
          subscribedRef.current.add(occId)
        }
      }
    }

    setupSignalR()

    // Poll every 5 s for Pendingâ†’Running transitions and run-level status updates
    pollingRef.current = setInterval(() => {
      if (!FINAL_RUN_STATUS.has(runStatusRef.current)) {
        loadRun(false)
      } else {
        clearInterval(pollingRef.current)
      }
    }, 5000)

    return () => {
      cancelled = true
      clearInterval(pollingRef.current)
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [run?.id])  // intentionally keyed on run ID only

  // Stop live updates when run reaches a final status
  useEffect(() => {
    if (!run || !FINAL_RUN_STATUS.has(run.status)) return

    clearInterval(pollingRef.current)
    setSignalRActive(false)

    for (const occId of subscribedRef.current) {
      signalRService.unsubscribeFromOccurrence(occId)
    }

    subscribedRef.current.clear()
  }, [run?.status])

  // OccurrenceUpdated: patch the matching step row immediately
  useEffect(() => {
    const handleOccurrenceUpdated = (updated) => {
      setRun(prev => {
        if (!prev) return prev

        const stepRuns = prev.stepRuns ?? []
        const matchIdx = stepRuns.findIndex(s => s.occurrenceId === updated.id)
        if (matchIdx === -1) return prev

        const newStepRuns = stepRuns.map((step, i) => {
          if (i !== matchIdx) return step
          return {
            ...step,
            ...(updated.stepStatus != null && { status: updated.stepStatus }),
            ...(updated.startTime  != null && { startTime: updated.startTime }),
            ...(updated.endTime    != null && { endTime: updated.endTime }),
            ...(updated.durationMs != null && { durationMs: updated.durationMs }),
            ...(updated.exception  != null && { error: updated.exception }),
          }
        })

        // When all active steps are done, trigger one final reload for the run-level status
        const allFinal = newStepRuns.every(s => FINAL_STEP_STATUS.has(s.status))
        if (allFinal) setTimeout(() => loadRun(false), 1500)

        return { ...prev, stepRuns: newStepRuns }
      })
    }

    return signalRService.on('OccurrenceUpdated', handleOccurrenceUpdated)
  }, [loadRun])

  // Unsubscribe from all on unmount
  useEffect(() => {
    return () => {
      clearInterval(pollingRef.current)
      for (const occId of subscribedRef.current) {
        signalRService.unsubscribeFromOccurrence(occId)
      }
      subscribedRef.current.clear()
    }
  }, [])

  if (loading) return <SkeletonDetail />

  if (!run) {
    return <div className="wfr-loading">Workflow run not found</div>
  }

  return (
    <div className="page wfr-page">
      {/* Header */}
      <div className="dtl-header">
        <div className="dtl-header-left">
          <Link to={`/workflows/${id}`} className="dtl-back" title="Back to Workflow">
            <Icon name="arrow_back" size={24} />
          </Link>
          <div className="dtl-title">
            <h1>
              <Icon name="play_circle" size={28} />
              {run.workflowName || 'Workflow Run'}
              <span className={`wfr-status-badge wfr-status-${workflowStatusColors[run.status]}`}>
                {workflowStatusLabels[run.status]}
              </span>
              {signalRActive && (
                <span className="wfr-live-badge" title="Receiving live updates">
                  <Icon name="sensors" size={14} /> LIVE
                </span>
              )}
            </h1>
            <p className="wfr-subtitle">
              Run <code>{run.id?.substring(0, 8)}...</code>
              {run.triggerReason && <> &middot; {run.triggerReason}</>}
            </p>
          </div>
        </div>
        <div className="dtl-actions">
          {(run.status === 0 || run.status === 1) && (
            <button className="dtl-btn dtl-btn--danger" onClick={handleCancelRun}>
              <Icon name="cancel" size={18} /> Cancel Run
            </button>
          )}
          <button className="dtl-btn dtl-btn--secondary" onClick={() => loadRun(false)}>
            <Icon name="refresh" size={18} /> Refresh
          </button>
        </div>
      </div>

      {/* Run Info */}
      <div className="wfr-info-grid">
        <div className="wfr-info-card">
          <label>Status</label>
          <span className={`wfr-status-badge wfr-status-${workflowStatusColors[run.status]}`}>
            {workflowStatusLabels[run.status]}
          </span>
        </div>
        <div className="wfr-info-card">
          <label>Start Time</label>
          <span>{run.startTime ? formatDate(run.startTime) : '-'}</span>
        </div>
        <div className="wfr-info-card">
          <label>End Time</label>
          <span>{run.endTime ? formatDate(run.endTime) : '-'}</span>
        </div>
        <div className="wfr-info-card">
          <label>Duration</label>
          <span>{run.durationMs ? `${(run.durationMs / 1000).toFixed(1)}s` : '-'}</span>
        </div>
        <div className="wfr-info-card">
          <label>Version</label>
          <span>v{run.workflowVersion}</span>
        </div>
        {run.error && (
          <div className="wfr-info-card wfr-info-error">
            <label>Error</label>
            <span>{run.error}</span>
          </div>
        )}
      </div>

      {/* DAG Visualization */}
      {run.steps && run.steps.length > 0 && (
        <CollapsibleSection
          className="wfr-dag-section"
          storageKey="milvaion.workflowRun.dagOpen"
          icon="schema"
          title="Workflow DAG"
          actions={signalRActive && (
            <span className="wfr-dag-live-indicator">
              <Icon name="sensors" size={16} /> Real-time updates active
            </span>
          )}
        >
          <WorkflowCanvas
            steps={run.steps || []}
            edges={run.edges || []}
            stepRuns={run.stepRuns || []}
            selectedStepId={selectedStep?.step?.id ?? null}
            onStepClick={handleStepClick}
            height={480}
          />

          {selectedStep && (
            <div className="wfr-step-inspector">
              <div className="wfr-step-inspector-header">
                <div className="wfr-step-inspector-title">
                  <Icon name="terminal" size={16} />
                  <strong>{selectedStep.step.stepName || 'Unnamed step'}</strong>
                  {selectedStep.run && (
                    <span className={`wfr-status-badge wfr-status-${stepStatusColors[selectedStep.run.status]}`}>
                      {stepStatusLabels[selectedStep.run.status]}
                    </span>
                  )}
                </div>

                <div className="wfr-step-inspector-actions">
                  {selectedStep.run?.occurrenceId && (
                    <Link
                      to={`/occurrences/${selectedStep.run.occurrenceId}`}
                      className="dtl-btn dtl-btn--secondary dtl-btn--sm"
                    >
                      <Icon name="open_in_new" size={14} /> Occurrence
                    </Link>
                  )}
                  <button
                    className="wfr-step-inspector-close"
                    onClick={() => setSelectedStep(null)}
                    title="Close"
                  >
                    <Icon name="close" size={16} />
                  </button>
                </div>
              </div>

              {selectedStep.run?.error && (
                <div className="wfr-step-inspector-error">{selectedStep.run.error}</div>
              )}

              <div className="wfr-step-inspector-logs">
                {!selectedStep.run
                  ? <span className="wfr-step-inspector-empty">This step did not run.</span>
                  : stepLogs.loading
                    ? <span className="wfr-step-inspector-empty">Loading logs…</span>
                    : stepLogs.error
                      ? <span className="wfr-step-inspector-empty">{stepLogs.error}</span>
                      : stepLogs.lines.length === 0
                        ? <span className="wfr-step-inspector-empty">No logs recorded for this step.</span>
                        : stepLogs.lines.map((line, index) => (
                          <div key={line.id ?? index} className={`wfr-log-line wfr-log-line--${(line.level || 'information').toLowerCase()}`}>
                            <span className="wfr-log-time">{line.timestamp ? formatDate(line.timestamp) : ''}</span>
                            <span className="wfr-log-level">{line.level}</span>
                            <span className="wfr-log-message">{line.message}</span>
                          </div>
                        ))}
              </div>
            </div>
          )}
        </CollapsibleSection>
      )}

      {/* Step Runs */}
      <CollapsibleSection
        className="wfr-steps-section"
        storageKey="milvaion.workflowRun.stepRunsOpen"
        icon="layers"
        title={`Step Runs (${run.stepRuns?.length || 0})`}
      >
        <div className="wfr-table-container">
          <table className="wfr-table">
            <thead>
              <tr>
                <th>#</th>
                <th>Step</th>
                <th>Job</th>
                <th>Status</th>
                <th>Start</th>
                <th>Duration</th>
                <th>Retries</th>
                <th>Error</th>
                <th>Occurrence</th>
              </tr>
            </thead>
            <tbody>
              {[...(run.stepRuns || [])].sort((a, b) => (a.order ?? 0) - (b.order ?? 0)).map(step => (
                <tr key={step.id}>
                  <td>{step.order ?? '-'}</td>
                  <td><strong>{step.stepName}</strong></td>
                  <td>
                    <Link to={`/jobs/${step.jobId}`} className="wfr-link">
                      {step.jobDisplayName || step.jobId?.substring(0, 8)}
                    </Link>
                  </td>
                  <td>
                    <span className={`wfr-status-badge wfr-status-${stepStatusColors[step.status]}`}>
                      {stepStatusLabels[step.status]}
                    </span>
                  </td>
                  <td>{step.startTime ? formatDate(step.startTime) : '-'}</td>
                  <td>{step.durationMs ? `${(step.durationMs / 1000).toFixed(1)}s` : '-'}</td>
                  <td>{step.retryCount > 0 ? step.retryCount : '-'}</td>
                  <td className="wfr-error-cell">{step.error || '-'}</td>
                  <td>
                    {step.occurrenceId ? (
                      <Link to={`/occurrences/${step.occurrenceId}`} className="dtl-btn dtl-btn--secondary dtl-btn--sm">
                        <Icon name="open_in_new" size={14} /> {step.occurrenceId.substring(0, 8)}...
                      </Link>
                    ) : '-'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </CollapsibleSection>

      <Modal {...modalProps} />
    </div>
  )
}

export default WorkflowRunDetail
