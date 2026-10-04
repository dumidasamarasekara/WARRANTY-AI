import type { ComponentType, ReactNode } from 'react'

export interface LinkLikeProps {
  to: string
  className?: string
  'aria-current'?: 'page' | undefined
  children: ReactNode
}

/**
 * Navigation components take the router's link through this type so the kit stays router-agnostic
 * (pass react-router's `Link`); without one they render a plain anchor.
 */
export type LinkComponent = ComponentType<LinkLikeProps>

export function AnchorLink({ to, ...rest }: LinkLikeProps) {
  return <a href={to} {...rest} />
}
