import { BaseEdge, EdgeLabelRenderer, getBezierPath } from 'reactflow'
import Icon from '../../../components/Icon'

 
export default function CustomEdge({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, style = {}, markerEnd, data, selected }) {
  const [edgePath, labelX, labelY] = getBezierPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
  })

  const onEdgeClick = (evt) => {
    evt.stopPropagation()
    data?.onDelete?.(id)
  }

  return (
    <>
      <BaseEdge path={edgePath} markerEnd={markerEnd} style={style} />
      <EdgeLabelRenderer>
        <div
          style={{
            position: 'absolute',
            transform: `translate(-50%, -50%) translate(${labelX}px,${labelY}px)`,
            pointerEvents: 'all',
          }}
          className={`wfb-edge-btn-wrapper ${selected ? 'wfb-edge-btn-wrapper--selected' : ''}`}
          title="Delete connection"
        >
          <button
            className="wfb-edge-delete-btn"
            onClick={onEdgeClick}
            aria-label="Delete connection"
          >
            <Icon name="close" size={14} />
          </button>
        </div>
      </EdgeLabelRenderer>
    </>
  )
}
