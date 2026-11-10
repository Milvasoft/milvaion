import * as signalR from '@microsoft/signalr'
import authService from './authService'

class SignalRService {
  constructor() {
    this.connection = null
    this.connectPromise = null
    this.retryTimer = null
    this.reconnectAttempts = 0
    this.maxReconnectAttempts = 5
    this.listeners = new Map()
    this.subscribedOccurrences = new Set()
  }

  connect() {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      return Promise.resolve()
    }

    // Concurrent callers share the one attempt in flight.
    if (!this.connectPromise) {
      this.connectPromise = this._startConnection().finally(() => {
        this.connectPromise = null
      })
    }

    return this.connectPromise
  }

  async _startConnection() {
    try {
      const apiUrl = import.meta.env.VITE_API_URL || window.location.origin
      const basePath = (window.__MILVAION_CONFIG__?.basePath ?? import.meta.env.VITE_BASE_PATH ?? '').replace(/\/$/, '')
      const hubUrl = `${apiUrl}${basePath}/hubs/jobs`

      this.connection = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl, {
          skipNegotiation: false,
          accessTokenFactory: () => authService.getAccessToken(),
          transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.ServerSentEvents,
        })
        .withAutomaticReconnect({
          nextRetryDelayInMilliseconds: retryContext => {
            if (retryContext.elapsedMilliseconds < 60000) {
              return Math.random() * 10000
            } else {
              return null
            }
          }
        })
        .configureLogging(signalR.LogLevel.Warning)
        .build()

      this.connection.onclose((error) => {
        if (error) {
          console.warn('SignalR connection closed:', error.message)
        }
      })

      this.connection.onreconnecting((error) => {
        console.warn('SignalR reconnecting...', error?.message || '')
      })

      this.connection.onreconnected((connectionId) => {
        console.log('✅ SignalR reconnected', connectionId || '')
        this.reconnectAttempts = 0
        this._resubscribeAll()
      })

      // SignalR event listeners
      this.connection.on('OccurrenceCreated', (data) => {
        this.notifyListeners('OccurrenceCreated', data)
      })
      this.connection.on('OccurrenceUpdated', (data) => {
        this.notifyListeners('OccurrenceUpdated', data)
      })
      this.connection.on('OccurrenceLogAdded', (data) => {
        this.notifyListeners('OccurrenceLogAdded', data)
      })

      // Add "Connected" event handler to suppress warning
      this.connection.on('Connected', (data) => {
        console.log('✅ SignalR server confirmed connection:', data)
      })

      await this.connection.start()
      console.log('✅ SignalR connected')
      this.reconnectAttempts = 0
    } catch (error) {
      const errorMessage = error?.message || error?.toString() || ''
      const isExtensionError = errorMessage.includes('message channel closed') ||
                               errorMessage.includes('Extension context invalidated')

      if (isExtensionError) {
        console.warn('⚠️ Browser extension interference (safe to ignore)')
      } else {
        console.error('❌ SignalR connection failed:', errorMessage)
      }

      this.reconnectAttempts++

      if (this.reconnectAttempts < this.maxReconnectAttempts && !isExtensionError) {
        console.log(`🔄 Retrying connection (${this.reconnectAttempts}/${this.maxReconnectAttempts})...`)
        this.retryTimer = setTimeout(() => this.connect(), 5000 * this.reconnectAttempts)
      }
    }
  }

  async _resubscribeAll() {
    if (!this.isConnected()) return

    for (const occId of this.subscribedOccurrences) {
      try {
        await this.connection.invoke('SubscribeToOccurrence', occId)
      } catch (err) {
        console.error('Failed to resubscribe to occurrence:', occId)
      }
    }
  }

  async disconnect() {
    clearTimeout(this.retryTimer)
    this.reconnectAttempts = 0

    if (this.connection) {
      this.subscribedOccurrences.clear()
      this.listeners.clear()

      await this.connection.stop()
      this.connection = null
    }
  }

  async subscribeToOccurrence(occurrenceId) {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      try {
        await this.connection.invoke('SubscribeToOccurrence', occurrenceId)
        this.subscribedOccurrences.add(occurrenceId)
      } catch (err) {
        console.error('Failed to subscribe to occurrence:', occurrenceId)
      }
    }
  }

  async unsubscribeFromOccurrence(occurrenceId) {
    this.subscribedOccurrences.delete(occurrenceId)
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      try {
        await this.connection.invoke('UnsubscribeFromOccurrence', occurrenceId)
      } catch (err) {
        console.error('Failed to unsubscribe from occurrence:', occurrenceId)
      }
    }
  }

  async invoke(methodName, ...args) {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      try {
        return await this.connection.invoke(methodName, ...args)
      } catch (error) {
        console.error(`Failed to invoke ${methodName}:`, error.message)
        throw error
      }
    } else {
      console.warn(`SignalR not connected, cannot invoke ${methodName}`)
      return null
    }
  }

  on(eventName, callback) {
    if (!this.listeners.has(eventName)) {
      this.listeners.set(eventName, [])
    }
    this.listeners.get(eventName).push(callback)

    return () => {
      const callbacks = this.listeners.get(eventName)
      if (callbacks) {
        const index = callbacks.indexOf(callback)
        if (index > -1) {
          callbacks.splice(index, 1)
        }
      }
    }
  }

  notifyListeners(eventName, data) {
    const callbacks = this.listeners.get(eventName)
    if (callbacks && callbacks.length > 0) {
      callbacks.forEach(callback => {
        try {
          callback(data)
        } catch (error) {
          console.error(`Error in ${eventName} listener:`, error)
        }
      })
    }
  }

  isConnected() {
    return this.connection?.state === signalR.HubConnectionState.Connected
  }

  getState() {
    return this.connection?.state || 'Disconnected'
  }
}

export default new SignalRService()
