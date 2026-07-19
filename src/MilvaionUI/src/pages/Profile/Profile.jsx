import { useState, useEffect, useCallback } from 'react'
import accountService from '../../services/accountService'
import authService from '../../services/authService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { getApiErrorMessage } from '../../utils/errorUtils'
import './Profile.css'
import { SkeletonCard } from '../../components/Skeleton'

function Profile() {
  const [profile, setProfile] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)

  const [passwordData, setPasswordData] = useState({ oldPassword: '', newPassword: '', confirmPassword: '' })
  const [passwordLoading, setPasswordLoading] = useState(false)
  const [passwordError, setPasswordError] = useState('')

  const { modalProps, showSuccess } = useModal()

  const currentUser = authService.getCurrentUser()

  const loadProfile = useCallback(async () => {
    try {
      setLoading(true)
      setError(null)
      const response = await accountService.getDetail(currentUser?.id)
      const data = response?.data || response
      setProfile(data)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load profile'))
      console.error(err)
    } finally {
      setLoading(false)
    }
  }, [currentUser?.id])

  useEffect(() => {
    loadProfile()
  }, [loadProfile])

  const handlePasswordChange = async () => {
    if (!passwordData.oldPassword.trim()) { setPasswordError('Current password is required'); return }
    if (!passwordData.newPassword.trim()) { setPasswordError('New password is required'); return }
    if (passwordData.newPassword !== passwordData.confirmPassword) { setPasswordError('Passwords do not match'); return }

    setPasswordLoading(true)
    setPasswordError('')

    try {
      const response = await accountService.changePassword(
        profile?.userName || currentUser?.username,
        passwordData.oldPassword,
        passwordData.newPassword
      )

      if (response?.isSuccess === false) {
        setPasswordError(response.messages?.[0]?.message || 'Failed to change password.')
        setPasswordLoading(false)
        return
      }

      setPasswordData({ oldPassword: '', newPassword: '', confirmPassword: '' })
      await showSuccess('Password changed successfully')
    } catch (err) {
      setPasswordError(err.response?.data?.messages?.[0]?.message || 'Failed to change password.')
      console.error(err)
    } finally {
      setPasswordLoading(false)
    }
  }

  if (loading) {
    return (
      <div className="page profile-page">
        <div className="page-header">
          <h1><Icon name="person" size={28} /> Profile</h1>
        </div>
        <SkeletonCard lines={4} />
        <div className="profile-grid" style={{ marginTop: '1.5rem' }}>
          <SkeletonCard lines={5} />
          <SkeletonCard lines={4} />
        </div>
      </div>
    )
  }

  if (error) return <div className="error">{error}</div>

  const getInitials = (p) => {
    if (!p) return '?'
    const n = (p.name?.trim()?.[0] || '').toUpperCase()
    const s = (p.surname?.trim()?.[0] || '').toUpperCase()
    return n + s || p.userName?.[0]?.toUpperCase() || '?'
  }

  const displayName = [profile?.name, profile?.surname].filter(Boolean).join(' ') || profile?.userName || '—'

  return (
    <div className="profile-page">
      <Modal {...modalProps} />

      <div className="page-header">
        <h1>
          <Icon name="person" size={24} />
          Profile
        </h1>
      </div>

      {/* Hero Card */}
      <div className="profile-hero-card">
        <div className="profile-avatar">
          <span className="avatar-initials">{getInitials(profile)}</span>
        </div>
        <div className="profile-hero-info">
          <div className="profile-hero-name">{displayName}</div>
          <div className="profile-hero-username">@{profile?.userName}</div>
          {profile?.roles?.length > 0 && (
            <div className="roles-badges">
              {profile.roles.map(role => (
                <span key={role.id} className="role-badge">
                  <Icon name="shield" size={12} />
                  {role.name}
                </span>
              ))}
            </div>
          )}
        </div>
      </div>

      <div className="profile-grid">
        {/* Account Info Card */}
        <div className="profile-card">
          <div className="card-header">
            <Icon name="badge" size={18} />
            <h2>Account Information</h2>
          </div>
          <div className="card-body">
            <div className="info-list">
              <div className="info-item">
                <span className="info-label">Username</span>
                <span className="info-value">{profile?.userName || '—'}</span>
              </div>
              <div className="info-item">
                <span className="info-label">Email</span>
                <span className="info-value">{profile?.email || '—'}</span>
              </div>
              <div className="info-item">
                <span className="info-label">Name</span>
                <span className="info-value">{profile?.name || '—'}</span>
              </div>
              <div className="info-item">
                <span className="info-label">Surname</span>
                <span className="info-value">{profile?.surname || '—'}</span>
              </div>
            </div>
          </div>
        </div>

        {/* Change Password Card */}
        <div className="profile-card">
          <div className="card-header">
            <Icon name="lock" size={18} />
            <h2>Change Password</h2>
          </div>
          <div className="card-body">
            {passwordError && (
              <div className="form-error">
                <Icon name="error" size={16} />
                <span>{passwordError}</span>
              </div>
            )}

            <div className="form-group">
              <label htmlFor="oldPassword">Current Password</label>
              <input
                id="oldPassword"
                type="password"
                value={passwordData.oldPassword}
                onChange={(e) => setPasswordData(prev => ({ ...prev, oldPassword: e.target.value }))}
                placeholder="Enter current password"
                className="form-input"
              />
            </div>

            <div className="form-group">
              <label htmlFor="newPassword">New Password</label>
              <input
                id="newPassword"
                type="password"
                value={passwordData.newPassword}
                onChange={(e) => setPasswordData(prev => ({ ...prev, newPassword: e.target.value }))}
                placeholder="Enter new password"
                className="form-input"
              />
            </div>

            <div className="form-group">
              <label htmlFor="confirmNewPassword">Confirm New Password</label>
              <input
                id="confirmNewPassword"
                type="password"
                value={passwordData.confirmPassword}
                onChange={(e) => setPasswordData(prev => ({ ...prev, confirmPassword: e.target.value }))}
                placeholder="Re-enter new password"
                className={`form-input${passwordData.confirmPassword && passwordData.newPassword !== passwordData.confirmPassword ? ' input-error' : ''}`}
              />
              {passwordData.confirmPassword && passwordData.newPassword !== passwordData.confirmPassword && (
                <span className="field-error">Passwords do not match</span>
              )}
            </div>

            <button
              className="btn-change-password"
              onClick={handlePasswordChange}
              disabled={passwordLoading}
            >
              <Icon name="lock_reset" size={18} />
              {passwordLoading ? 'Changing...' : 'Change Password'}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

export default Profile
