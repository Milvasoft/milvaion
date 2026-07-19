import { useState, useEffect, useCallback } from 'react'
import roleService from '../../services/roleService'
import permissionService from '../../services/permissionService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { SkeletonTable } from '../../components/Skeleton'
import { getApiErrorMessage } from '../../utils/errorUtils'
import AuditInfoCard from '../../components/AuditInfoCard'
import './RoleList.css'

function RoleList() {
  const [roles, setRoles] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [debouncedSearchTerm, setDebouncedSearchTerm] = useState('')
  const [currentPage, setCurrentPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [totalCount, setTotalCount] = useState(0)

  // Form state
  const [showFormModal, setShowFormModal] = useState(false)
  const [editingRole, setEditingRole] = useState(null)
  const [formData, setFormData] = useState({ name: '', permissionIdList: [] })
  const [formLoading, setFormLoading] = useState(false)
  const [formError, setFormError] = useState('')

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

  const loadRoles = useCallback(async (showLoading = false) => {
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

      const response = await roleService.getAll(requestBody)
      const data = response?.data?.data || response?.data || []
      const total = response?.data?.totalDataCount || response?.totalDataCount || 0

      setRoles(data)
      setTotalCount(total)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load roles'))
      console.error(err)
    } finally {
      if (showLoading) setLoading(false)
    }
  }, [currentPage, pageSize, debouncedSearchTerm])

  useEffect(() => {
    loadRoles(true)
  }, [loadRoles])

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
    setEditingRole(null)
    setFormData({ name: '', permissionIdList: [] })
    setFormError('')
    setPermissionSearch('')
    setShowFormModal(true)
  }

  const handleEdit = async (role) => {
    await loadPermissions()
    setFormError('')
    setPermissionSearch('')

    try {
      const response = await roleService.getById(role.id)
      const detail = response?.data || response

      setEditingRole(detail)
      setFormData({
        name: detail.name || '',
        permissionIdList: detail.permissions?.map(p => p.id) || []
      })
      setShowFormModal(true)
    } catch (err) {
      await showError('Failed to load role details')
      console.error(err)
    }
  }

  const handleDelete = async (id) => {
    const confirmed = await showConfirm(
      'Are you sure you want to delete this role? This action cannot be undone.',
      'Delete Role',
      'Delete',
      'Cancel'
    )
    if (!confirmed) return

    try {
      const response = await roleService.delete(id)
      if (response?.isSuccess === false) {
        const message = response.messages?.[0]?.message || 'Failed to delete role.'
        await showError(message)
        return
      }
      await loadRoles(true)
      await showSuccess('Role deleted successfully')
    } catch (err) {
      await showError('Failed to delete role. Please try again.')
      console.error(err)
    }
  }

  const handleFormSubmit = async () => {
    if (!formData.name.trim()) {
      setFormError('Role name is required')
      return
    }

    setFormLoading(true)
    setFormError('')

    try {
      if (editingRole) {
        const response = await roleService.update(editingRole.id, {
          name: formData.name,
          permissionIdList: formData.permissionIdList
        })
        if (response?.isSuccess === false) {
          setFormError(response.messages?.[0]?.message || 'Failed to update role.')
          setFormLoading(false)
          return
        }
        setShowFormModal(false)
        setFormLoading(false)
        loadRoles(true)
        await showSuccess('Role updated successfully')
      } else {
        const response = await roleService.create({
          name: formData.name,
          permissionIdList: formData.permissionIdList
        })
        if (response?.isSuccess === false) {
          setFormError(response.messages?.[0]?.message || 'Failed to create role.')
          setFormLoading(false)
          return
        }
        setShowFormModal(false)
        setFormLoading(false)
        loadRoles(true)
        await showSuccess('Role created successfully')
      }
    } catch (err) {
      setFormError(err.response?.data?.messages?.[0]?.message || 'An error occurred. Please try again.')
      setFormLoading(false)
      console.error(err)
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

  if (loading) return <SkeletonTable rows={pageSize} columns={3} />
  if (error) return <div className="error">{error}</div>

  return (
    <div className="page role-list">
      <Modal {...modalProps} />

      <div className="page-header">
        <h1>
          <Icon name="shield" size={28} />
          <span>Roles</span>
          <span>({totalCount})</span>
        </h1>
        <button className="create-btn" onClick={handleCreate}>
          <Icon name="add" size={18} />
          <span>Create Role</span>
        </button>
      </div>

      <div className="search-section">
        <div className="search-box">
          <input
            type="text"
            placeholder="Search by role name..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="search-input"
          />
          {searchTerm && (
            <button onClick={() => setSearchTerm('')} className="clear-search-btn" title="Clear search">
              <Icon name="close" size={16} />
            </button>
          )}
        </div>
      </div>

      {roles.length === 0 ? (
        <div className="empty-state-card">
          <div className="empty-icon">
            <Icon name="shield" size={64} />
          </div>
          <h3>No Roles Found</h3>
          <p>{searchTerm ? 'No roles match your search.' : 'Get started by creating your first role.'}</p>
          {!searchTerm && (
            <button className="empty-action-btn" onClick={handleCreate}>Create Your First Role</button>
          )}
        </div>
      ) : (
        <div className="table-container">
          <table className="data-table">
            <thead>
              <tr>
                <th>ID</th>
                <th>Name</th>
                <th className="actions-col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {roles.map(role => (
                <tr key={role.id} className="clickable-row" onClick={() => handleEdit(role)}>
                  <td className="id-col">{role.id}</td>
                  <td>
                    <div className="role-name">
                      <Icon name="shield" size={16} />
                      <span>{role.name}</span>
                    </div>
                  </td>
                  <td className="actions-col">
                    <div className="row-actions">
                      <button onClick={(e) => { e.stopPropagation(); handleEdit(role) }} className="action-btn edit" title="Edit">
                        <Icon name="edit" size={16} />
                      </button>
                      <button onClick={(e) => { e.stopPropagation(); handleDelete(role.id) }} className="action-btn delete" title="Delete">
                        <Icon name="delete" size={16} />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {/* Pagination */}
          {totalPages > 1 && (
            <div className="pagination">
              <div className="pagination-info">
                Showing {(currentPage - 1) * pageSize + 1}-{Math.min(currentPage * pageSize, totalCount)} of {totalCount}
              </div>
              <div className="pagination-controls">
                <button onClick={() => handlePageChange(1)} disabled={currentPage === 1} className="page-btn">
                  <Icon name="first_page" size={18} />
                </button>
                <button onClick={() => handlePageChange(currentPage - 1)} disabled={currentPage === 1} className="page-btn">
                  <Icon name="chevron_left" size={18} />
                </button>
                <span className="page-indicator">Page {currentPage} of {totalPages}</span>
                <button onClick={() => handlePageChange(currentPage + 1)} disabled={currentPage === totalPages} className="page-btn">
                  <Icon name="chevron_right" size={18} />
                </button>
                <button onClick={() => handlePageChange(totalPages)} disabled={currentPage === totalPages} className="page-btn">
                  <Icon name="last_page" size={18} />
                </button>
              </div>
              <div className="page-size-selector">
                <select value={pageSize} onChange={(e) => { setPageSize(Number(e.target.value)); setCurrentPage(1) }}>
                  <option value={10}>10 / page</option>
                  <option value={20}>20 / page</option>
                  <option value={50}>50 / page</option>
                </select>
              </div>
            </div>
          )}
        </div>
      )}

      {/* Create/Edit Modal */}
      {showFormModal && (
        <div className="modal-overlay" onClick={(e) => { if (e.target === e.currentTarget) setShowFormModal(false) }}>
          <div className="form-modal">
            <div className="form-modal-header">
              <h2>{editingRole ? 'Edit Role' : 'Create Role'}</h2>
              <button className="modal-close-btn" onClick={() => setShowFormModal(false)}>
                <Icon name="close" size={20} />
              </button>
            </div>

            <div className="form-modal-body">
              {formError && <div className="form-error"><Icon name="error" size={16} /><span>{formError}</span></div>}

              <div className="form-group">
                <label htmlFor="roleName">Role Name *</label>
                <input
                  id="roleName"
                  type="text"
                  value={formData.name}
                  onChange={(e) => setFormData(prev => ({ ...prev, name: e.target.value }))}
                  placeholder="e.g. Editor, Viewer"
                  className="form-input"
                />
              </div>

              <div className="form-group">
                <label>
                  Permissions
                  <span className="selected-count">({formData.permissionIdList.length} selected)</span>
                </label>

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

              {editingRole?.auditInfo && (
                <AuditInfoCard auditInfo={editingRole.auditInfo} inline />
              )}
            </div>

            <div className="form-modal-footer">
              <button className="btn-cancel" onClick={() => setShowFormModal(false)}>Cancel</button>
              <button className="btn-submit" onClick={handleFormSubmit} disabled={formLoading}>
                {formLoading ? 'Saving...' : (editingRole ? 'Update Role' : 'Create Role')}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

export default RoleList
