import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { DetalleDeTurnoListas } from './DetalleDeTurnoListas'
import type { GastoDeTurno, TicketDeTurno } from '../api/tipos'

afterEach(cleanup)

function ticket(id: number, sobrescribir: Partial<TicketDeTurno> = {}): TicketDeTurno {
  return {
    id,
    numero: id,
    numeroVisible: `T-${id}`,
    estado: 'Emitido',
    fecha: '2026-09-16T12:00:00Z',
    idPuntoVenta: 1,
    idCliente: 1,
    total: 100,
    ...sobrescribir,
  }
}

function gasto(id: number, sobrescribir: Partial<GastoDeTurno> = {}): GastoDeTurno {
  return {
    id,
    idPuntoVenta: 1,
    fecha: '2026-09-16T12:00:00Z',
    categoria: 'Otros',
    idMedioPago: 1,
    importe: 100,
    origenFondos: 'CajaTurno',
    idTurnoCaja: 1,
    turnoAbierto: false,
    idProveedor: null,
    idArea: null,
    concepto: `Gasto ${id}`,
    detalle: null,
    numeroFactura: null,
    idComprobanteCompra: null,
    idMovimientoPersonal: null,
    ...sobrescribir,
  }
}

function primeraColumna(tabla: HTMLElement): string[] {
  return within(tabla)
    .getAllByRole('row')
    .slice(1)
    .map((fila) => within(fila).getAllByRole('cell')[0].textContent ?? '')
}

describe('DetalleDeTurnoListas — orden por columna', () => {
  it('los tickets se ordenan por el total numérico y por la fecha cronológica', () => {
    render(
      <DetalleDeTurnoListas
        tickets={[
          ticket(1, { total: 5000, fecha: '2026-09-16T15:00:00Z' }),
          ticket(2, { total: 300, fecha: '2026-09-16T09:00:00Z' }),
          ticket(3, { total: 10000, fecha: '2026-09-16T12:00:00Z' }),
        ]}
        gastos={[]}
      />,
    )
    const [tickets] = screen.getAllByRole('table')

    fireEvent.click(within(within(tickets).getByRole('columnheader', { name: /Total/ })).getByRole('button'))
    expect(primeraColumna(tickets)).toEqual(['T-2', 'T-1', 'T-3'])

    fireEvent.click(within(within(tickets).getByRole('columnheader', { name: /Fecha/ })).getByRole('button'))
    expect(primeraColumna(tickets)).toEqual(['T-2', 'T-3', 'T-1'])
  })

  it('los gastos se ordenan por importe sin mover los tickets, y cada tabla lleva su propio orden', () => {
    render(
      <DetalleDeTurnoListas
        tickets={[ticket(1), ticket(2)]}
        gastos={[gasto(1, { categoria: 'Servicios', importe: 2000 }), gasto(2, { categoria: 'Otros', importe: 150 })]}
      />,
    )
    const [tickets, gastos] = screen.getAllByRole('table')

    const importe = within(gastos).getByRole('columnheader', { name: /Importe/ })
    fireEvent.click(within(importe).getByRole('button'))

    expect(importe).toHaveAttribute('aria-sort', 'ascending')
    expect(
      within(gastos)
        .getAllByRole('row')
        .slice(1)
        .map((fila) => within(fila).getAllByRole('cell')[1].textContent),
    ).toEqual(['Otros', 'Servicios'])
    expect(primeraColumna(tickets)).toEqual(['T-1', 'T-2'])
    expect(within(tickets).getByRole('columnheader', { name: /Número/ })).toHaveAttribute('aria-sort', 'none')
  })
})
