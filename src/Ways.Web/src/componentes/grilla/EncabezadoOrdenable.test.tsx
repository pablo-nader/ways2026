import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { EncabezadoOrdenable } from './EncabezadoOrdenable'
import type { EstadoDeOrden } from './ordenamiento'

function dibujar(orden: EstadoDeOrden, onAlternar = vi.fn(), className?: string) {
  render(
    <table>
      <thead>
        <tr>
          <EncabezadoOrdenable columna="nombre" orden={orden} onAlternar={onAlternar} className={className}>
            Nombre
          </EncabezadoOrdenable>
        </tr>
      </thead>
    </table>,
  )

  return onAlternar
}

describe('EncabezadoOrdenable', () => {
  it('sin orden sobre la columna anuncia aria-sort none', () => {
    dibujar(null)

    expect(screen.getByRole('columnheader', { name: 'Nombre' })).toHaveAttribute('aria-sort', 'none')
  })

  it('anuncia ascending y descending cuando la columna es la ordenada', () => {
    dibujar({ columna: 'nombre', direccion: 'ascendente' })
    expect(screen.getByRole('columnheader')).toHaveAttribute('aria-sort', 'ascending')
  })

  it('anuncia descending', () => {
    dibujar({ columna: 'nombre', direccion: 'descendente' })
    expect(screen.getByRole('columnheader')).toHaveAttribute('aria-sort', 'descending')
  })

  it('el orden de otra columna deja a esta en none', () => {
    dibujar({ columna: 'otra', direccion: 'ascendente' })
    expect(screen.getByRole('columnheader')).toHaveAttribute('aria-sort', 'none')
  })

  it('el nombre accesible del botón es el título y el indicador queda fuera', () => {
    dibujar({ columna: 'nombre', direccion: 'ascendente' })

    expect(screen.getByRole('button', { name: 'Nombre' })).toHaveTextContent('Nombre▲')
  })

  it('un clic pide alternar la columna', async () => {
    const onAlternar = dibujar(null)

    await userEvent.click(screen.getByRole('button', { name: 'Nombre' }))

    expect(onAlternar).toHaveBeenCalledExactlyOnceWith('nombre')
  })

  it('se activa con el teclado (Enter y espacio)', async () => {
    const usuario = userEvent.setup()
    const onAlternar = dibujar(null)

    await usuario.tab()
    expect(screen.getByRole('button', { name: 'Nombre' })).toHaveFocus()
    await usuario.keyboard('{Enter}')
    await usuario.keyboard(' ')

    expect(onAlternar).toHaveBeenCalledTimes(2)
  })

  it('conserva las clases del th', () => {
    dibujar(null, vi.fn(), 'text-end')

    expect(screen.getByRole('columnheader')).toHaveClass('text-end')
  })
})
