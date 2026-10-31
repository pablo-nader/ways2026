import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CuentaCorriente } from './CuentaCorriente'
import { Toaster } from '../toasts/Toaster'
import { SeccionesDeCobros } from '../componentes/cobros/SeccionesDeCobros'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type {
  ClienteListado,
  ComprobanteEmitido,
  DetalleDeConsumo,
  DetalleDeLinea,
  EstadoDeCuenta,
  MedioPagoListado,
  MovimientoDeCuentaCorriente,
  PuntoVentaListado,
  ReciboDeCliente,
  ResultadoDeReliquidacion,
  UsuarioAutenticado,
  VentaPendienteDeCobro,
  EstadoDeCobro,
} from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiDeleteMock = vi.fn()
// Las secciones de cobros (recibos y ventas pendientes) leen de rutas propias: se atienden aparte para que los
// `mockImplementation` de `apiGetMock`, que contestan todo `/cuenta-corriente`, no las confundan con el estado.
const recibosGetMock = vi.fn()
const ventasPendientesGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => {
      const ruta = args[0] as string
      if (ruta.includes('/cuenta-corriente/recibos')) return recibosGetMock(ruta)
      if (ruta.includes('/cuenta-corriente/ventas-pendientes')) return ventasPendientesGetMock(ruta)
      return apiGetMock(...(args as [string]))
    },
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
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

/** `usuarioActual` es mutable a propósito (reset en `beforeEach`) — cada test de gating de rol
 * (Vendedor vs. Supervisor/Admin) lo sobrescribe sin tener que remockear todo el módulo. */
function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 9,
    usuario: 'supervisor',
    mail: 'supervisor@ways.test',
    rolId: ROL.Supervisor,
    rol: 'Supervisor',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

function medioFixture(sobrescribir: Partial<MedioPagoListado> = {}): MedioPagoListado {
  return {
    id: 1,
    nombre: 'Efectivo',
    activo: true,
    idEmpresa: null,
    orden: 1,
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    recargoPorcentaje: null,
    porcentajeFacturacion: 0,
    ...sobrescribir,
  }
}

function clienteFixture(sobrescribir: Partial<ClienteListado> = {}): ClienteListado {
  return {
    id: 5,
    numero: 5,
    nombre: 'Juan',
    apellido: 'Pérez',
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
    limiteCredito: 5000,
    creditoIlimitado: false,
    saldo: 0,
    activo: true,
    idEmpresa: null,
    esConsumidorFinal: false,
    ...sobrescribir,
  }
}

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 7,
    idTenant: 1,
    idEmpresa: 1,
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
    ...sobrescribir,
  }
}

// `importe`/`saldoResultante` deliberadamente distintos del `saldo` del header (500) y entre sí,
// para que ningún assert de "$ 500,00" choque con una fila de la tabla en los tests.
function movimientoFixture(sobrescribir: Partial<MovimientoDeCuentaCorriente> = {}): MovimientoDeCuentaCorriente {
  return {
    id: 1,
    fecha: '2026-08-01T12:00:00Z',
    tipo: 'Consumo',
    importe: 300,
    saldoResultante: 200,
    detalle: null,
    idComprobanteVenta: 10,
    etiqueta: null,
    ...sobrescribir,
  }
}

function estadoFixture(sobrescribir: Partial<EstadoDeCuenta> = {}): EstadoDeCuenta {
  return {
    header: { saldo: 500, limiteCredito: 5000, creditoIlimitado: false, disponibilidad: 4500 },
    movimientos: [movimientoFixture()],
    historico: false,
    desde: '2026-07-01T00:00:00Z',
    hasta: null,
    ...sobrescribir,
  }
}

function comprobanteFixture(sobrescribir: Partial<ComprobanteEmitido> = {}): ComprobanteEmitido {
  return {
    id: 99,
    numero: 3,
    numeroVisible: '0007-00000003',
    estado: 'Emitido',
    fecha: '2026-08-04T15:00:00Z',
    idPuntoVenta: 7,
    idCliente: 5,
    idComprobanteAsociado: null,
    subtotal: 500,
    descuentoTotal: 0,
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
    total: 500,
    direccionEntrega: null,
    observaciones: null,
    items: [],
    pagos: [{ idMedioPago: 1, importe: 500, referencia: null, vuelto: 0 }],
    idPresupuestoOrigen: null,
    ...sobrescribir,
  }
}

function detalleLineaFixture(sobrescribir: Partial<DetalleDeLinea> = {}): DetalleDeLinea {
  return {
    idArticulo: 1,
    cantidad: 2,
    precioHistorico: 100,
    precioActual: 120,
    totalHistorico: 200,
    totalDelDia: 240,
    delta: 40,
    motivo: null,
    ...sobrescribir,
  }
}

function detalleConsumoFixture(sobrescribir: Partial<DetalleDeConsumo> = {}): DetalleDeConsumo {
  return { idMovimiento: 1, idComprobanteVenta: 10, delta: 40, lineas: [detalleLineaFixture()], ...sobrescribir }
}

function resultadoReliquidacionFixture(sobrescribir: Partial<ResultadoDeReliquidacion> = {}): ResultadoDeReliquidacion {
  return { delta: 40, idsMovimientosCubiertos: [1], detalle: [detalleConsumoFixture()], hayMas: false, ...sobrescribir }
}

function resultadoReliquidacionNoOpFixture(): ResultadoDeReliquidacion {
  return { delta: 0, idsMovimientosCubiertos: [], detalle: [], hayMas: false }
}

const medioEfectivo = medioFixture()
const puntoVentaCentro = puntoVentaFixture()

function renderPantalla(idCliente: number | string = 5, state?: { cliente: ClienteListado }) {
  return render(
    <MemoryRouter initialEntries={[{ pathname: `/clientes/${idCliente}/cuenta-corriente`, state }]}>
      <Routes>
        <Route path="/clientes/:id/cuenta-corriente" element={<CuentaCorriente />} />
      </Routes>
      <Toaster />
    </MemoryRouter>,
  )
}

/** El resultado de una escritura se avisa con un toast de éxito, nunca con un banner. */
async function toastDeExito(texto: string | RegExp) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('[role="status"]')).not.toBeNull()
  expect(document.body.querySelector('.alert-success')).toBeNull()
  return aviso
}

/** Un refresco fallido tras una escritura confirmada se avisa con un toast de advertencia, sin banner de carga. */
async function toastDeAdvertencia(texto: string) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('[role="status"]')).not.toBeNull()
  expect(document.body.querySelector('.alert-danger')).toBeNull()
}

/** Rutas comunes a toda la pantalla (cliente + medios de pago + puntos de venta + estado de
 * cuenta + preview de reliquidación) — un override toma prioridad sobre el default para que un
 * test pueda reemplazar cualquiera de estas rutas base. `GET /clientes/:id` cubre el fetch de
 * identidad cuando no llega `location.state` (Fix 2: único camino del Vendedor / cualquier
 * refresh). El preview de reliquidación se chequea ANTES que el catch-all de `/cuenta-corriente`
 * — su ruta también contiene ese substring. */
function mockearRutasBase(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    if (/^\/clientes\/\d+$/.test(ruta)) return Promise.resolve<ClienteListado>(clienteFixture())
    if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
    if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
    if (ruta.includes('/cuenta-corriente/reliquidacion')) {
      return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionNoOpFixture())
    }
    if (ruta.includes('/cuenta-corriente')) return Promise.resolve<EstadoDeCuenta>(estadoFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

/** Abre el modal de pago y completa una fila válida (Efectivo, importe dado) — espera a que
 * "Registrar pago" esté habilitado (fila con medio + importe cargados). */
async function abrirModalYCompletarFila(importe = '500') {
  renderPantalla()
  await screen.findByText('$ 500,00')
  await userEvent.click(screen.getByRole('button', { name: 'Ingresar pago' }))
  await screen.findByText('Ingresar pago a cuenta')
  await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
  await userEvent.type(screen.getByLabelText('Importe'), importe)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Registrar pago' })).not.toBeDisabled())
}

/** Abre el modal de ajuste manual — requiere Supervisor/Admin, `usuarioActual` es Supervisor por
 * default (ver `beforeEach`). */
async function abrirModalAjuste() {
  renderPantalla()
  await screen.findByText('$ 500,00')
  await userEvent.click(screen.getByRole('button', { name: 'Ajuste manual' }))
  await screen.findByText('Ajuste manual de cuenta corriente')
}

/** Abre el modal de reliquidación — el preview se dispara automáticamente al montar. */
async function abrirModalReliquidacion() {
  renderPantalla()
  await screen.findByText('$ 500,00')
  await userEvent.click(screen.getByRole('button', { name: 'Actualizar precios' }))
  await screen.findByText('Actualizar precios (reliquidación)')
}

afterEach(() => {
  vi.useRealTimers()
})

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiDeleteMock.mockReset()
  recibosGetMock.mockReset()
  recibosGetMock.mockImplementation(() => Promise.resolve<ReciboDeCliente[]>([]))
  ventasPendientesGetMock.mockReset()
  ventasPendientesGetMock.mockImplementation(() => Promise.resolve<VentaPendienteDeCobro[]>([]))
  usuarioActual = usuarioFixture()
})

describe('CuentaCorriente — header y ledger', () => {
  it('muestra saldo, límite de crédito y disponibilidad', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$ 500,00')
    expect(screen.getByText('$ 5.000,00')).toBeInTheDocument()
    expect(screen.getByText('$ 4.500,00')).toBeInTheDocument()
  })

  it('crédito ilimitado muestra "Ilimitado" en límite y disponibilidad, nunca un número fabricado', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente')) {
        return Promise.resolve<EstadoDeCuenta>(
          estadoFixture({ header: { saldo: 0, limiteCredito: 0, creditoIlimitado: true, disponibilidad: null } }),
        )
      }
      return undefined
    })
    renderPantalla()

    await screen.findByText('#0005 — Juan Pérez', { exact: false })
    expect(screen.getAllByText('Ilimitado')).toHaveLength(2)
  })

  it('un período sin movimientos muestra el estado vacío, con exactamente un GET (nunca una re-consulta)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente')) return Promise.resolve<EstadoDeCuenta>(estadoFixture({ movimientos: [] }))
      return undefined
    })
    renderPantalla()

    expect(await screen.findByText('No hay movimientos en el período seleccionado.')).toBeInTheDocument()
    const llamadasEstado = apiGetMock.mock.calls.filter((c) => (c[0] as string).includes('/cuenta-corriente'))
    expect(llamadasEstado).toHaveLength(1)
  })

  it('la falla al cargar medios de pago muestra un aviso y deja "Ingresar pago" realmente deshabilitado', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (/^\/clientes\/\d+$/.test(ruta)) return Promise.resolve<ClienteListado>(clienteFixture())
      if (ruta === '/catalogos/medios-pago') {
        return Promise.reject(new ErrorApi(500, 'error', 'No se pudieron cargar los medios de pago.'))
      }
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
      if (ruta.includes('/cuenta-corriente')) return Promise.resolve<EstadoDeCuenta>(estadoFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPantalla()

    expect(await screen.findByText(/No se pudieron cargar los medios de pago\./)).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ingresar pago' })).toBeDisabled())
  })

  it('el Consumidor Final (conocido por state de navegación) deja "Ingresar pago" deshabilitado', async () => {
    mockearRutasBase()
    const cf = clienteFixture({ id: 1, numero: 1, nombre: 'Consumidor Final', apellido: null, esConsumidorFinal: true })

    renderPantalla(1, { cliente: cf })

    await screen.findByText('$ 500,00')
    expect(screen.getByRole('button', { name: 'Ingresar pago' })).toBeDisabled()
  })

  it('sin state de navegación (único camino del Vendedor / cualquier refresh) busca el cliente por GET y muestra el nombre real, no el placeholder', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('#0005 — Juan Pérez', { exact: false })
    expect(screen.queryByText('Cliente #5', { exact: false })).not.toBeInTheDocument()
    expect(apiGetMock.mock.calls.some((c) => c[0] === '/clientes/5')).toBe(true)
  })

  it('Fix 2: si el fetch del cliente falla (sin state, único camino del Vendedor) el gate falla CERRADO — aviso visible y "Ingresar pago" deshabilitado, nunca habilitado por default', async () => {
    mockearRutasBase((ruta) =>
      /^\/clientes\/\d+$/.test(ruta)
        ? Promise.reject(new ErrorApi(500, 'error', 'No se pudo confirmar el cliente.'))
        : undefined,
    )

    renderPantalla()

    await screen.findByText('$ 500,00')
    expect(await screen.findByText(/No se pudo confirmar el cliente\./)).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ingresar pago' })).toBeDisabled())
  })

  it('un Consumidor Final llegado por URL directa (sin state) también deja "Ingresar pago" deshabilitado con el aviso de CF, una vez resuelta la identidad', async () => {
    const cf = clienteFixture({ esConsumidorFinal: true, nombre: 'Consumidor', apellido: 'Final' })
    mockearRutasBase((ruta) => (/^\/clientes\/\d+$/.test(ruta) ? Promise.resolve<ClienteListado>(cf) : undefined))

    renderPantalla()

    await screen.findByText('$ 500,00')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ingresar pago' })).toBeDisabled())
    expect(screen.getByRole('button', { name: 'Ingresar pago' })).toHaveAttribute(
      'title',
      'El Consumidor Final no tiene cuenta corriente.',
    )
  })
})

describe('CuentaCorriente — filtros (react-async-state regla 2)', () => {
  it('la ventana por defecto (design.md: "último mes") se precarga en Desde/Hasta y viaja en la consulta inicial', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2026, 7, 15, 12, 0, 0))
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$ 500,00')

    expect(screen.getByLabelText('Desde')).toHaveValue('15/07/2026')
    expect(screen.getByLabelText('Hasta')).toHaveValue('15/08/2026')

    const llamadaEstado = apiGetMock.mock.calls.find((c) => (c[0] as string).includes('/cuenta-corriente'))
    const query = decodeURIComponent(llamadaEstado?.[0] as string)
    expect(query).toContain('desde=2026-07-15T00:00:00')
    expect(query).toContain('hasta=2026-08-15T23:59:59.999')
  })

  it('"Ver histórico completo" limpia los inputs Desde/Hasta — la ventana en efecto (sin recorte) queda visible', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$ 500,00')
    expect((screen.getByLabelText('Desde') as HTMLInputElement).value).not.toBe('')

    await userEvent.click(screen.getByLabelText('Ver histórico completo'))

    expect((screen.getByLabelText('Desde') as HTMLInputElement).value).toBe('')
    expect((screen.getByLabelText('Hasta') as HTMLInputElement).value).toBe('')

    // Fix 4: destildar repuebla la ventana (último mes) — los inputs nunca quedan en blanco
    // mostrando una ventana invisible.
    await userEvent.click(screen.getByLabelText('Ver histórico completo'))

    expect((screen.getByLabelText('Desde') as HTMLInputElement).value).not.toBe('')
    expect((screen.getByLabelText('Hasta') as HTMLInputElement).value).not.toBe('')
  })

  it('los filtros disparan un nuevo GET; una respuesta obsoleta que llega tarde nunca pisa la más reciente', async () => {
    let llamadas = 0
    let resolverSegunda: (v: EstadoDeCuenta) => void = () => {}
    const segundaPendiente = new Promise<EstadoDeCuenta>((resolve) => {
      resolverSegunda = resolve
    })

    mockearRutasBase((ruta) => {
      if (!ruta.includes('/cuenta-corriente')) return undefined
      llamadas += 1
      if (llamadas === 1) return Promise.resolve<EstadoDeCuenta>(estadoFixture())
      if (llamadas === 2) return segundaPendiente
      if (llamadas === 3) {
        return Promise.resolve<EstadoDeCuenta>(
          estadoFixture({ movimientos: [movimientoFixture({ id: 3, importe: 999, saldoResultante: 111 })] }),
        )
      }
      return Promise.reject(new Error('llamada inesperada'))
    })

    renderPantalla()
    await screen.findByLabelText('Ver histórico completo')

    // 1ra: activa histórico (queda pendiente, lenta).
    await userEvent.click(screen.getByLabelText('Ver histórico completo'))
    expect(screen.getByLabelText('Desde')).toBeDisabled()

    // 2da: lo desactiva de nuevo — dispara una generación MÁS NUEVA, que resuelve rápido.
    await userEvent.click(screen.getByLabelText('Ver histórico completo'))

    await waitFor(() => expect(screen.getByText('$ 999,00')).toBeInTheDocument())

    // La respuesta obsoleta (de la generación anterior) llega tarde con datos distintos — no
    // puede pisar lo que ya se muestra.
    await act(async () => {
      resolverSegunda(estadoFixture({ movimientos: [movimientoFixture({ id: 2, importe: 777, saldoResultante: 888 })] }))
      await Promise.resolve()
    })
    expect(screen.getByText('$ 999,00')).toBeInTheDocument()
    expect(screen.queryByText('$ 777,00')).not.toBeInTheDocument()
    expect(screen.queryByText('$ 888,00')).not.toBeInTheDocument()
  })
})

describe('CuentaCorriente — modal de pago a cuenta', () => {
  it('un medio CuentaCorriente nunca aparece en el selector de medios del pago (design decisión 6)', async () => {
    const medioCc = medioFixture({ id: 2, nombre: 'Cuenta corriente del cliente', comportamiento: 'CuentaCorriente' })
    apiGetMock.mockImplementation((ruta: string) => {
      if (/^\/clientes\/\d+$/.test(ruta)) return Promise.resolve<ClienteListado>(clienteFixture())
      if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo, medioCc])
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
      if (ruta.includes('/cuenta-corriente')) return Promise.resolve<EstadoDeCuenta>(estadoFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPantalla()
    await screen.findByText('$ 500,00')
    await userEvent.click(screen.getByRole('button', { name: 'Ingresar pago' }))
    await screen.findByText('Ingresar pago a cuenta')

    const opciones = within(screen.getByLabelText('Medio de pago')).getAllByRole('option')
    expect(opciones.map((o) => o.textContent)).not.toContain('Cuenta corriente del cliente')
    expect(opciones.map((o) => o.textContent)).toContain('Efectivo')
  })

  it('doble click en "Registrar pago" dispara exactamente un POST', async () => {
    mockearRutasBase()
    let resolverPago: (c: ComprobanteEmitido) => void = () => {}
    const pagoPendiente = new Promise<ComprobanteEmitido>((resolve) => {
      resolverPago = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/pagos') return pagoPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalYCompletarFila()

    const boton = screen.getByRole('button', { name: 'Registrar pago' })
    await userEvent.click(boton)
    await userEvent.click(boton)
    fireEvent.click(boton)

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/clientes/5/cuenta-corriente/pagos')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Registrando…' })).toBeDisabled()

    await act(async () => {
      resolverPago(comprobanteFixture())
      await Promise.resolve()
    })
  })

  it('arma el cuerpo del POST de pago con la forma exacta del contrato', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/pagos') return Promise.resolve<ComprobanteEmitido>(comprobanteFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalYCompletarFila('500')
    await userEvent.type(screen.getByLabelText('Observaciones'), '  nota de prueba  ')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar pago' }))

    await toastDeExito(/Pago registrado/)

    const llamada = apiPostMock.mock.calls.find((c) => c[0] === '/clientes/5/cuenta-corriente/pagos')
    expect(llamada?.[1]).toEqual({
      idPuntoVenta: 7,
      pagos: [{ idMedioPago: 1, importe: 500, referencia: null, vuelto: 0 }],
      observaciones: 'nota de prueba',
    })
  })

  it('un pago 2xx nunca se reporta como fallo, aunque el refetch del ledger posterior falle', async () => {
    let llamadasEstado = 0
    mockearRutasBase((ruta) => {
      if (!ruta.includes('/cuenta-corriente')) return undefined
      llamadasEstado += 1
      if (llamadasEstado === 1) return Promise.resolve<EstadoDeCuenta>(estadoFixture())
      return Promise.reject(new Error('boom'))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/pagos') return Promise.resolve<ComprobanteEmitido>(comprobanteFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalYCompletarFila()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar pago' }))

    await toastDeExito('Pago registrado: comprobante 0007-00000003.')
    await toastDeAdvertencia('No se pudo cargar el estado de cuenta.')
    // el toast de éxito del pago sigue en pantalla — el fallo del refetch no lo pisa.
    expect(screen.getByText('Pago registrado: comprobante 0007-00000003.')).toBeInTheDocument()
  })

  it('turno_no_abierto durante el pago ofrece abrir el turno ahí mismo — sin reintento automático del pago', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/pagos') {
        return Promise.reject(new ErrorApi(409, 'turno_no_abierto', 'No hay un turno abierto en este punto de venta.'))
      }
      if (ruta === '/caja/turnos') return Promise.resolve({})
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalYCompletarFila()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar pago' }))

    await screen.findByText('No hay un turno abierto')
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/clientes/5/cuenta-corriente/pagos')).toHaveLength(1)

    await userEvent.type(screen.getByLabelText('Fondo inicial'), '500')
    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    await screen.findByText('Ingresar pago a cuenta')
    // sin reintento automático: reabrir el turno no reenvía el pago solo.
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/clientes/5/cuenta-corriente/pagos')).toHaveLength(1)
    // los datos del pago que ya se habían cargado en el modal siguen ahí — no se pierden al
    // recuperarse del gate de turno (el JSDoc de PanelAperturaDeTurnoEnModal lo promete).
    expect(screen.getByLabelText('Medio de pago')).toHaveValue('1')
    expect(screen.getByLabelText('Importe')).toHaveValue('500,00')
  })
})

describe('CuentaCorriente — gating de rol (Supervisor+Admin) para ajuste y reliquidación', () => {
  it('un Vendedor no ve las acciones de ajuste ni reliquidación', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$ 500,00')
    expect(screen.queryByRole('button', { name: 'Ajuste manual' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Actualizar precios' })).not.toBeInTheDocument()
    // el pago sigue disponible para cualquier rol — OperacionDePos, no SupervisionDeCuentaCorriente.
    expect(screen.getByRole('button', { name: 'Ingresar pago' })).toBeInTheDocument()
  })

  it('un Supervisor ve ambas acciones, habilitadas una vez cargados los datos', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$ 500,00')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ajuste manual' })).not.toBeDisabled())
    expect(screen.getByRole('button', { name: 'Actualizar precios' })).not.toBeDisabled()
  })

  it('un Admin también ve ambas acciones', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Admin, rol: 'Admin' })
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$ 500,00')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ajuste manual' })).not.toBeDisabled())
    expect(screen.getByRole('button', { name: 'Actualizar precios' })).not.toBeDisabled()
  })
})

describe('CuentaCorriente — modal de ajuste manual', () => {
  it('importe cero se rechaza localmente (ajuste_importe_invalido), sin llamar al servidor', async () => {
    mockearRutasBase()
    await abrirModalAjuste()

    await userEvent.type(screen.getByLabelText('Importe'), '0')
    await userEvent.type(screen.getByLabelText('Detalle (obligatorio)'), 'Detalle válido')
    await userEvent.click(screen.getByLabelText(/Entiendo que este ajuste modifica el saldo/))
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(screen.getByText('El importe del ajuste no puede ser cero.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un detalle recortado por debajo de 5 caracteres se rechaza localmente (ajuste_detalle_requerido)', async () => {
    mockearRutasBase()
    await abrirModalAjuste()

    await userEvent.type(screen.getByLabelText('Importe'), '40')
    await userEvent.type(screen.getByLabelText('Detalle (obligatorio)'), '  abcd  ')
    await userEvent.click(screen.getByLabelText(/Entiendo que este ajuste modifica el saldo/))
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(
      screen.getByText('El detalle del ajuste es obligatorio y tiene que tener al menos 5 caracteres.'),
    ).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('semántica con signo: positivo aumenta el saldo resultante, negativo lo reduce (header.saldo = 500)', async () => {
    mockearRutasBase()
    await abrirModalAjuste()

    await userEvent.type(screen.getByLabelText('Importe'), '40')
    expect(screen.getByText('Saldo resultante: $ 540,00')).toBeInTheDocument()

    await userEvent.clear(screen.getByLabelText('Importe'))
    await userEvent.type(screen.getByLabelText('Importe'), '-50')
    expect(screen.getByText('Saldo resultante: $ 450,00')).toBeInTheDocument()
  })

  it('arma el cuerpo del POST con la forma exacta del contrato, detalle recortado, y refresca el ledger', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/ajustes') {
        return Promise.resolve<MovimientoDeCuentaCorriente>(
          movimientoFixture({ tipo: 'Ajuste', importe: -50, saldoResultante: 450, etiqueta: 'Manual' }),
        )
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalAjuste()
    await userEvent.type(screen.getByLabelText('Importe'), '-50')
    await userEvent.type(screen.getByLabelText('Detalle (obligatorio)'), '  Descuento por reclamo  ')
    await userEvent.click(screen.getByLabelText(/Entiendo que este ajuste modifica el saldo/))
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await toastDeExito(/Ajuste registrado/)

    const llamada = apiPostMock.mock.calls.find((c) => c[0] === '/clientes/5/cuenta-corriente/ajustes')
    expect(llamada?.[1]).toEqual({ idPuntoVenta: 7, importe: -50, detalle: 'Descuento por reclamo' })
  })

  it('doble click en "Registrar ajuste" dispara exactamente un POST', async () => {
    mockearRutasBase()
    let resolverAjuste: (m: MovimientoDeCuentaCorriente) => void = () => {}
    const ajustePendiente = new Promise<MovimientoDeCuentaCorriente>((resolve) => {
      resolverAjuste = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/ajustes') return ajustePendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalAjuste()
    await userEvent.type(screen.getByLabelText('Importe'), '40')
    await userEvent.type(screen.getByLabelText('Detalle (obligatorio)'), 'Corrección de saldo')
    await userEvent.click(screen.getByLabelText(/Entiendo que este ajuste modifica el saldo/))

    const boton = screen.getByRole('button', { name: 'Registrar ajuste' })
    await userEvent.click(boton)
    await userEvent.click(boton)
    fireEvent.click(boton)

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/clientes/5/cuenta-corriente/ajustes')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Registrando…' })).toBeDisabled()

    await act(async () => {
      resolverAjuste(movimientoFixture({ tipo: 'Ajuste', importe: 40, etiqueta: 'Manual' }))
      await Promise.resolve()
    })
  })

  it('un ajuste 2xx nunca se reporta como fallo, aunque el refetch del ledger posterior falle', async () => {
    let llamadasEstado = 0
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) return undefined
      if (!ruta.includes('/cuenta-corriente')) return undefined
      llamadasEstado += 1
      if (llamadasEstado === 1) return Promise.resolve<EstadoDeCuenta>(estadoFixture())
      return Promise.reject(new Error('boom'))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/ajustes') {
        return Promise.resolve<MovimientoDeCuentaCorriente>(movimientoFixture({ tipo: 'Ajuste', importe: 40, etiqueta: 'Manual' }))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalAjuste()
    await userEvent.type(screen.getByLabelText('Importe'), '40')
    await userEvent.type(screen.getByLabelText('Detalle (obligatorio)'), 'Corrección')
    await userEvent.click(screen.getByLabelText(/Entiendo que este ajuste modifica el saldo/))
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await toastDeExito(/Ajuste registrado/)
    await toastDeAdvertencia('No se pudo cargar el estado de cuenta.')
    // el toast de éxito del ajuste sigue en pantalla — el fallo del refetch no lo pisa.
    expect(screen.getByText(/Ajuste registrado/)).toBeInTheDocument()
  })
})

describe('CuentaCorriente — modal de reliquidación a precio del día', () => {
  it('preview primero: muestra delta, consumos cubiertos y solo habilita ejecutar tras confirmar', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture())
      }
      return undefined
    })

    await abrirModalReliquidacion()

    const dialogo = screen.getByRole('dialog')
    expect(await within(dialogo).findByTestId('cc-reliq-delta-estimado')).toHaveTextContent('$ 40,00')
    expect(within(dialogo).getByRole('button', { name: 'Ejecutar reliquidación' })).toBeDisabled()

    await userEvent.click(screen.getByLabelText(/Confirmo que quiero actualizar los precios/))
    expect(within(dialogo).getByRole('button', { name: 'Ejecutar reliquidación' })).not.toBeDisabled()
  })

  it('hayMas en el preview muestra el aviso de que quedan más consumos pendientes', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture({ hayMas: true }))
      }
      return undefined
    })

    await abrirModalReliquidacion()

    expect(
      await screen.findByText(/Quedan más consumos pendientes — esta corrida no los cubre/),
    ).toBeInTheDocument()
  })

  it('preview sin consumos elegibles muestra el estado no-op y solo permite cerrar', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionNoOpFixture())
      }
      return undefined
    })

    await abrirModalReliquidacion()

    expect(await screen.findByText('No hay consumos pendientes de actualizar para este cliente.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Ejecutar reliquidación' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeInTheDocument()
  })

  it('ejecuta la reliquidación tras confirmar y arma el cuerpo del POST con la forma exacta del contrato', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture())
      }
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/reliquidacion') {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture())
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalReliquidacion()
    expect(await screen.findByTestId('cc-reliq-delta-estimado')).toHaveTextContent('$ 40,00')
    await userEvent.click(screen.getByLabelText(/Confirmo que quiero actualizar los precios/))
    await userEvent.click(screen.getByRole('button', { name: 'Ejecutar reliquidación' }))

    await toastDeExito(/Precios actualizados/)

    const llamada = apiPostMock.mock.calls.find((c) => c[0] === '/clientes/5/cuenta-corriente/reliquidacion')
    expect(llamada?.[1]).toEqual({ idPuntoVenta: 7 })
  })

  it('doble click en "Ejecutar reliquidación" dispara exactamente un POST', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture())
      }
      return undefined
    })
    let resolverReliquidacion: (r: ResultadoDeReliquidacion) => void = () => {}
    const reliquidacionPendiente = new Promise<ResultadoDeReliquidacion>((resolve) => {
      resolverReliquidacion = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/reliquidacion') return reliquidacionPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalReliquidacion()
    expect(await screen.findByTestId('cc-reliq-delta-estimado')).toHaveTextContent('$ 40,00')
    await userEvent.click(screen.getByLabelText(/Confirmo que quiero actualizar los precios/))

    const boton = screen.getByRole('button', { name: 'Ejecutar reliquidación' })
    await userEvent.click(boton)
    await userEvent.click(boton)
    fireEvent.click(boton)

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/clientes/5/cuenta-corriente/reliquidacion')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Ejecutando…' })).toBeDisabled()

    await act(async () => {
      resolverReliquidacion(resultadoReliquidacionFixture())
      await Promise.resolve()
    })
  })

  it('un commit no-op (carrera preview↔commit) se reporta como éxito, nunca como fallo', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture())
      }
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/reliquidacion') {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionNoOpFixture())
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalReliquidacion()
    expect(await screen.findByTestId('cc-reliq-delta-estimado')).toHaveTextContent('$ 40,00')
    await userEvent.click(screen.getByLabelText(/Confirmo que quiero actualizar los precios/))
    await userEvent.click(screen.getByRole('button', { name: 'Ejecutar reliquidación' }))

    await toastDeExito('No había nada para actualizar.')
  })

  it('una reliquidación 2xx nunca se reporta como fallo, aunque el refetch del ledger posterior falle', async () => {
    let llamadasEstado = 0
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture())
      }
      if (!ruta.includes('/cuenta-corriente')) return undefined
      llamadasEstado += 1
      if (llamadasEstado === 1) return Promise.resolve<EstadoDeCuenta>(estadoFixture())
      return Promise.reject(new Error('boom'))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/reliquidacion') {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture())
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalReliquidacion()
    expect(await screen.findByTestId('cc-reliq-delta-estimado')).toHaveTextContent('$ 40,00')
    await userEvent.click(screen.getByLabelText(/Confirmo que quiero actualizar los precios/))
    await userEvent.click(screen.getByRole('button', { name: 'Ejecutar reliquidación' }))

    await toastDeExito(/Precios actualizados/)
    await toastDeAdvertencia('No se pudo cargar el estado de cuenta.')
    // el toast de éxito de la reliquidación sigue en pantalla — el fallo del refetch no lo pisa.
    expect(screen.getByText(/Precios actualizados/)).toBeInTheDocument()
  })

  it('Fix 1: la vista previa muestra el detalle por consumo, con la línea omitida y su motivo', async () => {
    const detalle: DetalleDeConsumo[] = [
      detalleConsumoFixture({ idMovimiento: 1, delta: 40 }),
      detalleConsumoFixture({
        idMovimiento: 2,
        delta: 0,
        lineas: [
          detalleLineaFixture({ precioActual: null, totalDelDia: null, delta: 0, motivo: 'articulo_no_encontrado' }),
        ],
      }),
    ]
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) {
        return Promise.resolve<ResultadoDeReliquidacion>(
          resultadoReliquidacionFixture({ idsMovimientosCubiertos: [1, 2], detalle }),
        )
      }
      return undefined
    })

    await abrirModalReliquidacion()
    const dialogo = screen.getByRole('dialog')
    await within(dialogo).findByTestId('cc-reliq-delta-estimado')

    expect(within(dialogo).getByText('Movimiento #1 — $ 40,00')).toBeInTheDocument()
    expect(within(dialogo).getByText('Movimiento #2 — $ 0,00')).toBeInTheDocument()
    expect(within(dialogo).getByText('articulo_no_encontrado')).toBeInTheDocument()
  })

  it('el ajuste manual conservado se muestra en SU línea (rótulo y porcentaje) y ninguna otra lo muestra', async () => {
    // Cuatro líneas con artículos distintos: descuento, recargo con decimales, clave ausente (la
    // forma real del servidor sin ajuste: `WhenWritingNull`) y `null` explícito.
    const detalle: DetalleDeConsumo[] = [
      detalleConsumoFixture({
        idMovimiento: 1,
        lineas: [
          detalleLineaFixture({
            idArticulo: 701,
            cantidad: 1,
            precioHistorico: 100,
            precioActual: 120,
            totalHistorico: 100,
            totalDelDia: 108,
            delta: 8,
            ajusteManualPorcentaje: -10,
          }),
          detalleLineaFixture({ idArticulo: 702, ajusteManualPorcentaje: 12.5 }),
          detalleLineaFixture({ idArticulo: 703 }),
          detalleLineaFixture({ idArticulo: 704, ajusteManualPorcentaje: null }),
        ],
      }),
    ]
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture({ detalle }))
      }
      return undefined
    })

    await abrirModalReliquidacion()
    const dialogo = screen.getByRole('dialog')
    await within(dialogo).findByTestId('cc-reliq-delta-estimado')

    const filaDe = (idArticulo: number) => within(dialogo).getByText(String(idArticulo)).closest('tr')!
    expect(within(filaDe(701)).getByTestId('cc-reliq-ajuste-manual')).toHaveTextContent(/^Desc\. manual 10%$/)
    expect(within(filaDe(702)).getByTestId('cc-reliq-ajuste-manual')).toHaveTextContent(/^Recargo 12,5%$/)
    expect(within(filaDe(703)).queryByTestId('cc-reliq-ajuste-manual')).not.toBeInTheDocument()
    expect(within(filaDe(704)).queryByTestId('cc-reliq-ajuste-manual')).not.toBeInTheDocument()
    expect(within(dialogo).getAllByTestId('cc-reliq-ajuste-manual')).toHaveLength(2)
  })

  it('Fix 4: el toast de éxito muestra el delta EJECUTADO, nunca el delta previsualizado', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture({ delta: 40 }))
      }
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes/5/cuenta-corriente/reliquidacion') {
        return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture({ delta: 75 }))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await abrirModalReliquidacion()
    await screen.findByTestId('cc-reliq-delta-estimado')
    await userEvent.click(screen.getByLabelText(/Confirmo que quiero actualizar los precios/))
    await userEvent.click(screen.getByRole('button', { name: 'Ejecutar reliquidación' }))

    await toastDeExito(/Precios actualizados: \$ 75,00/)
    expect(screen.queryByText(/Precios actualizados: \$ 40,00/)).not.toBeInTheDocument()
  })
})

describe('CuentaCorriente — detalle de un movimiento ActualizacionPrecios en el ledger (Fix 1b)', () => {
  it('un detalle JSON válido se muestra como un resumen legible, nunca el JSON crudo', async () => {
    // El backend guarda este campo con `JsonSerializer.Serialize(resultado.Detalle)` SIN el naming
    // policy camelCase de la API — las claves llegan en PascalCase real (espejo de
    // `DetalleDeConsumo`/`DetalleDeLinea` en `ReliquidadorDeConsumos.cs`), nunca camelCase.
    const detalleCrudo = JSON.stringify([
      {
        IdMovimiento: 3,
        IdComprobanteVenta: 10,
        Delta: 55,
        Lineas: [
          {
            IdArticulo: 1,
            Cantidad: 2,
            PrecioHistorico: 100,
            PrecioActual: 120,
            TotalHistorico: 200,
            TotalDelDia: 240,
            Delta: 40,
            Motivo: null,
          },
        ],
      },
    ])
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) return undefined
      if (ruta.includes('/cuenta-corriente')) {
        return Promise.resolve<EstadoDeCuenta>(
          estadoFixture({
            movimientos: [movimientoFixture({ tipo: 'ActualizacionPrecios', detalle: detalleCrudo, importe: 55 })],
          }),
        )
      }
      return undefined
    })

    renderPantalla()

    expect(await screen.findByText('1 consumo re-preciado')).toBeInTheDocument()
    expect(screen.queryByText(detalleCrudo)).not.toBeInTheDocument()
  })

  it('un detalle malformado cae al texto crudo en vez de romper la fila', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente/reliquidacion')) return undefined
      if (ruta.includes('/cuenta-corriente')) {
        return Promise.resolve<EstadoDeCuenta>(
          estadoFixture({
            movimientos: [movimientoFixture({ tipo: 'ActualizacionPrecios', detalle: '{esto no es json', importe: 10 })],
          }),
        )
      }
      return undefined
    })

    renderPantalla()

    expect(await screen.findByText('{esto no es json')).toBeInTheDocument()
  })
})

/**
 * Vista de impresión (stage-11-exportacion-reportes, Slice 8, design decisión 13: mismo
 * componente, `@media print`, sin ruta ni fetch dedicados — print rendering en sí es una
 * exención registrada, verificada a ojo). Estos tests cubren lo único automatizable: el bloque de
 * impresión muestra los valores REALES de contexto (nunca un placeholder) y el chrome que no debe
 * imprimirse lleva `d-print-none`.
 */
describe('CuentaCorriente — vista de impresión (Slice 8)', () => {
  it('el bloque de impresión muestra el rango real de filtros y quién/cuándo lo generó', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2026, 7, 15, 12, 0, 0))
    mockearRutasBase()
    usuarioActual = usuarioFixture({ usuario: 'cajera_ana' })
    renderPantalla()

    await screen.findByText('$ 500,00')

    expect(screen.getByText('Rango: 2026-07-15 a 2026-08-15')).toBeInTheDocument()
    expect(screen.getByText(/Generado:.*— cajera_ana/)).toBeInTheDocument()
  })

  it('"Ver histórico completo" cambia el bloque de impresión de un rango de fechas a "Histórico completo"', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$ 500,00')
    expect(screen.getByText(/^Rango: \d{4}-\d{2}-\d{2} a \d{4}-\d{2}-\d{2}$/)).toBeInTheDocument()

    await userEvent.click(screen.getByLabelText('Ver histórico completo'))

    expect(await screen.findByText('Rango: Histórico completo')).toBeInTheDocument()
  })

  it('el botón Imprimir dispara window.print() y el resto del chrome de pantalla queda marcado d-print-none', async () => {
    mockearRutasBase()
    const imprimirSpy = vi.fn()
    window.print = imprimirSpy
    renderPantalla()

    await screen.findByText('$ 500,00')

    const botonImprimir = screen.getByRole('button', { name: 'Imprimir' })
    expect(botonImprimir).toHaveClass('d-print-none')
    await userEvent.click(botonImprimir)
    expect(imprimirSpy).toHaveBeenCalledTimes(1)

    expect(screen.getByRole('link', { name: 'Volver a clientes' })).toHaveClass('d-print-none')
    expect(screen.getByLabelText('Desde').closest('.row')).toHaveClass('d-print-none')
    expect(screen.getByRole('button', { name: 'Ingresar pago' }).closest('.d-flex')).toHaveClass('d-print-none')
  })
})

describe('CuentaCorriente — orden del ledger y del detalle por consumo', () => {
  const MOVIMIENTOS = [
    movimientoFixture({ id: 1, fecha: '2026-08-01T12:00:00Z', importe: 5000, saldoResultante: 5000 }),
    movimientoFixture({ id: 2, fecha: '2026-08-03T12:00:00Z', tipo: 'Pago', importe: -20, saldoResultante: 4980 }),
    movimientoFixture({ id: 3, fecha: '2026-08-02T12:00:00Z', importe: 100, saldoResultante: 5080 }),
  ]

  const celdas = (columna: number) =>
    Array.from(screen.getByRole('table', { name: 'Movimientos' }).querySelectorAll('tbody tr')).map((fila) => fila.querySelectorAll('td')[columna]?.textContent)

  it('el ledger conserva el orden del servidor, ordena por importe numérico a pedido y cada fila conserva su saldo guardado', async () => {
    mockearRutasBase((ruta) =>
      ruta.includes('/cuenta-corriente') && !ruta.includes('/reliquidacion')
        ? Promise.resolve<EstadoDeCuenta>(estadoFixture({ movimientos: MOVIMIENTOS }))
        : undefined,
    )
    renderPantalla()
    await screen.findByRole('table', { name: 'Movimientos' })
    await screen.findByText('$ 4.980,00')

    expect(celdas(4)).toEqual(['$ 5.000,00', '$ 4.980,00', '$ 5.080,00'])

    const importe = screen.getByRole('columnheader', { name: 'Importe' })
    await userEvent.click(within(importe).getByRole('button'))

    expect(importe).toHaveAttribute('aria-sort', 'ascending')
    expect(celdas(3)).toEqual(['-$ 20,00', '$ 100,00', '$ 5.000,00'])
    expect(celdas(4)).toEqual(['$ 4.980,00', '$ 5.080,00', '$ 5.000,00'])
  })

  it('la fecha ordena cronológicamente y el tercer clic vuelve al orden del servidor', async () => {
    mockearRutasBase((ruta) =>
      ruta.includes('/cuenta-corriente') && !ruta.includes('/reliquidacion')
        ? Promise.resolve<EstadoDeCuenta>(estadoFixture({ movimientos: MOVIMIENTOS }))
        : undefined,
    )
    renderPantalla()
    await screen.findByText('$ 4.980,00')

    const fecha = within(screen.getByRole('table', { name: 'Movimientos' })).getByRole('columnheader', { name: 'Fecha' })
    await userEvent.click(within(fecha).getByRole('button'))
    await userEvent.click(within(fecha).getByRole('button'))
    expect(fecha).toHaveAttribute('aria-sort', 'descending')
    expect(celdas(4)).toEqual(['$ 4.980,00', '$ 5.080,00', '$ 5.000,00'])

    await userEvent.click(within(fecha).getByRole('button'))
    expect(celdas(4)).toEqual(['$ 5.000,00', '$ 4.980,00', '$ 5.080,00'])
  })

  it('las líneas del detalle por consumo se ordenan por su delta numérico', async () => {
    const detalle: DetalleDeConsumo[] = [
      detalleConsumoFixture({
        idMovimiento: 1,
        lineas: [
          detalleLineaFixture({ idArticulo: 801, delta: 40 }),
          detalleLineaFixture({ idArticulo: 802, delta: -3 }),
          detalleLineaFixture({ idArticulo: 803, delta: 5 }),
        ],
      }),
    ]
    mockearRutasBase((ruta) =>
      ruta.includes('/cuenta-corriente/reliquidacion')
        ? Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionFixture({ detalle }))
        : undefined,
    )
    await abrirModalReliquidacion()
    const dialogo = within(screen.getByRole('dialog'))
    await dialogo.findByTestId('cc-reliq-delta-estimado')

    const tabla = dialogo.getByRole('table')
    await userEvent.click(within(within(tabla).getByRole('columnheader', { name: 'Delta' })).getByRole('button'))

    expect(Array.from(tabla.querySelectorAll('tbody tr')).map((fila) => fila.querySelector('td')?.textContent)).toEqual(['802', '803', '801'])
  })

  it('la columna Detalle ordena por el texto que muestra la celda, no por el JSON crudo de la actualización de precios', async () => {
    const detalleCrudo = JSON.stringify([{ IdMovimiento: 3, IdComprobanteVenta: 10, Delta: 55, Lineas: [] }])
    mockearRutasBase((ruta) =>
      ruta.includes('/cuenta-corriente') && !ruta.includes('/reliquidacion')
        ? Promise.resolve<EstadoDeCuenta>(
            estadoFixture({
              movimientos: [
                movimientoFixture({ id: 1, detalle: 'Zeta' }),
                movimientoFixture({ id: 2, tipo: 'ActualizacionPrecios', detalle: detalleCrudo, importe: 55 }),
                movimientoFixture({ id: 3, detalle: '0 cosa' }),
              ],
            }),
          )
        : undefined,
    )
    renderPantalla()
    await screen.findByText('1 consumo re-preciado')

    await userEvent.click(within(screen.getByRole('columnheader', { name: 'Detalle' })).getByRole('button'))

    const detalles = Array.from(screen.getByRole('table', { name: 'Movimientos' }).querySelectorAll('tbody tr')).map((fila) => fila.querySelectorAll('td')[2]?.textContent)
    expect(detalles[0]).toBe('0 cosa')
    expect(detalles[1]).toMatch(/^1 consumo re-preciado/)
    expect(detalles[2]).toBe('Zeta')
  })
})

function ventaPendienteFixture(sobrescribir: Partial<VentaPendienteDeCobro> = {}): VentaPendienteDeCobro {
  return {
    id: 11,
    codigoTipo: 'FA',
    letra: 'B',
    numero: 11,
    numeroVisible: '0001-00000011',
    fecha: '2026-09-01T12:00:00Z',
    parteEnCuentaCorriente: 150,
    cobrado: 0,
    saldoPendiente: 150,
    ...sobrescribir,
  }
}

const ventasPendientes: VentaPendienteDeCobro[] = [
  ventaPendienteFixture({
    id: 12,
    numero: 12,
    numeroVisible: '0001-00000012',
    fecha: '2026-09-05T12:00:00Z',
    parteEnCuentaCorriente: 400,
    cobrado: 100,
    saldoPendiente: 300,
  }),
  ventaPendienteFixture(),
]

const recibosFixture: ReciboDeCliente[] = [
  {
    id: 21,
    numero: 21,
    numeroVisible: '0007-00000021',
    fecha: '2026-09-10T12:00:00Z',
    netoCobrado: 450,
    aplicado: 150,
    disponible: 300,
    aplicaciones: [
      {
        id: 31,
        idComprobanteVenta: 10,
        numeroVisibleDeVenta: '0001-00000010',
        importe: 150,
        fecha: '2026-09-10T12:00:00Z',
        origen: 'Historial',
      },
    ],
  },
  {
    id: 22,
    numero: 22,
    numeroVisible: '0007-00000022',
    fecha: '2026-09-12T12:00:00Z',
    netoCobrado: 100,
    aplicado: 100,
    disponible: 0,
    aplicaciones: [
      {
        id: 32,
        idComprobanteVenta: 9,
        numeroVisibleDeVenta: '0001-00000009',
        importe: 100,
        fecha: '2026-09-12T12:00:00Z',
        origen: 'Manual',
      },
    ],
  },
]

const estadoDeCobroFixture: EstadoDeCobro = { idRecibo: 21, netoCobrado: 450, aplicado: 450, disponible: 0, aplicaciones: [] }

function conCobros() {
  mockearRutasBase()
  recibosGetMock.mockImplementation(() => Promise.resolve(recibosFixture))
  ventasPendientesGetMock.mockImplementation(() => Promise.resolve(ventasPendientes))
}

/** Espera a los DATOS de las dos secciones, no a sus títulos (que se renderizan antes de que lleguen). */
async function renderConCobros() {
  renderPantalla()
  const recibos = await screen.findByRole('region', { name: 'Recibos' })
  await within(recibos).findByText('0007-00000021')
  const ventas = screen.getByRole('region', { name: 'Ventas con saldo pendiente' })
  await within(ventas).findByText('0001-00000012')
  return { recibos, ventas }
}

function llamadasAlEstado() {
  return apiGetMock.mock.calls.filter(
    (c) => (c[0] as string).endsWith('/cuenta-corriente') || (c[0] as string).includes('/cuenta-corriente?'),
  )
}

describe('CuentaCorriente — cobros aplicados a ventas', () => {
  it('muestra los recibos con lo cobrado, aplicado y disponible, y la marca «Historial» solo en las aplicaciones del historial', async () => {
    conCobros()
    const { recibos } = await renderConCobros()

    const fila = within(recibos).getByText('0007-00000021').closest('tr') as HTMLElement
    expect(within(fila).getByText('$ 450,00')).toBeInTheDocument()
    expect(within(fila).getByText('$ 150,00')).toBeInTheDocument()
    expect(within(fila).getByText('$ 300,00')).toBeInTheDocument()

    const historial = screen.getByTestId('cc-aplicacion-31')
    expect(within(historial).getByText('Historial')).toBeInTheDocument()
    expect(within(historial).getByText(/0001-00000010/)).toBeInTheDocument()
    expect(within(screen.getByTestId('cc-aplicacion-32')).queryByText('Historial')).not.toBeInTheDocument()
  })

  it('lista las ventas con saldo pendiente con su parte en cuenta corriente, lo cobrado y el saldo', async () => {
    conCobros()
    const { ventas } = await renderConCobros()

    const fila = within(ventas).getByText('0001-00000012').closest('tr') as HTMLElement
    expect(within(fila).getByText('$ 400,00')).toBeInTheDocument()
    expect(within(fila).getByText('$ 100,00')).toBeInTheDocument()
    expect(within(fila).getByText('$ 300,00')).toBeInTheDocument()
  })

  it('«Solo con disponible» vuelve a pedir los recibos con el filtro', async () => {
    conCobros()
    const { recibos } = await renderConCobros()

    await userEvent.click(within(recibos).getByLabelText('Solo con disponible'))

    await waitFor(() =>
      expect(recibosGetMock).toHaveBeenLastCalledWith('/clientes/5/cuenta-corriente/recibos?conDisponible=true'),
    )
  })

  it('un Vendedor ve los recibos y las ventas pero no «Aplicar» ni «Desaplicar»', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    conCobros()
    const { recibos } = await renderConCobros()

    expect(within(recibos).queryByRole('button', { name: /Aplicar/ })).not.toBeInTheDocument()
    expect(within(recibos).queryByRole('button', { name: /Desaplicar/ })).not.toBeInTheDocument()
  })

  it('un recibo sin disponible no ofrece «Aplicar»', async () => {
    conCobros()
    await renderConCobros()

    expect(screen.getByRole('button', { name: 'Aplicar el recibo 0007-00000021' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Aplicar el recibo 0007-00000022' })).not.toBeInTheDocument()
  })

  it('aplicar un recibo reparte el disponible entre las ventas más viejas, envía el cuerpo y refresca recibos, ventas y estado', async () => {
    conCobros()
    apiPostMock.mockResolvedValue(estadoDeCobroFixture)
    await renderConCobros()
    const estadoAntes = llamadasAlEstado().length
    const recibosAntes = recibosGetMock.mock.calls.length
    const ventasAntes = ventasPendientesGetMock.mock.calls.length

    await userEvent.click(screen.getByRole('button', { name: 'Aplicar el recibo 0007-00000021' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Aplicar a las más viejas' }))
    expect(screen.getByTestId('cc-aplicar-restante')).toHaveTextContent('$ 0,00')
    await userEvent.click(screen.getByRole('button', { name: 'Aplicar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith('/cobros/21/aplicaciones', {
      aplicaciones: [
        { idComprobanteVenta: 12, importe: 150 },
        { idComprobanteVenta: 11, importe: 150 },
      ],
    })
    await toastDeExito('Cobro aplicado: recibo 0007-00000021.')
    expect(screen.queryByRole('dialog', { name: 'Aplicar el recibo 0007-00000021' })).not.toBeInTheDocument()
    await waitFor(() => {
      expect(recibosGetMock.mock.calls.length).toBeGreaterThan(recibosAntes)
      expect(ventasPendientesGetMock.mock.calls.length).toBeGreaterThan(ventasAntes)
      expect(llamadasAlEstado().length).toBeGreaterThan(estadoAntes)
    })
  })

  it('no envía si lo cargado supera el disponible del recibo', async () => {
    conCobros()
    await renderConCobros()

    await userEvent.click(screen.getByRole('button', { name: 'Aplicar el recibo 0007-00000021' }))
    await userEvent.type(await screen.findByLabelText('Importe a aplicar a la venta 0001-00000012'), '300')
    await userEvent.type(screen.getByLabelText('Importe a aplicar a la venta 0001-00000011'), '150')
    await userEvent.click(screen.getByRole('button', { name: 'Aplicar' }))

    expect(apiPostMock).not.toHaveBeenCalled()
    expect((await screen.findAllByText(/supera el importe disponible/)).length).toBeGreaterThan(0)
  })

  it('un rechazo del servidor deja el modal abierto con el mensaje y vuelve a pedir las listas', async () => {
    conCobros()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cobro_excede_disponible', 'El recibo ya no tiene ese disponible.'))
    await renderConCobros()
    const recibosAntes = recibosGetMock.mock.calls.length

    await userEvent.click(screen.getByRole('button', { name: 'Aplicar el recibo 0007-00000021' }))
    await userEvent.type(await screen.findByLabelText('Importe a aplicar a la venta 0001-00000011'), '100')
    await userEvent.click(screen.getByRole('button', { name: 'Aplicar' }))

    expect(await screen.findByText('El recibo ya no tiene ese disponible.')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Aplicar el recibo 0007-00000021' })).toBeInTheDocument()
    await waitFor(() => expect(recibosGetMock.mock.calls.length).toBeGreaterThan(recibosAntes))
  })

  it('tras un 409 y una recarga con menos disponible, el modal valida contra el disponible vigente y conserva lo tipeado', async () => {
    conCobros()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cobro_excede_disponible', 'El recibo ya no tiene ese disponible.'))
    await renderConCobros()
    recibosGetMock.mockImplementation(() =>
      Promise.resolve<ReciboDeCliente[]>([{ ...recibosFixture[0], aplicado: 350, disponible: 100 }, recibosFixture[1]]),
    )

    await userEvent.click(screen.getByRole('button', { name: 'Aplicar el recibo 0007-00000021' }))
    const modal = await screen.findByRole('dialog', { name: 'Aplicar el recibo 0007-00000021' })
    const importe = within(modal).getByLabelText('Importe a aplicar a la venta 0001-00000011')
    await userEvent.type(importe, '100')
    await userEvent.click(within(modal).getByRole('button', { name: 'Aplicar' }))

    expect(await within(modal).findByText('El recibo ya no tiene ese disponible.')).toBeInTheDocument()
    expect(await within(modal).findByText(/Disponible para aplicar: \$ 100,00/)).toBeInTheDocument()
    expect(importe).toHaveValue('100,00')

    await userEvent.type(within(modal).getByLabelText('Importe a aplicar a la venta 0001-00000012'), '50')
    expect((await within(modal).findAllByText(/supera el importe disponible/)).length).toBeGreaterThan(0)
  })

  it('si el recibo ya no tiene disponible tras la recarga, el modal se cierra con un aviso', async () => {
    conCobros()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cobro_excede_disponible', 'El recibo ya no tiene ese disponible.'))
    await renderConCobros()
    recibosGetMock.mockImplementation(() =>
      Promise.resolve<ReciboDeCliente[]>([{ ...recibosFixture[0], aplicado: 450, disponible: 0 }, recibosFixture[1]]),
    )

    await userEvent.click(screen.getByRole('button', { name: 'Aplicar el recibo 0007-00000021' }))
    await userEvent.type(await screen.findByLabelText('Importe a aplicar a la venta 0001-00000011'), '100')
    await userEvent.click(screen.getByRole('button', { name: 'Aplicar' }))

    expect(await screen.findByText(/El recibo 0007-00000021 ya no tiene importe disponible/)).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Aplicar el recibo 0007-00000021' })).not.toBeInTheDocument()
  })

  it('si la recarga de ventas falla tras un 409, el modal sigue abierto con el error y los importes', async () => {
    conCobros()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cobro_excede_disponible', 'El recibo ya no tiene ese disponible.'))
    await renderConCobros()
    ventasPendientesGetMock.mockImplementation(() =>
      Promise.reject(new ErrorApi(500, 'error', 'No se pudieron cargar las ventas pendientes.')),
    )

    await userEvent.click(screen.getByRole('button', { name: 'Aplicar el recibo 0007-00000021' }))
    const modal = await screen.findByRole('dialog', { name: 'Aplicar el recibo 0007-00000021' })
    await userEvent.type(within(modal).getByLabelText('Importe a aplicar a la venta 0001-00000011'), '100')
    await userEvent.click(within(modal).getByRole('button', { name: 'Aplicar' }))

    expect(await within(modal).findByText('No se pudieron cargar las ventas pendientes.')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Aplicar el recibo 0007-00000021' })).toBe(modal)
    expect(within(modal).getByLabelText('Importe a aplicar a la venta 0001-00000011')).toHaveValue('100,00')
    expect(within(modal).getByRole('button', { name: 'Aplicar' })).toBeDisabled()
  })

  it('al cambiar «Solo con disponible» los recibos anteriores dejan de ser accionables hasta que llega la respuesta', async () => {
    conCobros()
    const { recibos } = await renderConCobros()
    recibosGetMock.mockImplementation(() => new Promise(() => {}))

    await userEvent.click(within(recibos).getByLabelText('Solo con disponible'))

    expect(await within(recibos).findByText('Cargando recibos…')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Aplicar el recibo 0007-00000021' })).not.toBeInTheDocument()
  })

  it('al cambiar de cliente se descartan los recibos y las ventas del anterior antes de que llegue la nueva respuesta', async () => {
    conCobros()
    const props = { puedeSupervisar: true, versionExterna: 0, onEscritura: vi.fn() }
    const { rerender } = render(<SeccionesDeCobros idCliente={5} {...props} />)
    await screen.findByText('0007-00000021')
    await screen.findByText('0001-00000012')
    recibosGetMock.mockImplementation(() => new Promise(() => {}))
    ventasPendientesGetMock.mockImplementation(() => new Promise(() => {}))

    rerender(<SeccionesDeCobros idCliente={6} {...props} />)

    expect(screen.queryByRole('button', { name: 'Aplicar el recibo 0007-00000021' })).not.toBeInTheDocument()
    expect(screen.queryByText('0001-00000012')).not.toBeInTheDocument()
    expect(screen.getByText('Cargando recibos…')).toBeInTheDocument()
  })

  it('desaplicar pide confirmación, llama al servidor con el recibo y la aplicación y refresca', async () => {
    conCobros()
    apiDeleteMock.mockResolvedValue(estadoDeCobroFixture)
    await renderConCobros()
    const estadoAntes = llamadasAlEstado().length

    await userEvent.click(screen.getByRole('button', { name: 'Desaplicar $ 150,00 de la venta 0001-00000010' }))
    expect(apiDeleteMock).not.toHaveBeenCalled()
    await userEvent.click(await screen.findByRole('button', { name: 'Desaplicar' }))

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledTimes(1))
    expect(apiDeleteMock).toHaveBeenCalledWith('/cobros/21/aplicaciones/31')
    await toastDeExito('Cobro desaplicado: recibo 0007-00000021, venta 0001-00000010.')
    await waitFor(() => expect(llamadasAlEstado().length).toBeGreaterThan(estadoAntes))
  })

  it('cancelar la confirmación no desaplica nada', async () => {
    conCobros()
    await renderConCobros()

    await userEvent.click(screen.getByRole('button', { name: 'Desaplicar $ 150,00 de la venta 0001-00000010' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Cancelar' }))

    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.queryByRole('dialog', { name: 'Desaplicar cobro' })).not.toBeInTheDocument()
  })

  it('una falla al cargar los recibos se muestra en su sección sin tapar el resto de la pantalla', async () => {
    conCobros()
    recibosGetMock.mockImplementation(() => Promise.reject(new ErrorApi(500, 'error', 'No se pudieron cargar los recibos.')))
    renderPantalla()

    const recibos = await screen.findByRole('region', { name: 'Recibos' })
    expect(await within(recibos).findByText('No se pudieron cargar los recibos.')).toBeInTheDocument()
    expect(await screen.findByText('$ 500,00')).toBeInTheDocument()
  })
})
