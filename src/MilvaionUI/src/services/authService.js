import api from './api'

const TOKEN_KEY = 'accessToken'
const REFRESH_TOKEN_KEY = 'refreshToken'
const USER_KEY = 'user'
const DEVICE_ID_KEY = 'deviceId'

class AuthService {
  constructor() {
    // Generate or retrieve device ID
    this.deviceId = this.getOrCreateDeviceId()
  }

  getOrCreateDeviceId() {
    let deviceId = localStorage.getItem(DEVICE_ID_KEY)
    if (!deviceId) {
      // Generate unique device ID
      deviceId = `web-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`
      localStorage.setItem(DEVICE_ID_KEY, deviceId)
    }
    return deviceId
  }

  async login(username, password) {
    try {
      const response = await api.post('/account/login', {
        userName: username,
        password: password,
        deviceId: this.deviceId
      })

      if (response.isSuccess && response.data) {
        const { token, id, userType } = response.data

        // Store tokens
        this.setTokens(token.accessToken, token.refreshToken)

        // Store user info
        const user = {
          id,
          username,
          userType
        }
        localStorage.setItem(USER_KEY, JSON.stringify(user))

        return {
          success: true,
          user
        }
      }

      return {
        success: false,
        message: response.messages?.[0]?.message || 'Login failed'
      }
    } catch (error) {
      console.error('Login error:', error)
      return {
        success: false,
        message: error.response?.data?.messages?.[0]?.message || 'Network error during login'
      }
    }
  }

  async refreshToken() {
    try {
      const refreshToken = this.getRefreshToken()
      const user = this.getCurrentUser()

      if (!refreshToken || !user) {
        return false
      }

      // Fix: Correct endpoint path to match backend route
      const response = await api.post('/account/login/refresh', {
        userName: user.username,
        refreshToken: refreshToken,
        deviceId: this.deviceId
      })

      if (response.isSuccess && response.data) {
        const { token } = response.data

        // Update tokens
        this.setTokens(token.accessToken, token.refreshToken)

        return true
      }

      return false
    } catch (error) {
      console.error('Token refresh error:', error)
      return false
    }
  }

  logout() {
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(REFRESH_TOKEN_KEY)
    localStorage.removeItem(USER_KEY)
    // Keep device ID for future logins
  }

  setTokens(accessToken, refreshToken) {
    localStorage.setItem(TOKEN_KEY, accessToken)
    localStorage.setItem(REFRESH_TOKEN_KEY, refreshToken)
  }

  getAccessToken() {
    return localStorage.getItem(TOKEN_KEY)
  }

  getRefreshToken() {
    return localStorage.getItem(REFRESH_TOKEN_KEY)
  }

  getCurrentUser() {
    const userStr = localStorage.getItem(USER_KEY)
    if (userStr) {
      try {
        return JSON.parse(userStr)
      } catch {
        return null
      }
    }
    return null
  }

  isAuthenticated() {
    return !!this.getAccessToken()
  }

  // Check if token is expired (JWT parsing)
  isTokenExpired(token) {
    if (!token) return true

    try {
      const payload = JSON.parse(atob(token.split('.')[1]))
      const exp = payload.exp * 1000 // Convert to milliseconds
      return Date.now() >= exp
    } catch {
      return true
    }
  }

  shouldRefreshToken() {
    const token = this.getAccessToken()
    if (!token) return false

    try {
      const payload = JSON.parse(atob(token.split('.')[1]))
      const exp = payload.exp * 1000
      const now = Date.now()
      const timeUntilExpiry = exp - now

      // Refresh if less than 5 minutes until expiry
      return timeUntilExpiry < 5 * 60 * 1000
    } catch {
      return false
    }
  }
}

export default new AuthService()
