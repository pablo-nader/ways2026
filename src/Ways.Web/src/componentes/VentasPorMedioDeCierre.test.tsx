import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { VentasPorMedioDeCierre } from './VentasPorMedioDeCierre'
import type { ResumenDeCierrePorRetiro } from '../api/tipos'

afterEach(cleanup)

function cierreFixture(sobrescribir: Partial<ResumenDeCierrePorRetiro> = {}): ResumenDeCierrePorRetiro {
  return {
    idTurnoCaja: 501,
    puntoVenta: { id: 7, numero: 7, nombre: 'Local Centro' },
    fechaApertura: '2026-09-16T12:00:00Z',
    fechaCierre: '2026-09-16T20:00:00Z',
    vendedor: 'jperez',
    empleadoCierre: 'jperez',
    fondoInicial: 0,
    ventasPorMedio: [
      { idMedioPago: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', importe: 5000 },
      { idMedioPago: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', importe: 3000 },
    ],
    totalVentas: 8000,
    totalDigitales: 3000,
    totalEfectivo: 5000,
    ventasACuentaCorriente: 0,
    retiros: [],
    totalRetiros: 0,
    ventasEnEfectivoNetas: 5000,
    gastosEnEfectivo: 0,
    refuerzos: 0,
    diferencia: 0,
    ...sobrescribir,
  }
}

function celdasDeLaTabla(): string[][] {
  const tabla = screen.getByTestId('ventas-por-medio')
  return Array.from(tabla.querySelectorAll('tr'))
    .map((fila) => Array.from(fila.querySelectorAll('td')).map((c) => c.textContent ?? ''))
    .filter((celdas) => celdas.length > 0)
}

describe('VentasPorMedioDeCierre', () => {
  it('muestra cada medio y los subtotales y el total general que mandó el servidor', () => {
    render(<VentasPorMedioDeCierre cierre={cierreFixture()} />)

    expect(celdasDeLaTabla()).toEqual([
      ['Efectivo', '$ 5.000,00'],
      ['Tarjeta', '$ 3.000,00'],
      ['Total digitales', '$ 3.000,00'],
      ['Total efectivo', '$ 5.000,00'],
      ['Total general', '$ 8.000,00'],
    ])
  })

  it('un total que el servidor manda distinto de la suma de las filas se muestra tal cual, sin recalcular', () => {
    render(<VentasPorMedioDeCierre cierre={cierreFixture({ totalVentas: 9999, totalDigitales: 1234 })} />)

    const filas = celdasDeLaTabla()
    expect(filas).toContainEqual(['Total digitales', '$ 1.234,00'])
    expect(filas).toContainEqual(['Total general', '$ 9.999,00'])
  })

  it('muestra el fiado en cuenta corriente fuera de la tabla y solo cuando no es cero', () => {
    const { unmount } = render(<VentasPorMedioDeCierre cierre={cierreFixture()} />)
    expect(screen.queryByTestId('fiado-en-cuenta-corriente')).not.toBeInTheDocument()
    unmount()

    render(<VentasPorMedioDeCierre cierre={cierreFixture({ ventasACuentaCorriente: 700 })} />)
    const fiado = screen.getByTestId('fiado-en-cuenta-corriente')
    expect(fiado).toHaveTextContent('Fiado en cuenta corriente (no incluido en el total): $ 700,00')
    expect(screen.getByTestId('ventas-por-medio')).not.toContainElement(fiado)
    expect(celdasDeLaTabla()).toContainEqual(['Total general', '$ 8.000,00'])
  })

  it('un turno sin ventas lo dice y deja el total general en cero', () => {
    render(
      <VentasPorMedioDeCierre
        cierre={cierreFixture({ ventasPorMedio: [], totalVentas: 0, totalDigitales: 0, totalEfectivo: 0 })}
      />,
    )
    expect(screen.getByText('Sin ventas en el turno.')).toBeInTheDocument()
    expect(celdasDeLaTabla()).toContainEqual(['Total general', '$ 0,00'])
  })

  it.each([
    ['anterior al desglose', { totalDigitales: undefined, totalEfectivo: undefined, ventasACuentaCorriente: undefined }],
    ['parcial (falta el efectivo)', { totalEfectivo: undefined, ventasACuentaCorriente: 700 }],
    ['con un importe que no es número', { totalDigitales: Number.NaN, ventasACuentaCorriente: 700 }],
  ])('un payload %s no inventa subtotales ni fiado y conserva el total general', (_nombre, defecto) => {
    render(<VentasPorMedioDeCierre cierre={{ ...cierreFixture(), ...defecto } as ResumenDeCierrePorRetiro} />)

    expect(screen.queryByText('Total digitales')).not.toBeInTheDocument()
    expect(screen.queryByText('Total efectivo')).not.toBeInTheDocument()
    expect(screen.queryByTestId('fiado-en-cuenta-corriente')).not.toBeInTheDocument()
    expect(screen.getByTestId('ventas-por-medio')).not.toHaveTextContent(/undefined|NaN/)
    expect(celdasDeLaTabla()).toContainEqual(['Total general', '$ 8.000,00'])
  })
})

describe('VentasPorMedioDeCierre — orden por columna', () => {
  it('el importe ordena por su valor numérico y los totales del servidor quedan al pie sin ordenarse', () => {
    render(
      <VentasPorMedioDeCierre
        cierre={cierreFixture({
          ventasPorMedio: [
            { idMedioPago: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', importe: 5000 },
            { idMedioPago: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', importe: 3000 },
            { idMedioPago: 3, nombre: 'Cheque', comportamiento: 'Electronico', importe: 10000 },
          ],
        })}
      />,
    )

    const encabezado = screen.getByRole('columnheader', { name: /Importe/ })
    fireEvent.click(within(encabezado).getByRole('button'))

    expect(encabezado).toHaveAttribute('aria-sort', 'ascending')
    expect(celdasDeLaTabla().map((c) => c[0])).toEqual([
      'Tarjeta',
      'Efectivo',
      'Cheque',
      'Total digitales',
      'Total efectivo',
      'Total general',
    ])
  })

  it('el medio ordena alfabéticamente y el segundo clic invierte', () => {
    render(<VentasPorMedioDeCierre cierre={cierreFixture()} />)

    const encabezado = screen.getByRole('columnheader', { name: /Medio/ })
    fireEvent.click(within(encabezado).getByRole('button'))
    expect(celdasDeLaTabla().slice(0, 2).map((c) => c[0])).toEqual(['Efectivo', 'Tarjeta'])

    fireEvent.click(within(encabezado).getByRole('button'))
    expect(encabezado).toHaveAttribute('aria-sort', 'descending')
    expect(celdasDeLaTabla().slice(0, 2).map((c) => c[0])).toEqual(['Tarjeta', 'Efectivo'])
  })
})
