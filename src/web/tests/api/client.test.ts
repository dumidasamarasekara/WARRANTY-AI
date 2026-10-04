import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearClaimantToken, holdClaimantToken } from '../../src/shared/api/claimantToken'
import { ApiProblem, api, setStaffAccessTokenProvider, unwrap } from '../../src/shared/api/client'

let requests: Request[] = []
let nextResponse: () => Response = () => Response.json({})

beforeEach(() => {
  requests = []
  nextResponse = () => Response.json({})
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      requests.push(request)
      return nextResponse()
    }),
  )
  setStaffAccessTokenProvider(() => 'staff-token')
})

afterEach(() => {
  vi.unstubAllGlobals()
  clearClaimantToken()
  setStaffAccessTokenProvider(() => undefined)
})

const inOneHour = () => new Date(Date.now() + 3_600_000).toISOString()
const authorization = () => requests[0]?.headers.get('Authorization')

describe('api client authentication', () => {
  it('sends the staff OIDC token on staff calls', async () => {
    await api.GET('/api/me')
    expect(authorization()).toBe('Bearer staff-token')
  })

  it('sends the held claimant token, not the staff token, on claim-scoped calls', async () => {
    holdClaimantToken('ABCD234567', 'claimant-token', inOneHour())
    await api.GET('/api/public/claims/{reference}', { params: { path: { reference: 'abcd234567' } } })
    expect(authorization()).toBe('Bearer claimant-token')
  })

  it('sends no token for a claim other than the one the token was issued for', async () => {
    holdClaimantToken('ABCD234567', 'claimant-token', inOneHour())
    await api.GET('/api/public/claims/{reference}', { params: { path: { reference: 'ZZZZ234567' } } })
    expect(authorization()).toBeNull()
  })

  it('drops an expired claimant token', async () => {
    holdClaimantToken('ABCD234567', 'claimant-token', new Date(Date.now() - 1000).toISOString())
    await api.GET('/api/public/claims/{reference}', { params: { path: { reference: 'ABCD234567' } } })
    expect(authorization()).toBeNull()
  })

  it('sends no token on anonymous public calls', async () => {
    holdClaimantToken('ABCD234567', 'claimant-token', inOneHour())
    await api.GET('/api/public/tenant')
    expect(authorization()).toBeNull()
    await api.POST('/api/public/claims/access', { body: { reference: 'ABCD234567', contact: 'someone@example.test' } })
    expect(requests[1]?.headers.get('Authorization')).toBeNull()
  })
})

describe('unwrap', () => {
  it('returns the body of a successful call', async () => {
    nextResponse = () => Response.json({ displayName: 'Aurora Electronics' })
    await expect(unwrap(api.GET('/api/public/tenant'))).resolves.toEqual({ displayName: 'Aurora Electronics' })
  })

  it('throws the ProblemDetails of a failed call with its field errors', async () => {
    nextResponse = () =>
      Response.json(
        {
          title: 'One or more validation errors occurred.',
          status: 400,
          correlationId: '4bf92f3577b34da6a3ce929d0e0e4736',
          errors: { 'claim.description': ['Describe the problem in at least 20 characters.'] },
        },
        { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
      )
    const problem = await unwrap(api.GET('/api/me')).catch((error: unknown) => error)
    expect(problem).toBeInstanceOf(ApiProblem)
    expect(problem).toMatchObject({
      status: 400,
      title: 'One or more validation errors occurred.',
      correlationId: '4bf92f3577b34da6a3ce929d0e0e4736',
      errors: { 'claim.description': ['Describe the problem in at least 20 characters.'] },
    })
  })

  it('takes the correlation ID from the header and reads Retry-After when the body has none', async () => {
    nextResponse = () => new Response(null, { status: 429, headers: { 'X-Correlation-Id': 'abc123', 'Retry-After': '600' } })
    const problem = (await unwrap(api.GET('/api/me')).catch((error: unknown) => error)) as ApiProblem
    expect(problem.status).toBe(429)
    expect(problem.correlationId).toBe('abc123')
    expect(problem.retryAfterSeconds).toBe(600)
  })
})
