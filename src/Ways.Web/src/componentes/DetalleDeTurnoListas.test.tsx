import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { DetalleDeTurnoListas } from './DetalleDeTurnoListas'
import type { PagoDeTurno, TicketDeTurno } from '../api/tipos'

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

function pago(id: number, sobrescribir: Partial<PagoDeTurno> = {}): PagoDeTurno {
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
    concepto: `Pago ${id}`,
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
        pagos={[]}
      />,
    )
    const [tickets] = screen.getAllByRole('table')

    fireEvent.click(within(within(tickets).getByRole('columnheader', { name: /Total/ })).getByRole('button'))
    expect(primeraColumna(tickets)).toEqual(['T-2', 'T-1', 'T-3'])

    fireEvent.click(within(within(tickets).getByRole('columnheader', { name: /Fecha/ })).getByRole('button'))
    expect(primeraColumna(tickets)).toEqual(['T-2', 'T-3', 'T-1'])
  })

  it('los pagos se ordenan por importe sin mover los tickets, y cada tabla lleva su propio orden', () => {
    render(
      <DetalleDeTurnoListas
        tickets={[ticket(1), ticket(2)]}
        pagos={[pago(1, { categoria: 'Servicios', importe: 2000 }), pago(2, { categoria: 'Otros', importe: 150 })]}
      />,
    )
    const [tickets, pagos] = screen.getAllByRole('table')

    const importe = within(pagos).getByRole('columnheader', { name: /Importe/ })
    fireEvent.click(within(importe).getByRole('button'))

    expect(importe).toHaveAttribute('aria-sort', 'ascending')
    expect(
      within(pagos)
        .getAllByRole('row')
        .slice(1)
        .map((fila) => within(fila).getAllByRole('cell')[1].textContent),
    ).toEqual(['Otros', 'Servicios'])
    expect(primeraColumna(tickets)).toEqual(['T-1', 'T-2'])
    expect(within(tickets).getByRole('columnheader', { name: /Número/ })).toHaveAttribute('aria-sort', 'none')
  })
})

describe('DetalleDeTurnoListas — medios del pago', () => {
  const linea = (idItem: number, idMedioPago: number, nombreMedioPago: string, importe: number) => ({
    idItem,
    idMedioPago,
    nombreMedioPago,
    importe,
    referencia: null,
  })

  it('un pago con varios medios muestra el reparto; uno con un solo medio, ese medio', () => {
    render(
      <DetalleDeTurnoListas
        tickets={[]}
        pagos={[
          pago(1, { importe: 1000, lineas: [linea(1, 1, 'Efectivo', 600), linea(2, 3, 'Transferencia', 400)] }),
          pago(2, { importe: 150, lineas: [linea(3, 1, 'Efectivo', 150)] }),
        ]}
      />,
    )
    const [, pagos] = screen.getAllByRole('table')

    expect(within(pagos).getByRole('cell', { name: 'Efectivo $ 600,00 · Transferencia $ 400,00' })).toBeInTheDocument()
    expect(within(pagos).getByRole('cell', { name: 'Efectivo' })).toBeInTheDocument()
  })

  it('un pago de una respuesta sin líneas no inventa un medio', () => {
    render(<DetalleDeTurnoListas tickets={[]} pagos={[pago(1)]} />)
    const [, pagos] = screen.getAllByRole('table')

    expect(within(pagos).getByRole('cell', { name: '—' })).toBeInTheDocument()
  })
})
