import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ConteoDeInventario } from './ConteoDeInventario'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { ArticuloListado, LoteListado, PuntoVentaListado, ResultadoConteo, StockActual, UsuarioAutenticado } from '../api/tipos'

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

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 1,
    idTenant: 1,
    idEmpresa: 1,
    nombre: 'Casa Central',
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

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 10,
    codigoInterno: 'ART-10',
    nombre: 'Fideos 500g',
    descripcion: null,
    idArea: 1,
    idCategoria: null,
    idMarca: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 3,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    costoLista: null,
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
    ...sobrescribir,
  }
}

function renderConteo() {
  return render(<ConteoDeInventario />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

function renderConteoProtegido() {
  return render(
    <MemoryRouter initialEntries={['/stock/conteo']}>
      <Routes>
        <Route
          path="/stock/conteo"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Admin]}>
              <ConteoDeInventario />
            </RutaProtegida>
          }
        />
        <Route path="/" element={<div>Inicio (redirigido)</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

function mockearBase(stockActual: StockActual, sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
    if (ruta.startsWith('/articulos')) return Promise.resolve({ items: [articuloFixture()], total: 1, pagina: 1, tamanio: 25 })
    if (ruta.startsWith('/stock?')) return Promise.resolve(stockActual)
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function loteFixture(sobrescribir: Partial<LoteListado> = {}): LoteListado {
  return {
    idLote: 41,
    idArticulo: 10,
    codigo: '2026-08-20',
    fechaVencimiento: '2026-08-20',
    esSinIdentificar: false,
    cantidad: 12,
    estado: 'Vigente',
    sugerido: true,
    ...sobrescribir,
  }
}

/** stage-12-lotes-vencimientos (Slice 15) — variante lote-efectiva de `mockearBase`: el artículo
 * mockeado tiene `controlaLote: true` y `GET /api/stock/lotes` devuelve una lista fija de lotes.
 *
 * judgment-day (ronda juez A): el control EFECTIVO ahora es el AND con `lotes_habilitado` de la
 * empresa (`GET /api/parametros/lotes_habilitado`) — acá se mockea resuelto en `"true"` (el
 * módulo está ON), que es el escenario que estos tests ya asumían implícitamente. */
function mockearBaseConLote(
  stockActual: StockActual,
  lotes: LoteListado[] = [loteFixture({ idLote: 41 }), loteFixture({ idLote: 42, codigo: '2026-09-01' })],
  lotesHabilitado = 'true',
) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
    if (ruta.startsWith('/articulos')) return Promise.resolve({ items: [articuloFixture({ controlaLote: true })], total: 1, pagina: 1, tamanio: 25 })
    if (ruta.startsWith('/stock?')) return Promise.resolve(stockActual)
    if (ruta.startsWith('/stock/lotes?')) return Promise.resolve(lotes)
    if (ruta.startsWith('/parametros/lotes_habilitado')) return Promise.resolve({ clave: 'lotes_habilitado', valor: lotesHabilitado })
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

async function elegirPuntoVentaYArticulo(usuario: ReturnType<typeof userEvent.setup>) {
  await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '1')
  await usuario.type(screen.getByPlaceholderText('Buscar artículo…'), 'fideos')
  await screen.findByText('ART-10 — Fideos 500g')
  await usuario.click(screen.getByText('ART-10 — Fideos 500g'))
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  usuarioActual = usuarioFixture()
})

describe('ConteoDeInventario — flujo feliz', () => {
  it('muestra el stock actual al elegir punto de venta + artículo; doble click manda un solo POST y un conteo mayor produce un delta positivo', async () => {
    mockearBase({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    let resolverContar: (valor: ResultadoConteo) => void = () => {}
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/stock/conteos') return new Promise((resolve) => (resolverContar = resolve))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)

    await screen.findByText('40')

    await usuario.type(screen.getByLabelText('Cantidad contada'), '45')
    await usuario.type(screen.getByLabelText('Observaciones'), 'Recuento mensual')

    const boton = screen.getByRole('button', { name: 'Contar' })
    await usuario.click(boton)
    await usuario.click(boton)

    resolverContar({ idPuntoVenta: 1, idArticulo: 10, cantidad: 45, cantidadAnterior: 40, delta: 5, movimientoRegistrado: true })

    expect(await screen.findByText('Diferencia registrada: +5 (antes 40 → ahora 45).')).toBeInTheDocument()

    const llamadas = apiPostMock.mock.calls.filter((call: unknown[]) => call[0] === '/stock/conteos')
    expect(llamadas).toHaveLength(1)
    const [, cuerpo] = llamadas[0] as [string, Record<string, unknown>]
    expect(cuerpo).toEqual({ idPuntoVenta: 1, idArticulo: 10, contada: 45, observaciones: 'Recuento mensual' })
  })

  it('un conteo igual al stock actual se renderiza honestamente como no-op', async () => {
    mockearBase({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/stock/conteos')
        return Promise.resolve({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40, cantidadAnterior: 40, delta: 0, movimientoRegistrado: false })
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)
    await screen.findByText('40')

    await usuario.type(screen.getByLabelText('Cantidad contada'), '40')
    await usuario.type(screen.getByLabelText('Observaciones'), 'Confirmación de stock')
    await usuario.click(screen.getByRole('button', { name: 'Contar' }))

    expect(await screen.findByText('Sin diferencia — no se registró ningún movimiento.')).toBeInTheDocument()
  })

  it('un conteo menor al stock actual produce un delta negativo', async () => {
    mockearBase({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/stock/conteos')
        return Promise.resolve({ idPuntoVenta: 1, idArticulo: 10, cantidad: 33, cantidadAnterior: 40, delta: -7, movimientoRegistrado: true })
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)
    await screen.findByText('40')

    await usuario.type(screen.getByLabelText('Cantidad contada'), '33')
    await usuario.type(screen.getByLabelText('Observaciones'), 'Faltante detectado')
    await usuario.click(screen.getByRole('button', { name: 'Contar' }))

    expect(await screen.findByText('Diferencia registrada: -7 (antes 40 → ahora 33).')).toBeInTheDocument()
  })

  it('la respuesta manda: un GET desactualizado (40) nunca pisa la verdad de escritura del servidor (movimientoRegistrado + delta + cantidadAnterior)', async () => {
    // El GET previo dice 40 (posible venta concurrente de por medio); la respuesta del POST es la
    // única fuente de verdad — acá reporta un movimiento real con anterior=35, delta=5.
    mockearBase({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/stock/conteos')
        return Promise.resolve({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40, cantidadAnterior: 35, delta: 5, movimientoRegistrado: true })
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)
    await screen.findByText('40')

    await usuario.type(screen.getByLabelText('Cantidad contada'), '40')
    await usuario.type(screen.getByLabelText('Observaciones'), 'Recuento con venta concurrente')
    await usuario.click(screen.getByRole('button', { name: 'Contar' }))

    expect(await screen.findByText('Diferencia registrada: +5 (antes 35 → ahora 40).')).toBeInTheDocument()
    expect(screen.queryByText('Sin diferencia — no se registró ningún movimiento.')).not.toBeInTheDocument()
  })
})

describe('ConteoDeInventario — validaciones', () => {
  it('sin observaciones el botón queda deshabilitado', async () => {
    mockearBase({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)
    await usuario.type(screen.getByLabelText('Cantidad contada'), '45')

    expect(screen.getByRole('button', { name: 'Contar' })).toBeDisabled()
  })

  it('una cantidad contada negativa deja el botón deshabilitado (espejo de contada_invalida)', async () => {
    mockearBase({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)
    await usuario.type(screen.getByLabelText('Cantidad contada'), '-1')
    await usuario.type(screen.getByLabelText('Observaciones'), 'obs')

    expect(screen.getByRole('button', { name: 'Contar' })).toBeDisabled()
  })
})

describe('ConteoDeInventario — errores del servidor', () => {
  it('un fallo al cargar puntos de venta muestra un aviso y bloquea el envío', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.reject(new Error('caído'))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    renderConteo()

    expect(await screen.findByText(/No se pudieron cargar los puntos de venta\./)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Contar' })).toBeDisabled()
  })
})

describe('ConteoDeInventario — role gating', () => {
  it('un Vendedor es redirigido a "/" (ruta Admin-only, sin contraparte de lectura)', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearBase({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })

    renderConteoProtegido()

    await waitFor(() => expect(screen.getByText('Inicio (redirigido)')).toBeInTheDocument())
  })
})

// ---- Conteo por lote (stage-12-lotes-vencimientos, Slice 15) -----------------------------------

describe('ConteoDeInventario — conteo por lote (exactly-one-of)', () => {
  it('un artículo lote-efectivo muestra el grid por lote, nunca el campo agregado — exactly-one-of', async () => {
    mockearBaseConLote({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)

    await screen.findByText('2026-08-20')
    expect(screen.getByText('2026-09-01')).toBeInTheDocument()
    // Nunca las dos formas a la vez: el campo agregado desaparece cuando el grid por lote está.
    expect(screen.queryByLabelText('Cantidad contada')).not.toBeInTheDocument()
  })

  it('el botón queda deshabilitado hasta contar al menos un lote', async () => {
    mockearBaseConLote({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)
    await screen.findByText('2026-08-20')

    await usuario.type(screen.getByLabelText('Observaciones'), 'Recuento por lote')
    expect(screen.getByRole('button', { name: 'Contar' })).toBeDisabled()

    await usuario.type(screen.getByLabelText('Contada del lote 2026-08-20'), '10')
    expect(screen.getByRole('button', { name: 'Contar' })).not.toBeDisabled()
  })

  it('contador de lotes incompletos: baja a medida que se cuentan lotes (mirror de Transferencias.tsx)', async () => {
    mockearBaseConLote({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)
    await screen.findByText('2026-08-20')

    expect(screen.getByText('2 lote(s) sin contar — no se van a incluir en el conteo.')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Contada del lote 2026-08-20'), '10')
    expect(screen.getByText('1 lote(s) sin contar — no se van a incluir en el conteo.')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Contada del lote 2026-09-01'), '5')
    expect(screen.queryByText(/lote\(s\) sin contar/)).not.toBeInTheDocument()
  })

  it('envía la rama por lote — contada:null, lotes solo con los completos, exactamente un lote sin tocar queda afuera', async () => {
    mockearBaseConLote({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/stock/conteos')
        return Promise.resolve({
          idPuntoVenta: 1,
          idArticulo: 10,
          cantidad: 22,
          cantidadAnterior: 12,
          delta: 10,
          movimientoRegistrado: true,
          lotes: [{ idLote: 41, cantidad: 22, cantidadAnterior: 12, delta: 10, movimientoRegistrado: true }],
        })
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)
    await screen.findByText('2026-08-20')

    await usuario.type(screen.getByLabelText('Contada del lote 2026-08-20'), '22')
    await usuario.type(screen.getByLabelText('Observaciones'), 'Recuento por lote')
    await usuario.click(screen.getByRole('button', { name: 'Contar' }))

    expect(await screen.findByText('Diferencia registrada: +10 (antes 12 → ahora 22).')).toBeInTheDocument()
    const llamadas = apiPostMock.mock.calls.filter((call: unknown[]) => call[0] === '/stock/conteos')
    expect(llamadas).toHaveLength(1)
    const [, cuerpo] = llamadas[0] as [string, Record<string, unknown>]
    expect(cuerpo).toEqual({
      idPuntoVenta: 1,
      idArticulo: 10,
      contada: null,
      observaciones: 'Recuento por lote',
      lotes: [{ idLote: 41, contada: 22 }],
    })
  })

  it('un artículo SIN control de lote nunca muestra el grid por lote', async () => {
    mockearBase({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)

    expect(screen.queryByText('Conteo por lote')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Cantidad contada')).toBeInTheDocument()
  })
})

// ---- Control efectivo de lote — AND con `lotes_habilitado` (judgment-day, Slice 15, ronda A) ---

describe('ConteoDeInventario — control efectivo de lote (judgment-day fix, ronda juez A)', () => {
  it('(a) artículo con controlaLote pero módulo de lotes apagado (lotes_habilitado=false): cae al formulario agregado, usable, submit OK — sin dead-end', async () => {
    mockearBaseConLote({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 }, [], 'false')
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/stock/conteos')
        return Promise.resolve({ idPuntoVenta: 1, idArticulo: 10, cantidad: 45, cantidadAnterior: 40, delta: 5, movimientoRegistrado: true })
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)

    // el AND con `lotes_habilitado=false` cae al agregado: nunca la grilla por lote, aunque el
    // artículo tenga `controlaLote: true` — antes del fix, esto quedaba en un grid vacío sin
    // ninguna línea completable (dead-end permanente).
    await waitFor(() => expect(screen.getByLabelText('Cantidad contada')).toBeInTheDocument())
    expect(screen.queryByText('Conteo por lote')).not.toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Cantidad contada'), '45')
    await usuario.type(screen.getByLabelText('Observaciones'), 'Recuento con módulo apagado')

    expect(screen.getByRole('button', { name: 'Contar' })).not.toBeDisabled()
    await usuario.click(screen.getByRole('button', { name: 'Contar' }))

    expect(await screen.findByText('Diferencia registrada: +5 (antes 40 → ahora 45).')).toBeInTheDocument()
    const llamadas = apiPostMock.mock.calls.filter((call: unknown[]) => call[0] === '/stock/conteos')
    expect(llamadas).toHaveLength(1)
    const [, cuerpo] = llamadas[0] as [string, Record<string, unknown>]
    expect(cuerpo).toEqual({ idPuntoVenta: 1, idArticulo: 10, contada: 45, observaciones: 'Recuento con módulo apagado' })
  })

  it('(b) artículo con controlaLote y módulo de lotes prendido (lotes_habilitado=true): muestra la grilla por lote, como antes del fix', async () => {
    mockearBaseConLote({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)

    await screen.findByText('2026-08-20')
    expect(screen.queryByLabelText('Cantidad contada')).not.toBeInTheDocument()
  })

  it('(c) caso borde elegido: si la resolución de lotes_habilitado falla (red caída), cae al agregado en vez de bloquear al operador', async () => {
    // `mockearBaseConLote` con la ruta de `lotes_habilitado` sobrescrita a un reject.
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
      if (ruta.startsWith('/articulos')) return Promise.resolve({ items: [articuloFixture({ controlaLote: true })], total: 1, pagina: 1, tamanio: 25 })
      if (ruta.startsWith('/stock?')) return Promise.resolve({ idPuntoVenta: 1, idArticulo: 10, cantidad: 40 })
      if (ruta.startsWith('/parametros/lotes_habilitado')) return Promise.reject(new Error('caído'))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderConteo()
    await screen.findByLabelText('Punto de venta')
    await elegirPuntoVentaYArticulo(usuario)

    // Fetch fallido → `lotesHabilitado` se queda en `null` → default agregado (honesto, mismo
    // default que el servidor), nunca un dead-end.
    await waitFor(() => expect(screen.getByLabelText('Cantidad contada')).toBeInTheDocument())
    expect(screen.queryByText('Conteo por lote')).not.toBeInTheDocument()
  })
})
