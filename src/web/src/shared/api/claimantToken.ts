interface HeldToken {
  reference: string
  accessToken: string
  expiresAt: number
}

// Memory only — never web storage: the claim-scoped token (research R10) ends with the page.
let held: HeldToken | null = null

const normalize = (reference: string) => reference.trim().toUpperCase()

/** Keeps the token from `POST /api/public/claims/access` for the claim it was issued for. */
export function holdClaimantToken(reference: string, accessToken: string, expiresAt: string): void {
  held = { reference: normalize(reference), accessToken, expiresAt: Date.parse(expiresAt) }
}

/** The held token for `reference`, or `undefined` when there is none or it has expired. */
export function claimantTokenFor(reference: string): string | undefined {
  if (!held || held.reference !== normalize(reference)) return undefined
  if (!(held.expiresAt > Date.now())) {
    held = null
    return undefined
  }
  return held.accessToken
}

export function clearClaimantToken(): void {
  held = null
}
