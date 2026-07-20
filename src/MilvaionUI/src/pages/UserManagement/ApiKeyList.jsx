import { useState, useEffect, useCallback } from 'react'
import apiKeyService from '../../services/apiKeyService'
import permissionService from '../../services/permissionService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { SkeletonTable } from '../../components/Skeleton'
import { getApiErrorMessage } from '../../utils/errorUtils'
import AuditInfoCard from '../../components/AuditInfoCard'
import Pagination from '../../components/Pagination'
import TableActions, { ActionButton } from '../../components/TableActions'
import { TableToolbar, TableSearch, TableFooter, TimeCell, StatusCell } from '../../components/TableParts'
// RoleList.css carries the shared list-page styling (header, table, pagination, form modal), all scoped under
// .role-list. This page reuses that class alongside its own so the two screens cannot drift apart visually.
// ApiKeyList.css only adds what is specific to api keys.
import './RoleList.css'
import './ApiKeyList.css'

const EXPIRY_OPTIONS = [
  { label: '30 days', days: 30 },
  { label: '90 days', days: 90 },
  { label: '1 year', days: 365 },
  { label: 'Never expires', days: null }
]

/* `tone` is the shared vocabulary from `table.css`: it colours the status label and the
   row's left edge together, so a dead key is visible before the column is read. */
const getStatus = (apiKey) => {
  if (apiKey.revokedAt) return { label: 'Revoked', tone: 'danger', icon: 'block' }
  if (apiKey.expiresAt && new Date(apiKey.expiresAt) <= new Date()) {
    return { label: 'Expired', tone: 'warning', icon: 'schedule' }
  }

  return { label: 'Active', tone: 'success', icon: 'check_circle' }
}

function ApiKeyList() {
  const [apiKeys, setApiKeys] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [debouncedSearchTerm, setDebouncedSearchTerm] = useState('')
  const [currentPage, setCurrentPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [totalCount, setTotalCount] = useState(0)

  // Form state
  const [showFormModal, setShowFormModal] = useState(false)
  const [editingApiKey, setEditingApiKey] = useState(null)
  const [formData, setFormData] = useState({ name: '', description: '', expiryDays: 90, permissionIdList: [] })
  const [formLoading, setFormLoading] = useState(false)
  const [formError, setFormError] = useState('')

  // The generated key is returned exactly once, so it lives here until the user dismisses it.
  const [createdKey, setCreatedKey] = useState(null)
  const [keyCopied, setKeyCopied] = useState(false)

  // Permissions
  const [allPermissions, setAllPermissions] = useState([])
  const [permissionsLoading, setPermissionsLoading] = useState(false)
  const [permissionSearch, setPermissionSearch] = useState('')

  const { modalProps, showConfirm, showSuccess, showError } = useModal()

  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearchTerm(searchTerm)
      setCurrentPage(1)
    }, 500)
    return () => clearTimeout(timer)
  }, [searchTerm])

  const loadApiKeys = useCallback(async (showLoading = false) => {
    try {
      if (showLoading) setLoading(true)
      setError(null)

      const requestBody = {
        pageNumber: currentPage,
        rowCount: pageSize
      }

      if (debouncedSearchTerm) {
        requestBody.filtering = {
          criterias: [
            {
              filterBy: 'Name',
              value: debouncedSearchTerm,
              type: 1 // Contains
            }
          ]
        }
      }

      const response = await apiKeyService.getAll(requestBody)
      const data = response?.data?.data || response?.data || []
      const total = response?.data?.totalDataCount || response?.totalDataCount || 0

      setApiKeys(data)
      setTotalCount(total)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load api keys'))
      console.error(err)
    } finally {
      if (showLoading) setLoading(false)
    }
  }, [currentPage, pageSize, debouncedSearchTerm])

  useEffect(() => {
    loadApiKeys(true)
  }, [loadApiKeys])

  const loadPermissions = async () => {
    if (allPermissions.length > 0) return
    setPermissionsLoading(true)
    try {
      const response = await permissionService.getAll()
      const data = response?.data?.data || response?.data || []
      setAllPermissions(data)
    } catch (err) {
      console.error('Failed to load permissions', err)
    } finally {
      setPermissionsLoading(false)
    }
  }

  const handleCreate = async () => {
    await loadPermissions()
    setEditingApiKey(null)
    setFormData({ name: '', description: '', expiryDays: 90, permissionIdList: [] })
    setFormError('')
    setPermissionSearch('')
    setShowFormModal(true)
  }

  const handleEdit = async (apiKey) => {
    await loadPermissions()
    setFormError('')
    setPermissionSearch('')

    try {
      const response = await apiKeyService.getById(apiKey.id)
      const detail = response?.data || response

      setEditingApiKey(detail)
      setFormData({
        name: detail.name || '',
        description: detail.description || '',
        expiryDays: null,
        permissionIdList: detail.permissions?.map(p => p.id) || []
      })
      setShowFormModal(true)
    } catch (err) {
      await showError('Failed to load api key details')
      console.error(err)
    }
  }

  const handleRevoke = async (apiKey) => {
    const confirmed = await showConfirm(
      `"${apiKey.name}" will stop working immediately and anything using it will start failing. The record is kept for the audit trail.`,
      'Revoke Api Key',
      'Revoke',
      'Cancel'
    )
    if (!confirmed) return

    try {
      const response = await apiKeyService.revoke(apiKey.id)
      if (response?.isSuccess === false) {
        await showError(response.messages?.[0]?.message || 'Failed to revoke api key.')
        return
      }
      await loadApiKeys(true)
      await showSuccess('Api key revoked')
    } catch (err) {
      await showError('Failed to revoke api key. Please try again.')
      console.error(err)
    }
  }

  const handleDelete = async (id) => {
    const confirmed = await showConfirm(
      'Deleting removes the key and its audit trail. Revoke instead if you may need to explain later what this key was.',
      'Delete Api Key',
      'Delete',
      'Cancel'
    )
    if (!confirmed) return

    try {
      const response = await apiKeyService.delete(id)
      if (response?.isSuccess === false) {
        await showError(response.messages?.[0]?.message || 'Failed to delete api key.')
        return
      }
      await loadApiKeys(true)
      await showSuccess('Api key deleted successfully')
    } catch (err) {
      await showError('Failed to delete api key. Please try again.')
      console.error(err)
    }
  }

  const handleFormSubmit = async () => {
    if (!formData.name.trim()) {
      setFormError('Api key name is required')
      return
    }

    setFormLoading(true)
    setFormError('')

    try {
      if (editingApiKey) {
        const response = await apiKeyService.update(editingApiKey.id, {
          name: formData.name,
          description: formData.description,
          permissionIdList: formData.permissionIdList
        })
        if (response?.isSuccess === false) {
          setFormError(response.messages?.[0]?.message || 'Failed to update api key.')
          setFormLoading(false)
          return
        }
        setShowFormModal(false)
        setFormLoading(false)
        loadApiKeys(true)
        await showSuccess('Api key updated successfully')
      } else {
        const expiresAt = formData.expiryDays
          ? new Date(Date.now() + formData.expiryDays * 24 * 60 * 60 * 1000).toISOString()
          : null

        const response = await apiKeyService.create({
          name: formData.name,
          description: formData.description,
          expiresAt,
          permissionIdList: formData.permissionIdList
        })

        if (response?.isSuccess === false) {
          setFormError(response.messages?.[0]?.message || 'Failed to create api key.')
          setFormLoading(false)
          return
        }

        const created = response?.data || response

        setShowFormModal(false)
        setFormLoading(false)
        setKeyCopied(false)
        setCreatedKey(created)
        loadApiKeys(true)
      }
    } catch (err) {
      setFormError(err.response?.data?.messages?.[0]?.message || 'An error occurred. Please try again.')
      setFormLoading(false)
      console.error(err)
    }
  }

  const handleCopyKey = async () => {
    try {
      await navigator.clipboard.writeText(createdKey.key)
      setKeyCopied(true)
      setTimeout(() => setKeyCopied(false), 2000)
    } catch (err) {
      console.error('Failed to copy key', err)
    }
  }

  const togglePermission = (permId) => {
    setFormData(prev => ({
      ...prev,
      permissionIdList: prev.permissionIdList.includes(permId)
        ? prev.permissionIdList.filter(id => id !== permId)
        : [...prev.permissionIdList, permId]
    }))
  }

  const toggleGroupPermissions = (groupPermissions) => {
    const groupIds = groupPermissions.map(p => p.id)
    const allSelected = groupIds.every(id => formData.permissionIdList.includes(id))

    setFormData(prev => ({
      ...prev,
      permissionIdList: allSelected
        ? prev.permissionIdList.filter(id => !groupIds.includes(id))
        : [...new Set([...prev.permissionIdList, ...groupIds])]
    }))
  }

  const groupedPermissions = allPermissions.reduce((acc, perm) => {
    const group = perm.permissionGroup || 'Other'
    if (!acc[group]) acc[group] = { description: perm.permissionGroupDescription, permissions: [] }
    acc[group].permissions.push(perm)
    return acc
  }, {})

  const filteredGroups = Object.entries(groupedPermissions).filter(([groupName, group]) => {
    if (!permissionSearch) return true
    const search = permissionSearch.toLowerCase()
    return groupName.toLowerCase().includes(search) ||
      (group.description || '').toLowerCase().includes(search) ||
      group.permissions.some(p => p.name.toLowerCase().includes(search))
  })

  const totalPages = Math.ceil(totalCount / pageSize)

  const handlePageChange = (newPage) => {
    if (newPage >= 1 && newPage <= totalPages) {
      setCurrentPage(newPage)
    }
  }

  if (loading) return <SkeletonTable rows={pageSize} columns={6} />
  if (error) return <div className="error">{error}</div>

  return (
    <div className="page role-list api-key-list">
      <Modal {...modalProps} />

      <div className="page-header">
        <h1>
          <Icon name="key" size={28} />
          <span>Api Keys</span>
          <span>({totalCount})</span>
        </h1>
        <button className="create-btn" onClick={handleCreate}>
          <Icon name="add" size={18} />
          <span>Create Api Key</span>
        </button>
      </div>

      {apiKeys.length === 0 ? (
        <div className="empty-state-card">
          <div className="empty-icon">
            <Icon name="key" size={64} />
          </div>
          <h3>No Api Keys Found</h3>
          <p>
            {searchTerm
              ? 'No api keys match your search.'
              : 'Api keys let CI pipelines, scripts and MCP clients call Milvaion without an interactive login.'}
          </p>
          {!searchTerm && (
            <button className="empty-action-btn" onClick={handleCreate}>Create Your First Api Key</button>
          )}
        </div>
      ) : (
        <div className="mv-table-card">
          <TableToolbar>
            <TableSearch value={searchTerm} onChange={setSearchTerm} placeholder="Search by api key name…" />
          </TableToolbar>

          <div className="mv-table-scroll">
            <table className="mv-table">
              <thead>
                <tr>
                  <th>Key</th>
                  <th className="mv-col-tight">Status</th>
                  <th className="mv-col-tight">Expires</th>
                  <th className="mv-col-tight">Last used</th>
                  <th className="mv-table-actions">Actions</th>
                </tr>
              </thead>
              <tbody>
                {apiKeys.map(apiKey => {
                  const status = getStatus(apiKey)

                  return (
                    <tr
                      key={apiKey.id}
                      className={'mv-table-row is-clickable' + (status.tone === 'success' ? '' : ` tone-${status.tone}`)}
                      onClick={() => handleEdit(apiKey)}
                    >
                      <td>
                        {/* The masked key belongs under the name - it identifies the same
                            key, and nobody scans a column of masked strings. */}
                        <span className="mv-table-primary">{apiKey.name}</span>
                        <span className="mv-table-muted">{apiKey.maskedKey || '—'}</span>
                      </td>
                      <td className="mv-col-tight">
                        <StatusCell status={status} />
                      </td>
                      <td className="mv-col-tight">
                        {apiKey.expiresAt
                          ? <TimeCell value={apiKey.expiresAt} />
                          : <span className="mv-table-dim">Never</span>}
                      </td>
                      <td className="mv-col-tight">
                        <TimeCell value={apiKey.lastUsedAt} placeholder="never used" />
                      </td>
                      <td className="mv-table-actions">
                        <TableActions>
                          <ActionButton intent="edit" icon="edit" title="Edit" onClick={() => handleEdit(apiKey)} />
                          {!apiKey.revokedAt && (
                            <ActionButton intent="danger" icon="block" title="Revoke" onClick={() => handleRevoke(apiKey)} />
                          )}
                          <ActionButton intent="danger" icon="delete" title="Delete" onClick={() => handleDelete(apiKey.id)} />
                        </TableActions>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>

          <TableFooter totalCount={totalCount} noun="api keys">
            <Pagination
              page={currentPage}
              pageSize={pageSize}
              totalCount={totalCount}
              onPageChange={handlePageChange}
              onPageSizeChange={(size) => { setPageSize(size); setCurrentPage(1) }}
            />
          </TableFooter>
        </div>
      )}

      {/* Create/Edit Modal */}
      {showFormModal && (
        <div className="modal-overlay" onClick={(e) => { if (e.target === e.currentTarget) setShowFormModal(false) }}>
          <div className="form-modal">
            <div className="form-modal-header">
              <h2>{editingApiKey ? 'Edit Api Key' : 'Create Api Key'}</h2>
              <button className="modal-close-btn" onClick={() => setShowFormModal(false)}>
                <Icon name="close" size={20} />
              </button>
            </div>

            <div className="form-modal-body">
              {formError && <div className="form-error"><Icon name="error" size={16} /><span>{formError}</span></div>}

              <div className="form-group">
                <label htmlFor="apiKeyName">Name *</label>
                <input
                  id="apiKeyName"
                  type="text"
                  value={formData.name}
                  onChange={(e) => setFormData(prev => ({ ...prev, name: e.target.value }))}
                  placeholder="e.g. CI pipeline, Claude Code - Bugra"
                  className="form-input"
                />
              </div>

              <div className="form-group">
                <label htmlFor="apiKeyDescription">Description</label>
                <input
                  id="apiKeyDescription"
                  type="text"
                  value={formData.description}
                  onChange={(e) => setFormData(prev => ({ ...prev, description: e.target.value }))}
                  placeholder="What is this key used for?"
                  className="form-input"
                />
              </div>

              {!editingApiKey && (
                <div className="form-group">
                  <label htmlFor="apiKeyExpiry">Expires</label>
                  <select
                    id="apiKeyExpiry"
                    className="form-input"
                    value={formData.expiryDays === null ? 'never' : formData.expiryDays}
                    onChange={(e) => setFormData(prev => ({
                      ...prev,
                      expiryDays: e.target.value === 'never' ? null : Number(e.target.value)
                    }))}
                  >
                    {EXPIRY_OPTIONS.map(option => (
                      <option key={option.label} value={option.days === null ? 'never' : option.days}>
                        {option.label}
                      </option>
                    ))}
                  </select>
                  <span className="field-hint">Expiry is fixed at creation. To change it, create a new key and revoke this one.</span>
                </div>
              )}

              <div className="form-group">
                <label>
                  Permissions
                  <span className="selected-count">({formData.permissionIdList.length} selected)</span>
                </label>
                <span className="field-hint">
                  Grant only what this key needs. For a read-only integration, pick the List and Detail permissions and nothing else.
                </span>

                {permissionsLoading ? (
                  <div className="permissions-loading">Loading permissions...</div>
                ) : (
                  <>
                    <div className="permission-search">
                      <input
                        type="text"
                        value={permissionSearch}
                        onChange={(e) => setPermissionSearch(e.target.value)}
                        placeholder="Search permissions..."
                        className="form-input"
                      />
                    </div>
                    <div className="permissions-list">
                      {filteredGroups.map(([groupName, group]) => {
                        const groupIds = group.permissions.map(p => p.id)
                        const allSelected = groupIds.every(id => formData.permissionIdList.includes(id))
                        const someSelected = groupIds.some(id => formData.permissionIdList.includes(id))

                        return (
                          <div key={groupName} className="permission-group">
                            <div className="permission-group-header" onClick={() => toggleGroupPermissions(group.permissions)}>
                              <input
                                type="checkbox"
                                checked={allSelected}
                                ref={(el) => { if (el) el.indeterminate = someSelected && !allSelected }}
                                onChange={() => toggleGroupPermissions(group.permissions)}
                              />
                              {group.description && <span className="group-description">{group.description}</span>}
                            </div>
                            <div className="permission-items">
                              {group.permissions.map(perm => (
                                <label key={perm.id} className="permission-item">
                                  <input
                                    type="checkbox"
                                    checked={formData.permissionIdList.includes(perm.id)}
                                    onChange={() => togglePermission(perm.id)}
                                  />
                                  <span className="perm-name">{perm.name}</span>
                                  {perm.description && <span className="perm-description">{perm.description}</span>}
                                </label>
                              ))}
                            </div>
                          </div>
                        )
                      })}
                      {filteredGroups.length === 0 && (
                        <div className="no-permissions">No permissions found</div>
                      )}
                    </div>
                  </>
                )}
              </div>

              {editingApiKey?.auditInfo && (
                <AuditInfoCard auditInfo={editingApiKey.auditInfo} inline />
              )}
            </div>

            <div className="form-modal-footer">
              <button className="btn-cancel" onClick={() => setShowFormModal(false)}>Cancel</button>
              <button className="btn-submit" onClick={handleFormSubmit} disabled={formLoading}>
                {formLoading ? 'Saving...' : (editingApiKey ? 'Update Api Key' : 'Create Api Key')}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Generated key reveal - shown once, never again */}
      {createdKey && (
        <div className="modal-overlay">
          <div className="form-modal key-reveal-modal">
            <div className="form-modal-header">
              <h2>Copy your api key now</h2>
            </div>

            <div className="form-modal-body">
              <div className="key-reveal-warning">
                <Icon name="warning" size={18} />
                <span>
                  This is the only time the key is shown. It is not stored anywhere, so if you lose it you will have
                  to create a new one.
                </span>
              </div>

              <div className="form-group">
                <label>{createdKey.name}</label>
                <div className="key-reveal-box">
                  <code>{createdKey.key}</code>
                  <button className="key-copy-btn" onClick={handleCopyKey} title="Copy to clipboard">
                    <Icon name={keyCopied ? 'check' : 'content_copy'} size={16} />
                    <span>{keyCopied ? 'Copied' : 'Copy'}</span>
                  </button>
                </div>
              </div>

              <div className="form-group">
                <label>How to use it</label>
                <pre className="key-usage-snippet">{`X-ApiKey: ${createdKey.key.slice(0, 12)}...`}</pre>
                <span className="field-hint">Send the key in the X-ApiKey request header.</span>
              </div>
            </div>

            <div className="form-modal-footer">
              <button className="btn-submit" onClick={() => { setCreatedKey(null); setKeyCopied(false) }}>
                I have copied it
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

export default ApiKeyList
