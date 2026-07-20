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
import Pagination from '../../components/Pagination'
import useInfiniteScroll from '../../hooks/useInfiniteScroll'
import TableActions, { ActionButton } from '../../components/TableActions'
import { TableFooter } from '../../components/TableParts'
import { triggerResultModal } from '../../components/TriggerResult'
import ViewToggle from '../../components/ViewToggle'

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

  /*
   * Server-side paging. The list used to ask for everything in one request, which is
   * fine until an installation has more workflows than fit comfortably in one payload -
   * and by then the page is already slow for everyone who opens it.
   */
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [totalCount, setTotalCount] = useState(0)

  // Card view scrolls, table view pages. Both read the same server pages; only the way
  // they are presented differs, so the preference is worth remembering per user.
  const [viewMode, setViewMode] = useState(() => localStorage.getItem('workflowListViewMode') || 'card')

  const [cardWorkflows, setCardWorkflows] = useState([])
  const [cardPage, setCardPage] = useState(1)
  const [cardHasMore, setCardHasMore] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)

  const navigate = useNavigate()
  const { modalProps, showConfirm, showSuccess, showError, showModal } = useModal()

  const readList = (response) => ({
    items: response?.data?.data || response?.data || [],
    total: response?.data?.totalDataCount ?? response?.totalDataCount ?? 0,
  })

  const loadWorkflows = useCallback(async () => {
    try {
      setLoading(true)
      const { items, total } = readList(await workflowService.getAll({ pageNumber: page, rowCount: pageSize }))

      setWorkflows(items)
      setTotalCount(total)
    } catch (err) {
      setError('Failed to load workflows')
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [page, pageSize])

  const cardPageSize = 20

  const loadCardWorkflows = useCallback(async (targetPage) => {
    try {
      if (targetPage === 1) setLoading(true)
      else setLoadingMore(true)

      const { items, total } = readList(await workflowService.getAll({ pageNumber: targetPage, rowCount: cardPageSize }))

      // Appending rather than replacing is what makes this infinite scroll; page 1 still
      // replaces, so a refresh after a delete does not duplicate rows.
      setCardWorkflows(prev => targetPage === 1 ? items : [...prev, ...items])
      setTotalCount(total)
      setCardHasMore(targetPage * cardPageSize < total)
    } catch (err) {
      setError('Failed to load workflows')
      console.error(err)
    } finally {
      setLoading(false)
      setLoadingMore(false)
    }
  }, [])

  useEffect(() => {
    if (viewMode === 'table') loadWorkflows()
  }, [viewMode, loadWorkflows])

  useEffect(() => {
    if (viewMode === 'card') loadCardWorkflows(cardPage)
  }, [viewMode, cardPage, loadCardWorkflows])

  const sentinelRef = useInfiniteScroll({
    hasMore: cardHasMore,
    loading: loadingMore || loading,
    onLoadMore: () => setCardPage(p => p + 1),
    enabled: viewMode === 'card',
  })

  const toggleViewMode = (mode) => {
    setViewMode(mode)
    localStorage.setItem('workflowListViewMode', mode)

    // Each view keeps its own position; resetting avoids showing a stale half-loaded list.
    setCardPage(1)
    setPage(1)
  }

  // What the current view is showing, so the card markup and the table can share helpers.
  const visibleWorkflows = viewMode === 'card' ? cardWorkflows : workflows

  const reload = useCallback(() => {
    if (viewMode === 'card') {
      setCardPage(1)
      loadCardWorkflows(1)
    } else {
      loadWorkflows()
    }
  }, [viewMode, loadCardWorkflows, loadWorkflows])

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

      // Deleting the last row of the last page would otherwise leave the user on an
      // empty page with no indication of why.
      if (viewMode === 'table' && workflows.length === 1 && page > 1) {
        setPage(page - 1)
      } else {
        reload()
      }

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
          <h1>Workflows ({totalCount || workflows.length})</h1>
        </div>
        <div className="page-header-actions">
          <ViewToggle
            value={viewMode}
            onChange={toggleViewMode}
            options={[
              { value: 'card', icon: 'view_list', label: 'Cards' },
              { value: 'table', icon: 'table_rows', label: 'Table' },
            ]}
          />
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

      {viewMode === 'card' ? (
      <div className="workflow-grid">
        {visibleWorkflows.length === 0 && !loading ? (
          <div className="empty-state">
            <Icon name="account_tree" size={48} />
            <h3>No workflows yet</h3>
            <p>Create your first workflow to chain jobs together.</p>
            <Link to="/workflows/new" className="create-workflow-btn">Create Workflow</Link>
          </div>
        ) : (
          visibleWorkflows.map(workflow => (
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
      ) : (
        <div className="mv-table-card">
          <div className="mv-table-scroll">
          <table className="mv-table">
            <thead>
              <tr>
                <th className="mv-col-tight">Status</th>
                <th>Workflow</th>
                <th className="mv-col-tight">Steps</th>
                <th>Schedule</th>
                <th className="mv-col-tight">On failure</th>
                <th className="mv-table-actions">Actions</th>
              </tr>
            </thead>
            <tbody>
              {visibleWorkflows.length === 0 && !loading ? (
                <tr>
                  <td colSpan={6} className="mv-table-empty">No workflows found.</td>
                </tr>
              ) : (
                visibleWorkflows.map(workflow => (
                  <tr
                    key={workflow.id}
                    className={'mv-table-row is-clickable' + (workflow.isActive ? '' : ' tone-idle')}
                    onClick={() => navigate(`/workflows/${workflow.id}`)}
                  >
                    <td className="mv-col-tight">
                      <span className={'mv-status tone-' + (workflow.isActive ? 'success' : 'idle')}>
                        <Icon name={workflow.isActive ? 'check_circle' : 'cancel'} size={14} />
                        {workflow.isActive ? 'Active' : 'Inactive'}
                      </span>
                    </td>
                    <td>
                      {/* Version moves under the name: it identifies this workflow, it is
                          not something anyone scans a column of. */}
                      <span className="mv-table-primary">{workflow.name}</span>
                      <span className="mv-table-muted">
                        v{workflow.version}
                        {workflow.description ? ` · ${workflow.description}` : ''}
                      </span>
                    </td>
                    <td className="mv-col-tight">{workflow.stepCount}</td>
                    <td>
                      {workflow.cronExpression
                        ? <CronDisplay expression={workflow.cronExpression} showTooltip={false} />
                        : <span className="mv-table-dim">Manual</span>}
                    </td>
                    <td className="mv-col-tight">
                      <span className="mv-table-dim">{failureStrategyLabels[workflow.failureStrategy]}</span>
                    </td>
                    <td className="mv-table-actions">
                      <TableActions>
                        <ActionButton
                          intent="primary"
                          icon="play_arrow"
                          title="Run workflow"
                          onClick={() => handleTrigger(workflow)}
                          disabled={!workflow.isActive}
                        />
                        <ActionButton
                          intent="edit"
                          icon="visibility"
                          title="View workflow"
                          to={`/workflows/${workflow.id}`}
                        />
                        <ActionButton
                          intent="danger"
                          icon="delete"
                          title="Delete workflow"
                          onClick={() => handleDelete(workflow)}
                        />
                      </TableActions>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
          </div>

          {visibleWorkflows.length > 0 && (
            <TableFooter totalCount={totalCount} noun="workflows">
              <Pagination
                page={page}
                pageSize={pageSize}
                totalCount={totalCount}
                onPageChange={setPage}
                onPageSizeChange={(size) => {
                  setPageSize(size)
                  setPage(1)
                }}
              />
            </TableFooter>
          )}
        </div>
      )}

      {/* Card view streams; table view pages. Mixing the two would mean the scroll
          position and the page number disagreeing about where the user is. */}
      {viewMode === 'card' && (
        <>
          <div ref={sentinelRef} className="wf-scroll-sentinel" />
          {loadingMore && <div className="wf-loading-more">Loading more…</div>}
          {!cardHasMore && visibleWorkflows.length > 0 && (
            <div className="wf-loading-more">All {totalCount} workflows loaded.</div>
          )}
        </>
      )}

      {triggerTarget && (
        <TriggerWorkflowModal
          workflowId={triggerTarget.id}
          onClose={(errMsg) => {
            setTriggerTarget(null)
            if (errMsg) showError(errMsg)
          }}
          onSuccess={(runId) => {
            const workflowId = triggerTarget.id

            setTriggerTarget(null)

            /* The page no longer navigates on its own. It used to jump to the run and
               raise a dialog on top of it, so the dialog reported an id for a page the
               user was already looking at. Now the dialog leads and the button decides. */
            showModal(triggerResultModal({
              title: 'Workflow triggered',
              label: 'Run ID',
              id: runId,
              goToLabel: 'Go to run',
              to: `/workflows/${workflowId}/runs/${runId}`,
              navigate,
            }))
          }}
        />
      )}

      <Modal {...modalProps} />
    </div>
  )
}

export default WorkflowList
