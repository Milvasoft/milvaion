import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App.jsx'
import './index.css'

// Register PWA Service Worker
import { registerSW } from 'virtual:pwa-register'

const updateSW = registerSW({
  onNeedRefresh() {
    console.log('🔄 New content available, please refresh.')
    // Auto-update after 5 seconds
    setTimeout(() => {
      updateSW(true)
    }, 5000)
  },
  onOfflineReady() {
    console.log('✅ App ready to work offline')
  },
  onRegistered(registration) {
    console.log('✅ Service Worker registered')
    // Check for updates every hour
    setInterval(() => {
      registration?.update()
    }, 60 * 60 * 1000)
  },
  onRegisterError(error) {
    console.error('❌ Service Worker registration failed:', error)
  }
})

// Global error handler to suppress browser extension errors
window.addEventListener('unhandledrejection', (event) => {
  const errorMessage = event.reason?.message || event.reason?.toString() || ''
  
  // Filter out known browser extension errors
  const isExtensionError = 
    errorMessage.includes('message channel closed') ||
    errorMessage.includes('Extension context invalidated') ||
    errorMessage.includes('extensions::') ||
    errorMessage.includes('chrome-extension://') ||
    event.reason?.stack?.includes('extensions/')
  
  if (isExtensionError) {
    // Suppress browser extension errors (they don't affect functionality)
    console.warn('⚠️ Browser extension error suppressed (safe to ignore)')
    event.preventDefault()
    return
  }
  
  // Log other unhandled rejections
  console.error('Unhandled promise rejection:', event.reason)
})

ReactDOM.createRoot(document.getElementById('root')).render(
  <App />
)
