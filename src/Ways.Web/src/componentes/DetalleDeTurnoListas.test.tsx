import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DetalleDeTurnoListas, type EdicionDeTurno } from './DetalleDeTurnoListas'
import type { PagoDeTurno, TicketDeTurno } from '../api/tipos'

afterEach(cleanup)

function ticket(id: number, sobrescribir: Partial<TicketDeTurno> = {}): TicketDeTurno {
  return {
    id,
    numero: id,
    numeroVisible: `T-${id}`,
    estado: 'Emitido',
    fecha: '2026-09-16T12:00:00Z',
    idCliente: 1,
    nombreCliente: 'Cliente 1',
    total: 100,
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
    mediosDePago: [],
    estadoNotaDeCredito: null,
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

describe('DetalleDeTurnoListas — acciones de los tickets', () => {
  it('sin alReimprimir solo ofrece ver el detalle: ni Reimprimir ni Anular', () => {
    render(<DetalleDeTurnoListas tickets={[ticket(1), ticket(2, { estado: 'Anulado' })]} pagos={[]} />)

    expect(screen.getAllByRole('button', { name: 'Detalle' })).toHaveLength(2)
    expect(screen.queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /anular/i })).not.toBeInTheDocument()
  })

  it('con alReimprimir solo los tickets emitidos ofrecen Reimprimir', () => {
    render(<DetalleDeTurnoListas tickets={[ticket(1), ticket(2, { estado: 'Anulado' })]} pagos={[]} alReimprimir={() => {}} />)

    expect(screen.getAllByRole('button', { name: 'Reimprimir' })).toHaveLength(1)
  })
})

describe('DetalleDeTurnoListas — corrección de la administración', () => {
  function edicionFixture(sobrescribir: Partial<EdicionDeTurno> = {}): EdicionDeTurno {
    return { alCambiarMedio: vi.fn(), alEditarPago: vi.fn(), inerte: false, puedeCambiarMedio: true, ...sobrescribir }
  }

  const tickets = [ticket(1), ticket(2, { estado: 'Anulado' })]
  const pagos = [pago(10, { categoria: 'Servicios' }), pago(11, { categoria: 'Sueldos', idMovimientoPersonal: 5 })]

  it('sin edicion no ofrece ninguna acción de corrección ni la columna de acciones de los pagos', () => {
    render(<DetalleDeTurnoListas tickets={tickets} pagos={pagos} />)

    expect(screen.queryByRole('button', { name: /Cambiar medio/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Editar el pago/ })).not.toBeInTheDocument()
    const [, tablaDePagos] = screen.getAllByRole('table')
    expect(within(tablaDePagos).queryByRole('columnheader', { name: 'Acciones' })).not.toBeInTheDocument()
  })

  it('con edicion solo el ticket emitido ofrece cambiar el medio, y el pago nacido de personal no se edita', () => {
    render(<DetalleDeTurnoListas tickets={tickets} pagos={pagos} edicion={edicionFixture()} />)

    expect(screen.getAllByRole('button', { name: /Cambiar medio de pago/ })).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Cambiar medio de pago de la venta T-1' })).toBeEnabled()
    const editables = screen.getAllByRole('button', { name: /Editar el pago/ })
    expect(editables).toHaveLength(1)
    expect(editables[0].getAttribute('aria-label')).toContain('Servicios')
  })

  it('cada acción entrega al dueño el ticket o el pago de su fila', () => {
    const edicion = edicionFixture()
    render(<DetalleDeTurnoListas tickets={tickets} pagos={pagos} edicion={edicion} />)

    fireEvent.click(screen.getByRole('button', { name: 'Cambiar medio de pago de la venta T-1' }))
    fireEvent.click(screen.getByRole('button', { name: /Editar el pago de Servicios/ }))

    expect(edicion.alCambiarMedio).toHaveBeenCalledWith(tickets[0], expect.any(HTMLElement))
    expect(edicion.alEditarPago).toHaveBeenCalledWith(pagos[0], expect.any(HTMLElement))
  })

  it('inerte deja todas las acciones deshabilitadas', () => {
    render(<DetalleDeTurnoListas tickets={tickets} pagos={pagos} edicion={edicionFixture({ inerte: true })} />)

    expect(screen.getByRole('button', { name: /Cambiar medio de pago/ })).toBeDisabled()
    expect(screen.getByRole('button', { name: /Editar el pago/ })).toBeDisabled()
  })

  it('sin catálogo de medios solo se deshabilita el cambio de medio', () => {
    render(<DetalleDeTurnoListas tickets={tickets} pagos={pagos} edicion={edicionFixture({ puedeCambiarMedio: false })} />)

    expect(screen.getByRole('button', { name: /Cambiar medio de pago/ })).toBeDisabled()
    expect(screen.getByRole('button', { name: /Editar el pago/ })).toBeEnabled()
  })
})
