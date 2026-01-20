import axios from 'axios'
import authService from './authService'

const api = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || '/api/v1',
  headers: {
    'Content-Type': 'application/json',
    'Accept-Language': 'en-US',
  },
})

// Flag to prevent multiple refresh attempts
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

// Request interceptor for adding auth token
api.interceptors.request.use(
  (config) => {
    // Skip auth for login and refresh endpoints
    if (config.url?.includes('/account/login') || config.url?.includes('/account/refresh-login')) {
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

// Response interceptor for handling 401 and automatic token refresh
api.interceptors.response.use(
  (response) => response.data, // Extract data for convenience
  async (error) => {
    const originalRequest = error.config

    // If error is not 401/419 or request already retried, reject immediately
    if ((error.response?.status !== 401 && error.response?.status !== 419) || originalRequest._retry) {
      return Promise.reject(error)
    }

    // Skip refresh for login/refresh endpoints themselves
    if (originalRequest.url?.includes('/account/login') || originalRequest.url?.includes('/account/refresh-login')) {
      return Promise.reject(error)
    }

    // If already refreshing, queue this request
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

    originalRequest._retry = true
    isRefreshing = true

    try {
      const refreshed = await authService.refreshToken()

      if (refreshed) {
        const newToken = authService.getAccessToken()

        // Update original request with new token
        originalRequest.headers.Authorization = `Bearer ${newToken}`

        // Process queued requests
        processQueue(null, newToken)

        // Retry original request
        return api(originalRequest)
      } else {
        // Refresh failed - logout user
        processQueue(new Error('Token refresh failed'), null)
        authService.logout()

        // Redirect to login page
        if (window.location.pathname !== '/login') {
          window.location.href = '/login'
        }

        return Promise.reject(error)
      }
    } catch (refreshError) {
      processQueue(refreshError, null)
      authService.logout()

      if (window.location.pathname !== '/login') {
        window.location.href = '/login'
      }

      return Promise.reject(refreshError)
    } finally {
      isRefreshing = false
    }
  }
)

export default api
