import { UserManager } from 'oidc-client-ts'

const authority = import.meta.env.VITE_KEYCLOAK_AUTHORITY ?? 'http://localhost:8080/realms/warranty'
const clientId = import.meta.env.VITE_KEYCLOAK_CLIENT_ID ?? 'warranty-web'

/** Where the staff area sends Keycloak back to; the callback then restores the requested page. */
export const staffHome = '/staff'

/**
 * Staff sign-in through Keycloak's hosted login: Authorization Code + PKCE (oidc-client-ts uses S256
 * by default), tokens kept in session storage for the tab. One instance shared by the React
 * provider and the API client.
 */
export const userManager = new UserManager({
  authority,
  client_id: clientId,
  redirect_uri: `${window.location.origin}${staffHome}`,
  post_logout_redirect_uri: `${window.location.origin}${staffHome}`,
  scope: 'openid profile email',
})

/** The current staff access token for API calls, or `undefined` when signed out or expired. */
export async function staffAccessToken(): Promise<string | undefined> {
  const user = await userManager.getUser()
  return user && !user.expired ? user.access_token : undefined
}
