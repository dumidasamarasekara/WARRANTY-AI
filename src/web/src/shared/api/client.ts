import createClient, { type Middleware } from 'openapi-fetch'
import type { Problem } from '../ui'
import { claimantTokenFor } from './claimantToken'
import type { components, paths } from './schema'

export type Schemas = components['schemas']

type StaffTokenProvider = () => string | undefined | Promise<string | undefined>

let staffAccessToken: StaffTokenProvider = () => undefined

/** Registered by the app's OIDC setup; the client stays independent of the auth library. */
export function setStaffAccessTokenProvider(provider: StaffTokenProvider): void {
  staffAccessToken = provider
}

// Claim-scoped calls (contract security `claimantToken`); the other public paths are anonymous.
const claimantPath = /^\/api\/public\/claims\/\{reference\}/
const publicPath = /^\/api\/public\//

const authMiddleware: Middleware = {
  async onRequest({ request, schemaPath, params }) {
    let token: string | undefined
    if (claimantPath.test(schemaPath)) {
      const reference = params.path?.reference
      token = typeof reference === 'string' ? claimantTokenFor(reference) : undefined
    } else if (!publicPath.test(schemaPath)) {
      token = await staffAccessToken()
    }
    if (token) request.headers.set('Authorization', `Bearer ${token}`)
    return request
  },
}

export const correlationIdHeader = 'X-Correlation-Id'

/** A failed API call: RFC 9457 ProblemDetails plus the HTTP status and correlation ID. */
export class ApiProblem extends Error implements Problem {
  readonly status: number
  readonly title?: string
  readonly detail?: string
  readonly correlationId?: string
  /** Field errors of a ValidationProblemDetails (400), keyed by the API's field path. */
  readonly errors: Record<string, string[]>
  /** `Retry-After` in seconds (429). */
  readonly retryAfterSeconds?: number

  constructor(response: Response, body: unknown) {
    const problem = (typeof body === 'object' && body !== null ? body : {}) as Schemas['ValidationProblemDetails']
    super(problem.title ?? `Request failed with status ${response.status}`)
    this.name = 'ApiProblem'
    this.status = problem.status ?? response.status
    this.title = problem.title
    this.detail = problem.detail
    this.correlationId = problem.correlationId ?? response.headers.get(correlationIdHeader) ?? undefined
    this.errors = problem.errors ?? {}
    const retryAfter = Number(response.headers.get('Retry-After'))
    this.retryAfterSeconds = Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : undefined
  }
}

/** Same-origin client: in development Vite proxies `/api` with the original Host header. */
export const api = createClient<paths>({
  baseUrl: globalThis.location?.origin ?? '',
  // Resolved per call so test doubles and MSW installed after import are used.
  fetch: (request) => globalThis.fetch(request),
})
api.use(authMiddleware)

interface FetchResult<T> {
  data?: T
  error?: unknown
  response: Response
}

/**
 * Returns the response body of a successful call and throws an {@link ApiProblem} otherwise, so
 * TanStack Query sees failures as errors carrying the ProblemDetails.
 */
export async function unwrap<T>(call: Promise<FetchResult<T>>): Promise<T> {
  const { data, error, response } = await call
  if (!response.ok) throw new ApiProblem(response, error)
  return data as T
}
