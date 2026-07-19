import { useState, useEffect, useCallback } from 'react'
import userService from '../../services/userService'
import roleService from '../../services/roleService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { SkeletonTable } from '../../components/Skeleton'
import { getApiErrorMessage } from '../../utils/errorUtils'
import AuditInfoCard from '../../components/AuditInfoCard'
import './UserList.css'

function UserList() {
  const [users, setUsers] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [debouncedSearchTerm, setDebouncedSearchTerm] = useState('')
  const [currentPage, setCurrentPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [totalCount, setTotalCount] = useState(0)

  // Form state
  const [showFormModal, setShowFormModal] = useState(false)
  const [editingUser, setEditingUser] = useState(null)
  const [formData, setFormData] = useState({
    userName: '', email: '', name: '', surname: '', password: '', confirmPassword: '',
    userType: 1, roleIdList: [], allowedNotifications: []
  })
  const [formLoading, setFormLoading] = useState(false)
  const [formError, setFormError] = useState('')

  // Roles
  const [allRoles, setAllRoles] = useState([])
  const [rolesLoading, setRolesLoading] = useState(false)

  const { modalProps, showConfirm, showSuccess, showError } = useModal()

  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearchTerm(searchTerm)
      setCurrentPage(1)
    }, 500)
    return () => clearTimeout(timer)
  }, [searchTerm])

  const loadUsers = useCallback(async (showLoading = false) => {
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
              filterBy: 'UserName',
              value: debouncedSearchTerm,
              type: 1 // Contains
            }
          ]
        }
      }

      const response = await userService.getAll(requestBody)
      const data = response?.data?.data || response?.data || []
      const total = response?.data?.totalDataCount || response?.totalDataCount || 0

      setUsers(data)
      setTotalCount(total)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load users'))
      console.error(err)
    } finally {
      if (showLoading) setLoading(false)
    }
  }, [currentPage, pageSize, debouncedSearchTerm])

  useEffect(() => {
    loadUsers(true)
  }, [loadUsers])

  const loadRoles = async () => {
    if (allRoles.length > 0) return
    setRolesLoading(true)
    try {
      const response = await roleService.getAll({ pageNumber: 1, rowCount: 100000 })
      const data = response?.data?.data || response?.data || []
      setAllRoles(data)
    } catch (err) {
      console.error('Failed to load roles', err)
    } finally {
      setRolesLoading(false)
    }
  }

  const handleCreate = async () => {
    await loadRoles()
    setEditingUser(null)
    setFormData({
      userName: '', email: '', name: '', surname: '', password: '', confirmPassword: '',
      userType: 1, roleIdList: [], allowedNotifications: []
    })
    setFormError('')
    setShowFormModal(true)
  }

  const handleEdit = async (user) => {
    await loadRoles()
    setFormError('')

    try {
      const response = await userService.getById(user.id)
      const detail = response?.data || response

      setEditingUser(detail)
      setFormData({
        userName: detail.userName || '',
        email: detail.email || '',
        name: detail.name || '',
        surname: detail.surname || '',
        password: '',
        confirmPassword: '',
        userType: detail.userType ?? 1,
        roleIdList: detail.roles?.map(r => r.id) || [],
        allowedNotifications: detail.allowedNotifications || []
      })
      setShowFormModal(true)
    } catch (err) {
      await showError('Failed to load user details')
      console.error(err)
    }
  }

  const handleDelete = async (id) => {
    const confirmed = await showConfirm(
      'Are you sure you want to delete this user? This action cannot be undone.',
      'Delete User',
      'Delete',
      'Cancel'
    )
    if (!confirmed) return

    try {
      const response = await userService.delete(id)
      if (response?.isSuccess === false) {
        const message = response.messages?.[0]?.message || 'Failed to delete user.'
        await showError(message)
        return
      }
      await loadUsers(true)
      await showSuccess('User deleted successfully')
    } catch (err) {
      await showError('Failed to delete user. Please try again.')
      console.error(err)
    }
  }

  const handleFormSubmit = async () => {
    if (!editingUser) {
      if (!formData.userName.trim()) { setFormError('Username is required'); return }
      if (!formData.password.trim()) { setFormError('Password is required'); return }
    }

    if (formData.password && formData.password !== formData.confirmPassword) {
      setFormError('Passwords do not match'); return
    }

    setFormLoading(true)
    setFormError('')

    try {
      if (editingUser) {
        const updatedFields = ['name', 'surname', 'roleIdList', 'allowedNotifications']
        if (formData.password) updatedFields.push('newPassword')

        const updateData = {
          name: formData.name,
          surname: formData.surname,
          roleIdList: formData.roleIdList,
          allowedNotifications: formData.allowedNotifications
        }
        if (formData.password) updateData.newPassword = formData.password

        const response = await userService.update(editingUser.id, updateData, updatedFields)
        if (response?.isSuccess === false) {
          setFormError(response.messages?.[0]?.message || 'Failed to update user.')
          setFormLoading(false)
          return
        }
        setShowFormModal(false)
        setFormLoading(false)
        loadUsers(true)
        await showSuccess('User updated successfully')
      } else {
        const response = await userService.create({
          userName: formData.userName,
          email: formData.email,
          name: formData.name,
          surname: formData.surname,
          password: formData.password,
          userType: formData.userType,
          roleIdList: formData.roleIdList,
          allowedNotifications: formData.allowedNotifications
        })
        if (response?.isSuccess === false) {
          setFormError(response.messages?.[0]?.message || 'Failed to create user.')
          setFormLoading(false)
          return
        }
        setShowFormModal(false)
        setFormLoading(false)
        loadUsers(true)
        await showSuccess('User created successfully')
      }
    } catch (err) {
      setFormError(err.response?.data?.messages?.[0]?.message || 'An error occurred. Please try again.')
      setFormLoading(false)
      console.error(err)
    }
  }

  const toggleRole = (roleId) => {
    setFormData(prev => ({
      ...prev,
      roleIdList: prev.roleIdList.includes(roleId)
        ? prev.roleIdList.filter(id => id !== roleId)
        : [...prev.roleIdList, roleId]
    }))
  }

  const totalPages = Math.ceil(totalCount / pageSize)

  const handlePageChange = (newPage) => {
    if (newPage >= 1 && newPage <= totalPages) {
      setCurrentPage(newPage)
    }
  }

  if (loading) return <SkeletonTable rows={pageSize} columns={5} />
  if (error) return <div className="error">{error}</div>

  return (
    <div className="page user-list-page">
      <Modal {...modalProps} />

      <div className="page-header">
        <h1>
          <Icon name="group" size={28} />
          <span>Users</span>
          <span>({totalCount})</span>
        </h1>
        <button className="create-btn" onClick={handleCreate}>
          <Icon name="person_add" size={18} />
          <span>Create User</span>
        </button>
      </div>

      <div className="search-section">
        <div className="search-box">
          <input
            type="text"
            placeholder="Search by username..."
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

      {users.length === 0 ? (
        <div className="empty-state-card">
          <div className="empty-icon">
            <Icon name="group" size={64} />
          </div>
          <h3>No Users Found</h3>
          <p>{searchTerm ? 'No users match your search.' : 'Get started by creating your first user.'}</p>
          {!searchTerm && (
            <button className="empty-action-btn" onClick={handleCreate}>Create Your First User</button>
          )}
        </div>
      ) : (
        <div className="table-container">
          <table className="data-table">
            <thead>
              <tr>
                <th>ID</th>
                <th>Username</th>
                <th>Name</th>
                <th>Email</th>
                <th className="actions-col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {users.map(user => (
                <tr key={user.id} className="clickable-row" onClick={() => handleEdit(user)}>
                  <td className="id-col">{user.id}</td>
                  <td>
                    <div className="user-name-cell">
                      <Icon name="person" size={16} />
                      <span>{user.userName}</span>
                    </div>
                  </td>
                  <td>{user.name} {user.surname}</td>
                  <td className="email-col">{user.email || <span className="text-muted">—</span>}</td>
                  <td className="actions-col">
                    <div className="row-actions">
                      <button onClick={(e) => { e.stopPropagation(); handleEdit(user) }} className="action-btn edit" title="Edit">
                        <Icon name="edit" size={16} />
                      </button>
                      <button onClick={(e) => { e.stopPropagation(); handleDelete(user.id) }} className="action-btn delete" title="Delete">
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
              <h2>{editingUser ? 'Edit User' : 'Create User'}</h2>
              <button className="modal-close-btn" onClick={() => setShowFormModal(false)}>
                <Icon name="close" size={20} />
              </button>
            </div>

            <div className="form-modal-body">
              {formError && <div className="form-error"><Icon name="error" size={16} /><span>{formError}</span></div>}

              {!editingUser && (
                <>
                  <div className="form-group">
                    <label htmlFor="userName">Username *</label>
                    <input
                      id="userName"
                      type="text"
                      value={formData.userName}
                      onChange={(e) => setFormData(prev => ({ ...prev, userName: e.target.value }))}
                      placeholder="e.g. johndoe"
                      className="form-input"
                    />
                  </div>
                  <div className="form-group">
                    <label htmlFor="email">Email</label>
                    <input
                      id="email"
                      type="email"
                      value={formData.email}
                      onChange={(e) => setFormData(prev => ({ ...prev, email: e.target.value }))}
                      placeholder="e.g. john@example.com"
                      className="form-input"
                    />
                  </div>
                </>
              )}

              <div className="form-row">
                <div className="form-group">
                  <label htmlFor="name">Name</label>
                  <input
                    id="name"
                    type="text"
                    value={formData.name}
                    onChange={(e) => setFormData(prev => ({ ...prev, name: e.target.value }))}
                    placeholder="First name"
                    className="form-input"
                  />
                </div>
                <div className="form-group">
                  <label htmlFor="surname">Surname</label>
                  <input
                    id="surname"
                    type="text"
                    value={formData.surname}
                    onChange={(e) => setFormData(prev => ({ ...prev, surname: e.target.value }))}
                    placeholder="Last name"
                    className="form-input"
                  />
                </div>
              </div>

              <div className="form-group">
                <label htmlFor="password">{editingUser ? 'New Password' : 'Password *'}</label>
                <input
                  id="password"
                  type="password"
                  value={formData.password}
                  onChange={(e) => setFormData(prev => ({ ...prev, password: e.target.value }))}
                  placeholder={editingUser ? 'Leave empty to keep current' : 'Enter password'}
                  className="form-input"
                />
              </div>

              {(formData.password || !editingUser) && (
                <div className="form-group">
                  <label htmlFor="confirmPassword">{editingUser ? 'Confirm New Password' : 'Confirm Password *'}</label>
                  <input
                    id="confirmPassword"
                    type="password"
                    value={formData.confirmPassword}
                    onChange={(e) => setFormData(prev => ({ ...prev, confirmPassword: e.target.value }))}
                    placeholder="Re-enter password"
                    className={`form-input${formData.confirmPassword && formData.password !== formData.confirmPassword ? ' input-error' : ''}`}
                  />
                  {formData.confirmPassword && formData.password !== formData.confirmPassword && (
                    <span className="field-error">Passwords do not match</span>
                  )}
                </div>
              )}

              <div className="form-group">
                <label>
                  Roles
                  <span className="selected-count">({formData.roleIdList.length} selected)</span>
                </label>

                {rolesLoading ? (
                  <div className="roles-loading">Loading roles...</div>
                ) : (
                  <div className="roles-list">
                    {allRoles.map(role => (
                      <label key={role.id} className="role-item">
                        <input
                          type="checkbox"
                          checked={formData.roleIdList.includes(role.id)}
                          onChange={() => toggleRole(role.id)}
                        />
                        <Icon name="shield" size={14} />
                        <span>{role.name}</span>
                      </label>
                    ))}
                    {allRoles.length === 0 && <div className="no-roles">No roles available</div>}
                  </div>
                )}
              </div>

              {editingUser?.auditInfo && (
                <AuditInfoCard auditInfo={editingUser.auditInfo} inline />
              )}
            </div>

            <div className="form-modal-footer">
              <button className="btn-cancel" onClick={() => setShowFormModal(false)}>Cancel</button>
              <button className="btn-submit" onClick={handleFormSubmit} disabled={formLoading}>
                {formLoading ? 'Saving...' : (editingUser ? 'Update User' : 'Create User')}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

export default UserList
