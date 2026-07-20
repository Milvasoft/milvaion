import { useState, useEffect, useCallback, useMemo, useRef } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  applyNodeChanges,
  applyEdgeChanges,
  MarkerType,
  Panel,
  useReactFlow,
  ReactFlowProvider,
} from 'reactflow'
import 'reactflow/dist/style.css'
import workflowService from '../../../services/workflowService'
import jobService from '../../../services/jobService'
import workerService from '../../../services/workerService'
import { parseSchemaFields } from '../DataMappingEditor'
import Icon from '../../../components/Icon'
import Modal from '../../../components/Modal'
import { useModal } from '../../../hooks/useModal'
import CronExpressionInput from '../../../components/CronExpressionInput'
import StepNode from './StepNode'
import ConditionNode from './ConditionNode'
import MergeNode from './MergeNode'
import CustomEdge from './CustomEdge'
import StepConfigPanel from './StepConfigPanel'
import { Skeleton } from '../../../components/Skeleton'
import './WorkflowBuilder.css'

const nodeTypes = { stepNode: StepNode, conditionNode: ConditionNode, mergeNode: MergeNode }
const edgeTypes = { custom: CustomEdge }

const failureStrategies = [
  { value: 0, label: 'Stop on First Failure' },
  { value: 1, label: 'Continue on Failure' },
]

const defaultEdgeOptions = {
  markerEnd: { type: MarkerType.ArrowClosed, color: '#646cff' },
  style: { stroke: '#646cff', strokeWidth: 2 },
  animated: true,
}

let _tempIdSeq = 1
const newTempId = () => `step-${_tempIdSeq++}`

function stepsToNodes(steps, jobsMap, onDelete, incomingCounts = new Map()) {
  return steps.map((s, idx) => {
    let nodeType = 'stepNode'
    if (s.nodeType === 1) nodeType = 'conditionNode'
    else if (s.nodeType === 2) nodeType = 'mergeNode'

    return {
      id: s.tempId,
      type: nodeType,
      position: { x: s.positionX ?? idx * 240 + 40, y: s.positionY ?? 100 },
      data: { step: s, jobsMap, onDelete, incomingCount: incomingCounts.get(s.tempId) ?? 0 },
      connectable: true,
    }
  })
}

function serializeMappings(mappings) {
  if (!mappings?.length) return null
  const obj = {}
  mappings.forEach(m => {
    if (m.sourceStepTempId && m.sourcePath && m.targetPath)
      obj[`${m.sourceStepTempId}:${m.sourcePath}`] = m.targetPath
  })
  return Object.keys(obj).length ? JSON.stringify(obj) : null
}

function deserializeMappings(json) {
  if (!json) return []
  try {
    const obj = typeof json === 'string' ? JSON.parse(json) : json
    return Object.entries(obj).map(([key, targetPath]) => {
      const [sourceStepTempId, sourcePath] = key.includes(':') ? key.split(':', 2) : ['', key]
      return { sourceStepTempId, sourcePath, targetPath }
    })
  } catch { return [] }
}

// ─── Inner component (needs useReactFlow hook) ───────────────────────────────
function WorkflowBuilderInner() {
  const { id } = useParams()
  const navigate = useNavigate()
  const isEdit = !!id
  const { modalProps, showSuccess, showError } = useModal()
  const { fitView } = useReactFlow()

  const [jobs, setJobs] = useState([])
  const [workers, setWorkers] = useState([])
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [selectedStepId, setSelectedStepId] = useState(null)
  const [showSettings, setShowSettings] = useState(false)
  const [addMenuOpen, setAddMenuOpen] = useState(false)
  const addMenuRef = useRef(null)
  const [tagInput, setTagInput] = useState('')

  const [form, setForm] = useState({
    name: '',
    description: '',
    tags: [],
    isActive: true,
    failureStrategy: 0,
    maxStepRetries: 0,
    timeoutSeconds: '',
    cronExpression: '',
  })

  const [steps, setSteps] = useState([])
  const [nodes, setNodes] = useState([])
  const [edges, setEdges] = useState([])
  const [copiedNode, setCopiedNode] = useState(null)
  const [history, setHistory] = useState([{ steps: [], edges: [] }])
  const [historyIndex, setHistoryIndex] = useState(0)

  const onNodesChange = useCallback((changes) => {
    setNodes(prev => applyNodeChanges(changes, prev))
  }, [])

  const onEdgesChange = useCallback((changes) => {
    setEdges(prev => applyEdgeChanges(changes, prev))
  }, [])

  const onEdgesDelete = useCallback((deletedEdges) => {
    const edgeIds = deletedEdges.map(e => e.id)
    setEdges(prev => prev.filter(e => !edgeIds.includes(e.id)))
  }, [])

  const handleDeleteEdge = useCallback((edgeId) => {
    setEdges(prev => prev.filter(e => e.id !== edgeId))
  }, [])

  const jobsMap = useMemo(() => Object.fromEntries(jobs.map(j => [j.id, j])), [jobs])

  // jobNameInWorker → { resultFields, dataFields } — worker schema'larından oluşturulur
  const schemasMap = useMemo(() => {
    const map = {}
    for (const worker of workers) {
      for (const jobName of (worker.jobNames || [])) {
        const dataSchemaJson = worker.jobDataDefinitions?.[jobName]
        const resultSchemaJson = worker.jobResultDefinitions?.[jobName]
        map[jobName] = {
          dataFields: parseSchemaFields(dataSchemaJson),
          resultFields: parseSchemaFields(resultSchemaJson),
        }
      }
    }
    return map
  }, [workers])

  // ── Close add-menu on outside click ─────────────────────────────────────────
  useEffect(() => {
    if (!addMenuOpen) return
    const handleOutside = (e) => {
      if (addMenuRef.current && !addMenuRef.current.contains(e.target))
        setAddMenuOpen(false)
    }
    document.addEventListener('mousedown', handleOutside)
    return () => document.removeEventListener('mousedown', handleOutside)
  }, [addMenuOpen])

  // ── Undo/Redo History ────────────────────────────────────────────────────────
  const pushHistory = useCallback((newSteps, newEdges) => {
    setHistory(prev => [...prev.slice(0, historyIndex + 1), { steps: newSteps, edges: newEdges }])
    setHistoryIndex(prev => prev + 1)
  }, [historyIndex])

  const undo = useCallback(() => {
    if (historyIndex > 0) {
      const prevState = history[historyIndex - 1]
      setSteps(prevState.steps)
      setEdges(prevState.edges)
      setHistoryIndex(prev => prev - 1)
    }
  }, [history, historyIndex])

  const redo = useCallback(() => {
    if (historyIndex < history.length - 1) {
      const nextState = history[historyIndex + 1]
      setSteps(nextState.steps)
      setEdges(nextState.edges)
      setHistoryIndex(prev => prev + 1)
    }
  }, [history, historyIndex])

  // ── Delete step ─────────────────────────────────────────────────────────────
  const handleDeleteStep = useCallback((tempId) => {
    const newSteps = steps.filter(s => s.tempId !== tempId)
    const newEdges = edges.filter(e => e.source !== tempId && e.target !== tempId)
    pushHistory(newSteps, newEdges)
    setSteps(newSteps)
    setEdges(newEdges)
    setSelectedStepId(prev => prev === tempId ? null : prev)
  }, [steps, edges, pushHistory])

  // Merge kartı kaç dal beklediğini yazıyor. Kenar sayısı üzerinden hesaplandığı için
  // node'lar bağlantı değiştiğinde de yenileniyor; pozisyonlar steps'te tutulduğundan
  // yeniden kurulmaları sürüklenen yerleşimi bozmuyor.
  const incomingCounts = useMemo(() => {
    const counts = new Map()

    for (const edge of edges)
      counts.set(edge.target, (counts.get(edge.target) ?? 0) + 1)

    return counts
  }, [edges])

  // ── Sync steps → nodes ────────────────────────────────────────────────
  useEffect(() => {
    const newNodes = stepsToNodes(steps, jobsMap, handleDeleteStep, incomingCounts)
    setNodes(newNodes)
  }, [steps, jobsMap, handleDeleteStep, incomingCounts])

  // ── Load jobs ────────────────────────────────────────────────────────────────
  // A first page only; the step panel's picker searches the server for the rest. Loading
  // every job here meant the canvas could not open until the whole catalogue had arrived.
  useEffect(() => {
    jobService.getAll({ pageNumber: 1, rowCount: 50 })
      .then(r => setJobs(r?.data?.data || r?.data || []))
      .catch(() => {})
    workerService.getAll().then(r => setWorkers(r?.data || [])).catch(() => {})
  }, [])

  // Node labels read from `jobsMap`, so a job chosen through search has to land here too.
  const rememberJob = useCallback((job) => {
    if (!job) return

    setJobs(current => current.some(j => j.id === job.id) ? current : [...current, job])
  }, [])

  // ── Load workflow (edit mode) ─────────────────────────────────────────────────
  useEffect(() => {
    if (!isEdit) return
    setLoading(true)
    workflowService.getById(id)
      .then(r => {
        const d = r.data
        setForm({
          name: d.name || '',
          description: d.description || '',
          tags: d.tags ? d.tags.split(',').map(t => t.trim()).filter(Boolean) : [],
          isActive: d.isActive,
          failureStrategy: d.failureStrategy ?? 0,
          maxStepRetries: d.maxStepRetries || 0,
          timeoutSeconds: d.timeoutSeconds || '',
          cronExpression: d.cronExpression || '',
        })
        if (d.steps?.length > 0) {
          const loadedSteps = d.steps.map(s => ({
            tempId: s.id?.toString() ?? newTempId(),
            nodeType: s.nodeType ?? 0,
            jobId: s.jobId || '',
            stepName: s.stepName || '',
            order: s.order || 0,
            nodeConfigJson: s.nodeConfigJson || '',
            delaySeconds: s.delaySeconds || 0,
            jobDataOverride: s.jobDataOverride || '',
            dataMappings: deserializeMappings(s.dataMappings),
            positionX: s.positionX,
            positionY: s.positionY,
          }))

          const loadedEdges = d.edges?.length > 0 ? d.edges.map(e => ({
            id: e.id?.toString() || `e-${e.sourceStepId}-${e.targetStepId}`,
            source: e.sourceStepId?.toString(),
            target: e.targetStepId?.toString(),
            sourceHandle: e.sourcePort || null,
            targetHandle: e.targetPort || null,
            label: e.label || '',
            type: 'custom',
            data: { onDelete: handleDeleteEdge },
            ...defaultEdgeOptions,
          })) : []

          setSteps(loadedSteps)
          setEdges(loadedEdges)
          setHistory([{ steps: loadedSteps, edges: loadedEdges }])
          setHistoryIndex(0)

          setTimeout(() => fitView({ padding: 0.2, duration: 400 }), 150)
        }
      })
      .catch(() => showError('Failed to load workflow'))
      .finally(() => setLoading(false))
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, isEdit])

  // ── Add step ─────────────────────────────────────────────────────────────────
  const addStep = () => {
    const tempId = newTempId()
    const col = steps.length % 4
    const row = Math.floor(steps.length / 4)
    const newStep = {
      tempId, nodeType: 0, jobId: '', stepName: '', order: steps.length + 1,
      nodeConfigJson: '', delaySeconds: 0,
      jobDataOverride: '', dataMappings: [],
      positionX: 40 + col * 260,
      positionY: 80 + row * 180,
    }
    const newSteps = [...steps, newStep]
    pushHistory(newSteps, edges)
    setSteps(newSteps)
    setSelectedStepId(tempId)
  }

  const addCondition = () => {
    const tempId = newTempId()
    const col = steps.length % 4
    const row = Math.floor(steps.length / 4)
    const newStep = {
      tempId, nodeType: 1, jobId: null, stepName: 'Condition', order: steps.length + 1,
      nodeConfigJson: JSON.stringify({ expression: '' }), delaySeconds: 0,
      jobDataOverride: '', dataMappings: [],
      positionX: 40 + col * 260,
      positionY: 80 + row * 180,
    }
    const newSteps = [...steps, newStep]
    pushHistory(newSteps, edges)
    setSteps(newSteps)
    setSelectedStepId(tempId)
  }

  const addMerge = () => {
    const tempId = newTempId()
    const col = steps.length % 4
    const row = Math.floor(steps.length / 4)
    const newStep = {
      tempId, nodeType: 2, jobId: null, stepName: 'Merge', order: steps.length + 1,
      nodeConfigJson: '', delaySeconds: 0,
      jobDataOverride: '', dataMappings: [],
      positionX: 40 + col * 260,
      positionY: 80 + row * 180,
    }
    const newSteps = [...steps, newStep]
    pushHistory(newSteps, edges)
    setSteps(newSteps)
    setSelectedStepId(tempId)
  }

  // ── Drag stop → persist position ─────────────────────────────────────────────
  const onNodeDragStop = useCallback((_, node) => {
    setSteps(prev =>
      prev.map(s => s.tempId === node.id
        ? { ...s, positionX: Math.round(node.position.x), positionY: Math.round(node.position.y) }
        : s
      )
    )
  }, [])

  // ── Connect edge → add dependency ─────────────────────────────────────────────
  const onConnect = useCallback((params) => {
    if (!params.source || !params.target) return
    if (params.source === params.target) return

    const newEdge = {
      id: `e-${params.source}-${params.target}-${Date.now()}`,
      source: params.source,
      target: params.target,
      sourceHandle: params.sourceHandle || null,
      targetHandle: params.targetHandle || null,
      type: 'custom',
      data: { onDelete: handleDeleteEdge },
      ...defaultEdgeOptions,
    }
    const newEdges = [...edges, newEdge]
    pushHistory(steps, newEdges)
    setEdges(newEdges)
  }, [steps, edges, handleDeleteEdge, pushHistory])

  const onNodeClick = useCallback((_, node) => {
    setSelectedStepId(prev => prev === node.id ? null : node.id)
  }, [])

  const onPaneClick = useCallback(() => setSelectedStepId(null), [])

  // ── Copy/Paste ────────────────────────────────────────────────────────────────
  useEffect(() => {
    const handleKeyDown = (e) => {
      // Ctrl+Z or Cmd+Z - Undo
      if ((e.ctrlKey || e.metaKey) && e.key === 'z' && !e.shiftKey) {
        e.preventDefault()
        undo()
      }

      // Ctrl+Shift+Z or Cmd+Shift+Z or Ctrl+Y - Redo
      if (((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'z') || (e.ctrlKey && e.key === 'y')) {
        e.preventDefault()
        redo()
      }

      const activeTag = document.activeElement?.tagName?.toLowerCase()
      const isInputFocused = activeTag === 'input' || activeTag === 'textarea'

      // Delete or Backspace - Delete selected node (when no input focused)
      if ((e.key === 'Delete' || e.key === 'Backspace') && selectedStepId && !isInputFocused) {
        e.preventDefault()
        handleDeleteStep(selectedStepId)
      }

      // Ctrl+C or Cmd+C - Copy
      if ((e.ctrlKey || e.metaKey) && e.key === 'c' && selectedStepId && !isInputFocused) {
        e.preventDefault()
        const stepToCopy = steps.find(s => s.tempId === selectedStepId)
        if (stepToCopy) {
          setCopiedNode({ ...stepToCopy })
        }
      }

      // Ctrl+V or Cmd+V - Paste
      if ((e.ctrlKey || e.metaKey) && e.key === 'v' && copiedNode && !isInputFocused) {
        e.preventDefault()
        const tempId = newTempId()
        const pastedNode = {
          ...copiedNode,
          tempId,
          stepName: `${copiedNode.stepName} (Copy)`,
          order: steps.length + 1,
          positionX: (copiedNode.positionX || 0) + 60,
          positionY: (copiedNode.positionY || 0) + 60,
        }
        const newSteps = [...steps, pastedNode]
        pushHistory(newSteps, edges)
        setSteps(newSteps)
        setSelectedStepId(tempId)
      }
    }

    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [selectedStepId, steps, edges, copiedNode, undo, redo, pushHistory, handleDeleteStep])

  const updateSelectedStep = (updated) =>
    setSteps(prev => prev.map(s => s.tempId === updated.tempId ? { ...s, ...updated } : s))

  const selectedStep = steps.find(s => s.tempId === selectedStepId) ?? null

  // ── Tags ──────────────────────────────────────────────────────────────────────
  const handleAddTag = (e) => {
    if (e.type === 'keydown' && e.key !== 'Enter') return
    e.preventDefault()
    const tag = tagInput.trim()
    if (tag && !form.tags.includes(tag)) setForm(p => ({ ...p, tags: [...p.tags, tag] }))
    setTagInput('')
  }

  // ── Save ──────────────────────────────────────────────────────────────────────
  const handleSave = async () => {
    if (!form.name.trim()) { showError('Workflow name is required'); return }
    if (steps.length === 0) { showError('At least one step is required'); return }

    // Validate condition nodes have proper port assignments
    const conditionSteps = steps.filter(s => s.nodeType === 1)
    for (const condStep of conditionSteps) {
      const outgoingEdges = edges.filter(e => e.source === condStep.tempId)
      if (outgoingEdges.length === 0) continue

      const hasTruePort = outgoingEdges.some(e => e.sourceHandle === 'true')
      const hasFalsePort = outgoingEdges.some(e => e.sourceHandle === 'false')

      if (outgoingEdges.length > 0 && (!hasTruePort && !hasFalsePort)) {
        showError(`Condition node "${condStep.stepName}" must use TRUE or FALSE ports`)
        return
      }
    }

    setSaving(true)
    try {
      const payload = {
        name: form.name,
        description: form.description,
        tags: form.tags.join(','),
        isActive: form.isActive,
        failureStrategy: Number(form.failureStrategy),
        maxStepRetries: Number(form.maxStepRetries) || 0,
        timeoutSeconds: form.timeoutSeconds ? Number(form.timeoutSeconds) : null,
        cronExpression: form.cronExpression || null,
        steps: steps.map((s, idx) => ({
          tempId: s.tempId,
          nodeType: s.nodeType ?? 0,
          jobId: (s.jobId && s.jobId !== '') ? s.jobId : null,
          stepName: s.stepName,
          order: idx + 1,
          nodeConfigJson: s.nodeConfigJson || null,
          delaySeconds: Number(s.delaySeconds) || 0,
          jobDataOverride: s.jobDataOverride || null,
          dataMappings: serializeMappings(s.dataMappings),
          positionX: s.positionX,
          positionY: s.positionY,
        })),
        edges: edges.map((e, idx) => ({
          tempId: e.id,
          sourceTempId: e.source,
          targetTempId: e.target,
          sourcePort: e.sourceHandle || null,
          targetPort: e.targetHandle || null,
          label: e.label || null,
          order: idx + 1,
          edgeConfigJson: null,
        })),
      }

      if (isEdit) {
        await workflowService.update(id, payload)
        await showSuccess('Workflow saved successfully!')
      } else {
        const res = await workflowService.create(payload)
        await showSuccess('Workflow created!')
        navigate(`/workflows/${res.data}/builder`)
      }
    } catch (err) {
      const errorMsg = err.isValidationError
        ? err.message
        : 'Failed to save workflow'
      showError(errorMsg)
      console.error(err)
    } finally {
      setSaving(false)
    }
  }

  // ─────────────────────────────────────────────────────────────────────────────

  if (loading) {
    // Düzenleyicinin kendisi bir tuval; iskelet araç çubuğunu ve tuval alanını
    // taklit ediyor, böylece yükleme bitince yerleşim sıçramıyor.
    return (
      <div className="wfb-page">
        <div className="wfb-toolbar">
          <Skeleton variant="rectangular" width={34} height={34} />
          <Skeleton variant="rectangular" width={220} height={34} />
          <div style={{ marginLeft: 'auto', display: 'flex', gap: '0.625rem' }}>
            <Skeleton variant="rectangular" width={110} height={34} />
            <Skeleton variant="rectangular" width={90} height={34} />
          </div>
        </div>
        <div className="wfb-canvas-wrapper">
          <Skeleton variant="rectangular" width="100%" height="100%" />
        </div>
      </div>
    )
  }

  return (
    <div className="wfb-page">
      {/* ── Toolbar ── */}
      <div className="wfb-toolbar">
        <div className="wfb-toolbar-left">
          <Link to={isEdit ? `/workflows/${id}` : '/workflows'} className="wfb-back-btn" title="Back">
            <Icon name="arrow_back" size={22} />
          </Link>
          <label className="wfb-active-toggle">
            <input
              type="checkbox"
              checked={form.isActive}
              onChange={e => setForm(p => ({ ...p, isActive: e.target.checked }))}
            />
            <span className={`wfb-active-label ${form.isActive ? 'is-active' : 'is-inactive'}`}>
              {form.isActive ? 'Active' : 'Inactive'}
            </span>
          </label>
          |
          <input
            className="wfb-name-input"
            value={form.name}
            onChange={e => setForm(p => ({ ...p, name: e.target.value }))}
            placeholder="Workflow name..."
          />
        </div>

        <div className="wfb-toolbar-right">
          <span className="wfb-step-count">{steps.length} step{steps.length !== 1 ? 's' : ''}</span>
          <button
            className={`wfb-toolbar-btn${showSettings ? ' wfb-toolbar-btn--pressed' : ''}`}
            onClick={() => setShowSettings(p => !p)}
          >
            <Icon name="settings" size={17} /> Settings
          </button>
          <div className="wfb-add-menu" ref={addMenuRef}>
            <button
              className="wfb-toolbar-btn wfb-toolbar-btn--add"
              onClick={() => setAddMenuOpen(p => !p)}
            >
              <Icon name="add" size={17} /> Add Step
              <Icon name={addMenuOpen ? 'expand_less' : 'expand_more'} size={15} />
            </button>
            {addMenuOpen && (
              <div className="wfb-add-menu-dropdown">
                <button className="wfb-add-menu-item" onClick={() => { addStep(); setAddMenuOpen(false) }}>
                  <Icon name="smart_button" size={15} />
                  <div>
                    <span className="wfb-add-menu-item-label">Task Step</span>
                    <span className="wfb-add-menu-item-desc">Runs a scheduled job</span>
                  </div>
                </button>
                <button className="wfb-add-menu-item" onClick={() => { addCondition(); setAddMenuOpen(false) }}>
                  <Icon name="alt_route" size={15} />
                  <div>
                    <span className="wfb-add-menu-item-label">Condition</span>
                    <span className="wfb-add-menu-item-desc">Branches on true / false</span>
                  </div>
                </button>
                <button className="wfb-add-menu-item" onClick={() => { addMerge(); setAddMenuOpen(false) }}>
                  <Icon name="call_merge" size={15} />
                  <div>
                    <span className="wfb-add-menu-item-label">Merge</span>
                    <span className="wfb-add-menu-item-desc">Joins parallel branches</span>
                  </div>
                </button>
              </div>
            )}
          </div>
          <button className="wfb-toolbar-btn wfb-toolbar-btn--save" onClick={handleSave} disabled={saving}>
            <Icon name={saving ? 'hourglass_empty' : 'save'} size={17} />
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>

      {/* ── Settings Bar ── */}
      {showSettings && (
        <div className="wfb-settings-bar">
          <div className="wfb-settings-row">
            <div className="wfb-setting-field">
              <label>Description</label>
              <input
                className="wfb-input"
                value={form.description}
                onChange={e => setForm(p => ({ ...p, description: e.target.value }))}
                placeholder="Optional description..."
              />
            </div>

            <div className="wfb-setting-field">
              <label>Tags</label>
              <div className="wfb-tags-row">
                {form.tags.map(t => (
                  <span key={t} className="wfb-tag">
                    {t}
                    <button onClick={() => setForm(p => ({ ...p, tags: p.tags.filter(x => x !== t) }))}>×</button>
                  </span>
                ))}
                <input
                  className="wfb-tag-input"
                  value={tagInput}
                  onChange={e => setTagInput(e.target.value)}
                  onKeyDown={handleAddTag}
                  onBlur={handleAddTag}
                  placeholder="Add tag…"
                />
              </div>
            </div>

            <div className="wfb-setting-field wfb-setting-field--sm">
              <label>Failure Strategy</label>
              <select
                className="wfb-select"
                value={form.failureStrategy}
                onChange={e => setForm(p => ({ ...p, failureStrategy: Number(e.target.value) }))}
              >
                {failureStrategies.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
              </select>
            </div>

            <div className="wfb-setting-field wfb-setting-field--xs">
              <label>Max Retries</label>
              <input
                className="wfb-input"
                type="number"
                min={0}
                value={form.maxStepRetries}
                onChange={e => setForm(p => ({ ...p, maxStepRetries: Number(e.target.value) || 0 }))}
              />
            </div>

            <div className="wfb-setting-field wfb-setting-field--xs">
              <label>Timeout (s)</label>
              <input
                className="wfb-input"
                type="number"
                min={0}
                value={form.timeoutSeconds}
                onChange={e => setForm(p => ({ ...p, timeoutSeconds: e.target.value }))}
                placeholder="None"
              />
            </div>
          </div>

          <div className="wfb-settings-row">
            <div className="wfb-setting-field wfb-setting-field--full">
              <label>Cron Schedule</label>
              <CronExpressionInput
                value={form.cronExpression}
                onChange={e => setForm(p => ({ ...p, cronExpression: e.target.value }))}
              />
            </div>
          </div>
        </div>
      )}

      {/* ── Canvas + Config Panel ── */}
      <div className="wfb-canvas-wrapper">
        <ReactFlow
          nodes={nodes}
          edges={edges}
          onNodesChange={onNodesChange}
          onEdgesChange={onEdgesChange}
          onEdgesDelete={onEdgesDelete}
          onConnect={onConnect}
          onNodeDragStop={onNodeDragStop}
          onNodeClick={onNodeClick}
          onPaneClick={onPaneClick}
          nodeTypes={nodeTypes}
          edgeTypes={edgeTypes}
          defaultEdgeOptions={defaultEdgeOptions}
          proOptions={{ hideAttribution: true }}
          fitView
          fitViewOptions={{ padding: 0.2 }}
          deleteKeyCode={['Backspace', 'Delete']}
          edgesReconnectable={true}
          // Bağlantı bırakılırken bu yarıçap içindeki en yakın noktaya oturuyor.
          // Varsayılan 20px, yani imleci neredeyse noktanın üstüne getirmek gerekiyordu.
          connectionRadius={45}
          className="wfb-reactflow"
          style={{ width: '100%', height: '100%' }}
        >
          <Background variant="dots" gap={16} size={1} color="var(--border-color)" />
          <Controls showInteractive={false} />
          <MiniMap
            nodeStrokeWidth={3}
            pannable
            zoomable
            nodeColor="var(--accent-color)"
            maskColor="rgba(0,0,0,0.35)"
            style={{ background: 'var(--bg-secondary)' }}
          />

          {steps.length === 0 && (
            <Panel position="top-center" className="wfb-empty-hint">
              <Icon name="add_circle_outline" size={18} />
              Click <strong>Add Step</strong> to build your workflow DAG
            </Panel>
          )}
        </ReactFlow>

        {selectedStep && (
          <StepConfigPanel
            step={selectedStep}
            jobs={jobs}
            onJobResolved={rememberJob}
            allSteps={steps}
            edges={edges}
            schemasMap={schemasMap}
            onChange={updateSelectedStep}
            onClose={() => setSelectedStepId(null)}
          />
        )}
      </div>

      <Modal {...modalProps} />
    </div>
  )
}

// Wrap with provider
export default function WorkflowBuilder() {
  return (
    <ReactFlowProvider>
      <WorkflowBuilderInner />
    </ReactFlowProvider>
  )
}
