import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { api, unwrap } from '../../shared/api/client'
import { Button } from '../../shared/ui'
import { ClaimForm } from '../claimant/ClaimForm'
import { asClaimSubmissionForm, type ClaimContact, type SubmissionAccepted } from '../claimant/claimSubmission'
import styles from './NewClaimPage.module.css'

const submitStaffClaim = (body: FormData) => unwrap(api.POST('/api/claims', { body: asClaimSubmissionForm(body) }))

interface Submitted {
  accepted: SubmissionAccepted
  contact: ClaimContact
}

/** Confirmation: the reference to give the customer and a link to the new claim. */
function ClaimSubmitted({ accepted, contact, onAnother }: Submitted & { onAnother: () => void }) {
  const headingRef = useRef<HTMLHeadingElement>(null)

  useEffect(() => headingRef.current?.focus(), [])

  return (
    <div className={styles.success}>
      <span className={styles.symbol} aria-hidden="true">
        ✓
      </span>
      <div className={styles.successBody}>
        <h2 ref={headingRef} tabIndex={-1} className={styles.successTitle}>
          Claim submitted
        </h2>
        <div>
          <p className={styles.referenceLabel}>Claim reference</p>
          <p className={styles.reference}>
            <code>{accepted.reference}</code>
          </p>
        </div>
        <p>
          The claim is being evaluated. Give the customer this reference; they can check the claim with it and{' '}
          {contact.phone ? (
            <>
              <strong>{contact.email}</strong> or <strong>{contact.phone}</strong>
            </>
          ) : (
            <strong>{contact.email}</strong>
          )}
          .
        </p>
        <div className={styles.actions}>
          {accepted.claimId && (
            <Link className={styles.openClaim} to={`/staff/claims/${encodeURIComponent(accepted.claimId)}`}>
              Open claim
            </Link>
          )}
          <Button onClick={onAnother}>Submit another claim</Button>
        </div>
      </div>
    </div>
  )
}

/** `/staff/claims/new` (`claims-agent`): a claim submitted on a customer's behalf (FR-007, ui-design.md §6.6). */
export function NewClaimPage() {
  const [submitted, setSubmitted] = useState<Submitted | null>(null)
  // A new key gives the next claim a fresh, empty form.
  const [formKey, setFormKey] = useState(0)

  return (
    <section className={styles.page}>
      <div className={styles.intro}>
        <h1>New claim</h1>
        <p className={styles.subtitle}>Submitted on behalf of a customer · Agent portal</p>
      </div>
      {submitted ? (
        <ClaimSubmitted
          {...submitted}
          onAnother={() => {
            setSubmitted(null)
            setFormKey((key) => key + 1)
          }}
        />
      ) : (
        <ClaimForm
          key={formKey}
          variant="staff"
          submit={submitStaffClaim}
          onSubmitted={(accepted, contact) => setSubmitted({ accepted, contact })}
        />
      )}
    </section>
  )
}
