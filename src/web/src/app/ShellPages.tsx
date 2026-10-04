import { Link, Navigate } from 'react-router'
import { EmptyState } from '../shared/ui'
import { useStaffSession } from './useStaffSession'

/** `/staff`: reviewers land on their queue, everyone else on the claims list (ui-design.md §6.1). */
export function StaffLanding() {
  const session = useStaffSession()
  if (!session) return null
  return <Navigate to={session.roles.includes('claims-reviewer') ? '/staff/review' : '/staff/claims'} replace />
}

/** Holds a route until the page task that builds it lands. */
export function PlaceholderPage({ title }: { title: string }) {
  return (
    <section>
      <h1>{title}</h1>
      <EmptyState message="This page is not available yet." />
    </section>
  )
}

export function NotFoundPage({ home }: { home: string }) {
  return (
    <EmptyState
      message="We couldn't find that page."
      action={<Link to={home}>Go to the start page</Link>}
    />
  )
}
