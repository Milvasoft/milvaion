import ReactDOM from 'react-dom/client'
import App from './App.jsx'
import './index.css'
import './styles/page.css'
import './styles/detail.css'
import './styles/table.css'
import { registerSW } from 'virtual:pwa-register'

// A service worker is origin-scoped, so a PWA left registered on localhost:3000 keeps serving its cached
// shell to whatever app later runs on that same port - a blank page until a hard refresh (Ctrl+F5) bypasses
// it. We don't want PWA caching during local development at all, so on localhost we unregister any existing
// worker and drop its caches, and only register the service worker on real hosts.
const isLocalhost = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname)

if (isLocalhost) {
  if ('serviceWorker' in navigator)
    navigator.serviceWorker.getRegistrations().then(regs => regs.forEach(r => r.unregister())).catch(() => {})

  if (typeof caches !== 'undefined')
    caches.keys().then(keys => keys.forEach(k => caches.delete(k))).catch(() => {})
} else {
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
}

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
