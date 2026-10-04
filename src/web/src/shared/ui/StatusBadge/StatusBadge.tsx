import {
  claimStatusPresentation,
  decidedByPresentation,
  type ClaimStatus,
  type FinalDecidedBy,
} from '../../presentation/claimStatus'
import { Badge } from '../Badge'

export function StatusBadge({ status }: { status: ClaimStatus }) {
  const { label, tone } = claimStatusPresentation(status)
  return (
    <Badge tone={tone} dot>
      {label}
    </Badge>
  )
}

/** Who finalized the claim, shown as its own badge next to the status (ui-design.md §4.1). */
export function DecidedByBadge({ decidedBy }: { decidedBy: FinalDecidedBy }) {
  const { label, tone } = decidedByPresentation(decidedBy)
  return <Badge tone={tone}>{label}</Badge>
}
