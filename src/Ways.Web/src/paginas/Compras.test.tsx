import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Compras } from './Compras'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { CompraListada, PaginaDeCompras, ProveedorListado, SaldoDeProveedor, TipoComprobanteListado, UsuarioAutenticado } from '../api/tipos'

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

function tipoFixture(sobrescribir: Partial<TipoComprobanteListado> = {}): TipoComprobanteListado {
  return {
    id: 5,
    clase: 'Compra',
    codigo: 'C-FA',
    nombre: 'Factura A de compra',
    letra: 'A',
    signo: 1,
    discriminaIva: true,
    esFiscal: false,
    afectaStock: true,
    codigoAfip: null,
    activo: true,
    ...sobrescribir,
  }
}

function compraListadaFixture(sobrescribir: Partial<CompraListada> = {}): CompraListada {
  return {
    id: 1,
    idProveedor: 1,
    idTipoComprobante: 5,
    numeroExterno: '0003-00012345',
    estado: 'Confirmada',
    fechaRecepcion: '2026-08-05T12:00:00Z',
    total: 1149.5,
    ...sobrescribir,
  }
}

function paginaFixture(items: CompraListada[], sobrescribir: Partial<PaginaDeCompras> = {}): PaginaDeCompras {
  return { items, total: items.length, pagina: 1, tamanio: 25, ...sobrescribir }
}

function renderCompras() {
  return render(<Compras />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para `/compras` (decisión 11: la lectura
 * sigue `Politicas.OperacionDePos`) — prueba que el rol realmente llega a la pantalla. */
function renderComprasProtegido() {
  return render(
    <MemoryRouter initialEntries={['/compras']}>
      <Routes>
        <Route
          path="/compras"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
              <Compras />
            </RutaProtegida>
          }
        />
        <Route path="/" element={<div>Inicio (redirigido)</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/proveedores?')) return Promise.resolve({ items: [proveedorFixture()], total: 1, pagina: 1, tamanio: 200 })
    if (ruta === '/catalogos-fiscales/tipos-comprobante') return Promise.resolve([tipoFixture()])
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/compras?')) return Promise.resolve(paginaFixture([]))
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  usuarioActual = usuarioFixture()
})

describe('Compras — listado', () => {
  it('un listado vacío muestra un estado vacío, no un re-query', async () => {
    mockearRutasBase()
    renderCompras()

    expect(await screen.findByText('No hay compras que coincidan con los filtros.')).toBeInTheDocument()
    const llamadasAlListado = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/compras?'))
    expect(llamadasAlListado).toHaveLength(1)
  })

  it('renderiza las filas del listado con proveedor y tipo resueltos', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/compras?')) return Promise.resolve(paginaFixture([compraListadaFixture()]))
      return undefined
    })
    renderCompras()

    expect(await screen.findByText('0003-00012345')).toBeInTheDocument()
    const fila = screen.getByRole('row', { name: /0003-00012345/ })
    expect(within(fila).getByText('Proveedor Uno SA')).toBeInTheDocument()
    expect(within(fila).getByText('C-FA')).toBeInTheDocument()
    expect(within(fila).getByText('Confirmada')).toBeInTheDocument()
  })

  it('Admin ve el botón "Nueva compra"; Vendedor no', async () => {
    mockearRutasBase()
    const { rerender } = renderCompras()
    await screen.findByText('No hay compras que coincidan con los filtros.')
    expect(screen.getByRole('button', { name: 'Nueva compra' })).toBeInTheDocument()

    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    rerender(<Compras />)
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Nueva compra' })).not.toBeInTheDocument())
  })

  it('un Vendedor llega a la ruta del listado (decisión 11); un Root queda afuera', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()

    renderComprasProtegido()

    expect(await screen.findByText('No hay compras que coincidan con los filtros.')).toBeInTheDocument()
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })

  it('filtrar por proveedor carga su saldo y muestra el estado de pago por fila', async () => {
    const saldo: SaldoDeProveedor = {
      idProveedor: 1,
      saldo: 500,
      compras: [{ idComprobanteCompra: 1, numeroExterno: '0003-00012345', total: 1149.5, pagado: 649.5, estadoPago: 'Parcial' }],
    }
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/compras?')) return Promise.resolve(paginaFixture([compraListadaFixture()]))
      if (ruta === '/proveedores/1/saldo') return Promise.resolve(saldo)
      return undefined
    })
    const usuario = userEvent.setup()
    renderCompras()

    await screen.findByText('0003-00012345')
    expect(screen.getAllByText('—')[0]).toBeInTheDocument() // sin filtro de proveedor, sin estado de pago todavía

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '1')

    expect(await screen.findByText('Parcial')).toBeInTheDocument()
  })

  it('un Vendedor filtrando por proveedor ve el saldo agregado (decisión: entrada Vendedor-reachable, no solo Admin vía /proveedores)', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    const saldo: SaldoDeProveedor = {
      idProveedor: 1,
      saldo: 500,
      compras: [{ idComprobanteCompra: 1, numeroExterno: '0003-00012345', total: 1149.5, pagado: 649.5, estadoPago: 'Parcial' }],
    }
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/compras?')) return Promise.resolve(paginaFixture([compraListadaFixture()]))
      if (ruta === '/proveedores/1/saldo') return Promise.resolve(saldo)
      return undefined
    })
    const usuario = userEvent.setup()
    renderCompras()

    await screen.findByText('0003-00012345')
    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '1')

    expect(await screen.findByText('$500,00')).toBeInTheDocument()
    expect(screen.queryByText(/Saldo negativo/)).not.toBeInTheDocument()
  })

  it('un saldo negativo filtrando por proveedor muestra el callout de gasto colgante', async () => {
    const saldo: SaldoDeProveedor = {
      idProveedor: 1,
      saldo: -500,
      compras: [{ idComprobanteCompra: 1, numeroExterno: '0003-00012345', total: 1149.5, pagado: 0, estadoPago: 'Impaga' }],
    }
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/compras?')) return Promise.resolve(paginaFixture([compraListadaFixture()]))
      if (ruta === '/proveedores/1/saldo') return Promise.resolve(saldo)
      return undefined
    })
    const usuario = userEvent.setup()
    renderCompras()

    await screen.findByText('0003-00012345')
    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '1')

    expect(await screen.findByText('-$500,00')).toBeInTheDocument()
    expect(screen.getByText(/Saldo negativo: hay gastos de proveedor sin ligar/)).toBeInTheDocument()
  })

  it('una respuesta de listado desactualizada nunca pisa la más reciente (generación)', async () => {
    let resolverPrimera: (valor: PaginaDeCompras) => void = () => {}
    const primera = new Promise<PaginaDeCompras>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/compras?')) {
        cantidadDeLlamadas += 1
        if (cantidadDeLlamadas === 1) return primera
        return Promise.resolve(paginaFixture([compraListadaFixture({ id: 2, numeroExterno: 'segunda-respuesta' })]))
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderCompras()
    await screen.findByLabelText('Estado')

    // Dispara una segunda consulta (estado=Borrador) ANTES de que la primera (sin filtro) resuelva.
    await usuario.selectOptions(screen.getByLabelText('Estado'), 'Borrador')
    expect(await screen.findByText('segunda-respuesta')).toBeInTheDocument()

    // La primera respuesta, ahora obsoleta, resuelve tarde — no debe reemplazar la tabla.
    resolverPrimera(paginaFixture([compraListadaFixture({ id: 1, numeroExterno: 'primera-respuesta-vieja' })]))
    await waitFor(() => expect(screen.queryByText('primera-respuesta-vieja')).not.toBeInTheDocument())
    expect(screen.getByText('segunda-respuesta')).toBeInTheDocument()
  })
})
