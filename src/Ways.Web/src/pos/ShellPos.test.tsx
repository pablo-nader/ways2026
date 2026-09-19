import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ShellPos } from './ShellPos'
import { ROL } from '../api/tipos'
import { reporteZ, ticketDeVenta } from '../impresion/plantillas'
import type {
  ArticuloEscaneado,
  ClienteListado,
  ComprobanteEmitido,
  MedioPagoListado,
  PaginaDe,
  ParametroResuelto,
  PuntoVentaListado,
  ResumenDeTurno,
  TurnoConArqueos,
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

function resumenFixture(): ResumenDeTurno {
  return {
    idTurnoCaja: 501,
    idMedioAncla: 1,
    medios: [{ idMedioPago: 1, importeEsperado: 640 }],
    cantidadTickets: 0,
    primerTicket: null,
    ultimoTicket: null,
    ingresosPorArea: [],
    egresos: { porCategoria: [], porArea: [], retiros: 0 },
  }
}

function turnoConArqueosFixture(): TurnoConArqueos {
  return {
    ...turnoAbiertoFixture({ estado: 'Cerrado', fechaCierre: '2026-09-16T20:00:00Z', idEmpleadoCierre: 4 }),
    arqueos: [{ idMedioPago: 1, importeEsperado: 640, importeDeclarado: 640, diferencia: 0 }],
  }
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

describe('ShellPos — "Cerrar caja" vive en la pantalla de venta, no en el header (stage-pos-turno-y-foco)', () => {
  /**
   * stage-pos-turno-y-foco: el header YA NO tiene su propio botón "Cerrar caja" (ver el
   * doc-comment de `ShellPos.tsx`) — la propia pantalla `/vender` (`Pos.tsx`) muestra el estado
   * del turno y ofrece la acción, con el `idTurno` que ya resolvió al montar (nunca un GET
   * disparado por el click). Este describe reemplaza al viejo "'Cerrar caja' resuelve el turno
   * abierto antes de navegar", que probaba un mecanismo (`irACerrarCaja` del shell) que ya no
   * existe.
   */
  it('con un turno abierto, la pantalla de venta ofrece "Cerrar caja" y navega a CierreDeCaja con su idTurno (nunca sin ?idTurno=)', async () => {
    mockearRutasDePos((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      return undefined
    })
    renderShell()

    expect(await screen.findByText('Turno abierto')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))

    expect(await screen.findByText('Cierre de turno #501')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledWith('/caja/turnos/abierto?idPuntoVenta=7')
  })

  it('sin turno abierto, el header no muestra "Cerrar caja" y la pantalla de venta ofrece "Abrir turno" en su lugar', async () => {
    mockearRutasDePos((ruta) => (ruta === '/caja/turnos/abierto?idPuntoVenta=7' ? Promise.resolve(null) : undefined))
    renderShell()

    expect(await screen.findByText('Turno cerrado')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Abrir turno' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cerrar caja' })).not.toBeInTheDocument()
    expect(screen.getByText('Turno cerrado: abrí un turno para vender.')).toBeInTheDocument()
  })

  it('un cierre exitoso navega a la Caja Z (auto-impresión incluida) e imprime exactamente una vez', async () => {
    const conArqueos = turnoConArqueosFixture()
    mockearRutasDePos((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      if (ruta === '/caja/turnos/501/detalle') {
        return Promise.resolve({
          resumen: resumenFixture(),
          tickets: [],
          gastos: [],
        })
      }
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/caja/turnos/501/cierre' ? Promise.resolve<TurnoConArqueos>(conArqueos) : Promise.resolve(undefined),
    )
    renderShell()

    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))
    await screen.findByText('Cierre de turno #501')

    await userEvent.type(await screen.findByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    expect(await screen.findByText('Caja Z — turno #501')).toBeInTheDocument()
    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(1))
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
   * sobrevive a la navegación que `alCerrarExitosamente` dispara hacia la Caja Z. Evidencia de
   * mutación (mutation-proof-tests regla 2): moviendo el bloque `{avisoImpresion && (...)}` DENTRO
   * del `element` de la ruta `/cerrar-caja` (así se desmonta al navegar a `/caja/turnos/:id/z`),
   * este test falla — no encuentra el texto del aviso en la pantalla de Caja Z. Restaurado el
   * bloque a su lugar actual (fuera de `<Routes>`), vuelve a verde.
   */
  it('si falla la impresión del reporte Z tras cerrar caja, el aviso queda visible en la pantalla de Caja Z (sobrevive a la navegación)', async () => {
    const conArqueos = turnoConArqueosFixture()
    mockearRutasDePos((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      if (ruta === '/caja/turnos/501/detalle') return Promise.resolve({ resumen: resumenFixture(), tickets: [], gastos: [] })
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/caja/turnos/501/cierre' ? Promise.resolve<TurnoConArqueos>(conArqueos) : Promise.resolve(undefined),
    )
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    renderShell()

    await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))
    await screen.findByText('Cierre de turno #501')
    await userEvent.type(await screen.findByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    expect(await screen.findByText('Caja Z — turno #501')).toBeInTheDocument()
    expect(await screen.findByText('No se pudo imprimir el reporte Z: sin papel')).toBeInTheDocument()
    // No es el aviso propio de `CierreDeCaja` (ya no auto-imprime cuando el shell provee
    // `alCerrarExitosamente` — evita la doble impresión, ver `ShellPos.tsx`).
    expect(screen.queryByText('No se pudo imprimir: sin papel')).not.toBeInTheDocument()
  })

  it('un cierre y una venta exitosos imprimen exactamente una vez cada uno (sin duplicar)', async () => {
    mockearRutasDePos((ruta) => {
      if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
      if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
      if (ruta === '/caja/turnos/501/detalle') return Promise.resolve({ resumen: resumenFixture(), tickets: [], gastos: [] })
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/cierre') return Promise.resolve<TurnoConArqueos>(turnoConArqueosFixture())
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
    await screen.findByText('Cierre de turno #501')
    await userEvent.type(await screen.findByLabelText('Declarado de Efectivo'), '640')
    await userEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))

    await screen.findByText('Caja Z — turno #501')
    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(2))
  })
})

/** Rutas + POSTs de un cierre de caja exitoso, con las de una venta libre sumadas encima — punto
 * de partida de los tests de la cola FIFO (Fix judgment-day ronda 2: R2-1/R2-2), donde un reporte
 * Z y un ticket de venta pueden convivir en la cola del mismo shell. */
function mockearFlujoDeCierre() {
  const conArqueos = turnoConArqueosFixture()
  mockearRutasDePos((ruta) => {
    if (ruta === '/caja/turnos/abierto?idPuntoVenta=7') return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
    if (ruta === '/caja/turnos/501/resumen') return Promise.resolve<ResumenDeTurno>(resumenFixture())
    if (ruta === '/caja/turnos/501/detalle') return Promise.resolve({ resumen: resumenFixture(), tickets: [], gastos: [] })
    return undefined
  })
  apiPostMock.mockImplementation((ruta: string) => {
    if (ruta === '/caja/turnos/501/cierre') return Promise.resolve<TurnoConArqueos>(conArqueos)
    if (ruta === '/ofertas/resolver') {
      return Promise.resolve([{ idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] }])
    }
    if (ruta === '/ventas') return Promise.resolve(comprobanteEmitidoFixture())
    return Promise.resolve(undefined)
  })
  return conArqueos
}

async function cerrarCajaHastaZ() {
  await userEvent.click(await screen.findByRole('button', { name: 'Cerrar caja' }))
  await screen.findByText('Cierre de turno #501')
  await userEvent.type(await screen.findByLabelText('Declarado de Efectivo'), '640')
  await userEvent.click(screen.getByRole('checkbox'))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Finalizar cierre' })).toBeEnabled())
  await userEvent.click(screen.getByRole('button', { name: 'Finalizar cierre' }))
  await screen.findByText('Caja Z — turno #501')
}

describe('ShellPos — cola FIFO de impresión (Fix judgment-day ronda 2: R2-1 nunca descarta un trabajo, R2-2 un aviso por trabajo)', () => {
  /**
   * Cláusula bajo prueba: `encolarImpresion` siempre empuja a `colaDeImpresionRef` — nunca hace
   * `if (procesandoColaRef.current) return` antes de encolar. Evidencia de mutación
   * (mutation-proof-tests regla 2): reintroduciendo ese `return` temprano (la forma exacta del
   * defecto de la ronda 1, R2-1: un `imprimiendoRef` compartido descartaba en silencio el trabajo
   * que llegaba mientras otro imprimía), este test falla — el ticket de venta nunca se manda.
   * Restaurado, vuelve a verde.
   */
  it('un reporte Z pendiente y una venta nueva imprimen los dos, en orden, exactamente una vez cada uno (nunca se descarta un trabajo)', async () => {
    mockearFlujoDeCierre()
    let resolverZ: (r: { ok: true }) => void = () => {}
    const zPendiente = new Promise<{ ok: true }>((resolve) => {
      resolverZ = resolve
    })
    imprimirMock.mockImplementationOnce(() => zPendiente)

    renderShell()
    await cerrarCajaHastaZ()

    // El reporte Z es el primer (y único) trabajo en vuelo.
    expect(imprimirMock).toHaveBeenCalledTimes(1)

    await userEvent.click(screen.getByRole('link', { name: 'Vender' }))
    await completarVenta()

    // La venta encoló su propio trabajo: mientras el reporte Z siga sin resolver, la impresora
    // (recurso serial) no lo manda todavía — pero JAMÁS lo descarta.
    expect(imprimirMock).toHaveBeenCalledTimes(1)

    resolverZ({ ok: true })
    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(2))

    expect(imprimirMock.mock.calls[0][0]).toEqual(reporteZ(turnoConArqueosFixture(), CONTEXTO_DE_IMPRESION_SHELL))
    expect(imprimirMock.mock.calls[1][0]).toEqual(
      ticketDeVenta(comprobanteEmitidoFixture(), CONTEXTO_DE_IMPRESION_SHELL, [medioEfectivo]),
    )
    expect(screen.queryByText(/No se pudo imprimir/)).not.toBeInTheDocument()
  })

  it('un reporte Z fallido seguido de una venta que imprime bien: el aviso del reporte Z sigue visible (un slot por trabajo, regla 14)', async () => {
    mockearFlujoDeCierre()
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    renderShell()
    await cerrarCajaHastaZ()

    expect(await screen.findByText('No se pudo imprimir el reporte Z: sin papel')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('link', { name: 'Vender' }))
    await completarVenta()

    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(2))
    expect(screen.getByText('No se pudo imprimir el reporte Z: sin papel')).toBeInTheDocument()
    expect(screen.queryByText(/No se pudo imprimir el ticket de venta/)).not.toBeInTheDocument()
  })

  it('dos trabajos que fallan muestran dos avisos independientes, cada uno con su propio "Reimprimir"', async () => {
    mockearFlujoDeCierre()
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'impresora desconectada' })
    renderShell()
    await cerrarCajaHastaZ()
    await screen.findByText('No se pudo imprimir el reporte Z: sin papel')

    await userEvent.click(screen.getByRole('link', { name: 'Vender' }))
    await completarVenta()

    await screen.findByText('No se pudo imprimir el ticket de venta: impresora desconectada')
    expect(screen.getAllByRole('button', { name: 'Reimprimir' })).toHaveLength(2)
  })

  it('reimprimir con éxito borra solo su propio aviso — el otro sigue visible', async () => {
    mockearFlujoDeCierre()
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'impresora desconectada' })
    renderShell()
    await cerrarCajaHastaZ()
    await screen.findByText('No se pudo imprimir el reporte Z: sin papel')
    await userEvent.click(screen.getByRole('link', { name: 'Vender' }))
    await completarVenta()
    await screen.findByText('No se pudo imprimir el ticket de venta: impresora desconectada')

    const avisoDeVenta = screen.getByText(/No se pudo imprimir el ticket de venta/).closest('[role="alert"]') as HTMLElement
    imprimirMock.mockResolvedValueOnce({ ok: true })
    await userEvent.click(within(avisoDeVenta).getByRole('button', { name: 'Reimprimir' }))

    await waitFor(() => expect(screen.queryByText(/No se pudo imprimir el ticket de venta/)).not.toBeInTheDocument())
    expect(screen.getByText('No se pudo imprimir el reporte Z: sin papel')).toBeInTheDocument()
  })

  it('doble click en "Reimprimir" de UN aviso dispara un único reintento (gating por aviso, no global)', async () => {
    mockearFlujoDeCierre()
    imprimirMock.mockResolvedValueOnce({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    renderShell()
    await cerrarCajaHastaZ()
    await screen.findByText('No se pudo imprimir el reporte Z: sin papel')
    expect(imprimirMock).toHaveBeenCalledTimes(1)

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

    // 1 auto-impresión fallida + 1 reintento (el segundo click del mismo tick se gateó).
    expect(imprimirMock).toHaveBeenCalledTimes(2)
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
