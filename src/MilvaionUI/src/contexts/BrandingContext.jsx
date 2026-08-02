import { createContext, useContext, useEffect, useState, useCallback } from 'react'
import settingsService from '../services/settingsService'

/**
 * App-wide branding (title, subtitle) sourced from the public settings endpoint.
 *
 * Kept deliberately small: the logo and favicon are static files and are not managed here -
 * only the runtime-configurable text. Everything falls back to the shipped defaults, so the UI
 * renders correctly before the fetch resolves or if the request fails.
 */

const DEFAULTS = {
  title: 'Milvaion',
  subtitle: 'Job Scheduler & Workflow Engine',
}

const BrandingContext = createContext({ ...DEFAULTS, refresh: () => {} })

export function BrandingProvider({ children }) {
  const [branding, setBranding] = useState(DEFAULTS)

  const refresh = useCallback(async () => {
    try {
      const data = await settingsService.getPublicSettings()
      setBranding({
        title: data?.title || DEFAULTS.title,
        subtitle: data?.subtitle || DEFAULTS.subtitle,
      })
    } catch {
      // Keep the defaults; branding is non-critical and must never block the app.
    }
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  // Reflect the title in the browser tab.
  useEffect(() => {
    if (branding.title) document.title = branding.title
  }, [branding.title])

  return (
    <BrandingContext.Provider value={{ ...branding, refresh }}>
      {children}
    </BrandingContext.Provider>
  )
}

export function useBranding() {
  return useContext(BrandingContext)
}
