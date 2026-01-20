import { useState, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import authService from '../../services/authService'
import Icon from '../../components/Icon'
import './Login.css'

function Login() {
  const navigate = useNavigate()
  const [formData, setFormData] = useState({
    username: '',
    password: ''
  })
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [showPassword, setShowPassword] = useState(false)

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
          {/* Logo/Brand */}
          <div className="login-header">
              <img src="/logo.png" alt="Milvaion Logo" className="login-logo" />
            <h1>Milvaion Scheduler</h1>
            <p>Sign in to your account</p>
          </div>

          {/* Login Form */}
          <form onSubmit={handleSubmit} className="login-form">
            {/* Error Message */}
            {error && (
              <div className="error-message">
                <Icon name="error" size={20} />
                <span>{error}</span>
              </div>
            )}

            {/* Username Field */}
            <div className="form-group">
              <label htmlFor="username">
                <Icon name="person" size={20} />
                Username
              </label>
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

            {/* Password Field */}
            <div className="form-group">
              <label htmlFor="password">
                <Icon name="lock" size={20} />
                Password
              </label>
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
                  <Icon name={showPassword ? 'visibility_off' : 'visibility'} size={20} />
                </button>
              </div>
            </div>

            {/* Submit Button */}
            <button
              type="submit"
              className="login-button"
              disabled={loading}
            >
              {loading ? (
                <>
                  <Icon name="sync" size={20} className="spinning" />
                  Signing in...
                </>
              ) : (
                <>
                  <Icon name="login" size={20} />
                  Sign In
                </>
              )}
            </button>
          </form>

          {/* Footer */}
          <div className="login-footer">
            <p>Powered by Milvasoft</p>
          </div>
        </div>
      </div>
    </div>
  )
}

export default Login
