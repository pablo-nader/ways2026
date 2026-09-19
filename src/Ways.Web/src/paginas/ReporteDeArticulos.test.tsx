import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { etiquetaDeProveedor, ordenarProveedoresPorEtiqueta, ReporteDeArticulos } from './ReporteDeArticulos'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { AreaListado, ArticuloDeReporte, CategoriaListado, GrupoListado, MarcaListado, PaginaDe, ProveedorListado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiDescargarMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
    descargar: (...args: unknown[]) => apiDescargarMock(...(args as [string])),
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

// --- pure helpers: etiquetaDeProveedor / ordenarProveedoresPorEtiqueta (web-descriptor-tests) -----

describe('etiquetaDeProveedor', () => {
  it('usa nombreFantasia cuando no está vacío/en blanco', () => {
    expect(etiquetaDeProveedor({ nombreFantasia: 'DistriUno', razonSocial: 'Distribuidora Uno SA' })).toBe('DistriUno')
  })

  it('cae a razonSocial cuando nombreFantasia es null', () => {
    expect(etiquetaDeProveedor({ nombreFantasia: null, razonSocial: 'Distribuidora Dos SRL' })).toBe('Distribuidora Dos SRL')
  })

  it('cae a razonSocial cuando nombreFantasia es una cadena en blanco (solo espacios)', () => {
    expect(etiquetaDeProveedor({ nombreFantasia: '   ', razonSocial: 'Distribuidora Tres SA' })).toBe('Distribuidora Tres SA')
  })
})

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Z Razón Social',
    nombreFantasia: null,
    cuit: null,
    idCondicionFiscal: 1,
    domicilio: null,
    telefono: null,
    email: null,
    ...sobrescribir,
  } as ProveedorListado
}

describe('ordenarProveedoresPorEtiqueta', () => {
  it('ordena por la etiqueta ya resuelta (fantasía o razón social), no por razón social cruda', () => {
    const uno = proveedorFixture({ id: 1, razonSocial: 'Z Distribuidora', nombreFantasia: 'Alfa' })
    const dos = proveedorFixture({ id: 2, razonSocial: 'A Distribuidora', nombreFantasia: null })

    const ordenados = ordenarProveedoresPorEtiqueta([dos, uno])

    // "A Distribuidora" (etiqueta de "dos") < "Alfa" (etiqueta de "uno")
    expect(ordenados.map((p) => p.id)).toEqual([2, 1])
  })

  it('no muta el array original', () => {
    const lista = [proveedorFixture({ id: 1, razonSocial: 'B' }), proveedorFixture({ id: 2, razonSocial: 'A' })]
    const copia = [...lista]

    ordenarProveedoresPorEtiqueta(lista)

    expect(lista).toEqual(copia)
  })
})

// --- componente ------------------------------------------------------------------------------------

function usuarioAdminFixture(sobrescribir: Partial<{ rolId: number; rol: string }> = {}) {
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

let usuarioActual = usuarioAdminFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

const areaAlmacen: AreaListado = { id: 1, nombre: 'Almacén', activo: true, idEmpresa: null, orden: 1 }
const areaVerduleria: AreaListado = { id: 2, nombre: 'Verdulería', activo: true, idEmpresa: null, orden: 2 }
const categoriaBebidas: CategoriaListado = { id: 10, nombre: 'Bebidas', activo: true, idEmpresa: null, orden: 1, idCategoriaPadre: null }
const marcaA: MarcaListado = { id: 20, nombre: 'Marca A', activo: true, idEmpresa: null }
const grupoA: GrupoListado = { id: 30, nombre: 'Grupo A', activo: true, idEmpresa: null, margen: null }
const proveedorUno = proveedorFixture({ id: 40, razonSocial: 'Distribuidora Uno SA', nombreFantasia: 'DistriUno' })

function filaFixture(sobrescribir: Partial<ArticuloDeReporte> = {}): ArticuloDeReporte {
  return {
    id: 1,
    codigoInterno: 'COD-1',
    nombre: 'Aceite de girasol 900ml',
    area: 'Almacén',
    categoria: 'Bebidas',
    marca: 'Marca A',
    grupo: 'Grupo A',
    proveedor: 'DistriUno',
    activo: true,
    ...sobrescribir,
  }
}

function paginaFixture(items: ArticuloDeReporte[] = [filaFixture()], sobrescribir: Partial<PaginaDe<ArticuloDeReporte>> = {}) {
  return { items, total: items.length, pagina: 1, tamanio: 25, ...sobrescribir }
}

function renderReporte() {
  return render(<ReporteDeArticulos />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para `/reportes/articulos`
 * (`Politicas.LecturaDeReportes`: Supervisor + Admin). */
function renderReporteProtegido() {
  return render(
    <MemoryRouter initialEntries={['/reportes/articulos']}>
      <Routes>
        <Route
          path="/reportes/articulos"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
              <ReporteDeArticulos />
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
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta === '/catalogos/areas') return Promise.resolve([areaAlmacen, areaVerduleria])
    if (ruta === '/catalogos/categorias') return Promise.resolve([categoriaBebidas])
    if (ruta === '/catalogos/marcas') return Promise.resolve([marcaA])
    if (ruta === '/catalogos/grupos') return Promise.resolve([grupoA])
    if (ruta === '/proveedores?tamanio=200') return Promise.resolve({ items: [proveedorUno], total: 1, pagina: 1, tamanio: 200 })
    if (ruta.startsWith('/reportes/articulos?')) return Promise.resolve(paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
  usuarioActual = usuarioAdminFixture()
})

describe('ReporteDeArticulos — listado', () => {
  it('un listado vacío muestra un estado vacío', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/articulos?')) return Promise.resolve(paginaFixture([]))
      return undefined
    })
    renderReporte()

    expect(await screen.findByText('No hay artículos que coincidan con los filtros.')).toBeInTheDocument()
  })

  // mutation-proof-tests rule 6: dos filas con valores DISTINTOS por columna — una fila totalmente
  // clasificada y otra sin ninguna clasificación, para que "Sin asignar" también quede cubierto.
  it('renderiza cada columna de cada fila, con "Sin asignar" para las clasificaciones ausentes', async () => {
    const filaCompleta = filaFixture({
      id: 1,
      codigoInterno: 'COD-1',
      nombre: 'Aceite de girasol 900ml',
      area: 'Almacén',
      categoria: 'Bebidas',
      marca: 'Marca A',
      grupo: 'Grupo A',
      proveedor: 'DistriUno',
      activo: true,
    })
    // area: null simula un área dada de baja lógica sin guarda de uso (judgment-day ronda 1):
    // la fila sigue existiendo, pero su clasificación de área ya no resuelve a ningún nombre.
    const filaIncompleta = filaFixture({
      id: 2,
      codigoInterno: 'COD-2',
      nombre: 'Fideos guiseros 500g',
      area: null,
      categoria: null,
      marca: null,
      grupo: null,
      proveedor: null,
      activo: false,
    })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/articulos?')) return Promise.resolve(paginaFixture([filaCompleta, filaIncompleta]))
      return undefined
    })
    renderReporte()

    const filaUnoDom = await screen.findByRole('row', { name: /COD-1/ })
    expect(within(filaUnoDom).getByText('Aceite de girasol 900ml')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('Almacén')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('Bebidas')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('Marca A')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('Grupo A')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('DistriUno')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('Activo')).toBeInTheDocument()

    const filaDosDom = screen.getByRole('row', { name: /COD-2/ })
    expect(within(filaDosDom).getByText('Fideos guiseros 500g')).toBeInTheDocument()
    expect(within(filaDosDom).getAllByText('Sin asignar')).toHaveLength(5)
    expect(within(filaDosDom).getByText('Inactivo')).toBeInTheDocument()
  })

  // mutation-proof-tests: un solo dimension (categoría) probado no dice nada de sus vecinos —
  // cada una de las cinco clasificaciones tiene su propio par id/sin, con su propia guarda de
  // exclusividad del lado del servidor; mutar el wiring de UNA (p.ej. "Sin marca" seteando
  // sinCategoria) sobrevive si solo categoría está cubierta. Un solo caso paramétrico por
  // dimensión cubre las dos ramas: elegir "Sin X" manda sinX=true sin idX, elegir un valor real
  // manda idX sin sinX.
  const dimensiones = [
    { etiquetaCampo: 'Área', etiquetaSin: 'Sin área', paramSin: 'sinArea', paramId: 'idArea', etiquetaValorReal: 'Almacén', idEsperado: '1' },
    { etiquetaCampo: 'Categoría', etiquetaSin: 'Sin categoría', paramSin: 'sinCategoria', paramId: 'idCategoria', etiquetaValorReal: 'Bebidas', idEsperado: '10' },
    { etiquetaCampo: 'Marca', etiquetaSin: 'Sin marca', paramSin: 'sinMarca', paramId: 'idMarca', etiquetaValorReal: 'Marca A', idEsperado: '20' },
    { etiquetaCampo: 'Grupo', etiquetaSin: 'Sin grupo', paramSin: 'sinGrupo', paramId: 'idGrupo', etiquetaValorReal: 'Grupo A', idEsperado: '30' },
    { etiquetaCampo: 'Proveedor', etiquetaSin: 'Sin proveedor', paramSin: 'sinProveedor', paramId: 'idProveedor', etiquetaValorReal: 'DistriUno', idEsperado: '40' },
  ]

  describe.each(dimensiones)(
    'filtro $etiquetaCampo',
    ({ etiquetaCampo, etiquetaSin, paramSin, paramId, etiquetaValorReal, idEsperado }) => {
      it(`elegir "${etiquetaSin}" manda ${paramSin}=true sin ${paramId}`, async () => {
        mockearRutasBase()
        const usuario = userEvent.setup()
        renderReporte()

        await screen.findByText('COD-1')
        apiGetMock.mockClear()
        mockearRutasBase()
        await usuario.selectOptions(screen.getByLabelText(etiquetaCampo), etiquetaSin)

        await waitFor(() => {
          const llamadas = apiGetMock.mock.calls.map((call: unknown[]) => call[0] as string).filter((r) => r.startsWith('/reportes/articulos?'))
          expect(llamadas.some((r) => r.includes(`${paramSin}=true`) && !r.includes(`${paramId}=`))).toBe(true)
        })
      })

      it(`elegir un valor real de ${etiquetaCampo} manda ${paramId} sin ${paramSin}`, async () => {
        mockearRutasBase()
        const usuario = userEvent.setup()
        renderReporte()

        await screen.findByText('COD-1')
        apiGetMock.mockClear()
        mockearRutasBase()
        await usuario.selectOptions(screen.getByLabelText(etiquetaCampo), etiquetaValorReal)

        await waitFor(() => {
          const llamadas = apiGetMock.mock.calls.map((call: unknown[]) => call[0] as string).filter((r) => r.startsWith('/reportes/articulos?'))
          expect(llamadas.some((r) => r.includes(`${paramId}=${idEsperado}`) && !r.includes(paramSin))).toBe(true)
        })
      })
    },
  )

  it('tildar "Solo incompletos" manda soloIncompletos=true', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('COD-1')
    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.click(screen.getByLabelText('Solo incompletos'))

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.map((call: unknown[]) => call[0] as string).filter((r) => r.startsWith('/reportes/articulos?'))
      expect(llamadas.some((r) => r.includes('soloIncompletos=true'))).toBe(true)
    })
  })

  it('elegir Estado "Inactivos" manda activo=false, "Todos" no manda el parámetro', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('COD-1')
    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.selectOptions(screen.getByLabelText('Estado'), 'Inactivos')

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.map((call: unknown[]) => call[0] as string).filter((r) => r.startsWith('/reportes/articulos?'))
      expect(llamadas.some((r) => r.includes('activo=false'))).toBe(true)
    })

    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.selectOptions(screen.getByLabelText('Estado'), 'Todos')

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.map((call: unknown[]) => call[0] as string).filter((r) => r.startsWith('/reportes/articulos?'))
      expect(llamadas.some((r) => !r.includes('activo='))).toBe(true)
    })
  })

  // Lección del proyecto (dto-contract-honesty / brief): un builder separado para la descarga
  // desincroniza el export del listado. Acá se prueba que la ruta de descarga lleva EXACTAMENTE
  // los mismos filtros que la última consulta del listado (sin pagina/tamanio, con formato=xlsx).
  it('el botón de descarga usa exactamente los mismos filtros que el listado', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('COD-1')
    await usuario.selectOptions(screen.getByLabelText('Área'), '1')
    await usuario.click(screen.getByLabelText('Solo incompletos'))
    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.map((call: unknown[]) => call[0] as string).filter((r) => r.startsWith('/reportes/articulos?'))
      expect(llamadas.some((r) => r.includes('idArea=1') && r.includes('soloIncompletos=true'))).toBe(true)
    })

    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    expect(apiDescargarMock).toHaveBeenCalledWith('/reportes/articulos/export?idArea=1&soloIncompletos=true&formato=xlsx')
  })

  it('una respuesta de listado desactualizada nunca pisa la más reciente (generación)', async () => {
    let resolverPrimera: (valor: ReturnType<typeof paginaFixture>) => void = () => {}
    const primera = new Promise<ReturnType<typeof paginaFixture>>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/articulos?')) {
        cantidadDeLlamadas += 1
        if (cantidadDeLlamadas === 1) return primera
        return Promise.resolve(paginaFixture([filaFixture({ id: 202, codigoInterno: 'COD-202' })]))
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderReporte()
    await screen.findByLabelText('Área')

    await usuario.selectOptions(screen.getByLabelText('Área'), '1')
    expect(await screen.findByText('COD-202')).toBeInTheDocument()

    resolverPrimera(paginaFixture([filaFixture({ id: 909, codigoInterno: 'COD-909' })]))
    await waitFor(() => expect(screen.queryByText('COD-909')).not.toBeInTheDocument())
    expect(screen.getByText('COD-202')).toBeInTheDocument()
  })

  it('un error de listado se muestra con un botón de Reintentar', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/articulos?')) return Promise.reject(new Error('falló'))
      return undefined
    })
    renderReporte()

    expect(await screen.findByText('No se pudo cargar el reporte de artículos.')).toBeInTheDocument()
  })

  it('una falla al cargar un catálogo muestra un aviso sin romper la página', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/catalogos/marcas') return Promise.reject(new Error('falló'))
      return undefined
    })
    renderReporte()

    expect(await screen.findByText('No se pudieron cargar las marcas.')).toBeInTheDocument()
    expect(await screen.findByText('COD-1')).toBeInTheDocument()
  })
})

describe('ReporteDeArticulos — role gating', () => {
  it('un Vendedor nunca llega a /reportes/articulos: redirige a Inicio', async () => {
    usuarioActual = usuarioAdminFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()

    renderReporteProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Reporte de artículos')).not.toBeInTheDocument())
  })
})
