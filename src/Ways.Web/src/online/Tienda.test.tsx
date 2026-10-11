import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Tienda } from './Tienda'

describe('Tienda', () => {
  it('muestra el título y que el catálogo se está preparando', () => {
    render(<Tienda />)

    expect(screen.getByRole('heading', { level: 1, name: 'Tienda online' })).toBeInTheDocument()
    expect(screen.getByText('El catálogo se está preparando.')).toBeInTheDocument()
  })

  it('Volver al inicio lleva a la landing del comercio', () => {
    render(<Tienda />)

    expect(screen.getByRole('link', { name: 'Volver al inicio' })).toHaveAttribute('href', '/')
  })
})
