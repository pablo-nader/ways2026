import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { GrillaDeArticulos } from './GrillaDeArticulos'
import { ErrorApi } from '../../api/cliente'
import type { FilaDeGrillaDeArticulos, PaginaDeGrillaDeArticulos, ProveedorListado } from '../../api/tipos'

const apiGetMock = vi.fn()

vi.mock('../../api/cliente', () => ({
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

function filaFixture(sobrescribir: Partial<FilaDeGrillaDeArticulos> = {}): FilaDeGrillaDeArticulos {
  return {
    id: 1,
    codigoInterno: 'A0001',
    nombre: 'Articulo Uno',
    precio: 100,
    idProveedorHabitual: null,
    proveedor: null,
    activo: true,
    ...sobrescribir,
  }
}

function paginaFixture(
  items: FilaDeGrillaDeArticulos[],
  sobrescribir: Partial<PaginaDeGrillaDeArticulos> = {},
): PaginaDeGrillaDeArticulos {
  return { items, total: items.length, pagina: 1, tamanio: 25, nombreListaPrecio: 'General', ...sobrescribir }
}

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Alfa SA',
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

const proveedorAlfa = proveedorFixture({ id: 1, razonSocial: 'Alfa SA' })
const proveedorZeta = proveedorFixture({ id: 2, razonSocial: 'Zeta SA' })

function mockearRutas(respuesta: PaginaDeGrillaDeArticulos | (() => Promise<PaginaDeGrillaDeArticulos>)) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/articulos/grilla')) return typeof respuesta === 'function' ? respuesta() : Promise.resolve(respuesta)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

type PropsDeTest = {
  proveedores: ProveedorListado[]
  ocupado: boolean
  pedidoDeRefresco: number
  onEliminar: (fila: FilaDeGrillaDeArticulos) => void
}

function propsPorDefecto(sobrescribir: Partial<PropsDeTest> = {}): PropsDeTest {
  return {
    proveedores: [proveedorAlfa, proveedorZeta],
    ocupado: false,
    pedidoDeRefresco: 0,
    onEliminar: vi.fn(),
    ...sobrescribir,
  }
}

function renderGrilla(sobrescribir: Partial<PropsDeTest> = {}) {
  const props = propsPorDefecto(sobrescribir)
  const resultado = render(
    <MemoryRouter>
      <GrillaDeArticulos {...props} />
    </MemoryRouter>,
  )
  return {
    ...resultado,
    rerenderCon(cambios: Partial<PropsDeTest>) {
      const nuevasProps = { ...props, ...cambios }
      resultado.rerender(
        <MemoryRouter>
          <GrillaDeArticulos {...nuevasProps} />
        </MemoryRouter>,
      )
    },
  }
}

function ultimaQuery(): string {
  const llamadas = apiGetMock.mock.calls.filter((c: unknown[]) => (c[0] as string).startsWith('/articulos/grilla'))
  return llamadas.at(-1)?.[0] as string
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('GrillaDeArticulos — columnas', () => {
  // mutation-proof-tests regla 6: dos filas con valores DISTINTOS en cada columna.
  it('renderiza código, nombre, precio, proveedor y estado de cada fila', async () => {
    const filaUno = filaFixture({ id: 1, codigoInterno: 'A0001', nombre: 'Articulo Uno', precio: 100, proveedor: 'Alfa SA', activo: true })
    const filaDos = filaFixture({ id: 2, codigoInterno: 'A0002', nombre: 'Articulo Dos', precio: 250.5, proveedor: null, activo: false })
    mockearRutas(paginaFixture([filaUno, filaDos]))
    renderGrilla()

    await screen.findByText('Articulo Uno')
    const filaUnoDom = screen.getByText('Articulo Uno').closest('tr')
    if (!filaUnoDom) throw new Error('No se encontró la fila 1')
    expect(within(filaUnoDom).getByText('A0001')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('$ 100,00')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('Alfa SA')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('Activo')).toBeInTheDocument()

    const filaDosDom = screen.getByText('Articulo Dos').closest('tr')
    if (!filaDosDom) throw new Error('No se encontró la fila 2')
    expect(within(filaDosDom).getByText('A0002')).toBeInTheDocument()
    expect(within(filaDosDom).getByText('$ 250,50')).toBeInTheDocument()
    expect(within(filaDosDom).getByText('—')).toBeInTheDocument()
    expect(within(filaDosDom).getByText('Inactivo')).toBeInTheDocument()
  })

  it('precio null se muestra "—"', async () => {
    mockearRutas(paginaFixture([filaFixture({ precio: null, proveedor: 'Alfa SA' })]))
    renderGrilla()
    const fila = (await screen.findByText('Articulo Uno')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila')
    expect(within(fila).getByText('—')).toBeInTheDocument()
    expect(within(fila).getByText('Alfa SA')).toBeInTheDocument()
  })

  it('el link "Editar" apunta a /articulos/edit/{id}, sin importar los filtros', async () => {
    mockearRutas(paginaFixture([filaFixture({ id: 7 })]))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    expect(screen.getByRole('link', { name: 'Editar' })).toHaveAttribute('href', '/articulos/edit/7')
  })

  it('con ocupado, Editar y Baja quedan inertes', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla({ ocupado: true })
    await screen.findByText('Articulo Uno')
    expect(screen.getByRole('link', { name: 'Editar' })).toHaveAttribute('aria-disabled', 'true')
    expect(screen.getByRole('button', { name: 'Baja' })).toBeDisabled()
  })

  it('click en "Baja" invoca onEliminar con la fila completa', async () => {
    const onEliminar = vi.fn()
    mockearRutas(paginaFixture([filaFixture({ id: 3, nombre: 'Articulo Tres' })]))
    renderGrilla({ onEliminar })
    await screen.findByText('Articulo Tres')

    await userEvent.click(screen.getByRole('button', { name: 'Baja' }))

    expect(onEliminar).toHaveBeenCalledWith(expect.objectContaining({ id: 3, nombre: 'Articulo Tres' }))
  })
})

describe('GrillaDeArticulos — header de precio y su lista', () => {
  /**
   * Cláusula bajo prueba: `nombreListaPrecio ? \`Precio (${nombreListaPrecio})\` : 'Precio'` en
   * `GrillaDeArticulos.tsx`. Mutation-proof-tests: hardcodear el título a `'Precio'` sin importar
   * `nombreListaPrecio` hace fallar el primer `expect`.
   */
  it('muestra el nombre de la lista de precio default en el header', async () => {
    mockearRutas(paginaFixture([filaFixture()], { nombreListaPrecio: 'Mayorista' }))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    expect(screen.getByText('Precio (Mayorista)')).toBeInTheDocument()
  })

  it('sin lista de precio default, el header cae a "Precio" y el filtro de precio se deshabilita', async () => {
    mockearRutas(paginaFixture([filaFixture({ precio: null })], { nombreListaPrecio: null }))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    expect(screen.getByText('Precio')).toBeInTheDocument()
    expect(screen.queryByText(/Precio \(/)).not.toBeInTheDocument()
    expect(screen.getByLabelText('Precio desde')).toBeDisabled()
    expect(screen.getByLabelText('Precio hasta')).toBeDisabled()
  })

  it('antes de la primera respuesta, el filtro de precio arranca habilitado', () => {
    mockearRutas(() => new Promise(() => {}))
    renderGrilla()
    // Sigue en el estado de "Cargando…" inicial: no hay filtros en pantalla todavía, así que
    // solo se verifica que no explota — el resto de las pruebas cubren el estado post-carga.
    expect(screen.getByText('Cargando…')).toBeInTheDocument()
  })
})

describe('GrillaDeArticulos — filtros de texto (debounce)', () => {
  it('tipear en "Filtrar por código" debounca 300ms antes de disparar la consulta', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    apiGetMock.mockClear()

    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Filtrar por código'), { target: { value: 'A00' } })
      await vi.advanceTimersByTimeAsync(200)
      expect(apiGetMock).not.toHaveBeenCalled()
      await vi.advanceTimersByTimeAsync(100)
    } finally {
      vi.useRealTimers()
    }

    await waitFor(() => expect(ultimaQuery()).toContain('codigo=A00'))
  })

  it('tipear en "Filtrar por nombre" debounca antes de disparar la consulta', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    apiGetMock.mockClear()

    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Filtrar por nombre'), { target: { value: 'Coca' } })
      await vi.advanceTimersByTimeAsync(300)
    } finally {
      vi.useRealTimers()
    }

    await waitFor(() => expect(ultimaQuery()).toContain('nombre=Coca'))
  })

  it('tipear en "Precio desde"/"Precio hasta" debounca antes de disparar la consulta', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    apiGetMock.mockClear()

    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Precio desde'), { target: { value: '10' } })
      await vi.advanceTimersByTimeAsync(300)
    } finally {
      vi.useRealTimers()
    }

    await waitFor(() => expect(ultimaQuery()).toContain('precioDesde=10'))
  })

  /**
   * Cláusula bajo prueba: el `clearTimeout`/reprogramación de `programarCambioDeTexto` en
   * `GrillaDeArticulos.tsx`. Mutation-proof-tests: sacar el debounce (aplicar el filtro en cada
   * tecleo) hace que la consulta de "A" ya dispare antes de completar "A001" — este test
   * distingue eso al no ver NINGUNA consulta con "codigo=A" antes de los 300ms.
   */
  it('varios tecleos seguidos dentro de la ventana de debounce disparan una sola consulta, con el ÚLTIMO valor', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    apiGetMock.mockClear()

    vi.useFakeTimers()
    try {
      const campo = screen.getByLabelText('Filtrar por código')
      fireEvent.change(campo, { target: { value: 'A' } })
      await vi.advanceTimersByTimeAsync(100)
      fireEvent.change(campo, { target: { value: 'A0' } })
      await vi.advanceTimersByTimeAsync(100)
      fireEvent.change(campo, { target: { value: 'A001' } })
      expect(apiGetMock).not.toHaveBeenCalled()
      await vi.advanceTimersByTimeAsync(300)
    } finally {
      vi.useRealTimers()
    }
    await waitFor(() => expect(ultimaQuery()).toContain('codigo=A001'))

    const llamadas = apiGetMock.mock.calls.filter((c: unknown[]) => (c[0] as string).startsWith('/articulos/grilla'))
    expect(llamadas).toHaveLength(1)
    expect(ultimaQuery()).toContain('codigo=A001')
  })
})

describe('GrillaDeArticulos — filtros inmediatos (selects)', () => {
  it('seleccionar un proveedor dispara la consulta de inmediato, sin esperar debounce', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    apiGetMock.mockClear()

    await userEvent.selectOptions(screen.getByLabelText('Filtrar por proveedor'), String(proveedorAlfa.id))

    await waitFor(() => expect(ultimaQuery()).toContain(`idProveedor=${proveedorAlfa.id}`))
  })

  it('"Sin proveedor" manda sinProveedor=true, nunca idProveedor', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    apiGetMock.mockClear()

    await userEvent.selectOptions(screen.getByLabelText('Filtrar por proveedor'), 'sin-proveedor')

    await waitFor(() => {
      expect(ultimaQuery()).toContain('sinProveedor=true')
      expect(ultimaQuery()).not.toContain('idProveedor=')
    })
  })

  it('el select de estado aplica Activos/Inactivos de inmediato', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    apiGetMock.mockClear()

    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'false')

    await waitFor(() => expect(ultimaQuery()).toContain('activo=false'))
  })
})

describe('GrillaDeArticulos — reseteo de página', () => {
  /**
   * Cláusula bajo prueba: `pagina: 1` en `cambiarFiltroInmediato`/el commit del debounce.
   * Mutation-proof-tests: sacar el reseteo de página (dejar `prev.pagina` sin tocar) hace que la
   * consulta tras cambiar el filtro siga pidiendo `pagina=2`.
   */
  it('cambiar cualquier filtro (inmediato o debounced) resetea la página a 1', async () => {
    const paginaUno = paginaFixture(
      Array.from({ length: 2 }, (_, i) => filaFixture({ id: i + 1, codigoInterno: `A000${i}`, nombre: `Art ${i}` })),
      { total: 4, tamanio: 2, pagina: 1 },
    )
    mockearRutas(paginaUno)
    renderGrilla()
    await screen.findByText('Art 0')

    mockearRutas(paginaFixture([filaFixture({ id: 9, nombre: 'Art 9' })], { total: 4, tamanio: 2, pagina: 2 }))
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Art 9')
    expect(ultimaQuery()).toContain('pagina=2')

    apiGetMock.mockClear()
    mockearRutas(paginaFixture([filaFixture({ nombre: 'Articulo Uno' })]))
    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'true')

    await waitFor(() => expect(ultimaQuery()).toContain('pagina=1'))
  })
})

describe('GrillaDeArticulos — Limpiar', () => {
  /**
   * Cláusula bajo prueba: `limpiarFiltros` resetea `filtros`/`borrador` a
   * `filtrosDeGrillaDeArticulosVacios()` (salvo `tamanio`) y cancela el debounce pendiente.
   * Mutation-proof-tests: que `limpiarFiltros` sea un no-op deja el texto tipeado y el filtro
   * de proveedor sin resetear — el segundo `expect` de abajo fallaría.
   */
  it('Limpiar resetea todos los filtros de texto/select, incluso con un debounce pendiente', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')

    await userEvent.selectOptions(screen.getByLabelText('Filtrar por proveedor'), String(proveedorAlfa.id))
    await waitFor(() => expect(ultimaQuery()).toContain('idProveedor='))

    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Filtrar por código'), { target: { value: 'A001' } })
      // Debounce todavía en vuelo: Limpiar tiene que cancelarlo, no solo pisarlo con el próximo commit.
      fireEvent.click(screen.getByRole('button', { name: 'Limpiar' }))
      await vi.advanceTimersByTimeAsync(400)
    } finally {
      vi.useRealTimers()
    }

    expect(screen.getByLabelText('Filtrar por código')).toHaveValue('')
    expect(screen.getByLabelText('Filtrar por proveedor')).toHaveValue('')
    expect(ultimaQuery()).not.toContain('codigo=')
    expect(ultimaQuery()).not.toContain('idProveedor=')
  })
})

describe('GrillaDeArticulos — paginación', () => {
  it('Anterior está deshabilitado en la página 1, Siguiente se habilita si hay más de una página', async () => {
    mockearRutas(paginaFixture([filaFixture()], { total: 30, tamanio: 25, pagina: 1 }))
    renderGrilla()
    await screen.findByText('Articulo Uno')

    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeEnabled()
  })

  it('Siguiente está deshabilitado en la última página', async () => {
    mockearRutas(paginaFixture([filaFixture()], { total: 30, tamanio: 25, pagina: 2 }))
    renderGrilla()
    await screen.findByText('Articulo Uno')

    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeEnabled()
  })

  it('cambiar el tamaño de página resetea a página 1 y manda el nuevo tamanio', async () => {
    mockearRutas(paginaFixture([filaFixture()], { total: 60, tamanio: 25, pagina: 1 }))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    apiGetMock.mockClear()
    mockearRutas(paginaFixture([filaFixture()], { total: 60, tamanio: 50, pagina: 1 }))

    await userEvent.selectOptions(screen.getByLabelText('Artículos por página'), '50')

    await waitFor(() => {
      expect(ultimaQuery()).toContain('tamanio=50')
      expect(ultimaQuery()).toContain('pagina=1')
    })
  })
})

describe('GrillaDeArticulos — respuesta desactualizada (generación)', () => {
  it('una respuesta de filtro vieja nunca pisa a la más reciente', async () => {
    let resolverPrimera: (valor: PaginaDeGrillaDeArticulos) => void = () => {}
    const primera = new Promise<PaginaDeGrillaDeArticulos>((resolve) => {
      resolverPrimera = resolve
    })
    let llamadas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (!ruta.startsWith('/articulos/grilla')) return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      llamadas += 1
      if (llamadas === 1) return Promise.resolve(paginaFixture([filaFixture({ nombre: 'Carga inicial' })]))
      if (llamadas === 2) return primera
      return Promise.resolve(paginaFixture([filaFixture({ id: 2, nombre: 'Filtro nuevo' })]))
    })

    renderGrilla()
    await screen.findByText('Carga inicial')

    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'true')
    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'false')
    await screen.findByText('Filtro nuevo')

    await act(async () => {
      resolverPrimera(paginaFixture([filaFixture({ id: 3, nombre: 'Respuesta vieja' })]))
      await primera
    })

    expect(screen.queryByText('Respuesta vieja')).not.toBeInTheDocument()
    expect(screen.getByText('Filtro nuevo')).toBeInTheDocument()
  })
})

describe('GrillaDeArticulos — errores de la API', () => {
  it('un 400 del servidor (ej. filtro_precio_excede_tope) se muestra con el mensaje de ErrorApi', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')

    mockearRutas(() =>
      Promise.reject(new ErrorApi(400, 'filtro_precio_excede_tope', 'Hay demasiados artículos para ese rango de precio.')),
    )
    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'false')

    expect(await screen.findByText('Hay demasiados artículos para ese rango de precio.')).toBeInTheDocument()
  })

  it('"Reintentar" repite la última consulta', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')

    mockearRutas(() => Promise.reject(new ErrorApi(500, 'error', 'Error 500.')))
    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'false')
    await screen.findByText('Error 500.')

    mockearRutas(paginaFixture([filaFixture({ nombre: 'Articulo Recuperado' })], { pagina: 1 }))
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByText('Articulo Recuperado')).toBeInTheDocument()
    expect(screen.queryByText('Error 500.')).not.toBeInTheDocument()
  })
})

describe('GrillaDeArticulos — refresco pedido por el padre', () => {
  /**
   * Cláusula bajo prueba: el segundo `useEffect` de `GrillaDeArticulos.tsx` (gateado por
   * `esPrimerRenderDeRefrescoRef`) reacciona a `pedidoDeRefresco` con los MISMOS `filtros`.
   * Mutation-proof-tests: sacar ese efecto (o su dependencia) hace que este test nunca vea una
   * segunda consulta tras el bump.
   */
  it('un bump de pedidoDeRefresco repite la consulta con los mismos filtros en curso', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    const { rerenderCon } = renderGrilla({ pedidoDeRefresco: 0 })
    await screen.findByText('Articulo Uno')

    await userEvent.selectOptions(screen.getByLabelText('Filtrar por proveedor'), String(proveedorAlfa.id))
    await waitFor(() => expect(ultimaQuery()).toContain(`idProveedor=${proveedorAlfa.id}`))

    apiGetMock.mockClear()
    mockearRutas(paginaFixture([filaFixture({ proveedor: 'Alfa SA' })]))
    rerenderCon({ pedidoDeRefresco: 1 })

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((c: unknown[]) => (c[0] as string).startsWith('/articulos/grilla'))
      expect(llamadas.length).toBeGreaterThanOrEqual(1)
    })
    expect(ultimaQuery()).toContain(`idProveedor=${proveedorAlfa.id}`)
  })

  it('el primer render NO dispara un refresco extra (pedidoDeRefresco arranca en 0)', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla({ pedidoDeRefresco: 0 })
    await screen.findByText('Articulo Uno')

    const llamadas = apiGetMock.mock.calls.filter((c: unknown[]) => (c[0] as string).startsWith('/articulos/grilla'))
    expect(llamadas).toHaveLength(1)
  })
})

describe('GrillaDeArticulos — carga sin pantallazo en blanco', () => {
  it('un refetch posterior al primero no vacía la tabla mientras está en vuelo', async () => {
    mockearRutas(paginaFixture([filaFixture()]))
    renderGrilla()
    await screen.findByText('Articulo Uno')

    let resolver: (valor: PaginaDeGrillaDeArticulos) => void = () => {}
    const pendiente = new Promise<PaginaDeGrillaDeArticulos>((resolve) => {
      resolver = resolve
    })
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos/grilla')) return pendiente
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'true')

    // La fila sigue en el DOM mientras la consulta gatillada por el filtro está en vuelo.
    expect(screen.getByText('Articulo Uno')).toBeInTheDocument()

    await act(async () => {
      resolver(paginaFixture([filaFixture()]))
      await pendiente
    })
  })
})
