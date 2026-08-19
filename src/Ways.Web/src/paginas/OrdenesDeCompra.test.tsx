import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { OrdenesDeCompra } from './OrdenesDeCompra'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { OrdenDeCompraListada, PaginaDeOrdenesDeCompra, ProveedorListado, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

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
    id: 1,
    razonSocial: 'Proveedor Uno SA',
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

function ordenFixture(sobrescribir: Partial<OrdenDeCompraListada> = {}): OrdenDeCompraListada {
  return {
    id: 1,
    idProveedor: 1,
    idPuntoVenta: 7,
    numero: 12,
    fechaEmision: '2026-08-01T12:00:00Z',
    fechaEsperada: '2026-08-15',
    estado: 'Enviada',
    ...sobrescribir,
  }
}

function paginaFixture(sobrescribir: Partial<PaginaDeOrdenesDeCompra> = {}): PaginaDeOrdenesDeCompra {
  return { items: [ordenFixture()], total: 1, pagina: 1, tamanio: 25, ...sobrescribir }
}

function mockearRutasBase(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [proveedorFixture()], total: 1, pagina: 1, tamanio: 200 })
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/ordenes-compra?')) return Promise.resolve(paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderPantalla() {
  return render(<OrdenesDeCompra />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

function renderPantallaProtegida() {
  return render(
    <MemoryRouter initialEntries={['/ordenes-compra']}>
      <Routes>
        <Route
          path="/ordenes-compra"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
              <OrdenesDeCompra />
            </RutaProtegida>
          }
        />
        <Route path="/" element={<div>Inicio (redirigido)</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  apiGetMock.mockReset()
  usuarioActual = usuarioFixture()
})

describe('OrdenesDeCompra — listado', () => {
  it('muestra la fila con número, proveedor, punto de venta y estado', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('12')
    const fila = screen.getByText('12').closest('tr')!
    expect(within(fila).getByText('Proveedor Uno SA')).toBeInTheDocument()
    expect(within(fila).getByText('Local Centro')).toBeInTheDocument()
    expect(within(fila).getByText('Enviada')).toBeInTheDocument()
  })

  it('una orden sin número muestra #id', async () => {
    mockearRutasBase((ruta) => (ruta.startsWith('/ordenes-compra?') ? Promise.resolve(paginaFixture({ items: [ordenFixture({ numero: null })] })) : undefined))
    renderPantalla()

    expect(await screen.findByText('#1')).toBeInTheDocument()
  })

  it('sin resultados muestra el estado vacío', async () => {
    mockearRutasBase((ruta) => (ruta.startsWith('/ordenes-compra?') ? Promise.resolve(paginaFixture({ items: [], total: 0 })) : undefined))
    renderPantalla()

    expect(await screen.findByText('No hay órdenes de compra que coincidan con los filtros.')).toBeInTheDocument()
  })
})

describe('OrdenesDeCompra — filtros (react-async-state regla 2, mutation-proof-tests regla 7)', () => {
  it('una respuesta desactualizada nunca pisa la más reciente', async () => {
    let resolverPrimera: (v: PaginaDeOrdenesDeCompra) => void = () => {}
    const primeraPendiente = new Promise<PaginaDeOrdenesDeCompra>((resolve) => {
      resolverPrimera = resolve
    })
    let llamadas = 0

    mockearRutasBase((ruta) => {
      if (!ruta.startsWith('/ordenes-compra?')) return undefined
      llamadas += 1
      if (llamadas === 1) return Promise.resolve(paginaFixture())
      if (llamadas === 2) return primeraPendiente
      if (llamadas === 3) return Promise.resolve(paginaFixture({ items: [ordenFixture({ id: 3, numero: 999 })] }))
      return Promise.reject(new Error('llamada inesperada'))
    })

    renderPantalla()
    await screen.findByLabelText('Estado')

    // 1ra: cambia estado (queda pendiente, lenta).
    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'Enviada')
    // 2da: lo cambia de nuevo — dispara una generación MÁS NUEVA, que resuelve rápido.
    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'Cerrada')

    await waitFor(() => expect(screen.getByText('999')).toBeInTheDocument())

    await act(async () => {
      resolverPrimera(paginaFixture({ items: [ordenFixture({ id: 2, numero: 777 })] }))
      await primeraPendiente
    })
    expect(screen.getByText('999')).toBeInTheDocument()
    expect(screen.queryByText('777')).not.toBeInTheDocument()
  })
})

describe('OrdenesDeCompra — pager', () => {
  it('está deshabilitado en ambos bordes cuando hay una sola página', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('12')
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })

  it('navega entre páginas, con "Anterior"/"Siguiente" habilitados solo del lado correcto', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/ordenes-compra?')) {
        const paginaSolicitada = ruta.includes('pagina=2') ? 2 : 1
        return Promise.resolve(paginaFixture({ total: 50, pagina: paginaSolicitada, tamanio: 25 }))
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderPantalla()

    await screen.findByText(/Página 1 de 2/)
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeEnabled()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText(/Página 2 de 2/)
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })
})

describe('OrdenesDeCompra — "Nueva orden de compra" (Admin-only)', () => {
  it('Admin ve el botón; Vendedor no', async () => {
    mockearRutasBase()
    renderPantalla()

    expect(await screen.findByRole('button', { name: 'Nueva orden de compra' })).toBeInTheDocument()
  })

  it('un Vendedor no ve "Nueva orden de compra"', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('12')
    expect(screen.queryByRole('button', { name: 'Nueva orden de compra' })).not.toBeInTheDocument()
  })
})

describe('OrdenesDeCompra — role gating (mismo gate que /compras: Politicas.OperacionDePos)', () => {
  it('un Vendedor llega a /ordenes-compra', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()
    renderPantallaProtegida()

    await screen.findByText('12')
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })
})
