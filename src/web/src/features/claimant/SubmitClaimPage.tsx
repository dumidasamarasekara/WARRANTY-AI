import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { api, unwrap } from '../../shared/api/client'
import { Button, Stepper } from '../../shared/ui'
import { ClaimForm } from './ClaimForm'
import { asClaimSubmissionForm, claimantFormSteps, type ClaimContact, type SubmissionAccepted } from './claimSubmission'
import styles from './SubmitClaimPage.module.css'

const submitPublicClaim = (body: FormData) =>
  unwrap(api.POST('/api/public/claims', { body: asClaimSubmissionForm(body) }))

interface Submitted {
  accepted: SubmissionAccepted
  contact: ClaimContact
}

/** Success card: the reference and the contact the claimant uses to check the claim (FR-037a). */
function ClaimSubmitted({ accepted: { reference }, contact }: Submitted) {
  const navigate = useNavigate()
  const headingRef = useRef<HTMLHeadingElement>(null)

  useEffect(() => headingRef.current?.focus(), [])

  return (
    <section className={styles.page}>
      <Stepper label="Claim progress" steps={claimantFormSteps('submitted')} />
      <div className={styles.success}>
        <span className={styles.symbol} aria-hidden="true">
          ✓
        </span>
        <div className={styles.successBody}>
          <h1 ref={headingRef} tabIndex={-1} className={styles.successTitle}>
            Claim submitted
          </h1>
          <div>
            <p className={styles.referenceLabel}>Your claim reference</p>
            <p className={styles.reference}>
              <code>{reference}</code>
            </p>
          </div>
          <p>Use this reference and the email address or phone number you gave to check your claim:</p>
          <ul className={styles.contacts}>
            <li>{contact.email}</li>
            {contact.phone && <li>{contact.phone}</li>}
          </ul>
          <p>We're checking your claim against your warranty. You can see its progress at any time.</p>
          <div>
            <Button
              variant="primary"
              size="lg"
              className={styles.checkStatus}
              onClick={() => navigate(`/claims/access?reference=${encodeURIComponent(reference)}`)}
            >
              Check status
            </Button>
          </div>
        </div>
      </div>
    </section>
  )
}

/** `/` on a tenant channel host: a claimant submits a claim without an account (ui-design.md §6.6). */
export function SubmitClaimPage() {
  const [submitted, setSubmitted] = useState<Submitted | null>(null)

  if (submitted) return <ClaimSubmitted {...submitted} />

  return (
    <section className={styles.page}>
      <div className={styles.intro}>
        <h1>Submit a warranty claim</h1>
        <p className={styles.lead}>Tell us about your product and what went wrong. You don't need an account.</p>
      </div>
      <ClaimForm
        variant="claimant"
        submit={submitPublicClaim}
        onSubmitted={(accepted, contact) => setSubmitted({ accepted, contact })}
      />
    </section>
  )
}
