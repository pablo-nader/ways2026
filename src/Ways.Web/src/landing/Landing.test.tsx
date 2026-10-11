import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Landing } from './Landing'

describe('Landing', () => {
  it('muestra la marca y que la página del comercio se está preparando', () => {
    render(<Landing />)

    expect(screen.getByRole('heading', { level: 1, name: 'Ways' })).toBeInTheDocument()
    expect(screen.getByText('La página de este comercio se está preparando.')).toBeInTheDocument()
  })

  it('Ingresar lleva al login del back office bajo /admin', () => {
    render(<Landing />)

    expect(screen.getByRole('link', { name: 'Ingresar' })).toHaveAttribute('href', '/admin/login')
  })
})
