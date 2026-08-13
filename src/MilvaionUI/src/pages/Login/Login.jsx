import { useState, useCallback, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import authService from '../../services/authService'
import settingsService from '../../services/settingsService'
import { createOidcManager } from '../../services/oidcClient'
import Icon from '../../components/Icon'
import { useBranding } from '../../contexts/BrandingContext'
import './Login.css'

function Login() {
  const navigate = useNavigate()
  const { title: brandingTitle, subtitle: brandingSubtitle } = useBranding()
  const [formData, setFormData] = useState({
    username: '',
    password: ''
  })
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [oidc, setOidc] = useState(null)

  // Redirect authenticated users away from login page
  useEffect(() => {
    if (authService.isAuthenticated()) {
      navigate('/dashboard', { replace: true })
    }
  }, [navigate])

  // Load SSO availability so the login page can offer a redirect sign-in.
  useEffect(() => {
    let active = true
    settingsService.getPublicSettings()
      .then(settings => { if (active) setOidc(settings?.oidc || null) })
      .catch(() => { /* SSO simply stays hidden when this fails */ })
    return () => { active = false }
  }, [])

  const handleSsoLogin = async () => {
    const manager = createOidcManager(oidc)
    if (manager) {
      await manager.signinRedirect()
    }
  }

  const handleChange = useCallback((e) => {
    setFormData(prev => ({
      ...prev,
      [e.target.name]: e.target.value
    }))
    if (error) setError('')
  }, [error])

  const handleSubmit = async (e) => {
    e.preventDefault()
    setError('')

    // Validation
    if (!formData.username.trim()) {
      setError('Please enter your username')
      return
    }

    if (!formData.password) {
      setError('Please enter your password')
      return
    }

    setLoading(true)

    try {
      const result = await authService.login(formData.username, formData.password)

      if (result.success) {
        // Redirect to home page or dashboard
        navigate('/')
      } else {
        setError(result.message || 'Login failed. Please check your credentials.')
      }
    } catch (err) {
      setError('An unexpected error occurred. Please try again.')
      console.error('Login error:', err)
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="login-page">
      <div className="login-container">
        <div className="login-card">

          {/* ── Brand Panel (left) ── */}
          <div className="login-brand-panel">
            <div className="login-brand-content">
              <img src="/logo.png" alt="Milvaion Logo" className="login-logo" />
              <h1 className="login-brand-title">{brandingTitle}</h1>
              <p className="login-brand-subtitle">{brandingSubtitle}</p>
              <div className="login-brand-features">
                <div className="login-brand-feature">
                  <Icon name="schedule" size={14} />
                  <span>Smart job scheduling</span>
                </div>
                <div className="login-brand-feature">
                  <Icon name="account_tree" size={14} />
                  <span>Workflow automation</span>
                </div>
                <div className="login-brand-feature">
                  <Icon name="computer" size={14} />
                  <span>Worker management</span>
                </div>
              </div>
            </div>
          </div>

          {/* ── Form Panel (right) ── */}
          <div className="login-form-panel">
            <div className="login-form-header">
              <h2>Welcome back</h2>
              <p>Sign in to continue</p>
            </div>

            <form onSubmit={handleSubmit} className="login-form">
              {error && (
                <div className="error-message">
                  <Icon name="error" size={16} />
                  <span>{error}</span>
                </div>
              )}

              <div className="form-group">
                <label htmlFor="username">Username</label>
                <input
                  type="text"
                  id="username"
                  name="username"
                  value={formData.username}
                  onChange={handleChange}
                  placeholder="Enter your username"
                  disabled={loading}
                  autoComplete="username"
                  autoFocus
                />
              </div>

              <div className="form-group">
                <label htmlFor="password">Password</label>
                <div className="password-input-wrapper">
                  <input
                    type={showPassword ? 'text' : 'password'}
                    id="password"
                    name="password"
                    value={formData.password}
                    onChange={handleChange}
                    placeholder="Enter your password"
                    disabled={loading}
                    autoComplete="current-password"
                  />
                  <button
                    type="button"
                    className="password-toggle"
                    onClick={() => setShowPassword(!showPassword)}
                    tabIndex={-1}
                  >
                    <Icon name={showPassword ? 'visibility_off' : 'visibility'} size={18} />
                  </button>
                </div>
              </div>

              <button
                type="submit"
                className="login-button"
                disabled={loading}
              >
                {loading ? (
                  <>
                    <Icon name="sync" size={18} className="spinning" />
                    Signing in...
                  </>
                ) : (
                  <>
                    <Icon name="login" size={18} />
                    Sign In
                  </>
                )}
              </button>
            </form>

            {oidc?.enabled && (
              <div className="login-sso">
                <div className="login-divider"><span>or</span></div>
                <button type="button" className="login-sso-button" onClick={handleSsoLogin}>
                  <Icon name="shield" size={18} />
                  Sign in with SSO
                </button>
              </div>
            )}

            <div className="login-footer">
              <p>Powered by Milvasoft</p>
            </div>
          </div>

        </div>
      </div>
    </div>
  )
}

export default Login
