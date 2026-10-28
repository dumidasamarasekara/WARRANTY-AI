import { execFileSync } from 'node:child_process'
import path from 'node:path'

const run = (file: string, args: string[], env?: NodeJS.ProcessEnv) =>
  execFileSync(file, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'], env: { ...process.env, ...env } }).trim()

/**
 * Counts the claims already filed on the demo serials in the local database, or returns undefined when
 * the database can't be reached this way (no Podman, no running container, no user secret).
 */
export function existingDemoClaims(repoRoot: string, serials: string[]): number | undefined {
  try {
    const container = run('podman', ['ps', '--format', '{{.Names}}'])
      .split(/\r?\n/)
      .find((name) => name.startsWith('postgres-'))
    if (!container) return undefined
    const secrets = run('dotnet', ['user-secrets', 'list', '--project', path.join(repoRoot, 'src', 'Warranty.AppHost', 'Warranty.AppHost.csproj')])
    const password = secrets
      .split(/\r?\n/)
      .find((line) => line.startsWith('Parameters:postgres-password = '))
      ?.slice('Parameters:postgres-password = '.length)
    if (!password) return undefined
    const list = serials.map((serial) => `'${serial.replace(/'/g, "''")}'`).join(',')
    const count = run('podman', [
      'exec', '-e', `PGPASSWORD=${password}`, container,
      'psql', '-U', 'postgres', '-d', 'warranty', '-tAc', `select count(*) from claims.claims where serial_number in (${list})`,
    ])
    return Number.parseInt(count, 10)
  } catch {
    return undefined
  }
}
