import { UserManager, WebStorageStateStore } from 'oidc-client-ts'

const SESSION_KEY = 'oidcSession'
const REFRESH_LOCK = 'milvaion-oidc-refresh'

/**
 * Builds the redirect URI for the OIDC flow, honouring the app's base path so it matches whatever
 * is registered in the identity provider.
 */
function redirectPath(suffix) {
  const base = import.meta.env.BASE_URL || '/'
  const prefix = base === '/' ? '' : base.replace(/\/$/, '')
  return `${window.location.origin}${prefix}${suffix}`
}

let manager = null

/**
 * Lazily creates the OIDC UserManager from the public config returned by the API. Returns null when
 * SSO is not configured, so callers can fall back to the local form.
 *
 * @param {{ enabled?: boolean, authority?: string, clientId?: string }} oidc
 */
export function createOidcManager(oidc) {
  if (!oidc?.enabled || !oidc.authority || !oidc.clientId) return null
  if (manager) return manager

  manager = new UserManager({
    authority: oidc.authority,
    client_id: oidc.clientId,
    redirect_uri: redirectPath('/auth/callback'),
    post_logout_redirect_uri: redirectPath('/login'),
    response_type: 'code',
    scope: 'openid profile email offline_access',
    userStore: new WebStorageStateStore({ store: window.localStorage }),
    // Renewal runs only from refreshOidcSession. Providers that rotate refresh tokens revoke the whole
    // chain when one is used twice, so a second, timer-driven path would end the session.
    automaticSilentRenew: false,
  })

  return manager
}

function readSession() {
  try {
    return JSON.parse(localStorage.getItem(SESSION_KEY))
  } catch {
    return null
  }
}

/** Returns the manager, rebuilding it from the stored session after a page reload. */
export function getOidcManager() {
  return manager ?? createOidcManager(readSession())
}

/** Marks the current browser session as signed in through the provider. */
export function rememberOidcSession(oidc) {
  localStorage.setItem(SESSION_KEY, JSON.stringify({ enabled: true, authority: oidc.authority, clientId: oidc.clientId }))
}

export function isOidcSession() {
  return readSession() !== null
}

export function clearOidcSession() {
  const current = getOidcManager()

  localStorage.removeItem(SESSION_KEY)
  current?.removeUser().catch(() => {})
}

/**
 * Exchanges the stored refresh token for new tokens and returns the updated user, or null when the
 * session cannot be renewed. The Web Lock keeps open tabs from spending the same refresh token: a tab
 * that waited for the lock finds the tokens another tab already stored and uses those.
 *
 * @param {string} staleAccessToken the access token the caller holds
 */
export async function refreshOidcSession(staleAccessToken) {
  const current = getOidcManager()

  if (!current) return null

  const renew = async () => {
    const stored = await current.getUser()

    if (stored && !stored.expired && stored.access_token !== staleAccessToken) return stored
    if (!stored?.refresh_token) return null

    return current.signinSilent()
  }

  return navigator.locks ? navigator.locks.request(REFRESH_LOCK, renew) : renew()
}

/** Best effort: sign-out still completes locally when the provider cannot be reached. */
export async function revokeOidcRefreshToken() {
  await getOidcManager()?.revokeTokens(['refresh_token']).catch(() => {})
}
