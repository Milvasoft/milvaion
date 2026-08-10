import api from './api'

const TOKEN_KEY = 'accessToken'
const REFRESH_TOKEN_KEY = 'refreshToken'
const USER_KEY = 'user'
const DEVICE_ID_KEY = 'deviceId'

class AuthService {
  constructor() {
    this.deviceId = this.getOrCreateDeviceId()
  }

  getOrCreateDeviceId() {
    let deviceId = localStorage.getItem(DEVICE_ID_KEY)
    if (!deviceId) {
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
        const { token, id } = response.data

          this.setTokens(token.accessToken, token.refreshToken)

          const user = {
            id,
            username
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

        this.setTokens(token.accessToken, token.refreshToken)

        return true
      }

      return false
    } catch (error) {
      console.error('Token refresh error:', error)
      return false
    }
  }

  clearAuth() {
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(REFRESH_TOKEN_KEY)
    localStorage.removeItem(USER_KEY)
  }

  async logout() {
    try {
      const user = this.getCurrentUser()
      if (user) {
        await api.post('/account/logout', {
          userName: user.username,
          deviceId: this.deviceId
        })
      }
    } catch (error) {
      console.error('Logout API error:', error)
    } finally {
      this.clearAuth()
    }
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

  isTokenExpired(token) {
    if (!token) return true

    try {
      const payload = JSON.parse(atob(token.split('.')[1]))
      const exp = payload.exp * 1000
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

      return timeUntilExpiry < 5 * 60 * 1000
    } catch {
      return false
    }
  }
}

export default new AuthService()
