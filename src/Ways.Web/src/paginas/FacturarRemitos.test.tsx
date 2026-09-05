import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { FacturarRemitos } from './FacturarRemitos'
import type { ClienteListado, ComprobanteEmitido, MedioPagoListado, PaginaDeRemitos, ParametroResuelto, PuntoVentaListado, RemitoListado } from '../api/tipos'

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
    ...sobrescribir,
  }
}

function clienteFixture(sobrescribir: Partial<ClienteListado> = {}): ClienteListado {
  return {
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
    ...sobrescribir,
  }
}

function medioFixture(sobrescribir: Partial<MedioPagoListado> = {}): MedioPagoListado {
  return {
    id: 1,
    nombre: 'Efectivo',
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    activo: true,
    orden: 1,
    idEmpresa: null,
    recargoPorcentaje: null,
    ...sobrescribir,
  }
}

function remitoFixture(sobrescribir: Partial<RemitoListado> = {}): RemitoListado {
  return {
    id: 1,
    idPuntoVenta: 7,
    idCliente: 1,
    numero: 12,
    numeroFormateado: '0007-00000012',
    fechaEmision: '2026-08-01T12:00:00Z',
    total: 500,
    estado: 'Emitido',
    idComprobanteVenta: null,
    ...sobrescribir,
  }
}

function paginaFixture(sobrescribir: Partial<PaginaDeRemitos> = {}): PaginaDeRemitos {
  return { items: [remitoFixture()], total: 1, pagina: 1, tamanio: 200, ...sobrescribir }
}

function comprobanteFixture(sobrescribir: Partial<ComprobanteEmitido> = {}): ComprobanteEmitido {
  return {
    id: 90,
    numero: 5,
    numeroVisible: '0007-00000005',
    estado: 'Emitido',
    fecha: '2026-08-20T12:00:00Z',
    idPuntoVenta: 7,
    idCliente: 1,
    idComprobanteAsociado: null,
    subtotal: 500,
    descuentoTotal: 0,
    total: 500,
    direccionEntrega: null,
    observaciones: null,
    items: [],
    pagos: [],
    idPresupuestoOrigen: null,
    ...sobrescribir,
  }
}

function mockearRutasBase(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
    if (ruta === '/clientes') return Promise.resolve({ items: [clienteFixture()], total: 1, pagina: 1, tamanio: 25 })
    if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioFixture()])
    if (ruta.startsWith('/parametros/tolerancia_pago')) return Promise.resolve({ clave: 'tolerancia_pago', valor: '0' } satisfies ParametroResuelto)
    if (ruta.startsWith('/parametros/vuelto_maximo')) return Promise.resolve({ clave: 'vuelto_maximo', valor: '0' } satisfies ParametroResuelto)
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/remitos?')) return Promise.resolve(paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderPantalla() {
  return render(<FacturarRemitos />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

async function elegirClientePuntoVenta() {
  await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Centro')
  await userEvent.selectOptions(screen.getByLabelText('Cliente'), '#1 — Consumidor Final')
}

/** "Falta" y "Total a facturar" pueden mostrar el mismo monto formateado a la vez — se espera
 * al menos una aparición en vez de una única coincidencia (evita el falso "multiple elements"
 * de `findByText` cuando dos textos legítimos comparten el mismo valor). */
async function esperarMonto(texto: string) {
  await waitFor(() => expect(screen.getAllByText(texto).length).toBeGreaterThan(0))
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
})

describe('FacturarRemitos — picker cliente + punto de venta', () => {
  it('sin elegir ambos, muestra el mensaje guía y no lista remitos', async () => {
    mockearRutasBase()
    renderPantalla()

    expect(await screen.findByText(/Elegí un punto de venta y un cliente/)).toBeInTheDocument()
    expect(apiGetMock.mock.calls.some((c) => String(c[0]).startsWith('/remitos?'))).toBe(false)
  })

  it('al elegir ambos, lista los remitos emitido sin ligar de ese par', async () => {
    mockearRutasBase()
    renderPantalla()
    await screen.findByLabelText('Punto de venta')

    await elegirClientePuntoVenta()

    expect(await screen.findByText('0007-00000012')).toBeInTheDocument()
    await waitFor(() =>
      expect(apiGetMock).toHaveBeenCalledWith(expect.stringMatching(/^\/remitos\?.*idPuntoVenta=7.*idCliente=1.*estado=Emitido|^\/remitos\?.*idCliente=1.*idPuntoVenta=7.*estado=Emitido/)),
    )
  })

  it('sin remitos emitidos sin ligar, muestra el estado vacío', async () => {
    mockearRutasBase((ruta) => (ruta.startsWith('/remitos?') ? Promise.resolve(paginaFixture({ items: [], total: 0 })) : undefined))
    renderPantalla()
    await screen.findByLabelText('Punto de venta')

    await elegirClientePuntoVenta()

    expect(await screen.findByText(/no tiene remitos emitidos sin facturar/)).toBeInTheDocument()
  })
})

describe('FacturarRemitos — multi-select (task 8.8)', () => {
  it('elegir un remito suma su total al total a facturar', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/remitos?')
        ? Promise.resolve(paginaFixture({ items: [remitoFixture({ id: 1, total: 500 }), remitoFixture({ id: 2, numeroFormateado: '0007-00000013', total: 300 })] }))
        : undefined,
    )
    renderPantalla()
    await screen.findByLabelText('Punto de venta')
    await elegirClientePuntoVenta()
    await screen.findByText('0007-00000012')

    await userEvent.click(screen.getByLabelText('Elegir remito 0007-00000012'))
    await esperarMonto('$500,00')

    await userEvent.click(screen.getByLabelText('Elegir remito 0007-00000013'))
    await esperarMonto('$800,00')
  })

  it('"Elegir todos" selecciona/deselecciona el conjunto completo', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/remitos?')
        ? Promise.resolve(paginaFixture({ items: [remitoFixture({ id: 1, total: 500 }), remitoFixture({ id: 2, numeroFormateado: '0007-00000013', total: 300 })] }))
        : undefined,
    )
    renderPantalla()
    await screen.findByLabelText('Punto de venta')
    await elegirClientePuntoVenta()
    await screen.findByText('0007-00000012')

    await userEvent.click(screen.getByLabelText('Elegir todos'))
    await esperarMonto('$800,00')
    expect(screen.getByLabelText('Elegir remito 0007-00000012')).toBeChecked()
    expect(screen.getByLabelText('Elegir remito 0007-00000013')).toBeChecked()

    await userEvent.click(screen.getByLabelText('Elegir todos'))
    expect(screen.getByLabelText('Elegir remito 0007-00000012')).not.toBeChecked()
    expect(screen.getByLabelText('Elegir remito 0007-00000013')).not.toBeChecked()
  })

  it('cambiar el cliente/punto de venta limpia la selección previa', async () => {
    mockearRutasBase()
    renderPantalla()
    await screen.findByLabelText('Punto de venta')
    await elegirClientePuntoVenta()
    await screen.findByText('0007-00000012')

    await userEvent.click(screen.getByLabelText('Elegir remito 0007-00000012'))
    expect(await screen.findByText('Total a facturar (1 remito(s))')).toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), '')
    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Centro')

    // El checkbox de la fila vuelve a aparecer sin marcar — el conteo del resumen cae a 0, no
    // arrastra la selección del par cliente/PV anterior.
    await screen.findByText('0007-00000012')
    expect(screen.getByText('Total a facturar (0 remito(s))')).toBeInTheDocument()
    expect(screen.getByLabelText('Elegir remito 0007-00000012')).not.toBeChecked()
  })
})

describe('FacturarRemitos — guard de stale en cargarRemitos (mutation-proof-tests regla 7)', () => {
  it('una respuesta stale del cliente anterior no pisa la del cliente más nuevo, sobre la misma instancia montada', async () => {
    let resolverListaClienteA: (v: PaginaDeRemitos) => void = () => {}
    let promesaListaClienteA: Promise<PaginaDeRemitos> = Promise.resolve(paginaFixture())

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
      if (ruta === '/clientes')
        return Promise.resolve({ items: [clienteFixture(), clienteFixture({ id: 2, numero: 2, nombre: 'Cliente B' })], total: 2, pagina: 1, tamanio: 25 })
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioFixture()])
      if (ruta.startsWith('/parametros/tolerancia_pago')) return Promise.resolve({ clave: 'tolerancia_pago', valor: '0' } satisfies ParametroResuelto)
      if (ruta.startsWith('/parametros/vuelto_maximo')) return Promise.resolve({ clave: 'vuelto_maximo', valor: '0' } satisfies ParametroResuelto)
      // Cliente A (id 1): el fetch de sus remitos queda en vuelo — se resuelve MÁS TARDE, DESPUÉS
      // de que el cambio a Cliente B ya haya recargado.
      if (ruta.startsWith('/remitos?') && ruta.includes('idCliente=1')) {
        promesaListaClienteA = new Promise((resolve) => (resolverListaClienteA = resolve))
        return promesaListaClienteA
      }
      if (ruta.startsWith('/remitos?') && ruta.includes('idCliente=2')) {
        return Promise.resolve(paginaFixture({ items: [remitoFixture({ id: 9, idCliente: 2, numeroFormateado: '0007-00000099' })] }))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPantalla()
    await screen.findByLabelText('Punto de venta')
    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Centro')
    await userEvent.selectOptions(screen.getByLabelText('Cliente'), '#1 — Consumidor Final')
    // El fetch de remitos del Cliente A queda en vuelo — `resolverListaClienteA` todavía no se llamó.

    await userEvent.selectOptions(screen.getByLabelText('Cliente'), '#2 — Cliente B')
    await screen.findByText('0007-00000099')

    // regla 7: el flush del microtask stale va DENTRO de act — resolver DESPUÉS de que el
    // cliente más nuevo ya aterrizó no puede pisar la lista mostrada.
    await act(async () => {
      resolverListaClienteA(paginaFixture({ items: [remitoFixture({ id: 1, numeroFormateado: '0007-00000012' })] }))
      await promesaListaClienteA
    })

    expect(screen.getByText('0007-00000099')).toBeInTheDocument()
    expect(screen.queryByText('0007-00000012')).not.toBeInTheDocument()
  })
})

describe('FacturarRemitos — doble click en "Facturar" (react-async-state regla 9, tarea 8.10)', () => {
  it('dispara exactamente un POST', async () => {
    mockearRutasBase()
    renderPantalla()
    await screen.findByLabelText('Punto de venta')
    await elegirClientePuntoVenta()
    await screen.findByText('0007-00000012')
    await userEvent.click(screen.getByLabelText('Elegir remito 0007-00000012'))

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.type(screen.getByLabelText(/Importe de Efectivo/), '500')

    let resolverFacturar: (v: ComprobanteEmitido) => void = () => {}
    const facturarPendiente = new Promise<ComprobanteEmitido>((resolve) => {
      resolverFacturar = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/remitos/facturacion' ? facturarPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    const boton = await screen.findByRole('button', { name: 'Facturar' })
    expect(boton).toBeEnabled()

    // Los dos dispatchEvent viajan DENTRO de un mismo act() — jsdom no no-opea un click sobre un
    // elemento disabled, mismo patrón que Presupuesto.test.tsx/Remito.test.tsx.
    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/remitos/facturacion')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Facturando…' })).toBeDisabled()

    await act(async () => {
      resolverFacturar(comprobanteFixture())
      await facturarPendiente
    })

    expect(await screen.findByText(/Comprobante/)).toBeInTheDocument()
  })

  it('el POST nunca manda idCliente (dto-contract-honesty regla 1: el servidor lo deriva de los remitos)', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/remitos/facturacion' ? Promise.resolve(comprobanteFixture()) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))
    renderPantalla()
    await screen.findByLabelText('Punto de venta')
    await elegirClientePuntoVenta()
    await screen.findByText('0007-00000012')
    await userEvent.click(screen.getByLabelText('Elegir remito 0007-00000012'))
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.type(screen.getByLabelText(/Importe de Efectivo/), '500')

    await userEvent.click(screen.getByRole('button', { name: 'Facturar' }))

    // judgment-day Slice 8, ronda 1, juez B — MINOR: `expect.not.objectContaining` con
    // `expect.anything()` NO matchea un valor `null` explícito (el matcher de Jest/Vitest solo
    // discrimina "está ausente", no "es null") — assert fuerte: la KEY `idCliente` no existe en
    // absoluto en el body posteado.
    await waitFor(() => expect(apiPostMock).toHaveBeenCalled())
    const [, bodyIdCliente] = apiPostMock.mock.calls.find((c) => c[0] === '/remitos/facturacion')!
    expect(Object.keys(bodyIdCliente as object)).not.toContain('idCliente')
  })

  it('el POST lleva idsRemito EXACTAMENTE igual al subconjunto elegido, nunca todos los listados', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/remitos?')
        ? Promise.resolve(
            paginaFixture({
              items: [
                remitoFixture({ id: 1, total: 500 }),
                remitoFixture({ id: 2, numeroFormateado: '0007-00000013', total: 300 }),
                remitoFixture({ id: 3, numeroFormateado: '0007-00000014', total: 200 }),
              ],
            }),
          )
        : undefined,
    )
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/remitos/facturacion' ? Promise.resolve(comprobanteFixture()) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))
    renderPantalla()
    await screen.findByLabelText('Punto de venta')
    await elegirClientePuntoVenta()
    await screen.findByText('0007-00000012')

    // Subconjunto propio: remitos 1 y 3, NUNCA el 2 (listado pero sin tildar) — mutation target:
    // un mapper que mandara TODOS los remitos listados en vez de los tildados lo delataría acá.
    await userEvent.click(screen.getByLabelText('Elegir remito 0007-00000012'))
    await userEvent.click(screen.getByLabelText('Elegir remito 0007-00000014'))
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.type(screen.getByLabelText(/Importe de Efectivo/), '700')

    await userEvent.click(screen.getByRole('button', { name: 'Facturar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/remitos/facturacion', expect.objectContaining({ idsRemito: [1, 3] })))
  })
})
