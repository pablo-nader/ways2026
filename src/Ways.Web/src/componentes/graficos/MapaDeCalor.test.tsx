import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { MapaDeCalor } from './MapaDeCalor'

afterEach(cleanup)

function matriz(sobrescribir: Record<string, number> = {}): number[][] {
  return Array.from({ length: 7 }, (_, d) =>
    Array.from({ length: 24 }, (_, h) => sobrescribir[`${d}-${h}`] ?? 0),
  )
}

function renderMapa(valores: number[][]) {
  return render(
    <MapaDeCalor
      titulo="Importe promedio"
      valores={valores}
      formatearCelda={(v) => String(v)}
      formatearDetalle={(v) => `$ ${v},00`}
    />,
  )
}

describe('MapaDeCalor', () => {
  it('dibuja 7 días × 24 horas más encabezados y totales', () => {
    renderMapa(matriz({ '0-10': 200 }))
    const tabla = screen.getByRole('table', { name: 'Importe promedio' })

    const filas = within(tabla).getAllByRole('row')
    expect(filas).toHaveLength(1 + 7 + 1)
    expect(within(tabla).getAllByRole('columnheader')).toHaveLength(1 + 24 + 1)
    expect(within(tabla).getByRole('columnheader', { name: '00' })).toBeInTheDocument()
    expect(within(tabla).getByRole('columnheader', { name: '23' })).toBeInTheDocument()
    for (const dia of ['Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado', 'Domingo']) {
      expect(within(tabla).getByRole('rowheader', { name: dia })).toBeInTheDocument()
    }
  })

  it('cada celda lleva su número y un rótulo con día, franja y valor', () => {
    renderMapa(matriz({ '0-10': 200, '6-23': 5 }))

    const lunes10 = screen.getByRole('cell', { name: 'Lunes 10:00–11:00: $ 200,00' })
    expect(lunes10).toHaveTextContent('200')
    expect(lunes10).toHaveAttribute('title', 'Lunes 10:00–11:00: $ 200,00')
    expect(screen.getByRole('cell', { name: 'Domingo 23:00–00:00: $ 5,00' })).toHaveTextContent('5')
  })

  it('muestra los totales por día, por hora y el general', () => {
    renderMapa(matriz({ '0-10': 200, '0-11': 100, '2-10': 50 }))

    const lunes = screen.getByRole('rowheader', { name: 'Lunes' }).closest('tr') as HTMLElement
    const celdasLunes = within(lunes).getAllByRole('cell')
    expect(celdasLunes[celdasLunes.length - 1]).toHaveTextContent('300')

    const totales = screen.getByRole('rowheader', { name: 'Suma de promedios' }).closest('tr') as HTMLElement
    const celdasTotales = within(totales).getAllByRole('cell')
    expect(celdasTotales).toHaveLength(25)
    expect(celdasTotales[10]).toHaveTextContent('250')
    expect(celdasTotales[11]).toHaveTextContent('100')
    expect(celdasTotales[24]).toHaveTextContent('350')
  })

  it('pinta más fuerte la celda mayor y deja sin color los ceros y negativos', () => {
    renderMapa(matriz({ '0-10': 200, '1-10': 100, '2-10': -40 }))

    const mayor = screen.getByRole('cell', { name: 'Lunes 10:00–11:00: $ 200,00' })
    const mitad = screen.getByRole('cell', { name: 'Martes 10:00–11:00: $ 100,00' })
    const negativa = screen.getByRole('cell', { name: 'Miércoles 10:00–11:00: $ -40,00' })
    const vacia = screen.getByRole('cell', { name: 'Lunes 00:00–01:00: $ 0,00' })

    expect(mayor).toHaveStyle({ backgroundColor: 'rgba(247, 148, 29, 0.6)' })
    expect(mitad).toHaveStyle({ backgroundColor: 'rgba(247, 148, 29, 0.35)' })
    expect(negativa.style.backgroundColor).toBe('transparent')
    expect(vacia.style.backgroundColor).toBe('transparent')
  })

  it('con todo en cero no pinta ninguna celda', () => {
    renderMapa(matriz())

    const celdas = screen.getAllByRole('cell', { name: /^\S+ \d\d:00–\d\d:00:/ })
    expect(celdas).toHaveLength(168)
    expect(celdas.every((c) => c.style.backgroundColor === 'transparent')).toBe(true)
  })

  it('el scroll horizontal queda en un contenedor responsive', () => {
    const { container } = renderMapa(matriz())
    expect(container.querySelector('.table-responsive > table')).not.toBeNull()
  })
})
