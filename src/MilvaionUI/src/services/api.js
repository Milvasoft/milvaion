import axios from 'axios'
import authService from './authService'

const basePath = (window.__MILVAION_CONFIG__?.basePath ?? import.meta.env.VITE_BASE_PATH ?? '').replace(/\/$/, '')
const loginPath = `${basePath}/login`

const api = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || `${basePath}/api/v1`,
  headers: {
    'Content-Type': 'application/json',
    'Accept-Language': 'en-US',
  },
})

let isRefreshing = false
let failedQueue = []

const processQueue = (error, token = null) => {
  failedQueue.forEach(prom => {
    if (error) {
      prom.reject(error)
    } else {
      prom.resolve(token)
    }
  })

  failedQueue = []
}

api.interceptors.request.use(
  (config) => {
    if (config.url?.endsWith('/account/login')) {
      return config
    }

    const token = authService.getAccessToken()
    if (token) {
      config.headers.Authorization = `Bearer ${token}`
    }

    return config
  },
  (error) => {
    return Promise.reject(error)
  }
)

api.interceptors.response.use(
  (response) => {
    const data = response.data

    if (data && typeof data.isSuccess === 'boolean' && data.isSuccess === false) {
      const errorMessages = data.messages?.map(m => m.message).join('\n') || 'Request failed'
      const error = new Error(errorMessages)
      error.response = response
      error.isValidationError = true
      error.validationMessages = data.messages
      return Promise.reject(error)
    }

    return data
  },
  async (error) => {
    const originalRequest = error.config

    const status = error.response?.status

    // The local scheme reports an expired token as 419, but a provider-issued token that expired answers
    // 401, so an SSO session gets one refresh attempt on it before being signed out.
    const mayBeExpiredOidcToken = status === 401 && authService.isOidcSession() && !originalRequest._retry

    if (status === 401 && !mayBeExpiredOidcToken) {
      authService.clearAuth()
      if (window.location.pathname !== loginPath) {
        window.location.href = loginPath
      }
      return Promise.reject(error)
    }

    if ((status !== 419 && !mayBeExpiredOidcToken) || originalRequest._retry) {
      return Promise.reject(error)
    }

    if (originalRequest.url === '/account/login' || originalRequest.url === '/account/login/refresh') {
      authService.clearAuth()
      if (window.location.pathname !== loginPath) {
        window.location.href = loginPath
      }
      return Promise.reject(error)
    }

    originalRequest._retry = true

    if (isRefreshing) {
      return new Promise((resolve, reject) => {
        failedQueue.push({ resolve, reject })
      })
        .then(token => {
          originalRequest.headers.Authorization = `Bearer ${token}`
          return api(originalRequest)
        })
        .catch(err => {
          return Promise.reject(err)
        })
    }

    isRefreshing = true

    try {
      const refreshed = await authService.refreshToken()

      if (refreshed) {
        const newToken = authService.getAccessToken()

        originalRequest.headers.Authorization = `Bearer ${newToken}`

        processQueue(null, newToken)

        return api(originalRequest)
      } else {
        processQueue(new Error('Token refresh failed'), null)
          authService.clearAuth()

          if (window.location.pathname !== loginPath) {
            window.location.href = loginPath
          }

          return Promise.reject(error)
      }
    } catch (refreshError) {
      processQueue(refreshError, null)
      authService.clearAuth()

      if (window.location.pathname !== loginPath) {
        window.location.href = loginPath
      }

      return Promise.reject(refreshError)
    } finally {
      isRefreshing = false
    }
  }
)

export default api
