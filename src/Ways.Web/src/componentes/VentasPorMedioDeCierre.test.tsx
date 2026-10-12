import { cleanup, render, screen } from '@testing-library/react'
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
    totalCuentaCorriente: 0,
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

  it('muestra "Total cuenta corriente" solo cuando no es cero', () => {
    const { unmount } = render(<VentasPorMedioDeCierre cierre={cierreFixture()} />)
    expect(screen.queryByText('Total cuenta corriente')).not.toBeInTheDocument()
    unmount()

    render(<VentasPorMedioDeCierre cierre={cierreFixture({ totalCuentaCorriente: 700, totalVentas: 8700 })} />)
    expect(celdasDeLaTabla()).toContainEqual(['Total cuenta corriente', '$ 700,00'])
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

  it('un resumen sin los subtotales (payload viejo) no los inventa y conserva el total general', () => {
    const viejo: Partial<ResumenDeCierrePorRetiro> = cierreFixture()
    delete viejo.totalDigitales
    delete viejo.totalEfectivo
    delete viejo.totalCuentaCorriente
    render(<VentasPorMedioDeCierre cierre={viejo as ResumenDeCierrePorRetiro} />)

    expect(screen.queryByText('Total digitales')).not.toBeInTheDocument()
    expect(screen.queryByText('Total efectivo')).not.toBeInTheDocument()
    expect(celdasDeLaTabla()).toContainEqual(['Total general', '$ 8.000,00'])
  })
})
