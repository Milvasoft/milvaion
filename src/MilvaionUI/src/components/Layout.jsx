import { Link, useLocation, useNavigate } from 'react-router-dom'
import { useState, useEffect, useCallback } from 'react'
import PropTypes from 'prop-types'
import Icon from './Icon'
import NotificationPanel from './NotificationPanel'
import authService from '../services/authService'
import notificationService from '../services/notificationService'
import { useTheme } from '../contexts/ThemeContext'
import './Layout.css'

function Layout({ children }) {
const location = useLocation()
const navigate = useNavigate()
const { toggleTheme, isDark } = useTheme()
const [showUserMenu, setShowUserMenu] = useState(false)
const [showAdminMenu, setShowAdminMenu] = useState(true)
const [showUserMgmtMenu, setShowUserMgmtMenu] = useState(true)
const [isSidebarCollapsed, setIsSidebarCollapsed] = useState(false)
const [isMobileMenuOpen, setIsMobileMenuOpen] = useState(false)
const [isNotificationOpen, setIsNotificationOpen] = useState(false)
const [unseenCount, setUnseenCount] = useState(0)
const user = authService.getCurrentUser()

  useEffect(() => {
    setIsMobileMenuOpen(false)
  }, [location.pathname])

  useEffect(() => {
    const handleEscape = (e) => {
      if (e.key === 'Escape' && isMobileMenuOpen) {
        setIsMobileMenuOpen(false)
      }
    }

    document.addEventListener('keydown', handleEscape)
    return () => document.removeEventListener('keydown', handleEscape)
  }, [isMobileMenuOpen])

  useEffect(() => {
    if (isMobileMenuOpen) {
      document.body.style.overflow = 'hidden'
    } else {
      document.body.style.overflow = ''
    }

    return () => {
      document.body.style.overflow = ''
    }
  }, [isMobileMenuOpen])

  const fetchUnseenCount = useCallback(async () => {
    try {
      const response = await notificationService.getNotifications({ pageIndex: 0, itemCount: 100 })
      if (response?.isSuccess) {
        const items = response.data ?? []
        setUnseenCount(items.filter(n => !n.seenDate).length)
      }
    } catch (error) {
      // Ignore fetch errors for notification badge
      console.debug('Failed to fetch notification count:', error)
    }
  }, [])

  useEffect(() => {
    fetchUnseenCount()
    const interval = setInterval(fetchUnseenCount, 60000)
    return () => clearInterval(interval)
  }, [fetchUnseenCount])

  const handleNotificationClose = useCallback(() => {
    setIsNotificationOpen(false)
    fetchUnseenCount()
  }, [fetchUnseenCount])

  const isActive = (path) => {
    return location.pathname === path || location.pathname.startsWith(path + '/')
  }

  const isExactActive = (path) => {
    return location.pathname === path
  }

  const handleLogout = async () => {
    await authService.logout()
    navigate('/login')
  }

  const toggleSidebar = () => {
    setIsSidebarCollapsed(!isSidebarCollapsed)
  }

  const toggleMobileMenu = () => {
    setIsMobileMenuOpen(!isMobileMenuOpen)
  }

  return (
    <div className="layout">
      <button
        className="mobile-menu-toggle"
        onClick={toggleMobileMenu}
        aria-label="Toggle menu"
      >
        <Icon name={isMobileMenuOpen ? 'close' : 'menu'} size={24} />
      </button>

      <div
        className={`sidebar-backdrop ${isMobileMenuOpen ? 'visible' : ''}`}
        onClick={() => setIsMobileMenuOpen(false)}
        aria-hidden="true"
      />

      <nav className={`sidebar ${isSidebarCollapsed ? 'collapsed' : ''} ${isMobileMenuOpen ? 'mobile-open' : ''}`}>
        <div className="sidebar-header">
          <div className="sidebar-header-top">
            <Link to="/dashboard" className="logo-link">
              <img src="/logo.png" alt="Milvaion Logo" className="logo" />
              {!isSidebarCollapsed && (
                <div className="logo-text">
                  <h1>Milvaion</h1>
                </div>
              )}
            </Link>
            {!isSidebarCollapsed && (
              <button
                className="sidebar-toggle"
                onClick={toggleSidebar}
                title="Collapse sidebar"
              >
                <Icon name="chevron_left" size={24} />
              </button>
            )}
          </div>
          {isSidebarCollapsed && (
            <button
              className="sidebar-toggle"
              onClick={toggleSidebar}
              title="Expand sidebar"
            >
              <Icon name="chevron_right" size={24} />
            </button>
          )}
        </div>
        <ul className="nav-menu">
          <li className={isActive('/dashboard') ? 'active' : ''}>
            <Link to="/dashboard" title="Dashboard">
              <Icon name="dashboard" size={20} />
              {!isSidebarCollapsed && <span>Dashboard</span>}
            </Link>
          </li>
          <li className={isActive('/jobs') ? 'active' : ''}>
            <Link to="/jobs" title="Jobs">
              <Icon name="settings" size={20} />
              {!isSidebarCollapsed && <span>Jobs</span>}
            </Link>
          </li>
          <li className={isActive('/workflows') ? 'active' : ''}>
            <Link to="/workflows" title="Workflows">
              <Icon name="account_tree" size={20} />
              {!isSidebarCollapsed && <span>Workflows</span>}
            </Link>
          </li>
          <li className={isActive('/executions') ? 'active' : ''}>
            <Link to="/executions" title="Executions">
              <Icon name="assignment" size={20} />
              {!isSidebarCollapsed && <span>Executions</span>}
            </Link>
          </li>
          <li className={isActive('/upcoming') ? 'active' : ''}>
            <Link to="/upcoming" title="Upcoming Executions">
              <Icon name="schedule_send" size={20} />
              {!isSidebarCollapsed && <span>Upcoming Executions</span>}
            </Link>
          </li>
          <li className={isActive('/failed-executions') ? 'active' : ''}>
            <Link to="/failed-executions" title="Failed Executions">
              <Icon name="error" size={20} />
              {!isSidebarCollapsed && <span>Failed Executions</span>}
            </Link>
          </li>
          <li className={isActive('/tags') ? 'active' : ''}>
            <Link to="/tags" title="Tags">
              <Icon name="label" size={20} />
              {!isSidebarCollapsed && <span>Tags</span>}
            </Link>
          </li>
          <li className={isActive('/reports') ? 'active' : ''}>
            <Link to="/reports" title="Reports">
              <Icon name="analytics" size={20} />
              {!isSidebarCollapsed && <span>Reports</span>}
            </Link>
          </li>

          {!isSidebarCollapsed && (
            <li className="nav-group">
              <button
                className="nav-group-header"
                onClick={() => setShowAdminMenu(!showAdminMenu)}
                title="Admin"
              >
                <div className="nav-group-title">
                  <Icon name="admin_panel_settings" size={20} />
                  <span>Admin</span>
                </div>
                <Icon name={showAdminMenu ? 'expand_less' : 'expand_more'} size={20} />
              </button>
              {showAdminMenu && (
                <ul className="nav-submenu">
                  <li className={isActive('/workers') ? 'active' : ''}>
                    <Link to="/workers">
                      <Icon name="engineering" size={18} />
                      <span>Workers</span>
                    </Link>
                  </li>
                  <li className={isExactActive('/admin') ? 'active' : ''}>
                    <Link to="/admin">
                      <Icon name="monitor_heart" size={18} />
                      <span>Monitoring</span>
                    </Link>
                  </li>
                  <li className={isActive('/configuration') ? 'active' : ''}>
                    <Link to="/configuration">
                      <Icon name="tune" size={18} />
                      <span>Configuration</span>
                    </Link>
                  </li>
                </ul>
              )}
            </li>
          )}
          {!isSidebarCollapsed && (
            <li className="nav-group">
              <button
                className="nav-group-header"
                onClick={() => setShowUserMgmtMenu(!showUserMgmtMenu)}
                title="User Management"
              >
                <div className="nav-group-title">
                  <Icon name="manage_accounts" size={20} />
                  <span>User Management</span>
                </div>
                <Icon name={showUserMgmtMenu ? 'expand_less' : 'expand_more'} size={20} />
              </button>
              {showUserMgmtMenu && (
                <ul className="nav-submenu">
                  <li className={isActive('/users') ? 'active' : ''}>
                    <Link to="/users">
                      <Icon name="group" size={18} />
                      <span>Users</span>
                    </Link>
                  </li>
                  <li className={isActive('/roles') ? 'active' : ''}>
                    <Link to="/roles">
                      <Icon name="shield" size={18} />
                      <span>Roles</span>
                    </Link>
                  </li>
                  <li className={isActive('/api-keys') ? 'active' : ''}>
                    <Link to="/api-keys">
                      <Icon name="key" size={18} />
                      <span>Api Keys</span>
                    </Link>
                  </li>
                  <li className={isActive('/activity-logs') ? 'active' : ''}>
                    <Link to="/activity-logs">
                      <Icon name="history" size={18} />
                      <span>Activity Logs</span>
                    </Link>
                  </li>
                </ul>
              )}
            </li>
          )}
          {isSidebarCollapsed && (
            <>
              <li className={isActive('/workers') ? 'active' : ''}>
                <Link to="/workers" title="Workers">
                  <Icon name="engineering" size={20} />
                </Link>
              </li>
              <li className={isExactActive('/admin') ? 'active' : ''}>
                <Link to="/admin" title="Monitoring">
                  <Icon name="monitor_heart" size={20} />
                </Link>
              </li>
              <li className={isActive('/configuration') ? 'active' : ''}>
                <Link to="/configuration" title="Configuration">
                  <Icon name="tune" size={20} />
                </Link>
              </li>
              <li className={isActive('/reports') ? 'active' : ''}>
                <Link to="/reports" title="Reports">
                  <Icon name="analytics" size={20} />
                </Link>
              </li>
            </>
          )}
          {isSidebarCollapsed && (
            <>
              <li className={isActive('/users') ? 'active' : ''}>
                <Link to="/users" title="Users">
                  <Icon name="group" size={20} />
                </Link>
              </li>
              <li className={isActive('/roles') ? 'active' : ''}>
                <Link to="/roles" title="Roles">
                  <Icon name="shield" size={20} />
                </Link>
              </li>
              <li className={isActive('/api-keys') ? 'active' : ''}>
                <Link to="/api-keys" title="Api Keys">
                  <Icon name="key" size={20} />
                </Link>
              </li>
              <li className={isActive('/activity-logs') ? 'active' : ''}>
                <Link to="/activity-logs" title="Activity Logs">
                  <Icon name="history" size={20} />
                </Link>
              </li>
            </>
          )}
        </ul>


        {/* User Menu at Bottom */}
        <div className="sidebar-footer">

          {/* External Links & Theme Toggle */}
          <div className="sidebar-actions">


            {/* Notifications */}
            <button
              className="sidebar-action-btn notification-trigger-btn"
              onClick={() => setIsNotificationOpen(true)}
              title="Notifications"
            >
              <Icon name="notifications" size={20} />
              {unseenCount > 0 && (
                <span className="notification-badge">{unseenCount > 99 ? '99+' : unseenCount}</span>
              )}
            </button>

            {/* Theme Toggle with Circular Reveal Animation */}
            <button
              className="sidebar-action-btn theme-toggle-btn"
              onClick={(e) => toggleTheme(e)}
              title={isDark ? 'Switch to Light Mode' : 'Switch to Dark Mode'}
            >
              <Icon name={isDark ? 'light_mode' : 'dark_mode'} size={20} />
            </button>


            {/* Documentation Link */}
            {!isSidebarCollapsed && (
              <a
                href="https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/milvaion-doc-guide"
                target="_blank"
                rel="noopener noreferrer"
                className="sidebar-action-btn"
                title="Documentation"
              >
                <Icon name="description" size={20} />
              </a>
            )}

            {/* GitHub Link */}
            {!isSidebarCollapsed && (
              <a
                href="https://github.com/Milvasoft"
                target="_blank"
                rel="noopener noreferrer"
                className="sidebar-action-btn"
                title="GitHub"
              >
                <svg width="20" height="20" viewBox="0 0 24 24" fill="currentColor">
                  <path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z"/>
                </svg>
              </a>
            )}
          </div>

          <div className="user-menu-container">
            <button
              className="user-menu-trigger"
              onClick={() => setShowUserMenu(!showUserMenu)}
              title={user?.username || 'User'}
            >
                <Icon name="person" size={20} />
              {!isSidebarCollapsed && (
                <>
                  <div className="user-info">
                    <span className="user-name">{user?.username || 'User'}</span>
                    <span className="user-role">{user?.userType === 1 ? 'Manager' : 'User'}</span>
                  </div>
                  <Icon name={showUserMenu ? 'expand_less' : 'expand_more'} size={20} />
                </>
              )}
            </button>

            {!isSidebarCollapsed && showUserMenu && (
              <div className="user-menu-dropdown">
                <button onClick={() => { setShowUserMenu(false); navigate('/profile') }} className="profile-button">
                  <Icon name="account_circle" size={20} />
                  <span>Profile</span>
                </button>
                <button onClick={handleLogout} className="logout-button">
                  <Icon name="logout" size={20} />
                  <span>Sign Out</span>
                </button>
              </div>
            )}
          </div>
        </div>
      </nav>
      <main className={`main-content ${isSidebarCollapsed ? 'sidebar-collapsed' : ''}`}>
        {children}
      </main>
      <NotificationPanel isOpen={isNotificationOpen} onClose={handleNotificationClose} />
    </div>
  )
}

Layout.propTypes = {
  children: PropTypes.node.isRequired
}

export default Layout

