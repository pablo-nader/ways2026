import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ShellPos } from './ShellPos'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import { cierreDeTurno, pulsoDeCajon, ticketDeVenta } from '../impresion/plantillas'
import type {
  ArticuloEscaneado,
  ClienteListado,
  ComprobanteEmitido,
  MedioPagoListado,
  PaginaDe,
  ParametroResuelto,
  PuntoVentaListado,
  ResumenDeCierrePorRetiro,
  TurnoResumen,
  UsuarioAutenticado,
  VentaDeTurnoListado,
} from '../api/tipos'
import type { DispositivoActual } from '../api/dispositivos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
    delete: vi.fn(),
  },
  alPerderLaSesion: () => () => {},
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
    get esNoAutenticado() {
      return this.estado === 401
    }
  },
  // stage-pos-venta-offline-web: este árbol monta `Pos.tsx` (vía la ruta `/vender`), que ahora
  // hace `instanceof ErrorDeRed` (`useSincronizacionOffline`) — sin este mock el símbolo importado
  // queda `undefined` bajo este `vi.mock` y ese `instanceof` tira `TypeError`.
  ErrorDeRed: class ErrorDeRedMock extends Error {
    causa: unknown
    constructor(causa: unknown) {
      super('No se pudo contactar al servidor. Revisá tu conexión.')
      this.causa = causa
    }
  },
}))

const abrirConfiguracionMock = vi.fn()
const imprimirMock = vi.fn()
let escritorioMock = false
vi.mock('../impresion/impresora', () => ({
  enEscritorio: () => escritorioMock,
  abrirConfiguracion: (...args: unknown[]) => abrirConfiguracionMock(...args),
  imprimir: (...args: unknown[]) => imprimirMock(...args),
}))

const DISPOSITIVO: DispositivoActual = {
  id: 1,
  nombre: 'Caja 1',
  idPuntoVenta: 7,
  puntoVenta: { numero: 1, nombre: 'Local Centro' },
  empresa: { nombre: 'Almacén Demo' },
}

/** Mismo `ContextoDeImpresion` que arma `ShellPos` internamente a partir de `DISPOSITIVO`/la
 * sesión — se usa para reconstruir los bytes esperados (`reporteZ`/`ticketDeVenta`) y verificar
 * el ORDEN real en que la cola FIFO del shell los manda (Fix judgment-day R2-1). */
const CONTEXTO_DE_IMPRESION_SHELL = {
  empresa: DISPOSITIVO.empresa.nombre,
  puntoVenta: `PV ${DISPOSITIVO.puntoVenta.numero} — ${DISPOSITIVO.puntoVenta.nombre}`,
  cajero: 'jperez',
}

function usuarioFixture(): UsuarioAutenticado {
  return {
    id: 4,
    usuario: 'jperez',
    mail: 'jperez@ways.test',
    rolId: ROL.Vendedor,
    rol: 'Vendedor',
    ultimaConexion: null,
    idTenant: 1,
  }
}

function puntoVentaFixture(): PuntoVentaListado {
  return {
    id: 7,
    idTenant: 1,
    idEmpresa: 3,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Tenant Demo',
    razonSocialEmpresa: 'Empresa Demo',
    modo: 'Web',
  }
}

const consumidorFinal: ClienteListado = {
  id: 1,
  numero: 1,
  nombre: 'Consumidor Final',
  apellido: null,
  razonSocial: null,
  tipoDocumento: null,
  numeroDocumento: null,
  idCondicionFiscal: 1,
  nacimiento: null,
  domicilio: null,
  telefono: null,
  celular: null,
  email: null,
  observaciones: null,
  idListaPrecio: 1,
  limiteCredito: 0,
  creditoIlimitado: true,
  saldo: 0,
  activo: true,
  idEmpresa: null,
  esConsumidorFinal: true,
}

const medioEfectivo: MedioPagoListado = {
  id: 1,
  nombre: 'Efectivo',
  activo: true,
  idEmpresa: null,
  orden: 1,
  comportamiento: 'Efectivo',
  admiteVuelto: true,
  requiereReferencia: false,
  recargoPorcentaje: null,
}

function turnoAbiertoFixture(sobrescribir: Partial<TurnoResumen> = {}): TurnoResumen {
  return {
    id: 501,
    idPuntoVenta: 7,
    idEmpleadoApertura: 4,
    idEmpleadoCierre: null,
    fechaApertura: '2026-09-16T12:00:00Z',
    fechaCierre: null,
    fondoInicial: 500,
    estado: 'Abierto',
    observaciones: null,
    ...sobrescribir,
  }
}

/** `ResumenDeCierrePorRetiro` — respuesta de `POST cierre-por-retiro` (stage-pos-retiros-y-
 * cierre-por-retiro, etapa 5). Se usa para reconstruir los bytes esperados del comprobante de
 * cierre (`cierreDeTurno`) y verificar el ORDEN real en que la cola FIFO del shell los manda. */
function resumenCierreFixture(sobrescribir: Partial<ResumenDeCierrePorRetiro> = {}): ResumenDeCierrePorRetiro {
  return {
    idTurnoCaja: 501,
    puntoVenta: { id: 7, numero: 7, nombre: 'Local Centro' },
    fechaApertura: '2026-09-16T12:00:00-03:00',
    fechaCierre: '2026-09-16T20:00:00-03:00',
    vendedor: 'jperez',
    empleadoCierre: 'jperez',
    fondoInicial: 500,
    ventasPorMedio: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 640 }],
    totalVentas: 640,
    retiros: [],
    totalRetiros: 0,
    ventasEnEfectivoNetas: 640,
    gastosEnEfectivo: 0,
    refuerzos: 0,
    diferencia: 0,
    ...sobrescribir,
  }
}

const RUTA_MOVIMIENTOS = '/caja/turnos/501/movimientos'
const RUTA_CIERRE_POR_RETIRO = '/caja/turnos/501/cierre-por-retiro'

/** Echo mínimo de `MovimientoRegistrado` — alcanza para las auditorías (`AperturaCajon`,
 * `Retiro`) del nuevo flujo: ninguna pantalla lee más que el `tipo`/`importe` que ya mandó. */
function movimientoRegistradoFixture(cuerpo: unknown) {
  return { id: 1, idTurnoCaja: 501, ...(cuerpo as object), idEmpleado: 4, creadoEl: '2026-09-16T18:00:00Z' }
}

function articuloEscaneadoFixture(): ArticuloEscaneado {
  return { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad: 1 }
}

/** Fila de "Ventas del turno" para un `comprobante` ya emitido — mismos datos, la forma que
 * espera `GET /api/ventas/por-turno/{idTurno}` (`ServicioDeVentas.ListarPorTurnoAsync`). */
function ventaDeTurnoDesdeComprobante(comprobante: ComprobanteEmitido): VentaDeTurnoListado {
  return {
    id: comprobante.id,
    numero: comprobante.numero,
    numeroVisible: comprobante.numeroVisible,
    estado: comprobante.estado,
    fecha: comprobante.fecha,
    idCliente: comprobante.idCliente,
    nombreCliente: 'Consumidor Final',
    total: comprobante.total,
    mediosDePago: comprobante.pagos.map((p) => ({ idMedioPago: p.idMedioPago, nombre: medioEfectivo.nombre, importe: p.importe - p.vuelto })),
  }
}

function comprobanteEmitidoFixture(): ComprobanteEmitido {
  return {
    id: 900,
    numero: 1,
    numeroVisible: '0007-00000001',
    estado: 'Emitido',
    fecha: '2026-09-16T15:30:00Z',
    idPuntoVenta: 7,
    idCliente: 1,
    idComprobanteAsociado: null,
    subtotal: 100,
    descuentoTotal: 0,
    total: 100,
    direccionEntrega: null,
    observaciones: null,
    items: [
      {
        orden: 1,
        idArticulo: 1,
        descripcion: 'Coca Cola 1L',
        codigoBarra: '7790001234567',
        idArea: 1,
        idListaPrecio: 1,
        idOferta: null,
        idAlicuotaIva: 1,
        porcentajeIva: 21,
        cantidad: 1,
        precioUnitario: 100,
        descuento: 0,
        total: 100,
        idLote: null,
        codigoLote: null,
        loteVencido: false,
      },
    ],
    pagos: [{ idMedioPago: 1, importe: 100, referencia: null, vuelto: 0 }],
    idPresupuestoOrigen: null,
  }
}

/** Rutas que consume `Pos.tsx` una vez dentro del shell — se agregan al mock general para que la
 * pantalla de venta no se quede en un aviso de error, sin que este archivo tenga que probar de
 * nuevo el checkout (ya cubierto por `Pos.test.tsx`). Cada test de "Cerrar caja" suma encima las
 * rutas de caja que le hacen falta. */
function mockearRutasDePos(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribir?.(ruta)
    if (propia) return propia

    if (ruta === '/clientes') {
      const pagina: PaginaDe<ClienteListado> = { items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 }
      return Promise.resolve(pagina)
    }
    if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
    if (ruta.startsWith('/parametros/tolerancia_pago')) return Promise.resolve<ParametroResuelto>({ clave: 'tolerancia_pago', valor: '10' })
    if (ruta.startsWith('/articulos/escaneo?entrada=')) return Promise.resolve<ArticuloEscaneado>(articuloEscaneadoFixture())
    // stage-pos-turno-y-foco: turno ABIERTO por defecto — `Pos.tsx` lo consulta apenas monta
    // (ya no es el header del shell quien lo resuelve al hacer click en "Cerrar caja"); los tests
    // dedicados al turno cerrado sobrescriben esta ruta explícitamente.
    if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

/** POST que consume una venta libre de `Pos.tsx` (resolución de precio + checkout) — separado de
 * `mockearRutasDePos` porque el aviso de impresión del shell (Fix judgment-day W1/W2) es lo único
 * que estos tests necesitan de una venta, el checkout en sí ya está cubierto por `Pos.test.tsx`. */
function mockearVentaExitosa(comprobante: ComprobanteEmitido = comprobanteEmitidoFixture()) {
  apiPostMock.mockImplementation((ruta: string) => {
    if (ruta === '/ofertas/resolver') {
      return Promise.resolve([{ idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] }])
    }
    if (ruta === '/ventas') return Promise.resolve(comprobante)
    return Promise.resolve(undefined)
  })
}

/** Deja una venta completa emitida en pantalla (`/vender`, medio Efectivo por el total) — punto de
 * partida de los tests de impresión del ticket de venta (Fix judgment-day W1/W2). */
async function completarVenta() {
  await screen.findByRole('option', { name: /Consumidor Final/ })
  await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
  await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
  await screen.findByText('Coca Cola 1L')
  await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
  const importe = await screen.findByLabelText('Importe de Efectivo (fila 1)')
  await userEvent.type(importe, '100')
  await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
  await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
  await screen.findByText(/Venta 0007-00000001/)
}

function renderShell() {
  return render(
    <MemoryRouter initialEntries={['/vender']}>
      <ShellPos dispositivo={DISPOSITIVO} usuario={usuarioFixture()} puntoVenta={puntoVentaFixture()} alCerrarSesion={vi.fn()} />
    </MemoryRouter>,
  )
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPostMock.mockResolvedValue(undefined)
  abrirConfiguracionMock.mockReset()
  imprimirMock.mockReset()
  imprimirMock.mockResolvedValue({ ok: true })
  escritorioMock = false
  mockearRutasDePos()
})

describe('ShellPos', () => {
  it('el header muestra la empresa, el PV y el cajero del dispositivo/sesión', async () => {
    renderShell()
    expect(await screen.findByText('Almacén Demo')).toBeInTheDocument()
    expect(screen.getByText(/PV 1 — Local Centro · jperez/)).toBeInTheDocument()
  })

  it('sin escritorio (enEscritorio false), no aparece "Configuración"', async () => {
    renderShell()
    await screen.findByText('Almacén Demo')
    expect(screen.queryByRole('button', { name: 'Configuración' })).not.toBeInTheDocument()
  })

  it('con escritorio, "Configuración" invoca abrirConfiguracion', async () => {
    escritorioMock = true
    renderShell()
    await userEvent.click(await screen.findByRole('button', { name: 'Configuración' }))
    expect(abrirConfiguracionMock).toHaveBeenCalledTimes(1)
  })

  it('"Gastos" navega a la pantalla de gastos del turno', async () => {
    mockearRutasDePos((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
      if (ruta === '/proveedores/opciones') return Promise.resolve([])
      if (ruta === '/caja/turnos/501/detalle') {
        return Promise.resolve({
          resumen: {
            idTurnoCaja: 501,
            idMedioAncla: 1,
            medios: [],
            cantidadTickets: 0,
            primerTicket: null,
            ultimoTicket: null,
            ingresosPorArea: [],
            egresos: { porCategoria: [], porArea: [], retiros: 0 },
          },
          tickets: [],
          gastos: [],
        })
      }
      return undefined
    })
    renderShell()

    await userEvent.click(await screen.findByRole('link', { name: 'Gastos' }))

    expect(await screen.findByText('Gastos del turno')).toBeInTheDocument()
    expect(await screen.findByText('Este turno todavía no tiene gastos.')).toBeInTheDocument()
  })

  it('"Cerrar sesión" llama a POST /auth/logout y avisa alCerrarSesion', async () => {
    const alCerrarSesion = vi.fn()
    render(
      <MemoryRouter initialEntries={['/vender']}>
        <ShellPos dispositivo={DISPOSITIVO} usuario={usuarioFixture()} puntoVenta={puntoVentaFixture()} alCerrarSesion={alCerrarSesion} />
      </MemoryRouter>,
    )

    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar sesión' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/auth/logout'))
    await waitFor(() => expect(alCerrarSesion).toHaveBeenCalledTimes(1))
  })
})

describe('ShellPos — los controles de caja viven en el header, portaleados desde Pos.tsx (stage-pos-caja-en-cabecera)', () => {
  /**
   * stage-pos-caja-en-cabecera: la acción de caja se mueve del panel lateral de `Pos.tsx` al
   * header oscuro del shell — `ShellPos` reserva un contenedor vacío ahí y `Pos.tsx` portalea sus
   * controles (badge + "Abrir caja"/"Cerrar caja"/"Retirar", lógica de turno intacta) vía
   * `RanuraHeaderPosContext` (ver el doc-comment de `ShellPos.tsx`). Este describe reemplaza al
   * viejo "'Cerrar caja' vive en la pantalla de venta, no en el header" (etapa anterior).
   */
  it('con un turno abierto, el header (banner) muestra "Caja abierta" y "Cerrar caja"', async () => {
    mockearRutasDePos((ruta) => (ruta === '/caja/turnos/abierto?idPuntoVenta=7' ? Promise.resolve<TurnoResumen>(turnoAbiertoFixture()) : undefined))
    renderShell()

    // `Box.tsx` también renderiza su propio `<header>` (título de cada caja), así que el header
    // del shell se ubica por contenido (el nombre de la empresa, único ahí) en vez de por rol —
    // con varios `<header>` en pantalla, `getByRole('banner')` es ambiguo (`dom-accessibility-api`
    // no aplica la regla contextual de "banner solo fuera de main/article/etc.").
    const banner = (await screen.findByText('Almacén Demo')).closest('header') as HTMLElement
    expect(await within(banner).findByText('Caja abierta')).toBeInTheDocument()
    expect(within(banner).getByRole('button', { name: 'Cerrar caja' })).toBeInTheDocument()
    expect(within(banner).getByRole('button', { name: 'Retirar' })).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledWith('/caja/turnos/abierto?idPuntoVenta=7')
  })

  it('sin turno abierto, el header (banner) muestra "Caja cerrada" y "Abrir caja" (nunca "Cerrar caja" ni "Retirar"), y la pantalla de venta sigue con su propio aviso', async () => {
    mockearRutasDePos((ruta) => (ruta === '/caja/turnos/abierto?idPuntoVenta=7' ? Promise.resolve(null) : undefined))
    renderShell()

    const banner = (await screen.findByText('Almacén Demo')).closest('header') as HTMLElement
    expect(await within(banner).findByText('Caja cerrada')).toBeInTheDocument()
    expect(within(banner).getByRole('button', { name: 'Abrir caja' })).toBeInTheDocument()
    expect(within(banner).queryByRole('button', { name: 'Cerrar caja' })).not.toBeInTheDocument()
    expect(within(banner).queryByRole('button', { name: 'Retirar' })).not.toBeInTheDocument()
    expect(screen.getByText('Caja cerrada: abrí la caja para vender.')).toBeInTheDocument()
  })

  it('un 409 turno_no_abierto durante el cobro apaga "Caja abierta"/"Cerrar caja"/"Retirar" del header mientras el gate está arriba (judgment-day JD-E2-1)', async () => {
    mockearRutasDePos()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        return Promise.resolve([{ idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] }])
      }
      if (ruta === '/ventas') {
        return Promise.reject(new ErrorApi(409, 'turno_no_abierto', 'No hay un turno abierto en este punto de venta.'))
      }
      return Promise.resolve(undefined)
    })
    renderShell()

    const banner = (await screen.findByText('Almacén Demo')).closest('header') as HTMLElement
    await within(banner).findByText('Caja abierta')

    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    const importe = await screen.findByLabelText('Importe de Efectivo (fila 1)')
    await userEvent.type(importe, '100')
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    await screen.findByText('No hay un turno abierto')
    // El 409 es la confirmación más autoritativa de que el turno está cerrado — el header nunca
    // puede seguir mostrando "Caja abierta"/"Cerrar caja"/"Retirar" habilitados mientras el propio
    // gate dice que no hay turno abierto.
    expect(within(banner).queryByText('Caja abierta')).not.toBeInTheDocument()
    expect(within(banner).queryByRole('button', { name: 'Cerrar caja' })).not.toBeInTheDocument()
    expect(within(banner).queryByRole('button', { name: 'Retirar' })).not.toBeInTheDocument()
    expect(within(banner).getByText('Caja cerrada')).toBeInTheDocument()
  })

  /**
   * stage-pos-retiros-y-cierre-por-retiro (etapa 5): "Cerrar caja" del escritorio reemplaza a la
   * navegación clásica a `CierreDeCaja` — el cierre se resuelve íntegro en esta misma pantalla
   * (dos modales + dos trabajos de impresión), la pantalla NUNCA navega.
   */
  it('cerrar caja por retiro: header pasa a "Caja cerrada", nunca navega, imprime el pulso de cajón y el comprobante de cierre en ese orden', async () => {
    const resumen = resumenCierreFixture()
    mockearRutasDePos()
    apiPostMock.mockImplementation((ruta: string, cuerpo?: unknown) => {
      if (ruta === RUTA_MOVIMIENTOS) return Promise.resolve(movimientoRegistradoFixture(cuerpo))
      if (ruta === RUTA_CIERRE_POR_RETIRO) return Promise.resolve(resumen)
      return Promise.resolve(undefined)
    })
    renderShell()

    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))
    const modalConfirmar = within(await screen.findByRole('dialog', { name: '¿Querés cerrar el turno?' }))
    await userEvent.click(modalConfirmar.getByRole('button', { name: 'Sí' }))

    const modalMonto = within(await screen.findByRole('dialog', { name: 'Efectivo a retirar' }))
    await userEvent.type(modalMonto.getByLabelText('Monto'), '0')
    await waitFor(() => expect(modalMonto.getByRole('button', { name: 'Confirmar' })).toBeEnabled())
    await userEvent.click(modalMonto.getByRole('button', { name: 'Confirmar' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(await screen.findByText('Caja cerrada')).toBeInTheDocument()
    // Nunca navegó — el link "Vender" del header (siempre presente en el shell) sigue siendo la
    // única prueba de que seguimos ahí; nunca aparece nada de la vieja pantalla de cierre clásica.
    expect(screen.getByRole('link', { name: 'Vender' })).toBeInTheDocument()
    expect(screen.queryByText(/Cierre de turno/)).not.toBeInTheDocument()

    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(2))
    expect(imprimirMock.mock.calls[0][0]).toEqual(pulsoDeCajon())
    expect(imprimirMock.mock.calls[1][0]).toEqual(cierreDeTurno(resumen, CONTEXTO_DE_IMPRESION_SHELL))
  })
})

describe('ShellPos — aviso persistente de impresión (Fix judgment-day W1/W2: el shell es el único dueño de la impresión)', () => {
  it('si falla la impresión del ticket de venta, muestra el aviso con "Reimprimir" — reimprimir reenvía los mismos bytes y el aviso se limpia solo al tener éxito', async () => {
    mockearRutasDePos()
    mockearVentaExitosa()
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'impresora desconectada' })
    renderShell()

    await completarVenta()

    expect(await screen.findByText('No se pudo imprimir el ticket de venta: impresora desconectada')).toBeInTheDocument()
    expect(imprimirMock).toHaveBeenCalledTimes(1)
    const bytesOriginales = imprimirMock.mock.calls[0][0]

    imprimirMock.mockResolvedValueOnce({ ok: true })
    await userEvent.click(screen.getByRole('button', { name: 'Reimprimir' }))

    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(2))
    expect(imprimirMock.mock.calls[1][0]).toEqual(bytesOriginales)
    await waitFor(() => expect(screen.queryByText(/No se pudo imprimir/)).not.toBeInTheDocument())
  })

  it('"Reimprimir" con reentrancia: dos clicks en el mismo tick disparan un único reintento (react-async-state regla 9)', async () => {
    mockearRutasDePos()
    mockearVentaExitosa()
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'impresora desconectada' })
    renderShell()
    await completarVenta()
    await screen.findByText('No se pudo imprimir el ticket de venta: impresora desconectada')

    let resolverReimpresion: (r: { ok: true }) => void = () => {}
    const pendiente = new Promise<{ ok: true }>((resolve) => {
      resolverReimpresion = resolve
    })
    imprimirMock.mockReturnValueOnce(pendiente)

    const boton = screen.getByRole('button', { name: 'Reimprimir' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    await act(async () => {
      resolverReimpresion({ ok: true })
      await Promise.resolve()
    })

    // 1 auto-impresión fallida + 1 reintento (el segundo click del mismo tick se gateó).
    expect(imprimirMock).toHaveBeenCalledTimes(2)
  })

  it('camino feliz: una venta que imprime bien nunca muestra ningún aviso de impresión', async () => {
    mockearRutasDePos()
    mockearVentaExitosa()
    renderShell()

    await completarVenta()

    expect(screen.queryByText(/No se pudo imprimir/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el aviso de impresión vive fuera de `<Routes>` en `ShellPos`, así que
   * sobrevive a cualquier navegación del shell (ya no hay una navegación automática al cerrar
   * caja — stage-pos-retiros-y-cierre-por-retiro, etapa 5 — así que esta prueba usa una
   * navegación manual a "Ventas del turno" como vehículo). Evidencia de mutación
   * (mutation-proof-tests regla 2): moviendo el bloque `{avisosDeImpresion.map(...)}` DENTRO del
   * `<main><Routes>` (así se desmonta al navegar a otra ruta), este test falla — no encuentra el
   * texto del aviso tras navegar. Restaurado el bloque a su lugar actual (fuera de `<Routes>`),
   * vuelve a verde.
   */
  it('si falla la impresión del comprobante de cierre, el aviso sobrevive a navegar a otra pantalla del shell', async () => {
    const resumen = resumenCierreFixture()
    mockearRutasDePos()
    apiPostMock.mockImplementation((ruta: string, cuerpo?: unknown) => {
      if (ruta === RUTA_MOVIMIENTOS) return Promise.resolve(movimientoRegistradoFixture(cuerpo))
      if (ruta === RUTA_CIERRE_POR_RETIRO) return Promise.resolve(resumen)
      return Promise.resolve(undefined)
    })
    imprimirMock.mockResolvedValueOnce({ ok: true })
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    renderShell()

    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))
    const modalConfirmar = within(await screen.findByRole('dialog', { name: '¿Querés cerrar el turno?' }))
    await userEvent.click(modalConfirmar.getByRole('button', { name: 'Sí' }))
    const modalMonto = within(await screen.findByRole('dialog', { name: 'Efectivo a retirar' }))
    await userEvent.type(modalMonto.getByLabelText('Monto'), '0')
    await waitFor(() => expect(modalMonto.getByRole('button', { name: 'Confirmar' })).toBeEnabled())
    await userEvent.click(modalMonto.getByRole('button', { name: 'Confirmar' }))

    expect(await screen.findByText('No se pudo imprimir el comprobante de cierre de turno: sin papel')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('link', { name: 'Ventas del turno' }))
    expect(screen.getByText('No se pudo imprimir el comprobante de cierre de turno: sin papel')).toBeInTheDocument()
  })

  it('una venta y un cierre por retiro exitosos imprimen cada uno de sus trabajos, sin duplicar', async () => {
    const resumen = resumenCierreFixture()
    mockearRutasDePos()
    apiPostMock.mockImplementation((ruta: string, cuerpo?: unknown) => {
      if (ruta === RUTA_MOVIMIENTOS) return Promise.resolve(movimientoRegistradoFixture(cuerpo))
      if (ruta === RUTA_CIERRE_POR_RETIRO) return Promise.resolve(resumen)
      if (ruta === '/ofertas/resolver') {
        return Promise.resolve([{ idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] }])
      }
      if (ruta === '/ventas') return Promise.resolve(comprobanteEmitidoFixture())
      return Promise.resolve(undefined)
    })
    renderShell()

    await completarVenta()
    expect(imprimirMock).toHaveBeenCalledTimes(1)

    // El cobro exitoso muestra el modal "Venta finalizada" encima de la pantalla de venta (ya
    // reseteada) — mientras está abierto, "Cerrar caja" queda inerte, así que hace falta
    // cerrarlo con "Aceptar" primero (stage-pos-modales-de-cobro).
    await userEvent.click(screen.getByRole('button', { name: 'Aceptar' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))
    const modalConfirmar = within(await screen.findByRole('dialog', { name: '¿Querés cerrar el turno?' }))
    await userEvent.click(modalConfirmar.getByRole('button', { name: 'Sí' }))
    const modalMonto = within(await screen.findByRole('dialog', { name: 'Efectivo a retirar' }))
    await userEvent.type(modalMonto.getByLabelText('Monto'), '0')
    await waitFor(() => expect(modalMonto.getByRole('button', { name: 'Confirmar' })).toBeEnabled())
    await userEvent.click(modalMonto.getByRole('button', { name: 'Confirmar' }))

    await screen.findByText('Caja cerrada')
    // 1 ticket de venta + pulso de cajón + comprobante de cierre = 3, ninguno duplicado.
    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(3))
  })
})

/** Rutas + POSTs de un flujo de caja de escritorio (Retirar o Cerrar caja por retiro) exitoso, con
 * las de una venta libre sumadas encima — punto de partida de los tests de la cola FIFO (Fix
 * judgment-day ronda 2: R2-1/R2-2, extendido en stage-pos-retiros-y-cierre-por-retiro etapa 5),
 * donde los trabajos de un cierre y un ticket de venta pueden convivir en la cola del mismo
 * shell. */
function mockearFlujoDeCajaDeEscritorio() {
  mockearRutasDePos()
  apiPostMock.mockImplementation((ruta: string, cuerpo?: unknown) => {
    if (ruta === RUTA_MOVIMIENTOS) return Promise.resolve(movimientoRegistradoFixture(cuerpo))
    if (ruta === RUTA_CIERRE_POR_RETIRO) return Promise.resolve(resumenCierreFixture())
    if (ruta === '/ofertas/resolver') {
      return Promise.resolve([{ idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] }])
    }
    if (ruta === '/ventas') return Promise.resolve(comprobanteEmitidoFixture())
    return Promise.resolve(undefined)
  })
}

/** Maneja el flujo "Retirar" completo (dos modales) — deja el turno abierto, a diferencia de
 * `cerrarCajaPorRetiro`. */
async function retirarHastaTicket(monto: string) {
  await userEvent.click(await screen.findByRole('button', { name: 'Retirar' }))
  const modalApertura = within(await screen.findByRole('dialog', { name: '¿Querés retirar efectivo?' }))
  await userEvent.click(modalApertura.getByRole('button', { name: 'Sí' }))
  const modalMonto = within(await screen.findByRole('dialog', { name: 'Monto retirado' }))
  await userEvent.type(modalMonto.getByLabelText('Monto'), monto)
  await waitFor(() => expect(modalMonto.getByRole('button', { name: 'Confirmar' })).toBeEnabled())
  await userEvent.click(modalMonto.getByRole('button', { name: 'Confirmar' }))
}

/** Maneja el flujo "Cerrar caja" por retiro completo (dos modales) hasta que el header muestra
 * "Caja cerrada". */
async function cerrarCajaPorRetiro(monto = '0') {
  await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))
  const modalConfirmar = within(await screen.findByRole('dialog', { name: '¿Querés cerrar el turno?' }))
  await userEvent.click(modalConfirmar.getByRole('button', { name: 'Sí' }))
  const modalMonto = within(await screen.findByRole('dialog', { name: 'Efectivo a retirar' }))
  await userEvent.type(modalMonto.getByLabelText('Monto'), monto)
  await waitFor(() => expect(modalMonto.getByRole('button', { name: 'Confirmar' })).toBeEnabled())
  await userEvent.click(modalMonto.getByRole('button', { name: 'Confirmar' }))
  await screen.findByText('Caja cerrada')
}

describe('ShellPos — cola FIFO de impresión (Fix judgment-day ronda 2: R2-1 nunca descarta un trabajo, R2-2 un aviso por trabajo)', () => {
  /**
   * Cláusula bajo prueba: `encolarImpresion` siempre empuja a `colaDeImpresionRef` — nunca hace
   * `if (procesandoColaRef.current) return` antes de encolar. Evidencia de mutación
   * (mutation-proof-tests regla 2): reintroduciendo ese `return` temprano (la forma exacta del
   * defecto de la ronda 1, R2-1: un `imprimiendoRef` compartido descartaba en silencio el trabajo
   * que llegaba mientras otro imprimía), este test falla — el ticket de venta nunca se manda.
   * Restaurado, vuelve a verde.
   *
   * Vehículo (stage-pos-retiros-y-cierre-por-retiro, etapa 5): el flujo "Retirar" (no "Cerrar
   * caja") — deja el turno abierto, así que la venta interleaved sigue siendo posible.
   */
  it('el ticket de retiro pendiente y una venta nueva imprimen todos los trabajos, en orden, sin descartar ninguno', async () => {
    mockearFlujoDeCajaDeEscritorio()
    let resolverTicketRetiro: (r: { ok: true }) => void = () => {}
    const pendiente = new Promise<{ ok: true }>((resolve) => {
      resolverTicketRetiro = resolve
    })
    imprimirMock.mockResolvedValueOnce({ ok: true }) // el pulso de cajón
    imprimirMock.mockImplementationOnce(() => pendiente) // el ticket de retiro, pendiente

    renderShell()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await retirarHastaTicket('5000')

    // El pulso de cajón ya se mandó; el ticket de retiro es el segundo trabajo, en vuelo.
    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(2))

    await completarVenta()

    // La venta encoló su propio trabajo: mientras el ticket de retiro siga sin resolver, la
    // impresora (recurso serial) no lo manda todavía — pero JAMÁS lo descarta.
    expect(imprimirMock).toHaveBeenCalledTimes(2)

    resolverTicketRetiro({ ok: true })
    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(3))

    expect(imprimirMock.mock.calls[0][0]).toEqual(pulsoDeCajon())
    expect(imprimirMock.mock.calls[2][0]).toEqual(
      ticketDeVenta(comprobanteEmitidoFixture(), CONTEXTO_DE_IMPRESION_SHELL, [medioEfectivo]),
    )
    expect(screen.queryByText(/No se pudo imprimir el ticket de venta/)).not.toBeInTheDocument()
  })

  it('dos trabajos que fallan (pulso de cajón y comprobante de cierre) muestran dos avisos independientes, cada uno con su propio "Reimprimir" (regla 14)', async () => {
    mockearFlujoDeCajaDeEscritorio()
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'impresora desconectada' })
    renderShell()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await cerrarCajaPorRetiro()

    await screen.findByText('No se pudo imprimir la apertura de cajón: sin papel')
    await screen.findByText('No se pudo imprimir el comprobante de cierre de turno: impresora desconectada')
    expect(screen.getAllByRole('button', { name: 'Reimprimir' })).toHaveLength(2)
  })

  it('reimprimir con éxito borra solo su propio aviso — el otro sigue visible', async () => {
    mockearFlujoDeCajaDeEscritorio()
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'impresora desconectada' })
    renderShell()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await cerrarCajaPorRetiro()
    await screen.findByText('No se pudo imprimir la apertura de cajón: sin papel')
    await screen.findByText('No se pudo imprimir el comprobante de cierre de turno: impresora desconectada')

    const avisoDeApertura = screen.getByText(/No se pudo imprimir la apertura de cajón/).closest('[role="alert"]') as HTMLElement
    imprimirMock.mockResolvedValueOnce({ ok: true })
    await userEvent.click(within(avisoDeApertura).getByRole('button', { name: 'Reimprimir' }))

    await waitFor(() => expect(screen.queryByText(/No se pudo imprimir la apertura de cajón/)).not.toBeInTheDocument())
    expect(screen.getByText('No se pudo imprimir el comprobante de cierre de turno: impresora desconectada')).toBeInTheDocument()
  })

  it('doble click en "Reimprimir" de UN aviso dispara un único reintento (gating por aviso, no global)', async () => {
    mockearFlujoDeCajaDeEscritorio()
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    renderShell()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await cerrarCajaPorRetiro()
    await screen.findByText('No se pudo imprimir la apertura de cajón: sin papel')
    expect(imprimirMock).toHaveBeenCalledTimes(2)

    let resolverReintento: (r: { ok: true }) => void = () => {}
    const pendiente = new Promise<{ ok: true }>((resolve) => {
      resolverReintento = resolve
    })
    imprimirMock.mockReturnValueOnce(pendiente)

    const boton = screen.getByRole('button', { name: 'Reimprimir' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    await act(async () => {
      resolverReintento({ ok: true })
      await Promise.resolve()
    })

    // 1 auto-impresión fallida (apertura) + el comprobante de cierre (ok) + 1 reintento (el
    // segundo click del mismo tick se gateó) = 3, nunca 4.
    expect(imprimirMock).toHaveBeenCalledTimes(3)
  })
})

describe('ShellPos — "Reimprimir" de Ventas del turno pasa por la MISMA cola FIFO que el ticket de venta', () => {
  it('encola el ticket con la marca de reimpresión y la descripción "la reimpresión del ticket <numero>"', async () => {
    const comprobante = comprobanteEmitidoFixture()
    mockearRutasDePos((ruta) => {
      if (ruta.startsWith('/ventas/por-turno/')) return Promise.resolve<VentaDeTurnoListado[]>([ventaDeTurnoDesdeComprobante(comprobante)])
      if (ruta === `/ventas/${comprobante.id}`) return Promise.resolve<ComprobanteEmitido>(comprobante)
      return undefined
    })
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    renderShell()

    await userEvent.click(await screen.findByRole('link', { name: 'Ventas del turno' }))
    await screen.findByText('0007-00000001')
    await userEvent.click(screen.getByRole('button', { name: 'Reimprimir' }))

    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(1))
    expect(imprimirMock).toHaveBeenCalledWith(
      ticketDeVenta(comprobante, CONTEXTO_DE_IMPRESION_SHELL, [medioEfectivo], { reimpresion: true }),
    )
    expect(await screen.findByText('No se pudo imprimir la reimpresión del ticket 0007-00000001: sin papel')).toBeInTheDocument()
  })
})
