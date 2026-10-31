import { createRef } from 'react'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { TituloDeSeccion } from './TituloDeSeccion'

afterEach(cleanup)

describe('TituloDeSeccion', () => {
  it('es un h3 por defecto', () => {
    render(<TituloDeSeccion>Costos</TituloDeSeccion>)
    const titulo = screen.getByRole('heading', { name: 'Costos', level: 3 })
    expect(titulo).toHaveClass('titulo-de-seccion')
  })

  it.each([2, 4] as const)('respeta el nivel %i', (nivel) => {
    render(<TituloDeSeccion nivel={nivel}>Costos</TituloDeSeccion>)
    expect(screen.getByRole('heading', { name: 'Costos', level: nivel })).toBeInTheDocument()
  })

  it('reenvía id, ref, tabIndex y suma la clase recibida', () => {
    const ref = createRef<HTMLHeadingElement>()
    render(
      <TituloDeSeccion id="t-1" ref={ref} tabIndex={-1} className="mb-0">
        Familia
      </TituloDeSeccion>,
    )
    const titulo = screen.getByRole('heading', { name: 'Familia' })
    expect(titulo).toHaveAttribute('id', 't-1')
    expect(titulo).toHaveAttribute('tabindex', '-1')
    expect(titulo).toHaveClass('titulo-de-seccion', 'mb-0')
    expect(ref.current).toBe(titulo)
  })
})
