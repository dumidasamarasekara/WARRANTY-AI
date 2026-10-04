import { useMutation } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router'
import { holdClaimantToken } from '../../shared/api/claimantToken'
import { ApiProblem, api, unwrap } from '../../shared/api/client'
import { Alert, Button, Card, ProblemState, TextInput } from '../../shared/ui'
import styles from './AccessClaimPage.module.css'

interface AccessInput {
  reference: string
  contact: string
}

const normalizeReference = (reference: string) => reference.trim().toUpperCase()

function tooManyAttemptsMessage(problem: ApiProblem): string {
  if (!problem.retryAfterSeconds) return 'Too many attempts — try again later'
  const minutes = Math.max(1, Math.ceil(problem.retryAfterSeconds / 60))
  return `Too many attempts — try again in ${minutes} ${minutes === 1 ? 'minute' : 'minutes'}`
}

/** A failed access attempt; never says whether the claim exists (ui-design.md §6.6). */
function AccessError({ error }: { error: unknown }) {
  const problem = error instanceof ApiProblem ? error : undefined
  if (problem?.status === 401) {
    return (
      <Alert tone="err" urgent title="We couldn't find a claim with those details">
        Check the reference and use the email address or phone number you gave with your claim.
      </Alert>
    )
  }
  if (problem?.status === 429) {
    return <Alert tone="warn" urgent title={tooManyAttemptsMessage(problem)} />
  }
  return <ProblemState problem={problem ?? { title: "We couldn't check your claim right now. Please try again." }} />
}

/**
 * `/claims/access`: the claim reference and the contact given at submission are exchanged for a
 * claim-scoped token, held in memory only (research R10), then the status page opens.
 */
export function AccessClaimPage() {
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const [input, setInput] = useState<AccessInput>({ reference: searchParams.get('reference') ?? '', contact: '' })
  const [missing, setMissing] = useState<Partial<Record<keyof AccessInput, string>>>({})

  const access = useMutation({
    mutationFn: ({ reference, contact }: AccessInput) =>
      unwrap(api.POST('/api/public/claims/access', { body: { reference, contact } })),
    onSuccess: (token, { reference }) => {
      holdClaimantToken(reference, token.accessToken, token.expiresAt)
      navigate(`/claims/${encodeURIComponent(reference)}`)
    },
  })

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const reference = normalizeReference(input.reference)
    const contact = input.contact.trim()
    const errors: typeof missing = {}
    if (!reference) errors.reference = 'Enter your claim reference'
    if (!contact) errors.contact = 'Enter the email address or phone number you gave with your claim'
    setMissing(errors)
    if (errors.reference || errors.contact) return
    access.mutate({ reference, contact })
  }

  return (
    <section className={styles.page}>
      <div className={styles.intro}>
        <h1>Check your claim</h1>
        <p className={styles.lead}>
          Enter your claim reference and the email address or phone number you gave when you submitted your claim.
        </p>
      </div>
      <Card padding="lg">
        <form className={styles.form} onSubmit={submit} noValidate>
          <TextInput
            label="Claim reference"
            size="lg"
            required
            autoComplete="off"
            autoCapitalize="characters"
            spellCheck={false}
            className={styles.reference}
            value={input.reference}
            error={missing.reference}
            onChange={(event) => setInput({ ...input, reference: event.target.value })}
          />
          <TextInput
            label="Email address or phone number"
            size="lg"
            required
            autoComplete="email"
            value={input.contact}
            error={missing.contact}
            onChange={(event) => setInput({ ...input, contact: event.target.value })}
          />
          {access.isError && <AccessError error={access.error} />}
          <div className={styles.actions}>
            <Button type="submit" variant="primary" size="lg" loading={access.isPending} className={styles.submit}>
              View claim
            </Button>
          </div>
        </form>
      </Card>
    </section>
  )
}
