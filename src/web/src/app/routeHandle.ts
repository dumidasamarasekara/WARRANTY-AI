import type { Params } from 'react-router'

/** Route `handle` read by the staff header to build its breadcrumbs (tenant / section / item). */
export interface RouteHandle {
  crumb?: string | ((params: Params) => string)
}

export function crumbLabel(handle: unknown, params: Params): string | undefined {
  const crumb = (handle as RouteHandle | undefined)?.crumb
  return typeof crumb === 'function' ? crumb(params) : crumb
}
