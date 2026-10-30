import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { PercepcionFormulario } from '../api/compras'
import { PercepcionesDeCompra } from './PercepcionesDeCompra'

afterEach(cleanup)

const filas: PercepcionFormulario[] = [
  { tipo: 'iva', baseImponible: 20000, alicuota: 3, importe: 600, automatica: false },
  { tipo: 'iibb', baseImponible: 1000, alicuota: 2.5, importe: 25, automatica: false },
]

function percepcionesEnPantalla(): string[] {
  return screen
    .getAllByRole('row')
    .slice(1)
    .map((fila) => fila.querySelector('td')?.textContent ?? '')
}

describe('PercepcionesDeCompra — orden por columna', () => {
  it('la base imponible ordena por su valor numérico y el tercer clic vuelve al orden original', () => {
    render(<PercepcionesDeCompra filas={filas} />)

    const base = screen.getByRole('columnheader', { name: /Base imponible/ })
    fireEvent.click(within(base).getByRole('button'))
    expect(base).toHaveAttribute('aria-sort', 'ascending')
    expect(percepcionesEnPantalla()).toEqual(['Percepción IIBB', 'Percepción IVA'])

    fireEvent.click(within(base).getByRole('button'))
    expect(percepcionesEnPantalla()).toEqual(['Percepción IVA', 'Percepción IIBB'])

    fireEvent.click(within(base).getByRole('button'))
    expect(percepcionesEnPantalla()).toEqual(['Percepción IVA', 'Percepción IIBB'])
  })

  it('en el editor (editable) los encabezados no ordenan y las filas conservan el orden de carga', () => {
    render(<PercepcionesDeCompra filas={filas} onCambiar={vi.fn()} onQuitar={vi.fn()} />)

    expect(screen.getAllByRole('columnheader').filter((th) => within(th).queryByRole('button'))).toHaveLength(0)
    expect(percepcionesEnPantalla().map((t) => t.replace('Sugerida', ''))).toEqual(['Percepción IVA', 'Percepción IIBB'])
  })
})
