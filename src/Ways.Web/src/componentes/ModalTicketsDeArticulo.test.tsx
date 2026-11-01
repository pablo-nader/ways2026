import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ComprobanteEmitido, TicketsDeArticulo } from '../api/tipos'
import { fechaHoraEnZonaATexto } from '../formato/fechas'
import { ModalTicketsDeArticulo } from './ModalTicketsDeArticulo'

const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
  },
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

const FILTROS = { idEmpresa: 1, idPuntoVenta: 11 as number | null, desde: '2026-10-12', hasta: '2026-10-18' }

function respuestaFixture(sobrescribir: Partial<TicketsDeArticulo> = {}): TicketsDeArticulo {
  return {
    desde: '2026-10-12',
    hasta: '2026-10-18',
    zonaHoraria: 'America/Argentina/Buenos_Aires',
    idArticulo: 12,
    descripcion: 'Marlboro box 20',
    cantidadTotal: 3,
    total: 2500,
    tickets: [
      {
        idComprobante: 345,
        fecha: '2026-10-15T01:30:00+00:00',
        puntoVenta: 'Sucursal Centro',
        comprobante: 'Ticket 0001-00000345',
        cliente: null,
        cantidad: 4,
        total: 4000,
      },
      {
        idComprobante: 346,
        fecha: '2026-10-14T15:00:00+00:00',
        puntoVenta: 'Sucursal Centro',
        comprobante: 'Nota de crédito 0001-00000012',
        cliente: 'Ana Pérez',
        cantidad: -1,
        total: -1500,
      },
    ],
    ...sobrescribir,
  }
}

function comprobanteFixture(id: number, numeroVisible: string): ComprobanteEmitido {
  return {
    id,
    numero: 345,
    numeroVisible,
    estado: 'Emitido',
    fecha: '2026-10-15T01:30:00+00:00',
    idPuntoVenta: 11,
    idCliente: 1,
    idComprobanteAsociado: null,
    subtotal: 4000,
    descuentoTotal: 0,
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
    total: 4000,
    direccionEntrega: null,
    observaciones: null,
    items: [
      {
        orden: 1,
        idArticulo: 12,
        descripcion: 'Marlboro box 20',
        codigoBarra: null,
        idArea: 1,
        idListaPrecio: 1,
        idOferta: null,
        idAlicuotaIva: 1,
        porcentajeIva: 21,
        cantidad: 4,
        precioUnitario: 1000,
        descuento: 0,
        total: 4000,
        idLote: null,
        codigoLote: null,
        loteVencido: false,
      },
    ],
    pagos: [{ idMedioPago: 5, importe: 4000, referencia: null, vuelto: 0 }],
    idPresupuestoOrigen: null,
  } as unknown as ComprobanteEmitido
}

type Rutas = { tickets?: (idArticulo: number) => Promise<unknown>; venta?: (id: number) => Promise<unknown> }

function mockearRutas({ tickets, venta }: Rutas = {}) {
  apiGetMock.mockImplementation((ruta: string) => {
    const coincideTickets = /^\/reportes\/ventas\/por-articulo\/(\d+)\/tickets\?/.exec(ruta)
    if (coincideTickets) return (tickets ?? (() => Promise.resolve(respuestaFixture())))(Number(coincideTickets[1]))
    const coincideVenta = /^\/ventas\/(\d+)$/.exec(ruta)
    if (coincideVenta) {
      const id = Number(coincideVenta[1])
      return (venta ?? ((i: number) => Promise.resolve(comprobanteFixture(i, '0001-00000345'))))(id)
    }
    if (ruta.startsWith('/catalogos/medios-pago')) return Promise.resolve([{ id: 5, nombre: 'Efectivo' }])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function llamadasDeTickets(): string[] {
  return apiGetMock.mock.calls.map((c) => c[0] as string).filter((r) => r.includes('/tickets?'))
}

/** Hace de dueño del estado de la URL: el modal es controlado por `idTicket`. */
function Anfitrion({
  idArticulo = 12,
  desde = FILTROS.desde,
  ticketInicial = null,
  alCerrar = () => {},
}: {
  idArticulo?: number
  desde?: string
  ticketInicial?: number | null
  alCerrar?: () => void
}) {
  const [idTicket, setIdTicket] = useState<number | null>(ticketInicial)
  return (
    <ModalTicketsDeArticulo
      key={idArticulo}
      idArticulo={idArticulo}
      filtros={{ ...FILTROS, desde }}
      idTicket={idTicket}
      onAbrirTicket={(id) => setIdTicket(id)}
      onCerrarTicket={() => setIdTicket(null)}
      onCerrar={alCerrar}
    />
  )
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('fechaHoraEnZonaATexto', () => {
  it('muestra el instante en la zona horaria pedida y no en la del navegador', () => {
    expect(fechaHoraEnZonaATexto('2026-10-15T01:30:00+00:00', 'America/Argentina/Buenos_Aires')).toBe('14/10/2026, 22:30:00')
    expect(fechaHoraEnZonaATexto('2026-10-15T01:30:00+00:00', 'UTC')).toBe('15/10/2026, 01:30:00')
  })

  it('con una zona inválida no rompe: cae a la hora local', () => {
    expect(fechaHoraEnZonaATexto('2026-10-15T01:30:00+00:00', 'No/Existe')).not.toBe('')
  })
})

describe('ModalTicketsDeArticulo', () => {
  it('pide los tickets con el recorte del reporte y muestra cabecera, filas y totales', async () => {
    mockearRutas()
    render(<Anfitrion />)

    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    const fila = await screen.findByRole('row', { name: /Ticket 0001-00000345/ })

    expect(llamadasDeTickets()).toEqual([
      '/reportes/ventas/por-articulo/12/tickets?idEmpresa=1&idPuntoVenta=11&desde=2026-10-12&hasta=2026-10-18',
    ])
    const dialogo = screen.getByRole('dialog', { name: /Marlboro box 20/ })
    expect(within(dialogo).getByText(/12\/10\/2026/)).toBeInTheDocument()
    expect(within(dialogo).getByText(/18\/10\/2026/)).toBeInTheDocument()

    expect(within(fila).getByText('14/10/2026, 22:30:00')).toBeInTheDocument()
    expect(within(fila).getByText('Sucursal Centro')).toBeInTheDocument()
    expect(within(fila).getByText('Consumidor final')).toBeInTheDocument()
    expect(within(fila).getByText('4')).toBeInTheDocument()
    expect(within(fila).getByText('$ 4.000,00')).toBeInTheDocument()

    const totales = screen.getByRole('row', { name: /^Total/ })
    expect(within(totales).getByText('3')).toBeInTheDocument()
    expect(within(totales).getByText('$ 2.500,00')).toBeInTheDocument()
  })

  it('una nota de crédito se muestra con cantidad y total negativos', async () => {
    mockearRutas()
    render(<Anfitrion />)

    const fila = await screen.findByRole('row', { name: /Nota de crédito 0001-00000012/ })

    expect(within(fila).getByText('Ana Pérez')).toBeInTheDocument()
    expect(within(fila).getByText('-1')).toBeInTheDocument()
    expect(within(fila).getByText('-$ 1.500,00')).toBeInTheDocument()
  })

  it('sin tickets muestra el estado vacío y no dibuja totales', async () => {
    mockearRutas({
      tickets: () => Promise.resolve(respuestaFixture({ tickets: [], cantidadTotal: 0, total: 0 })),
    })
    render(<Anfitrion />)

    expect(await screen.findByText('No hay tickets de este artículo en el período.')).toBeInTheDocument()
    expect(screen.queryByRole('row', { name: /^Total/ })).not.toBeInTheDocument()
  })

  it('un fallo de la consulta muestra el mensaje y permite reintentar', async () => {
    let intento = 0
    mockearRutas({
      tickets: () => (++intento === 1 ? Promise.reject(new Error('boom')) : Promise.resolve(respuestaFixture())),
    })
    const usuario = userEvent.setup()
    render(<Anfitrion />)

    expect(await screen.findByText('No se pudieron cargar los tickets del artículo.')).toBeInTheDocument()
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByRole('row', { name: /Ticket 0001-00000345/ })).toBeInTheDocument()
    expect(screen.queryByText('No se pudieron cargar los tickets del artículo.')).not.toBeInTheDocument()
  })

  it('al cambiar de artículo mientras el anterior sigue en vuelo, solo se muestra el último', async () => {
    let resolverA: (valor: TicketsDeArticulo) => void = () => {}
    mockearRutas({
      tickets: (id) =>
        id === 12
          ? new Promise<TicketsDeArticulo>((resolver) => {
              resolverA = resolver
            })
          : Promise.resolve(respuestaFixture({ idArticulo: 13, descripcion: 'Fiambre suelto', tickets: [] })),
    })
    const { rerender } = render(<Anfitrion idArticulo={12} />)
    await waitFor(() => expect(llamadasDeTickets()).toHaveLength(1))

    rerender(<Anfitrion idArticulo={13} />)
    expect(await screen.findByRole('dialog', { name: /Fiambre suelto/ })).toBeInTheDocument()

    resolverA(respuestaFixture({ idArticulo: 12, descripcion: 'Marlboro box 20' }))
    await new Promise((r) => setTimeout(r, 0))

    expect(screen.getByRole('dialog', { name: /Fiambre suelto/ })).toBeInTheDocument()
    expect(screen.queryByText('Ticket 0001-00000345')).not.toBeInTheDocument()
  })

  it('una respuesta vieja del mismo artículo no pisa la de los filtros nuevos', async () => {
    let resolverViejo: (valor: TicketsDeArticulo) => void = () => {}
    mockearRutas({
      tickets: () =>
        llamadasDeTickets().length === 1
          ? new Promise<TicketsDeArticulo>((resolver) => {
              resolverViejo = resolver
            })
          : Promise.resolve(respuestaFixture({ tickets: [], cantidadTotal: 0, total: 0 })),
    })
    const { rerender } = render(<Anfitrion />)
    await waitFor(() => expect(llamadasDeTickets()).toHaveLength(1))

    rerender(<Anfitrion desde="2026-10-14" />)
    expect(await screen.findByText('No hay tickets de este artículo en el período.')).toBeInTheDocument()

    resolverViejo(respuestaFixture())
    await new Promise((r) => setTimeout(r, 0))

    expect(screen.getByText('No hay tickets de este artículo en el período.')).toBeInTheDocument()
    expect(screen.queryByText('Ticket 0001-00000345')).not.toBeInTheDocument()
  })

  it('abrir un ticket pide la venta y muestra sus ítems; cerrar el detalle vuelve a la lista', async () => {
    mockearRutas()
    const usuario = userEvent.setup()
    render(<Anfitrion />)

    const fila = await screen.findByRole('row', { name: /Ticket 0001-00000345/ })
    const boton = within(fila).getByRole('button', { name: /Ver el detalle del ticket Ticket 0001-00000345/ })
    await usuario.click(boton)

    const detalle = await screen.findByRole('dialog', { name: 'Detalle de la venta 0001-00000345' })
    expect(await within(detalle).findByText('Efectivo')).toBeInTheDocument()
    expect(within(detalle).getByText('Marlboro box 20')).toBeInTheDocument()
    expect(within(detalle).queryByRole('button', { name: /Imprimir/ })).not.toBeInTheDocument()
    expect(apiGetMock.mock.calls.map((c) => c[0])).toContain('/ventas/345')

    await usuario.click(within(detalle).getByRole('button', { name: 'Cerrar' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: /Detalle de la venta/ })).not.toBeInTheDocument())
    expect(screen.getByRole('dialog', { name: /Marlboro box 20/ })).toBeInTheDocument()
    expect(boton).toHaveFocus()
  })

  it('con el ticket ya elegido (URL) abre el detalle sin click y toma el cliente de la lista', async () => {
    mockearRutas({ venta: (id) => Promise.resolve(comprobanteFixture(id, '0001-00000012')) })
    render(<Anfitrion ticketInicial={346} />)

    const detalle = await screen.findByRole('dialog', { name: 'Detalle de la venta 0001-00000012' })

    expect(await within(detalle).findByText('Ana Pérez')).toBeInTheDocument()
  })

  it('un detalle viejo no pisa al ticket elegido después', async () => {
    let resolverPrimero: (valor: ComprobanteEmitido) => void = () => {}
    mockearRutas({
      venta: (id) =>
        id === 345
          ? new Promise<ComprobanteEmitido>((resolver) => {
              resolverPrimero = resolver
            })
          : Promise.resolve(comprobanteFixture(id, '0001-00000012')),
    })
    const usuario = userEvent.setup()
    render(<Anfitrion />)

    const primera = await screen.findByRole('row', { name: /Ticket 0001-00000345/ })
    await usuario.click(within(primera).getByRole('button', { name: /Ver el detalle/ }))
    await waitFor(() => expect(apiGetMock.mock.calls.map((c) => c[0])).toContain('/ventas/345'))
    await usuario.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog', { name: /Detalle de la venta/ })).not.toBeInTheDocument())

    const segunda = screen.getByRole('row', { name: /Nota de crédito 0001-00000012/ })
    await usuario.click(within(segunda).getByRole('button', { name: /Ver el detalle/ }))
    expect(await screen.findByRole('dialog', { name: 'Detalle de la venta 0001-00000012' })).toBeInTheDocument()

    resolverPrimero(comprobanteFixture(345, '0001-00000345'))
    await new Promise((r) => setTimeout(r, 0))

    expect(screen.queryByRole('dialog', { name: 'Detalle de la venta 0001-00000345' })).not.toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Detalle de la venta 0001-00000012' })).toBeInTheDocument()
  })

  it('"Cerrar" de la lista avisa al dueño', async () => {
    mockearRutas()
    const alCerrar = vi.fn()
    const usuario = userEvent.setup()
    render(<Anfitrion alCerrar={alCerrar} />)
    await screen.findByRole('row', { name: /Ticket 0001-00000345/ })

    const dialogo = screen.getByRole('dialog', { name: /Marlboro box 20/ })
    await usuario.click(within(dialogo).getByRole('button', { name: 'Cerrar' }))

    expect(alCerrar).toHaveBeenCalledTimes(1)
  })
})
