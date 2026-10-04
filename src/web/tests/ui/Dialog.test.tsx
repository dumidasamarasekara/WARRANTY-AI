import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { Button, Dialog, TextInput } from '../../src/shared/ui'

function Harness({ submitting = false }: { submitting?: boolean }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <Button onClick={() => setOpen(true)}>Approve</Button>
      <Button>Outside</Button>
      <Dialog
        open={open}
        onClose={() => setOpen(false)}
        title="Approve claim"
        submitting={submitting}
        footer={
          <>
            <Button onClick={() => setOpen(false)}>Cancel</Button>
            <Button variant="primary">Confirm</Button>
          </>
        }
      >
        <TextInput label="Message to the claimant" />
      </Dialog>
    </>
  )
}

describe('Dialog', () => {
  it('is a modal labelled by its title and focuses the first field', async () => {
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'Approve' }))

    const dialog = screen.getByRole('dialog', { name: 'Approve claim' })
    expect(dialog).toHaveAttribute('aria-modal', 'true')
    expect(screen.getByLabelText('Message to the claimant')).toHaveFocus()
  })

  it('traps focus in both directions', async () => {
    const user = userEvent.setup()
    render(<Harness />)
    await user.click(screen.getByRole('button', { name: 'Approve' }))

    const close = screen.getByRole('button', { name: 'Close dialog' })
    const confirm = screen.getByRole('button', { name: 'Confirm' })

    confirm.focus()
    await user.tab()
    expect(close).toHaveFocus()

    await user.tab({ shift: true })
    expect(confirm).toHaveFocus()

    await user.tab()
    await user.tab()
    expect(screen.getByLabelText('Message to the claimant')).toHaveFocus()
    expect(screen.getByRole('button', { name: 'Outside' })).not.toHaveFocus()
  })

  it('pulls focus back when it escapes', async () => {
    const user = userEvent.setup()
    render(<Harness />)
    await user.click(screen.getByRole('button', { name: 'Approve' }))

    screen.getByRole('button', { name: 'Outside', hidden: true }).focus()

    expect(screen.getByRole('dialog')).toContainElement(document.activeElement as HTMLElement)
  })

  it('closes on Esc and returns focus to the trigger', async () => {
    const user = userEvent.setup()
    render(<Harness />)
    const trigger = screen.getByRole('button', { name: 'Approve' })
    await user.click(trigger)

    await user.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(trigger).toHaveFocus()
  })

  it('returns focus to the trigger when closed by a button', async () => {
    const user = userEvent.setup()
    render(<Harness />)
    const trigger = screen.getByRole('button', { name: 'Approve' })
    await user.click(trigger)

    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(trigger).toHaveFocus()
  })

  it('stays open on Esc while submitting', async () => {
    const user = userEvent.setup()
    render(<Harness submitting />)
    await user.click(screen.getByRole('button', { name: 'Approve' }))

    await user.keyboard('{Escape}')

    expect(screen.getByRole('dialog', { name: 'Approve claim' })).toHaveAttribute('aria-busy', 'true')
    expect(screen.getByRole('button', { name: 'Close dialog' })).toBeDisabled()
  })
})
