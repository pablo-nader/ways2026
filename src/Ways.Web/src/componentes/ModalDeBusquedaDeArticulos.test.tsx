import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalDeBusquedaDeArticulos } from './ModalDeBusquedaDeArticulos'
import type { ArticuloListado, PaginaDe, ResultadoDeResolucion } from '../api/tipos'
import type { OfertasVigentesDeArticulo } from '../api/ofertasVigentes'

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

function articuloListadoFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    disponibleOnline: true,
    disponibleVenta: true,
    tieneImagen: false,
    id: 9,
    codigoInterno: 'A0009',
    nombre: 'Fanta 1.5L',
    descripcion: null,
    idArea: 1,
    idCategoria: null,
    idMarca: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 1,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    costoLista: null,
    costoListaPor: 'Unidad',
    costoListaIncluyeIva: true,
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
    acumulaEnVenta: true,
    idFamilia: null,
    ...sobrescribir,
  }
}

function paginaDe(items: ArticuloListado[]): PaginaDe<ArticuloListado> {
  return { items, total: items.length, pagina: 1, tamanio: 25 }
}

function propsDe(sobrescribir: Partial<Parameters<typeof ModalDeBusquedaDeArticulos>[0]> = {}) {
  return {
    idListaPrecio: 1,
    idEmpresa: 1,
    onAgregar: vi.fn(),
    onCerrar: vi.fn(),
    ...sobrescribir,
  }
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
})

describe('ModalDeBusquedaDeArticulos — contexto de precio', () => {
  it('con idListaPrecio null no resuelve precio (queda inerte) aunque la búsqueda encuentre resultados', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture()])) : Promise.reject(new Error(ruta)),
    )

    render(<ModalDeBusquedaDeArticulos {...propsDe({ idListaPrecio: null })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(await screen.findByText('Fanta 1.5L')).toBeInTheDocument()
    expect(screen.getByText('—')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('la resolución de precio viaja con idEmpresa/idListaPrecio de las props y cantidad = 1 por artículo', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture()])) : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') return Promise.resolve<ResultadoDeResolucion[]>([])
      return Promise.reject(new Error(ruta))
    })

    render(<ModalDeBusquedaDeArticulos {...propsDe({ idListaPrecio: 3, idEmpresa: 7 })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    await screen.findByText('Fanta 1.5L')
    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/ofertas/resolver', {
        lineas: [{ idArticulo: 9, idEmpresa: 7, idListaPrecio: 3, cantidad: 1 }],
        agregacion: 'PorLinea',
      }),
    )
  })
})

describe('ModalDeBusquedaDeArticulos — solo artículos vendibles', () => {
  /**
   * Cláusula bajo prueba: el buscador de la caja pide `soloVendibles=true`; sin él un artículo de uso interno
   * aparecería para cargarlo y recién la venta lo rechazaría. Mutación: quitar la opción en la llamada hace fallar.
   */
  it('la búsqueda pide soloVendibles=true', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture()])) : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockResolvedValue([])

    render(<ModalDeBusquedaDeArticulos {...propsDe()} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    await screen.findByText('Fanta 1.5L')
    expect(apiGetMock).toHaveBeenCalledWith('/articulos?busqueda=fa&soloVendibles=true')
  })
})

describe('ModalDeBusquedaDeArticulos — errores y reentrancia', () => {
  it('una búsqueda rechazada muestra un error y limpia los resultados previos', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos?busqueda=fa')) return Promise.resolve(paginaDe([articuloListadoFixture()]))
      if (ruta.startsWith('/articulos?busqueda=zz')) return Promise.reject(new Error('boom'))
      return Promise.reject(new Error(ruta))
    })
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/resolver' ? Promise.resolve<ResultadoDeResolucion[]>([]) : Promise.reject(new Error(ruta)),
    )

    render(<ModalDeBusquedaDeArticulos {...propsDe()} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')

    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    await screen.findByText('Fanta 1.5L')

    fireEvent.change(input, { target: { value: 'zz' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(await screen.findByText('No se pudo buscar artículos.')).toBeInTheDocument()
    expect(screen.queryByText('Fanta 1.5L')).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el guard `agregadoRef.current` al tope de `agregar()`. Evidencia de
   * mutación (mutation-proof-tests regla 2): con el guard comentado, este test falla
   * (`onAgregar` se llama 2 veces); restaurado, vuelve a verde — ver el reporte de la tarea.
   */
  it('dos clicks sincrónicos en "Agregar" de la misma fila llaman a onAgregar una sola vez', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture()])) : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/resolver' ? Promise.resolve<ResultadoDeResolucion[]>([]) : Promise.reject(new Error(ruta)),
    )
    const onAgregar = vi.fn()

    render(<ModalDeBusquedaDeArticulos {...propsDe({ onAgregar })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    const boton = await screen.findByRole('button', { name: 'Agregar' })

    fireEvent.click(boton)
    fireEvent.click(boton)

    expect(onAgregar).toHaveBeenCalledTimes(1)
    expect(onAgregar).toHaveBeenCalledWith(
      { idArticulo: 9, codigoInterno: 'A0009', nombre: 'Fanta 1.5L', codigoBarra: null, acumulaEnVenta: true, unidadVenta: 'Unidad' },
      1,
    )
  })

  it.each([true, false])('"Agregar" de un artículo con acumulaEnVenta = %s lo pasa tal cual a onAgregar', async (acumula) => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa')
        ? Promise.resolve(paginaDe([articuloListadoFixture({ acumulaEnVenta: acumula })]))
        : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/resolver' ? Promise.resolve<ResultadoDeResolucion[]>([]) : Promise.reject(new Error(ruta)),
    )
    const onAgregar = vi.fn()

    render(<ModalDeBusquedaDeArticulos {...propsDe({ onAgregar })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    fireEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    expect(onAgregar).toHaveBeenCalledWith(expect.objectContaining({ idArticulo: 9, acumulaEnVenta: acumula }), 1)
  })

  it('"Agregar" le pasa a onAgregar la unidad de venta del artículo elegido', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=qu')
        ? Promise.resolve(paginaDe([articuloListadoFixture({ id: 10, codigoInterno: 'A0010', nombre: 'Queso', unidadVenta: 'Peso' })]))
        : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/resolver' ? Promise.resolve<ResultadoDeResolucion[]>([]) : Promise.reject(new Error(ruta)),
    )
    const onAgregar = vi.fn()

    render(<ModalDeBusquedaDeArticulos {...propsDe({ onAgregar })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'qu' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    fireEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    expect(onAgregar).toHaveBeenCalledWith(expect.objectContaining({ idArticulo: 10, unidadVenta: 'Peso' }), 1)
  })

  it('Escape invoca a onCerrar', () => {
    const onCerrar = vi.fn()
    render(<ModalDeBusquedaDeArticulos {...propsDe({ onCerrar })} />)

    fireEvent.keyDown(document, { key: 'Escape' })

    expect(onCerrar).toHaveBeenCalledTimes(1)
  })
})

describe('ModalDeBusquedaDeArticulos — foco al abrir (stage-pos-turno-y-foco)', () => {
  it('el input de búsqueda queda enfocado al montar', () => {
    render(<ModalDeBusquedaDeArticulos {...propsDe()} />)

    expect(screen.getByLabelText('Buscar artículo por nombre')).toHaveFocus()
  })
})

/**
 * `aria-modal="true"` exige una trampa de foco real (react-async-state regla 13). Mutation-proof-
 * tests: con el markup inline anterior (sin `Modal`), el foco se queda en "Buscar" y este test falla.
 */
describe('ModalDeBusquedaDeArticulos — trampa de foco (sobre Modal)', () => {
  it('Tab en el último control vuelve al primero ("Cerrar") en vez de escapar a la pantalla de atrás', () => {
    render(<ModalDeBusquedaDeArticulos {...propsDe()} />)
    const buscar = screen.getByRole('button', { name: 'Buscar' })

    buscar.focus()
    fireEvent.keyDown(buscar, { key: 'Tab' })

    expect(screen.getByRole('button', { name: 'Cerrar' })).toHaveFocus()
  })
})

describe('ModalDeBusquedaDeArticulos — motivoSinAgregar (stage-pos-turno-y-foco: turno cerrado, solo consulta)', () => {
  it('con motivoSinAgregar definido, el precio se muestra pero "Agregar" queda deshabilitado (con ese motivo como title) y no dispara onAgregar', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture()])) : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/resolver'
        ? Promise.resolve<ResultadoDeResolucion[]>([
            { idArticulo: 9, idListaPrecio: 1, precioOriginal: 250, precioFinal: 200, descuentoUnitario: 50, aplicadas: [] },
          ])
        : Promise.reject(new Error(ruta)),
    )
    const onAgregar = vi.fn()

    render(<ModalDeBusquedaDeArticulos {...propsDe({ onAgregar, motivoSinAgregar: 'Turno cerrado: abrí un turno para vender.' })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(await screen.findByText('$ 200,00')).toBeInTheDocument()
    const boton = screen.getByRole('button', { name: 'Agregar' })
    expect(boton).toBeDisabled()
    expect(boton).toHaveAttribute('title', 'Turno cerrado: abrí un turno para vender.')

    fireEvent.click(boton)
    expect(onAgregar).not.toHaveBeenCalled()
  })

  /** judgment-day ronda 1, T4: "turno todavía cargando" y "turno confirmado cerrado" son avisos
   * distintos — el componente solo refleja lo que le pasan (el mensaje en sí lo arma `Pos.tsx`,
   * cubierto en `Pos.test.tsx`), pero este test prueba que el `title` cambia con el mensaje. */
  it('con otro motivo (ej. "todavía consultando el turno"), el title del botón refleja ESE mensaje', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture()])) : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/resolver' ? Promise.resolve<ResultadoDeResolucion[]>([]) : Promise.reject(new Error(ruta)),
    )

    render(<ModalDeBusquedaDeArticulos {...propsDe({ motivoSinAgregar: 'Consultando turno…' })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    const boton = await screen.findByRole('button', { name: 'Agregar' })
    expect(boton).toBeDisabled()
    expect(boton).toHaveAttribute('title', 'Consultando turno…')
  })

  it('sin la prop (default), "Agregar" queda habilitado — comportamiento sin cambios para los demás llamadores', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture()])) : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/resolver' ? Promise.resolve<ResultadoDeResolucion[]>([]) : Promise.reject(new Error(ruta)),
    )

    render(<ModalDeBusquedaDeArticulos {...propsDe()} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(await screen.findByRole('button', { name: 'Agregar' })).toBeEnabled()
  })
})

describe('ModalDeBusquedaDeArticulos — columna de costo (solo back-office)', () => {
  async function buscarFanta(props: Partial<Parameters<typeof ModalDeBusquedaDeArticulos>[0]>) {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa')
        ? Promise.resolve(paginaDe([articuloListadoFixture({ costoNominal: 1234.5 })]))
        : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/ofertas/resolver'
        ? Promise.resolve<ResultadoDeResolucion[]>([
            { idArticulo: 9, precioFinal: 2000, precioOriginal: 2000, ofertaAplicada: null } as unknown as ResultadoDeResolucion,
          ])
        : Promise.reject(new Error(ruta)),
    )

    render(<ModalDeBusquedaDeArticulos {...propsDe(props)} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    // Espera el DATO (el precio resuelto, que llega después de la fila), no solo el nombre.
    await screen.findByText('$ 2.000,00')
  }

  it('con mostrarCosto, la columna Costo muestra el costoNominal junto al precio', async () => {
    await buscarFanta({ mostrarCosto: true })

    expect(screen.getByRole('columnheader', { name: 'Costo' })).toBeInTheDocument()
    expect(screen.getByText('$ 1.234,50')).toBeInTheDocument()
  })

  it('con mostrarCosto y costoNominal null, la celda muestra "—"', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/articulos?busqueda=fa') ? Promise.resolve(paginaDe([articuloListadoFixture({ costoNominal: null })])) : Promise.reject(new Error(ruta)),
    )
    apiPostMock.mockResolvedValue([{ idArticulo: 9, precioFinal: 2000 }])

    render(<ModalDeBusquedaDeArticulos {...propsDe({ mostrarCosto: true })} />)
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    await screen.findByText('$ 2.000,00')
    expect(screen.getByText('—')).toBeInTheDocument()
  })

  it('uso del POS (sin la prop): ni la columna ni el costo se renderizan aunque la API lo mande', async () => {
    await buscarFanta({})

    expect(screen.queryByRole('columnheader', { name: 'Costo' })).not.toBeInTheDocument()
    expect(screen.queryByText('$ 1.234,50')).not.toBeInTheDocument()
  })
})

describe('ModalDeBusquedaDeArticulos — resultados paginados por el servidor', () => {
  /** La búsqueda trae una sola página (25 por defecto): reordenar en el cliente ordenaría solo esa página, así que las
   * columnas no ofrecen orden. */
  it('los encabezados no son ordenables', () => {
    render(<ModalDeBusquedaDeArticulos {...propsDe({ mostrarCosto: true })} />)

    const encabezados = screen.getAllByRole('columnheader')
    expect(encabezados.map((th) => th.textContent)).toEqual(['Código', 'Nombre', 'Costo', 'Precio', 'Acciones'])
    for (const th of encabezados) {
      expect(within(th).queryByRole('button')).not.toBeInTheDocument()
      expect(th).not.toHaveAttribute('aria-sort')
    }
  })
})

describe('ModalDeBusquedaDeArticulos — marca de ofertas vigentes', () => {
  const vigentesDe9: OfertasVigentesDeArticulo[] = [
    {
      idArticulo: 9,
      ofertas: [
        {
          idOferta: 1,
          nombre: 'Gaseosas',
          alcance: 'Articulo',
          cantidadMinima: 3,
          soloMultiplos: false,
          porcentaje: 10,
          precioUnitario: null,
          importeFijo: null,
          fechaHasta: null,
          acumulable: false,
        },
      ],
    },
  ]

  function simularApi(vigentes: (cuerpo: unknown) => Promise<unknown>) {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos?busqueda=fa')) return Promise.resolve(paginaDe([articuloListadoFixture()]))
      if (ruta.startsWith('/articulos?busqueda=co'))
        return Promise.resolve(paginaDe([articuloListadoFixture({ id: 10, codigoInterno: 'A0010', nombre: 'Coca 1L' })]))
      return Promise.reject(new Error(ruta))
    })
    apiPostMock.mockImplementation((ruta: string, cuerpo: unknown) => {
      if (ruta === '/ofertas/resolver') return Promise.resolve<ResultadoDeResolucion[]>([])
      if (ruta === '/ofertas/vigentes') return vigentes(cuerpo)
      return Promise.reject(new Error(ruta))
    })
  }

  function buscar(termino: string) {
    const input = screen.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: termino } })
    fireEvent.keyDown(input, { key: 'Enter' })
  }

  it('muestra la marca solo en el artículo con ofertas y el tooltip trae la condición de cantidad', async () => {
    simularApi(() => Promise.resolve(vigentesDe9))

    render(<ModalDeBusquedaDeArticulos {...propsDe()} />)
    buscar('fa')

    const marca = await screen.findByRole('button', { name: 'Oferta' })
    expect(screen.getAllByRole('tooltip')).toHaveLength(1)
    expect(screen.getByRole('tooltip')).toHaveTextContent('Gaseosas — 10% off · Llevando 3')

    fireEvent.focus(marca)
    expect(screen.getByRole('tooltip')).toHaveAttribute('data-visible', 'true')
  })

  it('la consulta viaja con los ids, idEmpresa e idListaPrecio', async () => {
    simularApi(() => Promise.resolve([]))

    render(<ModalDeBusquedaDeArticulos {...propsDe({ idEmpresa: 7, idListaPrecio: 3 })} />)
    buscar('fa')

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/ofertas/vigentes', { idsArticulos: [9], idEmpresa: 7, idListaPrecio: 3 }),
    )
  })

  it('un artículo sin ofertas no lleva marca', async () => {
    simularApi(() => Promise.resolve([]))

    render(<ModalDeBusquedaDeArticulos {...propsDe()} />)
    buscar('fa')

    await screen.findByText('Fanta 1.5L')
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/ofertas/vigentes', expect.anything()))
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })

  it('si falla la consulta de ofertas la búsqueda lista igual, sin marca ni error', async () => {
    simularApi(() => Promise.reject(new Error('boom')))

    render(<ModalDeBusquedaDeArticulos {...propsDe()} />)
    buscar('fa')

    expect(await screen.findByText('Fanta 1.5L')).toBeInTheDocument()
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/ofertas/vigentes', expect.anything()))
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `generacionRef.current !== generacion` en `cargarVigentes`; sin ella la respuesta tardía de
   * "fa" (que trae ofertas del id 10) pintaría la marca en el resultado de "co". */
  it('una respuesta tardía de una búsqueda vieja no pinta marcas en la nueva', async () => {
    let resolverViejo: (v: OfertasVigentesDeArticulo[]) => void = () => {}
    simularApi((cuerpo) => {
      const ids = (cuerpo as { idsArticulos: number[] }).idsArticulos
      if (ids[0] === 9) return new Promise((resolver) => (resolverViejo = resolver as typeof resolverViejo))
      return Promise.resolve([])
    })

    render(<ModalDeBusquedaDeArticulos {...propsDe()} />)
    buscar('fa')
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/ofertas/vigentes', expect.objectContaining({ idsArticulos: [9] })))

    buscar('co')
    expect(await screen.findByText('Coca 1L')).toBeInTheDocument()
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/ofertas/vigentes', expect.objectContaining({ idsArticulos: [10] })))

    resolverViejo([{ ...vigentesDe9[0], idArticulo: 10 }])
    await new Promise((r) => setTimeout(r, 0))

    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })

  it('Escape oculta el tooltip sin cerrar el modal; el siguiente Escape sí lo cierra', async () => {
    simularApi(() => Promise.resolve(vigentesDe9))
    const onCerrar = vi.fn()

    render(<ModalDeBusquedaDeArticulos {...propsDe({ onCerrar })} />)
    buscar('fa')
    const marca = await screen.findByRole('button', { name: 'Oferta' })
    fireEvent.focus(marca)

    fireEvent.keyDown(marca, { key: 'Escape' })
    expect(screen.getByRole('tooltip')).toHaveAttribute('data-visible', 'false')
    expect(onCerrar).not.toHaveBeenCalled()

    fireEvent.keyDown(marca, { key: 'Escape' })
    expect(onCerrar).toHaveBeenCalledTimes(1)
  })

  it('el clic en la marca no agrega el artículo', async () => {
    simularApi(() => Promise.resolve(vigentesDe9))
    const onAgregar = vi.fn()

    render(<ModalDeBusquedaDeArticulos {...propsDe({ onAgregar })} />)
    buscar('fa')
    fireEvent.click(await screen.findByRole('button', { name: 'Oferta' }))

    expect(onAgregar).not.toHaveBeenCalled()
  })
})
