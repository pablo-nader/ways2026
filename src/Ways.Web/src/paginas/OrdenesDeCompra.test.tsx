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
    percibeIibb: false,
    percibeIva: false,
    preciosIncluyenIva: false,
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

function ordenFixture(sobrescribir: Partial<OrdenDeCompraListada> = {}): OrdenDeCompraListada {
  return {
    id: 1,
    idProveedor: 1,
    idPuntoVenta: 7,
    numero: 12,
    fechaEmision: '2026-08-01T12:00:00Z',
    fechaEsperada: '2026-08-15',
    estado: 'Enviada',
    nombreProveedor: 'Proveedor Uno SA',
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

  it('un proveedor fuera de la lista de referencia igual muestra el nombre que manda el servidor', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/ordenes-compra?')
        ? Promise.resolve(
            paginaFixture({
              items: [
                ordenFixture({ id: 2, idProveedor: 777, numero: 31, nombreProveedor: 'Proveedor Remoto SRL' }),
                ordenFixture({ id: 3, idProveedor: 778, numero: 32, nombreProveedor: null }),
              ],
              total: 2,
            }),
          )
        : undefined,
    )
    renderPantalla()

    await screen.findByText('31')
    expect(within(screen.getByText('31').closest('tr')!).getByText('Proveedor Remoto SRL')).toBeInTheDocument()
    expect(within(screen.getByText('32').closest('tr')!).getByText('Proveedor #778')).toBeInTheDocument()
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

describe('OrdenesDeCompra — orden en el servidor', () => {
  const llamadasAlListado = () =>
    apiGetMock.mock.calls.map((c: unknown[]) => c[0] as string).filter((ruta) => ruta.startsWith('/ordenes-compra?'))

  it('el clic en un encabezado pide el orden al servidor, recorre ascendente, descendente y el orden de siempre', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderPantalla()
    await screen.findByText('12')
    expect(llamadasAlListado().every((ruta) => !ruta.includes('orden='))).toBe(true)

    await usuario.click(screen.getByRole('button', { name: /^Fecha esperada/ }))
    await waitFor(() => expect(llamadasAlListado().at(-1)).toContain('orden=fechaEsperada&direccion=ascendente'))
    expect(screen.getByRole('columnheader', { name: /Fecha esperada/ })).toHaveAttribute('aria-sort', 'ascending')

    await usuario.click(screen.getByRole('button', { name: /^Fecha esperada/ }))
    await waitFor(() => expect(llamadasAlListado().at(-1)).toContain('orden=fechaEsperada&direccion=descendente'))

    await usuario.click(screen.getByRole('button', { name: /^Fecha esperada/ }))
    await waitFor(() => expect(llamadasAlListado().at(-1)).not.toContain('orden='))
  })

  it.each([
    ['Número', 'numero'],
    ['Proveedor', 'proveedor'],
    ['Punto de venta', 'puntoVenta'],
    ['Estado', 'estado'],
    ['Fecha de emisión', 'fechaEmision'],
    ['Fecha esperada', 'fechaEsperada'],
  ])('la columna %s ordena por la clave %s', async (titulo, clave) => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderPantalla()
    await screen.findByText('12')

    await usuario.click(screen.getByRole('button', { name: new RegExp(`^${titulo}`) }))

    await waitFor(() => expect(llamadasAlListado().at(-1)).toContain(`orden=${clave}&direccion=ascendente`))
  })

  it('cambiar el orden desde la página 2 vuelve a la página 1 y conserva los filtros', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/ordenes-compra?')
        ? Promise.resolve(paginaFixture({ total: 50, pagina: ruta.includes('pagina=2') ? 2 : 1, tamanio: 25 }))
        : undefined,
    )
    const usuario = userEvent.setup()
    renderPantalla()
    await screen.findByText(/Página 1 de 2/)
    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'Cerrada')
    await screen.findByText(/Página 1 de 2/)
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText(/Página 2 de 2/)

    await usuario.click(screen.getByRole('button', { name: /^Estado/ }))

    await waitFor(() => {
      const ultima = llamadasAlListado().at(-1)!
      expect(ultima).toContain('orden=estado&direccion=ascendente')
      expect(ultima).toContain('pagina=1')
      expect(ultima).toContain('estado=Cerrada')
    })
    await screen.findByText(/Página 1 de 2/)
  })

  it('una respuesta stale de un orden anterior no pisa la del orden más nuevo', async () => {
    let resolverPorNumero: (v: PaginaDeOrdenesDeCompra) => void = () => {}
    let promesaPorNumero: Promise<PaginaDeOrdenesDeCompra> = Promise.resolve(paginaFixture())
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/ordenes-compra?') && ruta.includes('orden=numero')) {
        promesaPorNumero = new Promise((resolve) => (resolverPorNumero = resolve))
        return promesaPorNumero
      }
      if (ruta.startsWith('/ordenes-compra?') && ruta.includes('orden=estado')) {
        return Promise.resolve(paginaFixture({ items: [ordenFixture({ id: 2, numero: 99 })] }))
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderPantalla()
    await screen.findByText('12')

    await usuario.click(screen.getByRole('button', { name: /^Número/ }))
    await usuario.click(screen.getByRole('button', { name: /^Estado/ }))
    await screen.findByText('99')

    await act(async () => {
      resolverPorNumero(paginaFixture({ items: [ordenFixture({ id: 3, numero: 199 })] }))
      await promesaPorNumero
    })

    expect(screen.getByText('99')).toBeInTheDocument()
    expect(screen.queryByText('199')).not.toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Estado/ })).toHaveAttribute('aria-sort', 'ascending')
  })

  it('si la consulta con orden falla, la tabla y su encabezado desaparecen: nunca queda un orden que las filas no tienen', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/ordenes-compra?') && ruta.includes('orden=numero')
        ? Promise.reject(new Error('caída'))
        : undefined,
    )
    const usuario = userEvent.setup()
    renderPantalla()
    await screen.findByText('12')

    await usuario.click(screen.getByRole('button', { name: /^Número/ }))

    await screen.findByText('No se pudieron cargar las órdenes de compra.')
    expect(screen.queryByRole('columnheader', { name: /Número/ })).not.toBeInTheDocument()
    expect(screen.queryByText('12')).not.toBeInTheDocument()
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
