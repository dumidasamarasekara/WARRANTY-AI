// Variables the AppHost passes to the Vite dev server (T039); every one has a local fallback.
interface ImportMetaEnv {
  readonly VITE_KEYCLOAK_AUTHORITY?: string
  readonly VITE_KEYCLOAK_CLIENT_ID?: string
  /** Comma-separated hosts that serve only the staff area; any other host is a claimant channel. */
  readonly VITE_STAFF_HOSTS?: string
}
