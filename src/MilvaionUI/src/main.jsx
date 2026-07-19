import ReactDOM from 'react-dom/client'
import App from './App.jsx'
import './index.css'
import './styles/page.css'
import './styles/detail.css'
import { registerSW } from 'virtual:pwa-register'

const updateSW = registerSW({
  onNeedRefresh() {
    console.log('🔄 New content available, please refresh.')
    setTimeout(() => {
      updateSW(true)
    }, 5000)
  },
  onOfflineReady() {
    console.log('✅ App ready to work offline')
  },
  onRegistered(registration) {
    console.log('✅ Service Worker registered')
    setInterval(() => {
      registration?.update()
    }, 60 * 60 * 1000)
  },
  onRegisterError(error) {
    console.error('❌ Service Worker registration failed:', error)
  }
})

window.addEventListener('unhandledrejection', (event) => {
  const errorMessage = event.reason?.message || event.reason?.toString() || ''

  const isExtensionError = 
    errorMessage.includes('message channel closed') ||
    errorMessage.includes('Extension context invalidated') ||
    errorMessage.includes('extensions::') ||
    errorMessage.includes('chrome-extension://') ||
    event.reason?.stack?.includes('extensions/')

  if (isExtensionError) {
    console.warn('⚠️ Browser extension error suppressed (safe to ignore)')
    event.preventDefault()
    return
  }

  console.error('Unhandled promise rejection:', event.reason)
})

ReactDOM.createRoot(document.getElementById('root')).render(
  <App />
)
