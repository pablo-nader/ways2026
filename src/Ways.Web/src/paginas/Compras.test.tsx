import { act, render, screen, waitFor, within } from '@testing-library/react'
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
    percibeIibb: false,
    percibeIva: false,
    preciosIncluyenIva: false,
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
    registraLibroIva: true,
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
    saldoPendiente: 0,
    origen: 'Manual',
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

    expect(await screen.findByText('$ 500,00')).toBeInTheDocument()
    expect(screen.queryByText(/Saldo negativo/)).not.toBeInTheDocument()
  })

  it('un saldo negativo filtrando por proveedor muestra el callout de saldo a favor', async () => {
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

    expect(await screen.findByText('-$ 500,00')).toBeInTheDocument()
    expect(screen.getByText('Saldo a favor.')).toBeInTheDocument()
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

describe('Compras — orden en el servidor', () => {
  const llamadasAlListado = () =>
    apiGetMock.mock.calls.map((c: unknown[]) => c[0] as string).filter((ruta) => ruta.startsWith('/compras?'))

  const conUnaFila = (ruta: string) =>
    ruta.startsWith('/compras?') ? Promise.resolve(paginaFixture([compraListadaFixture()])) : undefined

  it('el clic en un encabezado pide el orden al servidor, recorre ascendente, descendente y el orden de siempre', async () => {
    mockearRutasBase(conUnaFila)
    const usuario = userEvent.setup()
    renderCompras()
    await screen.findByText('0003-00012345')
    expect(llamadasAlListado().every((ruta) => !ruta.includes('orden='))).toBe(true)

    await usuario.click(screen.getByRole('button', { name: /^Total/ }))
    await waitFor(() => expect(llamadasAlListado().at(-1)).toContain('orden=total&direccion=ascendente'))
    expect(screen.getByRole('columnheader', { name: /Total/ })).toHaveAttribute('aria-sort', 'ascending')

    await usuario.click(screen.getByRole('button', { name: /^Total/ }))
    await waitFor(() => expect(llamadasAlListado().at(-1)).toContain('orden=total&direccion=descendente'))

    await usuario.click(screen.getByRole('button', { name: /^Total/ }))
    await waitFor(() => expect(llamadasAlListado().at(-1)).not.toContain('orden='))
  })

  it.each([
    ['Número', 'numero'],
    ['Proveedor', 'proveedor'],
    ['Tipo', 'tipo'],
    ['Estado', 'estado'],
    ['Fecha de recepción', 'fechaRecepcion'],
    ['Total', 'total'],
  ])('la columna %s ordena por la clave %s', async (titulo, clave) => {
    mockearRutasBase(conUnaFila)
    const usuario = userEvent.setup()
    renderCompras()
    await screen.findByText('0003-00012345')

    await usuario.click(screen.getByRole('button', { name: new RegExp(`^${titulo}`) }))

    await waitFor(() => expect(llamadasAlListado().at(-1)).toContain(`orden=${clave}&direccion=ascendente`))
  })

  it('"Estado de pago" no ordena: la pantalla lo deriva de otra consulta y no es una clave del servidor', async () => {
    mockearRutasBase(conUnaFila)
    renderCompras()
    await screen.findByText('0003-00012345')

    expect(screen.getByRole('columnheader', { name: 'Estado de pago' })).not.toHaveAttribute('aria-sort')
    expect(screen.queryByRole('button', { name: /Estado de pago/ })).not.toBeInTheDocument()
  })

  it('cambiar el orden desde la página 2 vuelve a la página 1 y conserva los filtros', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/compras?')
        ? Promise.resolve(
            paginaFixture([compraListadaFixture()], { total: 50, pagina: ruta.includes('pagina=2') ? 2 : 1, tamanio: 25 }),
          )
        : undefined,
    )
    const usuario = userEvent.setup()
    renderCompras()
    await screen.findByText(/Página 1 de 2/)
    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'Anulada')
    await screen.findByText(/Página 1 de 2/)
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText(/Página 2 de 2/)

    await usuario.click(screen.getByRole('button', { name: /^Total/ }))

    await waitFor(() => {
      const ultima = llamadasAlListado().at(-1)!
      expect(ultima).toContain('orden=total&direccion=ascendente')
      expect(ultima).toContain('pagina=1')
      expect(ultima).toContain('estado=Anulada')
    })
    await screen.findByText(/Página 1 de 2/)
  })

  it('una respuesta stale de un orden anterior no pisa la del orden más nuevo', async () => {
    let resolverPorTotal: (v: PaginaDeCompras) => void = () => {}
    let promesaPorTotal: Promise<PaginaDeCompras> = Promise.resolve(paginaFixture([]))
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/compras?') && ruta.includes('orden=total')) {
        promesaPorTotal = new Promise((resolve) => (resolverPorTotal = resolve))
        return promesaPorTotal
      }
      if (ruta.startsWith('/compras?') && ruta.includes('orden=estado')) {
        return Promise.resolve(paginaFixture([compraListadaFixture({ id: 2, numeroExterno: 'nueva-99' })]))
      }
      return conUnaFila(ruta)
    })
    const usuario = userEvent.setup()
    renderCompras()
    await screen.findByText('0003-00012345')

    await usuario.click(screen.getByRole('button', { name: /^Total/ }))
    await usuario.click(screen.getByRole('button', { name: /^Estado/ }))
    await screen.findByText('nueva-99')

    await act(async () => {
      resolverPorTotal(paginaFixture([compraListadaFixture({ id: 3, numeroExterno: 'vieja-199' })]))
      await promesaPorTotal
    })

    expect(screen.getByText('nueva-99')).toBeInTheDocument()
    expect(screen.queryByText('vieja-199')).not.toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Estado$/ })).toHaveAttribute('aria-sort', 'ascending')
  })

  it('si la consulta con orden falla, la tabla y su encabezado desaparecen: nunca queda un orden que las filas no tienen', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/compras?') && ruta.includes('orden=total') ? Promise.reject(new Error('caída')) : conUnaFila(ruta),
    )
    const usuario = userEvent.setup()
    renderCompras()
    await screen.findByText('0003-00012345')

    await usuario.click(screen.getByRole('button', { name: /^Total/ }))

    await screen.findByText('No se pudieron cargar las compras.')
    expect(screen.queryByRole('columnheader', { name: /Total/ })).not.toBeInTheDocument()
    expect(screen.queryByText('0003-00012345')).not.toBeInTheDocument()
  })
})

describe('Compras — origen', () => {
  it('muestra la insignia "Asistente" solo en las filas cargadas por el asistente', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/compras?')) {
        return Promise.resolve(
          paginaFixture([
            compraListadaFixture({ id: 1, numeroExterno: 'fila-manual', origen: 'Manual' }),
            compraListadaFixture({ id: 2, numeroExterno: 'fila-asistida', origen: 'Asistente' }),
          ]),
        )
      }
      return undefined
    })
    renderCompras()

    await screen.findByText('fila-asistida')
    expect(within(screen.getByRole('row', { name: /fila-asistida/ })).getByText('Asistente')).toBeInTheDocument()
    expect(within(screen.getByRole('row', { name: /fila-manual/ })).queryByText('Asistente')).not.toBeInTheDocument()
  })

  it('el filtro de origen manda el parámetro al listado y "Todas" lo quita', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderCompras()
    await screen.findByText('No hay compras que coincidan con los filtros.')

    const consultas = () =>
      apiGetMock.mock.calls.map((call: unknown[]) => call[0] as string).filter((ruta: string) => ruta.startsWith('/compras?'))
    expect(consultas()[0]).not.toContain('origen=')

    await usuario.selectOptions(screen.getByLabelText('Origen'), 'Asistente')
    await waitFor(() => expect(consultas().at(-1)).toContain('origen=Asistente'))

    await usuario.selectOptions(screen.getByLabelText('Origen'), '')
    await waitFor(() => expect(consultas().at(-1)).not.toContain('origen='))
  })
})
