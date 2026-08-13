import { UserManager, WebStorageStateStore } from 'oidc-client-ts'

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
    scope: 'openid profile email',
    userStore: new WebStorageStateStore({ store: window.localStorage }),
    automaticSilentRenew: true,
  })

  return manager
}

export function getOidcManager() {
  return manager
}
