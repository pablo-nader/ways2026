import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import type { ResultadoDeResolucion } from '../api/tipos'
import { PrecioUnitarioDeLinea } from './PrecioUnitarioDeLinea'

afterEach(cleanup)

function resultado(sobrescribir: Partial<ResultadoDeResolucion> = {}): ResultadoDeResolucion {
  return {
    idArticulo: 1,
    idListaPrecio: 1,
    precioOriginal: 4000,
    precioFinal: 3750,
    descuentoUnitario: 250,
    aplicadas: [{ idOferta: 1, nombre: '2x7500', descuentoUnitario: 250, multiploDe: 2 }],
    descuentoDeLinea: 500,
    ...sobrescribir,
  }
}

describe('PrecioUnitarioDeLinea', () => {
  it('con un resto sin cubrir muestra el precio regular y el desglose, no el precio de oferta', () => {
    render(<PrecioUnitarioDeLinea cantidad={3} resultado={resultado()} precioUnitario={3750} tieneDescuento />)
    expect(screen.getByText('2 × $ 3.750,00 + 1 × $ 4.000,00')).toBeInTheDocument()
    expect(screen.getByText('2x7500')).toBeInTheDocument()
    expect(screen.getByText((_, nodo) => nodo?.tagName === 'DIV' && nodo.textContent === '$ 4.000,002x7500')).toBeInTheDocument()
    expect(screen.queryByText('$ 3.750,00')).not.toBeInTheDocument()
  })

  it('con todas las unidades cubiertas queda el precio de oferta y no hay desglose', () => {
    render(<PrecioUnitarioDeLinea cantidad={4} resultado={resultado({ descuentoDeLinea: 1000 })} precioUnitario={3750} tieneDescuento />)
    expect(screen.getByText('$ 4.000,00')).toBeInTheDocument()
    expect(screen.getByText(/\$ 3\.750,00/)).toBeInTheDocument()
    expect(screen.queryByText(/×/)).not.toBeInTheDocument()
  })
})
