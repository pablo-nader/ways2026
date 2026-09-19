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
  it('muestra el nombre de la lista de precio default en el header y habilita el filtro de precio', async () => {
    mockearRutas(paginaFixture([filaFixture()], { nombreListaPrecio: 'Mayorista' }))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    expect(screen.getByText('Precio (Mayorista)')).toBeInTheDocument()
    // Cláusula bajo prueba: `precioDeshabilitado` en `GrillaDeArticulos.tsx`. Mutation-proof-tests:
    // hardcodear `precioDeshabilitado = true` hace fallar estos dos `toBeEnabled()`.
    expect(screen.getByLabelText('Precio desde')).toBeEnabled()
    expect(screen.getByLabelText('Precio hasta')).toBeEnabled()
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
      await vi.advanceTimersByTimeAsync(200)
      expect(apiGetMock).not.toHaveBeenCalled()
      await vi.advanceTimersByTimeAsync(100)
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
    // Cláusula bajo prueba adicional: `precioDeshabilitado` — si el campo estuviera deshabilitado,
    // el `fireEvent.change` de abajo lo pasaría por alto igual (bypasea `disabled`), así que se
    // afirma habilitado ANTES de tipear.
    expect(screen.getByLabelText('Precio desde')).toBeEnabled()

    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Precio desde'), { target: { value: '10' } })
      await vi.advanceTimersByTimeAsync(200)
      expect(apiGetMock).not.toHaveBeenCalled()
      await vi.advanceTimersByTimeAsync(100)
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

    // Cláusula bajo prueba: `cambiarFiltroInmediato` NO pasa por `programarCambioDeTexto`
    // (sin debounce). `fireEvent.change` + assert sincrónico (sin avanzar ningún timer ni usar
    // `waitFor`) es lo único que distingue esto de un debounce de 300ms silenciosamente agregado.
    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Filtrar por proveedor'), { target: { value: String(proveedorAlfa.id) } })
      expect(ultimaQuery()).toContain(`idProveedor=${proveedorAlfa.id}`)
    } finally {
      vi.useRealTimers()
    }
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

    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Filtrar por estado'), { target: { value: 'false' } })
      expect(ultimaQuery()).toContain('activo=false')
    } finally {
      vi.useRealTimers()
    }
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

  /**
   * Cláusula bajo prueba: `pagina: 1` en el commit del debounce (`programarCambioDeTexto`) —
   * distinto código de `cambiarFiltroInmediato` de arriba. Mutation-proof-tests: sacar el reseteo
   * de página SOLO en el commit debounced (dejando el de los selects intacto) sobreviviría a la
   * prueba de arriba, que nunca tipea texto desde la página 2.
   */
  it('tipear un filtro de texto (debounced) desde la página 2 también resetea la página a 1', async () => {
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

    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Filtrar por código'), { target: { value: 'A00' } })
      await vi.advanceTimersByTimeAsync(300)
    } finally {
      vi.useRealTimers()
    }

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

    apiGetMock.mockClear()
    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Filtrar por código'), { target: { value: 'A001' } })
      // Debounce todavía en vuelo: Limpiar tiene que cancelarlo, no solo pisarlo con el próximo commit.
      fireEvent.click(screen.getByRole('button', { name: 'Limpiar' }))
      // Avances finos (no un único salto de 400ms): si `clearTimeout` faltara, el debounce
      // abandonado de "A001" reviviría DESPUÉS del commit de Limpiar — un solo
      // `advanceTimersByTimeAsync(400)` puede coalescer ambos efectos en un solo flush de React y
      // esconder el orden real. Se avanza hasta justo antes de los 300ms, se confirma que todavía
      // no volvió a pedirse `codigo=`, y recién ahí se cruza el umbral del debounce abandonado.
      await vi.advanceTimersByTimeAsync(299)
      expect(ultimaQuery()).not.toContain('codigo=')
      await vi.advanceTimersByTimeAsync(1)
      await vi.advanceTimersByTimeAsync(100)
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

    // Sale de la página 1 ANTES de cambiar el tamaño: si `cambiarTamanio` no reseteara `pagina`,
    // esta prueba (a diferencia de una que arrancara y se quedara en la página 1) lo detecta.
    mockearRutas(paginaFixture([filaFixture({ id: 9, nombre: 'Art 9' })], { total: 60, tamanio: 25, pagina: 2 }))
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Art 9')

    apiGetMock.mockClear()
    mockearRutas(paginaFixture([filaFixture()], { total: 60, tamanio: 50, pagina: 1 }))

    await userEvent.selectOptions(screen.getByLabelText('Artículos por página'), '50')

    await waitFor(() => {
      expect(ultimaQuery()).toContain('tamanio=50')
      expect(ultimaQuery()).toContain('pagina=1')
    })
  })

  /**
   * Cláusula bajo prueba: `Math.max(1, prev.pagina + delta)` en `cambiarPagina`. Mutation-proof-
   * tests: sacar el `Math.max(1, …)` deja pasar `pagina=0` cuando dos clicks sincrónicos en
   * "Anterior" ocurren en el mismo tick (el segundo click ve el mismo `pagina.pagina` de la
   * respuesta todavía en 2, porque el botón no se re-deshabilita hasta el próximo render).
   */
  it('dos clicks sincrónicos en "Anterior" desde la página 2 nunca piden pagina=0', async () => {
    // Arranca REALMENTE en la página 1 (el fixture inicial coincide con `filtros.pagina`, el
    // estado interno que `cambiarPagina` incrementa/decrementa) y navega a la página 2 con un
    // click real de "Siguiente" — así `filtros.pagina` interno queda en 2 de verdad, no solo la
    // etiqueta mostrada.
    mockearRutas(paginaFixture([filaFixture()], { total: 50, tamanio: 25, pagina: 1 }))
    renderGrilla()
    await screen.findByText('Articulo Uno')

    mockearRutas(paginaFixture([filaFixture()], { total: 50, tamanio: 25, pagina: 2 }))
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(ultimaQuery()).toContain('pagina=2'))

    apiGetMock.mockClear()
    mockearRutas(paginaFixture([filaFixture()], { total: 50, tamanio: 25, pagina: 1 }))

    const botonAnterior = screen.getByRole('button', { name: 'Anterior' })
    act(() => {
      fireEvent.click(botonAnterior)
      fireEvent.click(botonAnterior)
    })

    await waitFor(() => expect(ultimaQuery()).toContain('pagina=1'))
    expect(ultimaQuery()).not.toContain('pagina=0')
  })

  /**
   * Cláusula bajo prueba: `|| cargando` en el `disabled` de "Anterior"/"Siguiente". Mutation-
   * proof-tests: sacar ese `|| cargando` deja habilitados ambos botones en una página intermedia
   * mientras hay una consulta en vuelo — acá se fuerza esa combinación (página 2 de 3, fetch
   * pendiente) donde ninguna otra condición (`pagina.pagina <= 1` / `>= totalPaginas`) explica el
   * disabled.
   */
  it('con una consulta pendiente, "Anterior" y "Siguiente" quedan deshabilitados por cargando', async () => {
    mockearRutas(paginaFixture([filaFixture()], { total: 75, tamanio: 25, pagina: 2 }))
    renderGrilla()
    await screen.findByText('Articulo Uno')
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeEnabled()

    mockearRutas(() => new Promise(() => {}))
    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'true')

    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
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

  /**
   * Cláusula bajo prueba: el gateo de generación en el `.catch` de `cargar` (no solo en el
   * `.then`). Mutation-proof-tests: sacar el `if (generacionRef.current !== generacion) return`
   * del `.catch` deja que una petición SUPERADA, que rechaza DESPUÉS de que la más nueva ya
   * resolvió, pise la pantalla con su propio error.
   */
  it('una petición superada que rechaza después de que la más nueva ya resolvió no muestra su error', async () => {
    let rechazarPrimera: (error: unknown) => void = () => {}
    const primera = new Promise<PaginaDeGrillaDeArticulos>((_resolve, reject) => {
      rechazarPrimera = reject
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
      rechazarPrimera(new ErrorApi(500, 'error', 'Error viejo que no debería verse.'))
      await primera.catch(() => {})
    })

    expect(screen.queryByText('Error viejo que no debería verse.')).not.toBeInTheDocument()
    expect(screen.getByText('Filtro nuevo')).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el gateo de generación en el `.finally` de `cargar`. Mutation-proof-
   * tests: sacar el `if (generacionRef.current !== generacion) return` del `.finally` hace que,
   * al asentarse una petición SUPERADA, `cargando` se apague igual — reactivando la paginación
   * aunque la petición VIGENTE siga pendiente.
   */
  it('el finally de una petición superada no reactiva la paginación mientras la vigente sigue pendiente', async () => {
    let resolverPrimera: (valor: PaginaDeGrillaDeArticulos) => void = () => {}
    const primera = new Promise<PaginaDeGrillaDeArticulos>((resolve) => {
      resolverPrimera = resolve
    })
    const segundaPendiente = new Promise<PaginaDeGrillaDeArticulos>(() => {})
    let llamadas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (!ruta.startsWith('/articulos/grilla')) return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      llamadas += 1
      if (llamadas === 1) return Promise.resolve(paginaFixture([filaFixture()], { total: 50, tamanio: 25, pagina: 1 }))
      if (llamadas === 2) return primera
      return segundaPendiente
    })

    renderGrilla()
    await screen.findByText('Articulo Uno')
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeEnabled()

    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'true')
    await userEvent.selectOptions(screen.getByLabelText('Filtrar por estado'), 'false')

    // La petición VIGENTE (la 3ra, `segundaPendiente`) sigue en vuelo cuando la SUPERADA
    // (la 2da, `primera`) recién ahora se resuelve.
    await act(async () => {
      resolverPrimera(paginaFixture([filaFixture({ id: 9, nombre: 'Respuesta superada' })], { total: 50, tamanio: 25, pagina: 1 }))
      await primera
    })

    expect(screen.queryByText('Respuesta superada')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
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

describe('GrillaDeArticulos — página fuera de rango tras una Baja (GW1)', () => {
  /**
   * Cláusula bajo prueba: el clamp de `cargar` en `GrillaDeArticulos.tsx` — cuando `p.total > 0`
   * y `p.pagina` quedó fuera de rango, pide la última página válida en vez de renderizar la
   * respuesta tal cual. Mutation-proof-tests: sacar el clamp deja la grilla en "No hay artículos…"
   * con "Página 2 de 1" — este test nunca vería "Articulo Uno" ni "Página 1 de 1".
   */
  it('tras dar de baja la única fila de la última página, la grilla cae a la última página válida con la fila restante', async () => {
    mockearRutas(paginaFixture([filaFixture({ nombre: 'Articulo Uno' })], { total: 2, tamanio: 1, pagina: 1 }))
    const { rerenderCon } = renderGrilla()
    await screen.findByText('Articulo Uno')

    mockearRutas(paginaFixture([filaFixture({ id: 2, nombre: 'Articulo Dos' })], { total: 2, tamanio: 1, pagina: 2 }))
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Articulo Dos')

    // Simula el refresco pedido por el padre tras la Baja: el servidor sigue "en" la página 2
    // pedida (ya no existe ningún artículo ahí) pero todavía tiene 1 artículo en total.
    apiGetMock.mockImplementation((ruta: string) => {
      if (!ruta.startsWith('/articulos/grilla')) return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      if (ruta.includes('pagina=2')) return Promise.resolve(paginaFixture([], { total: 1, tamanio: 1, pagina: 2 }))
      return Promise.resolve(paginaFixture([filaFixture({ nombre: 'Articulo Uno' })], { total: 1, tamanio: 1, pagina: 1 }))
    })
    rerenderCon({ pedidoDeRefresco: 1 })

    await screen.findByText('Articulo Uno')
    expect(screen.queryByText('Articulo Dos')).not.toBeInTheDocument()
    expect(screen.queryByText('No hay artículos que coincidan con los filtros.')).not.toBeInTheDocument()
    expect(screen.getByText(/Página 1 de 1/)).toBeInTheDocument()
    expect(ultimaQuery()).toContain('pagina=1')
  })

  /**
   * Cláusula bajo prueba: el clamp de `cargar` también actúa cuando `p.total === 0` (GW13).
   * Mutation-proof-tests: el guard previo `if (p.total > 0)` dejaba pasar la respuesta tal cual
   * cuando el total cae a 0 estando en una página > 1 — este test nunca vería "Página 1 de 1", solo
   * "Página 2 de 1" con la tabla vacía.
   */
  it('un refresco que deja total en 0 estando en la página 2 también cae a la página 1 (nunca "2 de 1")', async () => {
    mockearRutas(paginaFixture([filaFixture({ nombre: 'Articulo Uno' })], { total: 2, tamanio: 1, pagina: 1 }))
    const { rerenderCon } = renderGrilla()
    await screen.findByText('Articulo Uno')

    mockearRutas(paginaFixture([filaFixture({ id: 2, nombre: 'Articulo Dos' })], { total: 2, tamanio: 1, pagina: 2 }))
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Articulo Dos')

    // El refresco pedido por el padre encuentra que YA NO QUEDA NINGÚN artículo (total === 0).
    apiGetMock.mockImplementation((ruta: string) => {
      if (!ruta.startsWith('/articulos/grilla')) return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      if (ruta.includes('pagina=2')) return Promise.resolve(paginaFixture([], { total: 0, tamanio: 1, pagina: 2 }))
      return Promise.resolve(paginaFixture([], { total: 0, tamanio: 1, pagina: 1 }))
    })
    rerenderCon({ pedidoDeRefresco: 1 })

    await waitFor(() => expect(ultimaQuery()).toContain('pagina=1'))
    expect(await screen.findByText(/Página 1 de 1/)).toBeInTheDocument()
    expect(screen.queryByText('Página 2 de 1')).not.toBeInTheDocument()

    const llamadas = apiGetMock.mock.calls.filter((c: unknown[]) => (c[0] as string).startsWith('/articulos/grilla'))
    expect(llamadas.length).toBeLessThanOrEqual(4)
  })

  /**
   * Cláusula bajo prueba: el `if (!seProgramoClamp) setCargando(false)` del `.finally` de `cargar`
   * (GW15). Mutation-proof-tests: sacar ese guard apaga `cargando` apenas llega la respuesta que
   * dispara el clamp — este test observa el instante EXACTO en que esa respuesta ya se resolvió
   * (microtasks del `.then`/`.finally` agotados) pero el refetch correctivo todavía no salió
   * (requiere una vuelta de macrotask, vía el efecto pasivo que reacciona al cambio de `filtros`):
   * ahí es donde el guard importa, antes de que el propio refetch correctivo vuelva a prender
   * `cargando`. Sin el guard, "Anterior" aparece habilitado y sin dimming en ese instante.
   */
  it('en el instante entre la respuesta que clampea y el refetch correctivo, la grilla sigue en estado de carga', async () => {
    mockearRutas(paginaFixture([filaFixture({ nombre: 'Articulo Uno' })], { total: 2, tamanio: 1, pagina: 1 }))
    const { rerenderCon, container } = renderGrilla()
    await screen.findByText('Articulo Uno')

    mockearRutas(paginaFixture([filaFixture({ id: 2, nombre: 'Articulo Dos' })], { total: 2, tamanio: 1, pagina: 2 }))
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Articulo Dos')

    let resolverPrimera: (valor: PaginaDeGrillaDeArticulos) => void = () => {}
    const primera = new Promise<PaginaDeGrillaDeArticulos>((resolve) => {
      resolverPrimera = resolve
    })
    apiGetMock.mockImplementation((ruta: string) => {
      if (!ruta.startsWith('/articulos/grilla')) return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      if (ruta.includes('pagina=2')) return primera
      // El refetch correctivo (pagina=1) queda pendiente a propósito: lo que importa es el
      // instante ANTERIOR a que siquiera se dispare.
      return new Promise(() => {})
    })
    rerenderCon({ pedidoDeRefresco: 1 })

    resolverPrimera(paginaFixture([], { total: 0, tamanio: 1, pagina: 2 }))
    // Agota las microtasks del `.then`/`.catch`/`.finally` de `cargar` (la respuesta que clampea).
    // React todavía no confirmó el render de este ciclo — commitear una actualización de estado
    // pedida desde afuera de un handler de evento requiere cruzar una vuelta de macrotask.
    for (let i = 0; i < 10; i++) {
      // eslint-disable-next-line no-await-in-loop
      await Promise.resolve()
    }

    // Cruza UNA vuelta de macrotask: acá React confirma el render de la respuesta que clampea. El
    // efecto pasivo que dispara el refetch correctivo (y volvería a prender `cargando`) todavía no
    // corrió en este punto — es el único instante donde el guard hace una diferencia observable.
    await new Promise((resolve) => setTimeout(resolve, 0))

    // Todavía no se pidió la página correctiva: la última consulta emitida sigue siendo la 2.
    expect(ultimaQuery()).toContain('pagina=2')
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    const tbody = container.querySelector('tbody')
    expect(tbody).toHaveStyle({ opacity: '0.6' })
  })
})
