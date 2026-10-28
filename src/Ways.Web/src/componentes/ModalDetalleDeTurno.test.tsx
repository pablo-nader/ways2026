import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fechaHoraATexto } from '../formato/fechas'
import { ModalDetalleDeTurno } from './ModalDetalleDeTurno'
import type {
  DetalleDeTurno,
  MedioPagoListado,
  ResumenDeCierrePorRetiro,
  TurnoConArqueos,
} from '../api/tipos'

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
      this.name = 'ErrorApi'
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

function medio(id: number, nombre: string): MedioPagoListado {
  return {
    id,
    nombre,
    activo: true,
    idEmpresa: null,
    orden: id,
    comportamiento: id === 1 ? 'Efectivo' : 'Electronico',
    admiteVuelto: id === 1,
    requiereReferencia: false,
    recargoPorcentaje: null,
    porcentajeFacturacion: 0,
  }
}

function detalleFixture(sobrescribir: Partial<DetalleDeTurno> = {}): DetalleDeTurno {
  return {
    resumen: {
      idTurnoCaja: 501,
      idMedioAncla: 1,
      medios: [
        { idMedioPago: 1, importeEsperado: 1000 },
        { idMedioPago: 2, importeEsperado: 500 },
      ],
      cantidadTickets: 1,
      primerTicket: { numero: 1, fecha: '2026-09-16T13:00:00Z', codigo: 'TX' },
      ultimoTicket: { numero: 1, fecha: '2026-09-16T13:00:00Z', codigo: 'TX' },
      ingresosPorArea: [],
      egresos: { porCategoria: [], porArea: [], retiros: 100 },
    },
    tickets: [
      { id: 1, numero: 1, numeroVisible: '0007-00000001', estado: 'Emitido', fecha: '2026-09-16T13:00:00Z', idPuntoVenta: 7, idCliente: 1, total: 750 },
    ],
    gastos: [
      { id: 9, idPuntoVenta: 7, fecha: '2026-09-16T14:00:00Z', categoria: 'Sueldos', idMedioPago: 1, importe: 300, origenFondos: 'CajaTurno', idTurnoCaja: 501, turnoAbierto: false, idProveedor: null, idArea: null, concepto: 'Pago', detalle: null, numeroFactura: null, idComprobanteCompra: null, idMovimientoPersonal: null },
    ],
    fechaRecalculo: null,
    idEmpleadoRecalculo: null,
    ...sobrescribir,
  }
}

function turnoFixture(sobrescribir: Partial<TurnoConArqueos> = {}): TurnoConArqueos {
  return {
    id: 501,
    idPuntoVenta: 7,
    idEmpleadoApertura: 1,
    idEmpleadoCierre: 1,
    fechaApertura: '2026-09-16T12:00:00Z',
    fechaCierre: '2026-09-16T20:00:00Z',
    fondoInicial: 500,
    estado: 'Cerrado',
    observaciones: null,
    arqueos: [
      { idMedioPago: 1, importeEsperado: 1000, importeDeclarado: 900, diferencia: 100, importeEsperadoOriginal: null },
      { idMedioPago: 2, importeEsperado: 500, importeDeclarado: 550, diferencia: -50, importeEsperadoOriginal: null },
    ],
    fechaRecalculo: null,
    idEmpleadoRecalculo: null,
    ...sobrescribir,
  }
}

function cierreFixture(sobrescribir: Partial<ResumenDeCierrePorRetiro> = {}): ResumenDeCierrePorRetiro {
  return {
    idTurnoCaja: 501,
    puntoVenta: { id: 7, numero: 7, nombre: 'Local Centro' },
    fechaApertura: '2026-09-16T12:00:00Z',
    fechaCierre: '2026-09-16T20:00:00Z',
    vendedor: 'jperez',
    empleadoCierre: 'mgomez',
    fondoInicial: 500,
    ventasPorMedio: [],
    totalVentas: 0,
    totalDigitales: 0,
    totalEfectivo: 0,
    ventasACuentaCorriente: 0,
    retiros: [],
    totalRetiros: 0,
    ventasEnEfectivoNetas: 0,
    gastosEnEfectivo: 0,
    refuerzos: 0,
    diferencia: 0,
    ...sobrescribir,
  }
}

type RutasDeTurno = {
  detalle?: unknown
  turno?: unknown
  cierre?: unknown
}

/** Responde las cuatro lecturas del modal para el turno `id`; un valor `Error` se rechaza. */
function rutasDe(id: number, rutas: RutasDeTurno = {}) {
  const responder = (valor: unknown) => (valor instanceof Error ? Promise.reject(valor) : Promise.resolve(valor))
  return {
    [`/caja/turnos/${id}/detalle`]: () => responder(rutas.detalle ?? detalleFixture()),
    [`/caja/turnos/${id}`]: () => responder(rutas.turno ?? turnoFixture()),
    [`/caja/turnos/${id}/resumen-de-cierre`]: () => responder(rutas.cierre ?? cierreFixture()),
  } as Record<string, () => Promise<unknown>>
}

function mockearRutas(tabla: Record<string, () => Promise<unknown>>, medios: MedioPagoListado[] | Error = [medio(1, 'Efectivo'), medio(2, 'Tarjeta')]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/catalogos/medios-pago') return medios instanceof Error ? Promise.reject(medios) : Promise.resolve(medios)
    const respuesta = tabla[ruta]
    return respuesta ? respuesta() : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('ModalDetalleDeTurno', () => {
  it('muestra la cabecera del turno, el arqueo por medio con totales, y los tickets y pagos', async () => {
    mockearRutas(rutasDe(501))
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)

    const dialogo = await screen.findByRole('dialog', { name: 'Detalle del cierre #501' })
    // Espera al DATO (el cajero llega con la última lectura), no al diálogo, que existe antes.
    await within(dialogo).findByText('jperez')

    expect(within(dialogo).getByText('PV 7 — Local Centro')).toBeInTheDocument()
    expect(within(dialogo).getByText(fechaHoraATexto('2026-09-16T12:00:00Z'))).toBeInTheDocument()
    expect(within(dialogo).getByText(fechaHoraATexto('2026-09-16T20:00:00Z'))).toBeInTheDocument()
    expect(within(dialogo).getByText('mgomez')).toBeInTheDocument()
    expect(within(dialogo).getByText('Fondo inicial').nextElementSibling).toHaveTextContent('$ 500,00')

    const efectivo = within(dialogo).getByText('Efectivo').closest('tr') as HTMLElement
    expect(within(efectivo).getAllByRole('cell').map((c) => c.textContent)).toEqual(['Efectivo', '$ 1.000,00', '$ 900,00', '$ 100,00'])
    const tarjeta = within(dialogo).getByText('Tarjeta').closest('tr') as HTMLElement
    expect(within(tarjeta).getAllByRole('cell').map((c) => c.textContent)).toEqual(['Tarjeta', '$ 500,00', '$ 550,00', '-$ 50,00'])
    const totales = dialogo.querySelector('tfoot tr') as HTMLElement
    expect(within(totales).getAllByRole('cell').map((c) => c.textContent)).toEqual(['Total', '$ 1.500,00', '$ 1.450,00', '$ 50,00'])

    expect(within(dialogo).getByText('0007-00000001')).toBeInTheDocument()
    expect(within(dialogo).getByText('Sueldos')).toBeInTheDocument()
  })

  it('muestra las ventas por medio de pago con los subtotales y el total general del servidor', async () => {
    const cierre = cierreFixture({
      ventasPorMedio: [
        { idMedioPago: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', importe: 950 },
        { idMedioPago: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', importe: 300 },
      ],
      totalVentas: 1250,
      totalDigitales: 300,
      totalEfectivo: 950,
    })
    mockearRutas(rutasDe(501, { cierre }))
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)

    const dialogo = await screen.findByRole('dialog', { name: 'Detalle del cierre #501' })
    const tabla = await within(dialogo).findByTestId('ventas-por-medio')

    const filas = Array.from(tabla.querySelectorAll('tr'))
      .map((fila) => Array.from(fila.querySelectorAll('td')).map((c) => c.textContent))
      .filter((celdas) => celdas.length > 0)
    expect(filas).toEqual([
      ['Efectivo', '$ 950,00'],
      ['Tarjeta', '$ 300,00'],
      ['Total digitales', '$ 300,00'],
      ['Total efectivo', '$ 950,00'],
      ['Total general', '$ 1.250,00'],
    ])
  })

  it('mientras las lecturas siguen en vuelo muestra "Cargando detalle…" y ningún dato', async () => {
    mockearRutas({ ...rutasDe(501), '/caja/turnos/501/detalle': () => new Promise(() => undefined) })
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)

    const dialogo = await screen.findByRole('dialog')
    expect(within(dialogo).getByText('Cargando detalle…')).toBeInTheDocument()
    expect(within(dialogo).queryByText('Tickets')).not.toBeInTheDocument()
  })

  it('si falla una lectura muestra el error dentro del modal y no muestra datos parciales', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas(rutasDe(501, { detalle: new ErrorApi(500, 'error', 'No se pudo leer el detalle.') }))
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)

    const dialogo = await screen.findByRole('dialog')
    expect(await within(dialogo).findByText('No se pudo leer el detalle.')).toBeInTheDocument()
    expect(within(dialogo).queryByText('jperez')).not.toBeInTheDocument()
    expect(within(dialogo).queryByText('Cargando detalle…')).not.toBeInTheDocument()
  })

  it('un error que no es de la API muestra el mensaje genérico', async () => {
    mockearRutas(rutasDe(501, { turno: new Error('boom') }))
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)

    expect(await screen.findByText('No se pudo cargar el detalle del cierre.')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el `.catch` del catálogo de medios — sin él, un nombre faltante tumba todo el detalle. */
  it('si falla el catálogo de medios igual muestra el detalle, con "Medio #id" en lugar del nombre', async () => {
    mockearRutas(rutasDe(501), new Error('catálogo caído'))
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)

    const dialogo = await screen.findByRole('dialog')
    await within(dialogo).findByText('jperez')
    expect(within(dialogo).getByText('Medio #1')).toBeInTheDocument()
    expect(within(dialogo).getByText('Medio #2')).toBeInTheDocument()
  })

  /** Misma convención que el cierre en `CierreDeCaja.tsx`: `diferencia` del servidor tal cual (esperado - declarado, positivo = faltante), sin negar ni colorear. */
  it('la diferencia es la del servidor sin cambiar de signo, y el esperado original de un recálculo se muestra bajo el esperado', async () => {
    mockearRutas(
      rutasDe(501, {
        turno: turnoFixture({
          arqueos: [{ idMedioPago: 1, importeEsperado: 1000, importeDeclarado: 1030, diferencia: -30, importeEsperadoOriginal: 1200 }],
        }),
      }),
    )
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)

    const dialogo = await screen.findByRole('dialog')
    const fila = (await within(dialogo).findByText('Efectivo')).closest('tr') as HTMLElement
    const celdas = within(fila).getAllByRole('cell')
    expect(celdas[3]).toHaveTextContent(/^-\$ 30,00$/)
    expect(celdas[1]).toHaveTextContent('Esperado original: $ 1.200,00')
  })

  it('un turno sin medios arqueados lo dice en lugar de mostrar una tabla vacía', async () => {
    mockearRutas(rutasDe(501, { turno: turnoFixture({ arqueos: [] }) }))
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)

    expect(await screen.findByText('Este turno no tuvo actividad: no hay ningún medio arqueado.')).toBeInTheDocument()
  })

  it('un cierre con recálculo administrativo lo marca "Recalculado"', async () => {
    mockearRutas(rutasDe(501, { detalle: detalleFixture({ fechaRecalculo: '2026-09-17T09:00:00Z' }) }))
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)

    const dialogo = await screen.findByRole('dialog')
    await within(dialogo).findByText('jperez')
    expect(within(dialogo).getByText('Recalculado')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la generación de la lectura. Cambiar `idTurno` con la anterior en vuelo no debe dejar que la tardía pise la nueva. */
  it('una respuesta tardía del turno anterior no pisa el detalle del turno actual', async () => {
    let resolverDetalleViejo: (d: DetalleDeTurno) => void = () => undefined
    mockearRutas({
      ...rutasDe(501),
      ...rutasDe(502, { cierre: cierreFixture({ idTurnoCaja: 502, vendedor: 'cajera502' }), turno: turnoFixture({ id: 502 }), detalle: detalleFixture() }),
      '/caja/turnos/501/detalle': () => new Promise<DetalleDeTurno>((resolve) => (resolverDetalleViejo = resolve)),
    })
    const { rerender } = render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/caja/turnos/501/detalle'))

    rerender(<ModalDetalleDeTurno idTurno={502} onCerrar={vi.fn()} />)
    const dialogo = await screen.findByRole('dialog', { name: 'Detalle del cierre #502' })
    await within(dialogo).findByText('cajera502')

    await act(async () => {
      resolverDetalleViejo(detalleFixture({ tickets: [] }))
    })

    expect(within(dialogo).getByText('0007-00000001')).toBeInTheDocument()
    expect(within(dialogo).queryByText('Sin tickets.')).not.toBeInTheDocument()
  })

  it('un rechazo tardío del turno anterior no reemplaza con un error el detalle del turno actual', async () => {
    let rechazarDetalleViejo: (e: Error) => void = () => undefined
    mockearRutas({
      ...rutasDe(501),
      ...rutasDe(502, { cierre: cierreFixture({ idTurnoCaja: 502, vendedor: 'cajera502' }), turno: turnoFixture({ id: 502 }) }),
      '/caja/turnos/501/detalle': () => new Promise<DetalleDeTurno>((_resolve, rechazar) => (rechazarDetalleViejo = rechazar)),
    })
    const { rerender } = render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/caja/turnos/501/detalle'))

    rerender(<ModalDetalleDeTurno idTurno={502} onCerrar={vi.fn()} />)
    const dialogo = await screen.findByRole('dialog', { name: 'Detalle del cierre #502' })
    await within(dialogo).findByText('cajera502')

    await act(async () => {
      rechazarDetalleViejo(new Error('tarde'))
    })

    expect(within(dialogo).getByText('cajera502')).toBeInTheDocument()
    expect(within(dialogo).queryByText('No se pudo cargar el detalle del cierre.')).not.toBeInTheDocument()
  })

  it('"Cerrar" y la × llaman a onCerrar', async () => {
    mockearRutas(rutasDe(501))
    const onCerrar = vi.fn()
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={onCerrar} />)
    await screen.findByText('jperez')

    await userEvent.click(screen.getByRole('button', { name: 'Cerrar' }))
    await userEvent.click(screen.getByRole('button', { name: 'Cerrar detalle' }))

    expect(onCerrar).toHaveBeenCalledTimes(2)
  })

  it('el cuerpo scrollea dentro del modal: usa el modal desplazable', async () => {
    mockearRutas(rutasDe(501))
    render(<ModalDetalleDeTurno idTurno={501} onCerrar={vi.fn()} />)

    const dialogo = await screen.findByRole('dialog')
    expect(dialogo.querySelector('.modal-dialog')).toHaveClass('modal-dialog-scrollable')
  })
})
