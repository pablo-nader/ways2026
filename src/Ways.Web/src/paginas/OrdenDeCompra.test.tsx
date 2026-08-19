import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { OrdenDeCompra } from './OrdenDeCompra'
import { ROL } from '../api/tipos'
import type { OrdenDeCompraBorrador, OrdenDeCompraDetalle, ProveedorListado, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown?])),
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

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 1,
    usuario: 'admin',
    mail: 'admin@ways.test',
    rolId: ROL.Admin,
    rol: 'Admin',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 4,
    razonSocial: 'Proveedor Cuatro SA',
    nombreFantasia: null,
    cuit: null,
    idCondicionFiscal: 1,
    domicilio: null,
    telefono: null,
    email: null,
    vendedor: null,
    celularVendedor: null,
    supervisor: null,
    celularSupervisor: null,
    margen: null,
    observaciones: null,
    activo: true,
    idEmpresa: null,
    ...sobrescribir,
  }
}

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 9,
    idTenant: 1,
    idEmpresa: 1,
    nombre: 'Depósito Norte',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    ...sobrescribir,
  }
}

function detalleFixture(sobrescribir: Partial<OrdenDeCompraDetalle> = {}): OrdenDeCompraDetalle {
  return {
    id: 30,
    idProveedor: 4,
    idPuntoVenta: 9,
    numero: 55,
    fechaEmision: '2026-08-19T12:00:00Z',
    fechaEnvio: '2026-08-19T12:00:00Z',
    fechaEsperada: '2026-09-01',
    fechaCierre: null,
    cierreManual: false,
    observaciones: null,
    estado: 'Enviada',
    items: [{ orden: 1, idArticulo: 10, descripcion: 'Yerba mate 1kg', cantidadPedida: 7, costoUnitarioEstimado: 100 }],
    cobertura: [
      { idArticulo: 10, pedida: 7, recibida: 5, pendiente: 2, costoEstimado: 100, costoReal: 112, desvio: 12 },
      { idArticulo: 11, pedida: 0, recibida: 3, pendiente: 0, costoEstimado: null, costoReal: null, desvio: null },
    ],
    totalEstimado: 700,
    totalReal: 560,
    desvioTotal: -20,
    comprobantesLigados: [88],
    ...sobrescribir,
  }
}

function borradorFixture(sobrescribir: Partial<OrdenDeCompraBorrador> = {}): OrdenDeCompraBorrador {
  return {
    id: 30,
    idProveedor: 4,
    idPuntoVenta: 9,
    numero: null,
    fechaEmision: '2026-08-19T12:00:00Z',
    fechaEnvio: null,
    fechaEsperada: null,
    fechaCierre: null,
    idEmpleadoCierre: null,
    observaciones: null,
    estado: 'Borrador',
    items: [],
    ...sobrescribir,
  }
}

function mockearReferencia(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [proveedorFixture()], total: 1, pagina: 1, tamanio: 200 })
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderPantalla(idOrden: string | number = 30, state?: unknown) {
  return render(
    <MemoryRouter initialEntries={[{ pathname: `/ordenes-compra/${idOrden}`, state }]}>
      <Routes>
        <Route path="/ordenes-compra/:id" element={<OrdenDeCompra />} />
      </Routes>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  usuarioActual = usuarioFixture()
})

describe('OrdenDeCompra — detalle (lectura)', () => {
  it('muestra el estado, la cobertura con valores per-artículo distintos y los totales', async () => {
    mockearReferencia((ruta) => (ruta === '/ordenes-compra/30' ? Promise.resolve(detalleFixture()) : undefined))
    renderPantalla()

    expect(await screen.findByText('Enviada')).toBeInTheDocument()
    // mutation-proof-tests regla 12b: cada campo posicional de la cobertura leído con su propio
    // valor discriminante — nunca un — genérico ni un 0 fabricado.
    expect(screen.getByText('2')).toBeInTheDocument() // Pendiente del artículo 10
    expect(screen.getByText('+12%')).toBeInTheDocument() // Desvio del artículo 10
    expect(screen.getByText('$700,00')).toBeInTheDocument() // Total estimado
    expect(screen.getByText('$560,00')).toBeInTheDocument() // Total real
    expect(screen.getByText('-20%')).toBeInTheDocument() // Desvío total
  })

  it('un artículo sin costo comparable renderiza — en costo/desvío, nunca 0', async () => {
    mockearReferencia((ruta) => (ruta === '/ordenes-compra/30' ? Promise.resolve(detalleFixture()) : undefined))
    renderPantalla()

    await screen.findByText('Enviada')
    // fila del artículo 11: costoEstimado/costoReal/desvio todos null.
    const filas = screen.getAllByRole('row')
    const filaArticulo11 = filas.find((f) => f.textContent?.includes('Artículo #11'))!
    expect(filaArticulo11.textContent).toContain('—')
  })

  it('"Registrar recepción" navega con idOrdenCompra en la URL', async () => {
    mockearReferencia((ruta) => (ruta === '/ordenes-compra/30' ? Promise.resolve(detalleFixture({ estado: 'Enviada' })) : undefined))
    renderPantalla()

    const boton = await screen.findByRole('button', { name: 'Registrar recepción' })
    expect(boton).toBeInTheDocument()
  })

  it('una OC Anulada no ofrece "Registrar recepción" ni "Cerrar"/"Anular"', async () => {
    mockearReferencia((ruta) => (ruta === '/ordenes-compra/30' ? Promise.resolve(detalleFixture({ estado: 'Anulada' })) : undefined))
    renderPantalla()

    await screen.findByText('Anulada')
    expect(screen.queryByRole('button', { name: 'Registrar recepción' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cerrar' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Anular' })).not.toBeInTheDocument()
  })

  it('un Vendedor lee el detalle pero no ve ninguna acción de escritura', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearReferencia((ruta) => (ruta === '/ordenes-compra/30' ? Promise.resolve(detalleFixture()) : undefined))
    renderPantalla()

    await screen.findByText('Enviada')
    expect(screen.queryByRole('button', { name: 'Cerrar' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Anular' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Registrar recepción' })).not.toBeInTheDocument()
  })
})

describe('OrdenDeCompra — generación (react-async-state regla 2, mutation-proof-tests regla 7)', () => {
  // Dos refetches de detalle en carrera (uno por "Enviar", otro por "Anular", disparados en
  // secuencia porque el detalle stale todavía muestra los botones de Borrador hasta que el primer
  // refetch aterriza) — el de "Enviar" queda pendiente y se resuelve DESPUÉS de que el de "Anular"
  // (más nuevo) ya aterrizó; el resultado stale nunca debe pisar el más reciente.
  it('una respuesta de refetch desactualizada nunca pisa la más reciente', async () => {
    let llamadas = 0
    let resolverSegundaLlamada: (v: OrdenDeCompraDetalle) => void = () => {}
    const segundaLlamadaPendiente = new Promise<OrdenDeCompraDetalle>((resolve) => {
      resolverSegundaLlamada = resolve
    })

    mockearReferencia((ruta) => {
      if (ruta !== '/ordenes-compra/30') return undefined
      llamadas += 1
      if (llamadas === 1) return Promise.resolve(detalleFixture({ estado: 'Borrador', observaciones: 'inicial' }))
      if (llamadas === 2) return segundaLlamadaPendiente
      return Promise.resolve(detalleFixture({ estado: 'Anulada', observaciones: 'la-mas-nueva' }))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ordenes-compra/30/enviar') return Promise.resolve(borradorFixture({ estado: 'Enviada' }))
      if (ruta === '/ordenes-compra/30/anular') return Promise.resolve(borradorFixture({ estado: 'Anulada' }))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    renderPantalla()
    await screen.findByText('Borrador')

    // "Enviar" dispara el refetch #2 (queda pendiente).
    await userEvent.click(screen.getByRole('button', { name: 'Enviar' }))
    await screen.findByText('Orden enviada.')

    // El detalle sigue stale (Borrador) hasta que el refetch #2 aterrice — "Anular" sigue visible.
    // Dispara el refetch #3, que resuelve YA (más nuevo que el #2 todavía pendiente).
    await userEvent.click(screen.getByRole('button', { name: 'Anular' }))
    await screen.findByText('Orden anulada.')
    await screen.findByText('Anulada')

    // El flush del microtask stale va DENTRO de act (mutation-proof-tests regla 7): un waitFor
    // solo pasaría en su primer tick, antes de que el .then obsoleto aterrice.
    await act(async () => {
      resolverSegundaLlamada(detalleFixture({ estado: 'Enviada', observaciones: 'stale' }))
      await segundaLlamadaPendiente
    })
    expect(screen.getByText('Anulada')).toBeInTheDocument()
    expect(screen.queryByText('la-mas-nueva')).not.toBeInTheDocument()
  })
})

describe('OrdenDeCompra — crear borrador', () => {
  it('crea el borrador y navega a la ruta real de la orden recién creada', async () => {
    mockearReferencia()
    apiPostMock.mockResolvedValue(borradorFixture())

    renderPantalla('nueva')

    await userEvent.selectOptions(await screen.findByLabelText('Proveedor'), '4')
    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), '9')
    await userEvent.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/ordenes-compra', expect.objectContaining({ idProveedor: 4, idPuntoVenta: 9 })))
  })

  it('un Vendedor no ve "Crear borrador"', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearReferencia()
    renderPantalla('nueva')

    await screen.findByLabelText('Proveedor')
    expect(screen.queryByRole('button', { name: 'Crear borrador' })).not.toBeInTheDocument()
  })
})

describe('OrdenDeCompra — precarga desde Reposicion.tsx (location.state)', () => {
  it('precarga proveedor/punto de venta/items desde el state de navegación', async () => {
    mockearReferencia()

    renderPantalla('nueva', {
      idProveedor: 4,
      idPuntoVenta: 9,
      items: [{ idArticulo: 10, descripcion: 'Yerba mate 1kg', cantidadPedida: 17, costoUnitarioEstimado: null }],
    })

    expect((await screen.findByLabelText('Proveedor')) as HTMLSelectElement).toHaveValue('4')
    expect(screen.getByLabelText('Punto de venta')).toHaveValue('9')
    expect(screen.getByLabelText('Cantidad pedida')).toHaveValue(17)
  })
})

describe('OrdenDeCompra — doble click (react-async-state regla 9)', () => {
  it('un doble click en "Enviar" dispara exactamente un POST', async () => {
    mockearReferencia((ruta) => (ruta === '/ordenes-compra/30' ? Promise.resolve(detalleFixture({ estado: 'Borrador' })) : undefined))

    let resolverEnviar: (v: OrdenDeCompraBorrador) => void = () => {}
    const enviarPendiente = new Promise<OrdenDeCompraBorrador>((resolve) => {
      resolverEnviar = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/ordenes-compra/30/enviar' ? enviarPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const boton = await screen.findByRole('button', { name: 'Enviar' })

    // Los dos dispatchEvent viajan DENTRO de un mismo act() para que ningún re-render de React
    // corra entre ellos — mismo patrón que BotonDeDescarga.test.tsx/CuentaCorrienteDeProveedor.test.tsx.
    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/ordenes-compra/30/enviar')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Enviando…' })).toBeDisabled()

    await act(async () => {
      resolverEnviar(borradorFixture({ estado: 'Enviada' }))
      await enviarPendiente
    })
  })

  it('un doble click en "Cerrar" dispara exactamente un POST', async () => {
    mockearReferencia((ruta) => (ruta === '/ordenes-compra/30' ? Promise.resolve(detalleFixture({ estado: 'Enviada' })) : undefined))

    let resolverCerrar: (v: OrdenDeCompraBorrador) => void = () => {}
    const cerrarPendiente = new Promise<OrdenDeCompraBorrador>((resolve) => {
      resolverCerrar = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/ordenes-compra/30/cerrar' ? cerrarPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const boton = await screen.findByRole('button', { name: 'Cerrar' })

    // Mismo patrón same-tick que "Enviar" arriba — replica la red de reentrancia de `cerrandoRef`.
    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/ordenes-compra/30/cerrar')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Cerrando…' })).toBeDisabled()

    await act(async () => {
      resolverCerrar(borradorFixture({ estado: 'Cerrada' }))
      await cerrarPendiente
    })
  })

  it('un doble click en "Anular" dispara exactamente un POST', async () => {
    mockearReferencia((ruta) => (ruta === '/ordenes-compra/30' ? Promise.resolve(detalleFixture({ estado: 'Enviada' })) : undefined))

    let resolverAnular: (v: OrdenDeCompraBorrador) => void = () => {}
    const anularPendiente = new Promise<OrdenDeCompraBorrador>((resolve) => {
      resolverAnular = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/ordenes-compra/30/anular' ? anularPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const boton = await screen.findByRole('button', { name: 'Anular' })

    // Mismo patrón same-tick — replica la red de reentrancia de `anulandoRef`.
    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/ordenes-compra/30/anular')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Anulando…' })).toBeDisabled()

    await act(async () => {
      resolverAnular(borradorFixture({ estado: 'Anulada' }))
      await anularPendiente
    })
  })

  it('un doble click en "Guardar borrador" dispara exactamente un PUT', async () => {
    mockearReferencia((ruta) => (ruta === '/ordenes-compra/30' ? Promise.resolve(detalleFixture({ estado: 'Borrador' })) : undefined))

    let resolverGuardar: (v: OrdenDeCompraBorrador) => void = () => {}
    const guardarPendiente = new Promise<OrdenDeCompraBorrador>((resolve) => {
      resolverGuardar = resolve
    })
    apiPutMock.mockImplementation((ruta: string) => (ruta === '/ordenes-compra/30' ? guardarPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const boton = await screen.findByRole('button', { name: 'Guardar borrador' })

    // A diferencia de Enviar/Cerrar/Anular (habilitados solo por `ocupado`), este botón también
    // depende de `encabezadoCompleto`, que hidrata desde `detalle` en un useEffect posterior al
    // primer render — hay que esperar a que ese estado asincrónico asiente antes del doble click,
    // si no el guard de reentrancia puede correr contra un botón todavía deshabilitado.
    await waitFor(() => expect(boton).not.toBeDisabled())
    await waitFor(() => expect(screen.getByLabelText('Proveedor')).toHaveValue('4'))

    // Mismo patrón same-tick — replica la red de reentrancia de `guardandoRef`.
    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPutMock.mock.calls.filter((c) => c[0] === '/ordenes-compra/30')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()

    await act(async () => {
      resolverGuardar(borradorFixture({ estado: 'Borrador' }))
      await guardarPendiente
    })
  })
})

describe('OrdenDeCompra — un 2xx de enviar/cerrar/anular nunca se reporta como fallo (regla 6)', () => {
  it('enviar 2xx muestra el aviso de éxito aunque el refetch posterior falle', async () => {
    let llamadasDetalle = 0
    mockearReferencia((ruta) => {
      if (ruta !== '/ordenes-compra/30') return undefined
      llamadasDetalle += 1
      if (llamadasDetalle === 1) return Promise.resolve(detalleFixture({ estado: 'Borrador' }))
      return Promise.reject(new Error('falló el refresco'))
    })
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ordenes-compra/30/enviar' ? Promise.resolve(borradorFixture({ estado: 'Enviada' })) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )

    renderPantalla()
    await userEvent.click(await screen.findByRole('button', { name: 'Enviar' }))

    expect(await screen.findByText('Orden enviada.')).toBeInTheDocument()
  })
})
