import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CuentaCorriente } from './CuentaCorriente'
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
  ParametroResuelto,
  PuntoVentaListado,
  ResultadoDeReliquidacion,
  UsuarioAutenticado,
} from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
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
    ...sobrescribir,
  }
}

// `importe`/`saldoResultante` deliberadamente distintos del `saldo` del header (500) y entre sí,
// para que ningún assert de "$500,00" choque con una fila de la tabla en los tests.
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
    total: 500,
    direccionEntrega: null,
    observaciones: null,
    items: [],
    pagos: [{ idMedioPago: 1, importe: 500, referencia: null, vuelto: 0 }],
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
    </MemoryRouter>,
  )
}

/** Rutas comunes a toda la pantalla (cliente + medios de pago + puntos de venta + estado de
 * cuenta + vuelto_maximo + preview de reliquidación) — un override toma prioridad sobre el
 * default para que un test pueda reemplazar cualquiera de estas rutas base. `GET /clientes/:id`
 * cubre el fetch de identidad cuando no llega `location.state` (Fix 2: único camino del Vendedor /
 * cualquier refresh). El preview de reliquidación se chequea ANTES que el catch-all de
 * `/cuenta-corriente` — su ruta también contiene ese substring. */
function mockearRutasBase(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    if (/^\/clientes\/\d+$/.test(ruta)) return Promise.resolve<ClienteListado>(clienteFixture())
    if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
    if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
    if (ruta.startsWith('/parametros/vuelto_maximo')) {
      return Promise.resolve<ParametroResuelto>({ clave: 'vuelto_maximo', valor: '20' })
    }
    if (ruta.includes('/cuenta-corriente/reliquidacion')) {
      return Promise.resolve<ResultadoDeReliquidacion>(resultadoReliquidacionNoOpFixture())
    }
    if (ruta.includes('/cuenta-corriente')) return Promise.resolve<EstadoDeCuenta>(estadoFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

/** Abre el modal de pago y completa una fila válida (Efectivo, importe dado) — espera a que
 * "Registrar pago" esté habilitado (vuelto_maximo ya resuelto). */
async function abrirModalYCompletarFila(importe = '500') {
  renderPantalla()
  await screen.findByText('$500,00')
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
  await screen.findByText('$500,00')
  await userEvent.click(screen.getByRole('button', { name: 'Ajuste manual' }))
  await screen.findByText('Ajuste manual de cuenta corriente')
}

/** Abre el modal de reliquidación — el preview se dispara automáticamente al montar. */
async function abrirModalReliquidacion() {
  renderPantalla()
  await screen.findByText('$500,00')
  await userEvent.click(screen.getByRole('button', { name: 'Actualizar precios' }))
  await screen.findByText('Actualizar precios (reliquidación)')
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  usuarioActual = usuarioFixture()
})

describe('CuentaCorriente — header y ledger', () => {
  it('muestra saldo, límite de crédito y disponibilidad', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$500,00')
    expect(screen.getByText('$5.000,00')).toBeInTheDocument()
    expect(screen.getByText('$4.500,00')).toBeInTheDocument()
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

    await screen.findByText('$500,00')
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

    await screen.findByText('$500,00')
    expect(await screen.findByText(/No se pudo confirmar el cliente\./)).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ingresar pago' })).toBeDisabled())
  })

  it('un Consumidor Final llegado por URL directa (sin state) también deja "Ingresar pago" deshabilitado con el aviso de CF, una vez resuelta la identidad', async () => {
    const cf = clienteFixture({ esConsumidorFinal: true, nombre: 'Consumidor', apellido: 'Final' })
    mockearRutasBase((ruta) => (/^\/clientes\/\d+$/.test(ruta) ? Promise.resolve<ClienteListado>(cf) : undefined))

    renderPantalla()

    await screen.findByText('$500,00')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ingresar pago' })).toBeDisabled())
    expect(screen.getByRole('button', { name: 'Ingresar pago' })).toHaveAttribute(
      'title',
      'El Consumidor Final no tiene cuenta corriente.',
    )
  })
})

describe('CuentaCorriente — filtros (react-async-state regla 2)', () => {
  it('la ventana por defecto (design.md: "último mes") se precarga en Desde/Hasta y viaja en la consulta inicial', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$500,00')

    const inputDesde = screen.getByLabelText('Desde') as HTMLInputElement
    const inputHasta = screen.getByLabelText('Hasta') as HTMLInputElement
    expect(inputDesde.value).not.toBe('')
    expect(inputHasta.value).not.toBe('')

    const llamadaEstado = apiGetMock.mock.calls.find((c) => (c[0] as string).includes('/cuenta-corriente'))
    const query = decodeURIComponent(llamadaEstado?.[0] as string)
    expect(query).toContain(`desde=${inputDesde.value}T00:00:00`)
    expect(query).toContain(`hasta=${inputHasta.value}T23:59:59.999`)
  })

  it('"Ver histórico completo" limpia los inputs Desde/Hasta — la ventana en efecto (sin recorte) queda visible', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$500,00')
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

    await waitFor(() => expect(screen.getByText('$999,00')).toBeInTheDocument())

    // La respuesta obsoleta (de la generación anterior) llega tarde con datos distintos — no
    // puede pisar lo que ya se muestra.
    await act(async () => {
      resolverSegunda(estadoFixture({ movimientos: [movimientoFixture({ id: 2, importe: 777, saldoResultante: 888 })] }))
      await Promise.resolve()
    })
    expect(screen.getByText('$999,00')).toBeInTheDocument()
    expect(screen.queryByText('$777,00')).not.toBeInTheDocument()
    expect(screen.queryByText('$888,00')).not.toBeInTheDocument()
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
    await screen.findByText('$500,00')
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

    await screen.findByText(/Pago registrado/)

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

    expect(await screen.findByText('Pago registrado: comprobante 0007-00000003.')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByText('No se pudo cargar el estado de cuenta.')).toBeInTheDocument())
    // el aviso de éxito del pago sigue en pantalla — el fallo del refetch no lo pisa.
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
    expect(screen.getByLabelText('Importe')).toHaveValue(500)
  })
})

describe('CuentaCorriente — gating de rol (Supervisor+Admin) para ajuste y reliquidación', () => {
  it('un Vendedor no ve las acciones de ajuste ni reliquidación', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$500,00')
    expect(screen.queryByRole('button', { name: 'Ajuste manual' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Actualizar precios' })).not.toBeInTheDocument()
    // el pago sigue disponible para cualquier rol — OperacionDePos, no SupervisionDeCuentaCorriente.
    expect(screen.getByRole('button', { name: 'Ingresar pago' })).toBeInTheDocument()
  })

  it('un Supervisor ve ambas acciones, habilitadas una vez cargados los datos', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$500,00')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Ajuste manual' })).not.toBeDisabled())
    expect(screen.getByRole('button', { name: 'Actualizar precios' })).not.toBeDisabled()
  })

  it('un Admin también ve ambas acciones', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Admin, rol: 'Admin' })
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('$500,00')
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
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(screen.getByText('El importe del ajuste no puede ser cero.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un detalle recortado por debajo de 5 caracteres se rechaza localmente (ajuste_detalle_requerido)', async () => {
    mockearRutasBase()
    await abrirModalAjuste()

    await userEvent.type(screen.getByLabelText('Importe'), '40')
    await userEvent.type(screen.getByLabelText('Detalle (obligatorio)'), '  abcd  ')
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
    expect(screen.getByText('Saldo resultante: $540,00')).toBeInTheDocument()

    await userEvent.clear(screen.getByLabelText('Importe'))
    await userEvent.type(screen.getByLabelText('Importe'), '-50')
    expect(screen.getByText('Saldo resultante: $450,00')).toBeInTheDocument()
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
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await screen.findByText(/Ajuste registrado/)

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
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText(/Ajuste registrado/)).toBeInTheDocument()
    await waitFor(() => expect(screen.getByText('No se pudo cargar el estado de cuenta.')).toBeInTheDocument())
    // el aviso de éxito del ajuste sigue en pantalla — el fallo del refetch no lo pisa.
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
    await within(dialogo).findByText('$40,00')
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
    await screen.findByText('$40,00')
    await userEvent.click(screen.getByLabelText(/Confirmo que quiero actualizar los precios/))
    await userEvent.click(screen.getByRole('button', { name: 'Ejecutar reliquidación' }))

    await screen.findByText(/Precios actualizados/)

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
    await screen.findByText('$40,00')
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
    await screen.findByText('$40,00')
    await userEvent.click(screen.getByLabelText(/Confirmo que quiero actualizar los precios/))
    await userEvent.click(screen.getByRole('button', { name: 'Ejecutar reliquidación' }))

    expect(await screen.findByText('No había nada para actualizar.')).toBeInTheDocument()
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
    await screen.findByText('$40,00')
    await userEvent.click(screen.getByLabelText(/Confirmo que quiero actualizar los precios/))
    await userEvent.click(screen.getByRole('button', { name: 'Ejecutar reliquidación' }))

    expect(await screen.findByText(/Precios actualizados/)).toBeInTheDocument()
    await waitFor(() => expect(screen.getByText('No se pudo cargar el estado de cuenta.')).toBeInTheDocument())
    // el aviso de éxito de la reliquidación sigue en pantalla — el fallo del refetch no lo pisa.
    expect(screen.getByText(/Precios actualizados/)).toBeInTheDocument()
  })
})
