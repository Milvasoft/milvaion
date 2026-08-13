import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import api from '../../services/api'
import settingsService from '../../services/settingsService'
import { createOidcManager } from '../../services/oidcClient'

/**
 * Completes the OIDC redirect. Exchanges the authorization code for tokens, stores the access token
 * where the API client expects it, and sends the user on to the app. On failure it returns to the
 * login page.
 */
export default function AuthCallback() {
  const navigate = useNavigate()
  const [error, setError] = useState('')

  useEffect(() => {
    let active = true

    async function complete() {
      try {
        const settings = await settingsService.getPublicSettings()
        const manager = createOidcManager(settings?.oidc)

        if (!manager) {
          navigate('/login', { replace: true })
          return
        }

        const user = await manager.signinRedirectCallback()

        if (user?.access_token) {
          localStorage.setItem('accessToken', user.access_token)

          if (user.refresh_token)
            localStorage.setItem('refreshToken', user.refresh_token)

          const username = user.profile?.preferred_username || user.profile?.name || ''

          // SSO users never hit /account/login, so the browser has no local user id yet. Fetch the account
          // detail with the fresh token (it resolves the current user from the token) so the rest of the app
          // has the same { id, username } a local login would have stored.
          let id = null
          try {
            const detail = await api.get('/account/detail')
            id = detail?.data?.id ?? detail?.id ?? null
          } catch { /* fall back to username only; profile-scoped calls may be limited */ }

          localStorage.setItem('user', JSON.stringify({ id, username }))
        }

        navigate('/dashboard', { replace: true })
      } catch {
        if (!active) return
        setError('Sign-in failed. Returning to the login page.')
        setTimeout(() => navigate('/login', { replace: true }), 2500)
      }
    }

    complete()

    return () => { active = false }
  }, [navigate])

  return (
    <div style={{ minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: '2rem', textAlign: 'center' }}>
      {error || 'Signing you in...'}
    </div>
  )
}
