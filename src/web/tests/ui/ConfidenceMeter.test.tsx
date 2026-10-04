import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ConfidenceMeter } from '../../src/shared/ui'

describe('ConfidenceMeter', () => {
  it('exposes meter semantics with the visible number', () => {
    render(<ConfidenceMeter value={82} />)

    const meter = screen.getByRole('meter', { name: 'Confidence' })
    expect(meter).toHaveAttribute('aria-valuenow', '82')
    expect(meter).toHaveAttribute('aria-valuemin', '0')
    expect(meter).toHaveAttribute('aria-valuemax', '100')
    expect(meter).toHaveAttribute('aria-valuetext', '82%')
    expect(meter).toHaveTextContent('82%')
  })

  it('clamps and rounds the value', () => {
    const { rerender } = render(<ConfidenceMeter value={140} label="Policy agent confidence" />)
    expect(screen.getByRole('meter', { name: 'Policy agent confidence' })).toHaveAttribute('aria-valuenow', '100')

    rerender(<ConfidenceMeter value={-3} label="Policy agent confidence" />)
    expect(screen.getByRole('meter')).toHaveAttribute('aria-valuenow', '0')

    rerender(<ConfidenceMeter value={66.6} label="Policy agent confidence" size="md" />)
    expect(screen.getByRole('meter')).toHaveAttribute('aria-valuenow', '67')
    expect(screen.getByRole('meter')).toHaveTextContent('67%')
  })
})
