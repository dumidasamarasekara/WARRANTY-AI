import { describe, expect, it } from 'vitest'
import { isTenantChannelHost } from '../../src/app/channel'
import { rolesFromAccessToken } from '../../src/app/roles'

function token(payload: object): string {
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_')
  return `${encode({ alg: 'RS256' })}.${encode(payload)}.signature`
}

describe('isTenantChannelHost', () => {
  it('treats the staff hosts as the staff area', () => {
    expect(isTenantChannelHost('localhost', ['localhost', '127.0.0.1'])).toBe(false)
    expect(isTenantChannelHost('LOCALHOST', ['localhost'])).toBe(false)
  })

  it('treats any other host as a claimant channel', () => {
    expect(isTenantChannelHost('aurora.localhost', ['localhost'])).toBe(true)
    expect(isTenantChannelHost('borealis.localhost', ['localhost'])).toBe(true)
  })
})

describe('rolesFromAccessToken', () => {
  it('reads the staff realm roles and ignores the others', () => {
    const accessToken = token({ realm_access: { roles: ['offline_access', 'claims-reviewer', 'claims-agent'] } })
    expect(rolesFromAccessToken(accessToken)).toEqual(['claims-agent', 'claims-reviewer'])
  })

  it('returns no roles for a missing or malformed token', () => {
    expect(rolesFromAccessToken(undefined)).toEqual([])
    expect(rolesFromAccessToken('not-a-jwt')).toEqual([])
    expect(rolesFromAccessToken(token({ realm_access: { roles: 'auditor' } }))).toEqual([])
  })
})
