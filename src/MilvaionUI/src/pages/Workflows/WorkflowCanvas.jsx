import { useMemo, useCallback } from 'react'
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  MarkerType,
  Panel,
  ReactFlowProvider,
} from 'reactflow'
import 'reactflow/dist/style.css'
import Icon from '../../components/Icon'
import StepNode from './WorkflowBuilder/StepNode'
import ConditionNode from './WorkflowBuilder/ConditionNode'
import MergeNode from './WorkflowBuilder/MergeNode'
import './WorkflowBuilder/WorkflowBuilder.css'
import './WorkflowCanvas.css'

const nodeTypes = { stepNode: StepNode, conditionNode: ConditionNode, mergeNode: MergeNode }

// Aynı palet WorkflowDAG'dan geliyor; sunucudaki WorkflowStepStatus sırasına karşılık gelir.
const statusColors = {
  0: '#94a3b8',  // Pending
  1: '#3b82f6',  // Running
  2: '#22c55e',  // Completed
  3: '#ef4444',  // Failed
  4: '#a855f7',  // Skipped
  5: '#6b7280',  // Cancelled
  6: '#f59e0b',  // Delayed
}

const NODE_WIDTH = 220
const NODE_HEIGHT = 90
const H_GAP = 90
const V_GAP = 40

/**
 * Kaydedilmiş pozisyonu olmayan adımlar için soldan sağa akan bir yerleşim üretir.
 *
 * Builder pozisyonları kaydediyor, ama bir workflow API üzerinden ya da MCP ile oluşturulduysa
 * pozisyon hiç yazılmamış olabilir. O durumda hepsi (0,0) noktasında üst üste binerdi.
 */
function autoLayout(steps, edges) {
  const stepIds = new Set(steps.map(s => s.id))
  const incoming = new Map(steps.map(s => [s.id, []]))
  const outgoing = new Map(steps.map(s => [s.id, []]))

  for (const edge of edges || []) {
    if (!stepIds.has(edge.sourceStepId) || !stepIds.has(edge.targetStepId)) continue
    incoming.get(edge.targetStepId).push(edge.sourceStepId)
    outgoing.get(edge.sourceStepId).push(edge.targetStepId)
  }

  // Kahn'ın algoritması: her düğümün seviyesi, kendisine gelen en uzun yolun uzunluğu.
  const levels = new Map()
  const inDegree = new Map()
  const queue = []

  for (const step of steps) {
    const degree = incoming.get(step.id).length
    inDegree.set(step.id, degree)

    if (degree === 0) {
      queue.push(step.id)
      levels.set(step.id, 0)
    }
  }

  for (let i = 0; i < queue.length; i++) {
    const current = queue[i]
    const level = levels.get(current) ?? 0

    for (const child of outgoing.get(current) || []) {
      levels.set(child, Math.max(levels.get(child) ?? 0, level + 1))

      const remaining = inDegree.get(child) - 1
      inDegree.set(child, remaining)

      if (remaining === 0) queue.push(child)
    }
  }

  // Döngü varsa bazı düğümler kuyruğa hiç girmez. Grafiği çizmeyi büsbütün bırakmaktansa
  // onları sıfırıncı seviyeye koyuyoruz - bozuk bir DAG'ı görebilmek onu düzeltmenin ön koşulu.
  const grouped = new Map()

  for (const step of steps) {
    const level = levels.get(step.id) ?? 0
    if (!grouped.has(level)) grouped.set(level, [])
    grouped.get(level).push(step)
  }

  const positions = new Map()

  for (const [level, group] of grouped) {
    group.sort((a, b) => (a.order ?? 0) - (b.order ?? 0))

    group.forEach((step, index) => {
      positions.set(step.id, {
        x: level * (NODE_WIDTH + H_GAP) + 40,
        y: index * (NODE_HEIGHT + V_GAP) + 40,
      })
    })
  }

  return positions
}

/**
 * Salt okunur DAG görünümü. Builder ile aynı React Flow kurulumunu ve aynı node bileşenlerini
 * kullanır, böylece workflow üç ekranda da aynı görünür.
 */
/* eslint-disable react/prop-types */
function WorkflowCanvasInner({
  steps = [],
  edges = [],
  stepRuns = null,
  onStepClick = null,
  selectedStepId = null,
  height = 460,
}) {
  // Adım id'si → o adımın çalışma sonucu. Run ekranında dolu, tanım ekranlarında null.
  const runsByStepId = useMemo(() => {
    if (!stepRuns) return null

    return new Map(stepRuns.filter(r => r.workflowStepId).map(r => [r.workflowStepId, r]))
  }, [stepRuns])

  // Patlayan bir adımın aşağısında kalan her şey soluklaşsın: çalışmamış olmakla
  // atlanmış olmak farklı şeyler ve fark ekranda görünmeli.
  const blockedStepIds = useMemo(() => {
    if (!runsByStepId) return new Set()

    const failed = steps.filter(s => runsByStepId.get(s.id)?.status === 3).map(s => s.id)

    if (failed.length === 0) return new Set()

    const downstream = new Map()

    for (const edge of edges || []) {
      if (!downstream.has(edge.sourceStepId)) downstream.set(edge.sourceStepId, [])
      downstream.get(edge.sourceStepId).push(edge.targetStepId)
    }

    const blocked = new Set()
    const stack = [...failed]

    while (stack.length > 0) {
      const current = stack.pop()

      for (const next of downstream.get(current) || []) {
        // Çalışmış bir adım bloke değildir - dallanma sayesinde yolu bulmuş olabilir.
        if (blocked.has(next) || runsByStepId.get(next)) continue

        blocked.add(next)
        stack.push(next)
      }
    }

    return blocked
  }, [steps, edges, runsByStepId])

  // Bir adımdan çıkan kenarların etiketleri, kartın içinde gösterilmek üzere.
  // Kenarın üzerinde dururken uzaklaştığında okunmuyor, kenarlar kesiştiğinde de
  // hangi etiketin hangi kenara ait olduğu belirsizleşiyordu.
  const branchesByStepId = useMemo(() => {
    const map = new Map()

    for (const edge of edges || []) {
      const label = edge.label || edge.sourcePort

      if (!label) continue

      const port = (edge.sourcePort || '').toLowerCase()
      const tone = port === 'true' ? 'success' : port === 'false' ? 'danger' : 'neutral'

      if (!map.has(edge.sourceStepId)) map.set(edge.sourceStepId, [])
      map.get(edge.sourceStepId).push({ label, tone })
    }

    return map
  }, [edges])

  // Merge node kartı kaç dal beklediğini yazıyor, o sayı buradan geliyor.
  const incomingCountByStepId = useMemo(() => {
    const counts = new Map()

    for (const edge of edges || [])
      counts.set(edge.targetStepId, (counts.get(edge.targetStepId) ?? 0) + 1)

    return counts
  }, [edges])

  const flowNodes = useMemo(() => {
    const needsLayout = steps.some(s => s.positionX == null || s.positionY == null)
    const computed = needsLayout ? autoLayout(steps, edges) : null

    return steps.map((step, index) => {
      let type = 'stepNode'
      if (step.nodeType === 1) type = 'conditionNode'
      else if (step.nodeType === 2) type = 'mergeNode'

      const fallback = computed?.get(step.id) ?? { x: index * (NODE_WIDTH + H_GAP) + 40, y: 40 }

      return {
        id: step.id?.toString(),
        type,
        position: {
          x: step.positionX ?? fallback.x,
          y: step.positionY ?? fallback.y,
        },
        selected: selectedStepId != null && selectedStepId === step.id,
        draggable: false,
        connectable: false,
        deletable: false,
        data: {
          step: { ...step, tempId: step.id?.toString() },
          jobsMap: null,
          readOnly: true,
          run: runsByStepId?.get(step.id) ?? null,
          dimmed: blockedStepIds.has(step.id),
          branches: branchesByStepId.get(step.id) ?? null,
          incomingCount: incomingCountByStepId.get(step.id) ?? 0,
        },
      }
    })
  }, [steps, edges, selectedStepId, runsByStepId, blockedStepIds, branchesByStepId, incomingCountByStepId])

  const flowEdges = useMemo(() => (edges || []).map(edge => {
    const sourceRun = runsByStepId?.get(edge.sourceStepId)

    // Kenarı kaynağının sonucuyla boyuyoruz. Hangi dalın gerçekten yürüdüğü,
    // düğümleri tek tek okumadan bu sayede görülüyor.
    const color = sourceRun ? (statusColors[sourceRun.status] ?? '#646cff') : '#646cff'
    const traversed = !!sourceRun && (sourceRun.status === 2 || sourceRun.status === 3)

    return {
      id: `${edge.sourceStepId}-${edge.targetStepId}-${edge.sourcePort ?? ''}`,
      source: edge.sourceStepId?.toString(),
      target: edge.targetStepId?.toString(),
      sourceHandle: edge.sourcePort || null,
      targetHandle: edge.targetPort || null,
      // Etiket bilerek yok: kaynak düğümün içinde gösteriliyor.
      animated: sourceRun?.status === 1,
      style: {
        stroke: color,
        strokeWidth: traversed ? 2.5 : 1.5,
        strokeDasharray: runsByStepId && !traversed ? '6 4' : undefined,
        opacity: runsByStepId && !sourceRun ? 0.35 : 1,
      },
      markerEnd: { type: MarkerType.ArrowClosed, color },
    }
  }), [edges, runsByStepId])

  const handleNodeClick = useCallback((_event, node) => {
    onStepClick?.(node.data.step, node.data.run)
  }, [onStepClick])

  return (
    <div className="wfc-canvas" style={{ height }}>
      <ReactFlow
        nodes={flowNodes}
        edges={flowEdges}
        nodeTypes={nodeTypes}
        onNodeClick={onStepClick ? handleNodeClick : undefined}
        proOptions={{ hideAttribution: true }}
        fitView
        fitViewOptions={{ padding: 0.2 }}
        nodesDraggable={false}
        nodesConnectable={false}
        elementsSelectable={!!onStepClick}
        deleteKeyCode={null}
        className="wfb-reactflow"
      >
        <Background variant="dots" gap={16} size={1} color="var(--border-color)" />
        <Controls showInteractive={false} />

        {steps.length > 6 && (
          <MiniMap
            nodeStrokeWidth={3}
            pannable
            zoomable
            nodeColor="var(--accent-color)"
            maskColor="rgba(0,0,0,0.35)"
            style={{ background: 'var(--bg-secondary)' }}
          />
        )}

        {steps.length === 0 && (
          <Panel position="top-center" className="wfb-empty-hint">
            <Icon name="schema" size={18} />
            This workflow has no steps yet
          </Panel>
        )}
      </ReactFlow>
    </div>
  )
}

export default function WorkflowCanvas(props) {
  return (
    <ReactFlowProvider>
      <WorkflowCanvasInner {...props} />
    </ReactFlowProvider>
  )
}
