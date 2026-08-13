import { useState, useEffect, useCallback } from 'react'
import userService from '../../services/userService'
import roleService from '../../services/roleService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { SkeletonTable } from '../../components/Skeleton'
import { getApiErrorMessage } from '../../utils/errorUtils'
import AuditInfoCard from '../../components/AuditInfoCard'
import Pagination from '../../components/Pagination'
import TableActions, { ActionButton } from '../../components/TableActions'
import { TableToolbar, TableSearch, TableFooter } from '../../components/TableParts'
import './UserList.css'

// Notification (AlertType) options for the multi-select, mirroring Milvaion.Domain.Enums.AlertType.
// Values are the numeric enum members the API stores/accepts; "All" (0) means every alert type.
const NOTIFICATION_OPTIONS = [
  { value: 0, name: 'All' },
  { value: 1, name: 'JobDispatcherMemoryUsageCritical' },
  { value: 2, name: 'DatabaseConnectionFailed' },
  { value: 3, name: 'ZombieOccurrenceDetected' },
  { value: 4, name: 'JobAutoDisabled' },
  { value: 5, name: 'QueueDepthCritical' },
  { value: 6, name: 'WorkerDisconnected' },
  { value: 7, name: 'RedisConnectionFailed' },
  { value: 8, name: 'RabbitMQConnectionFailed' },
  { value: 9, name: 'JobExecutionFailed' },
  { value: 10, name: 'UnknownException' },
  { value: 11, name: 'FailedOccurrenceReceived' },
  { value: 12, name: 'ServiceDegraded' },
  { value: 13, name: 'MemoryLeakDetected' },
  { value: 14, name: 'ApiKeyExpiring' },
  { value: 15, name: 'ApiKeyExpired' },
  { value: 16, name: 'JobMisfired' }
]

const NOTIFICATION_NAME_TO_VALUE = Object.fromEntries(NOTIFICATION_OPTIONS.map(o => [o.name, o.value]))

// The API may return these as numbers or as their enum names depending on serialization; normalize to numbers.
const normalizeNotifications = (list) =>
  (list || [])
    .map(n => (typeof n === 'number' ? n : NOTIFICATION_NAME_TO_VALUE[n]))
    .filter(n => n !== undefined && n !== null)

// "JobExecutionFailed" -> "Job Execution Failed"
const humanizeNotification = (name) =>
  name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')

// Maps the ExternalProvider enum (number or name) to a label and whether the record is externally managed.
function providerMeta(p) {
  const map = {
    0: 'Local', Local: 'Local',
    1: 'SSO (OIDC)', Oidc: 'SSO (OIDC)',
    2: 'LDAP / Active Directory', Ldap: 'LDAP / Active Directory'
  }
  const label = map[p] ?? 'Local'
  return { label, external: label !== 'Local' }
}

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
    roleIdList: [], allowedNotifications: []
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
      roleIdList: [], allowedNotifications: []
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
        roleIdList: detail.roles?.map(r => r.id) || [],
        allowedNotifications: normalizeNotifications(detail.allowedNotifications)
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

  const toggleNotification = (value) => {
    setFormData(prev => ({
      ...prev,
      allowedNotifications: prev.allowedNotifications.includes(value)
        ? prev.allowedNotifications.filter(v => v !== value)
        : [...prev.allowedNotifications, value]
    }))
  }

  // Externally managed users (OIDC/LDAP): identity, password and roles are owned by the provider, so those
  // inputs are locked here; only notification preferences remain editable.
  const editUserProvider = providerMeta(editingUser?.provider)
  const isExternalUser = !!editingUser && editUserProvider.external

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
        <div className="mv-table-card">
          <TableToolbar>
            <TableSearch value={searchTerm} onChange={setSearchTerm} placeholder="Search by username…" />
          </TableToolbar>

          <div className="mv-table-scroll">
            <table className="mv-table">
              <thead>
                <tr>
                  <th>User</th>
                  <th>Email</th>
                  <th className="mv-col-tight">ID</th>
                  <th className="mv-table-actions">Actions</th>
                </tr>
              </thead>
              <tbody>
                {users.map(user => (
                  <tr key={user.id} className="mv-table-row is-clickable" onClick={() => handleEdit(user)}>
                    <td>
                      {/* Real name under the username: it identifies the same person, so
                          it does not need a column of its own. */}
                      <span className="mv-table-primary">{user.userName}</span>
                      <span className="mv-table-muted">
                        {[user.name, user.surname].filter(Boolean).join(' ') || 'no name set'}
                      </span>
                    </td>
                    <td>{user.email || <span className="mv-table-dim">—</span>}</td>
                    <td className="mv-col-tight">
                      <code className="mv-table-code">{user.id}</code>
                    </td>
                    <td className="mv-table-actions">
                      <TableActions>
                        <ActionButton intent="edit" icon="edit" title="Edit" onClick={() => handleEdit(user)} />
                        <ActionButton intent="danger" icon="delete" title="Delete" onClick={() => handleDelete(user.id)} />
                      </TableActions>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <TableFooter totalCount={totalCount} noun="users">
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
              <h2>{editingUser ? 'Edit User' : 'Create User'}</h2>
              <button className="modal-close-btn" onClick={() => setShowFormModal(false)}>
                <Icon name="close" size={20} />
              </button>
            </div>

            <div className="form-modal-body">
              {formError && <div className="form-error"><Icon name="error" size={16} /><span>{formError}</span></div>}

              {isExternalUser && (
                <div className="form-error" style={{ background: 'transparent', color: 'var(--text-muted, #888)' }}>
                  <Icon name="info" size={16} />
                  <span>This user is managed by {editUserProvider.label}. Identity, password and roles are set there; only notification preferences are editable here.</span>
                </div>
              )}

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
                    disabled={isExternalUser}
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
                    disabled={isExternalUser}
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
                  disabled={isExternalUser}
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
                          disabled={isExternalUser}
                        />
                        <Icon name="shield" size={14} />
                        <span>{role.name}</span>
                      </label>
                    ))}
                    {allRoles.length === 0 && <div className="no-roles">No roles available</div>}
                  </div>
                )}
              </div>

              <div className="form-group">
                <label>
                  Notifications
                  <span className="selected-count">({formData.allowedNotifications.length} selected)</span>
                </label>
                <p style={{ fontSize: '12px', color: 'var(--text-muted, #888)', margin: '0 0 8px' }}>
                  Alert types this user receives. Selecting “All” covers every type.
                </p>

                <div className="roles-list">
                  {NOTIFICATION_OPTIONS.map(opt => {
                    // When "All" is picked, the specific types are implied - show them checked and locked.
                    const covered = opt.value !== 0 && formData.allowedNotifications.includes(0)
                    return (
                      <label key={opt.value} className="role-item">
                        <input
                          type="checkbox"
                          checked={covered || formData.allowedNotifications.includes(opt.value)}
                          disabled={covered}
                          onChange={() => toggleNotification(opt.value)}
                        />
                        <Icon name="notifications" size={14} />
                        <span>{humanizeNotification(opt.name)}</span>
                      </label>
                    )
                  })}
                </div>
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
