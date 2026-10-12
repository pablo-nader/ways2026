import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { CatalogoPublico, ConsultaDeCatalogo } from '../api/publico'
import { articuloPublico, catalogoPublico, categoriaPublica, diferida } from './datosDePrueba'
import { TAMANO_DE_PAGINA, useCatalogo, type FiltroDelCatalogo } from './useCatalogo'

const obtenerCatalogoMock = vi.fn<(consulta: ConsultaDeCatalogo) => Promise<CatalogoPublico>>()

vi.mock('../api/publico', () => ({
  clienteDePublico: { obtenerCatalogo: (consulta: ConsultaDeCatalogo) => obtenerCatalogoMock(consulta) },
}))

const SIN_FILTROS: FiltroDelCatalogo = { buscar: '', idCategoria: null }

beforeEach(() => {
  obtenerCatalogoMock.mockReset()
})

describe('useCatalogo', () => {
  it('pide la primera página con el filtro y el tamaño de página, y guarda lo que llega', async () => {
    obtenerCatalogoMock.mockResolvedValue(
      catalogoPublico({ articulos: [articuloPublico(1)], categorias: [categoriaPublica(5, 'Herramientas', 1)], total: 1 }),
    )

    const { result } = renderHook(() => useCatalogo({ buscar: 'mart', idCategoria: 5 }))

    expect(result.current.fase).toBe('cargando')
    expect(result.current.cargadoAlgunaVez).toBe(false)
    await waitFor(() => expect(result.current.fase).toBe('listo'))
    expect(obtenerCatalogoMock).toHaveBeenCalledWith({ buscar: 'mart', idCategoria: 5, pagina: 1, tamano: TAMANO_DE_PAGINA })
    expect(result.current.articulos.map((a) => a.id)).toEqual([1])
    expect(result.current.categorias).toEqual([categoriaPublica(5, 'Herramientas', 1)])
    expect(result.current.cargadoAlgunaVez).toBe(true)
    expect(result.current.hayMas).toBe(false)
  })

  it('una respuesta tardía de un filtro viejo no pisa la del filtro nuevo', async () => {
    const vieja = diferida<CatalogoPublico>()
    const nueva = diferida<CatalogoPublico>()
    obtenerCatalogoMock.mockImplementation(({ buscar }) => (buscar === 'a' ? vieja.promesa : nueva.promesa))

    const { result, rerender } = renderHook((filtro: FiltroDelCatalogo) => useCatalogo(filtro), {
      initialProps: { buscar: 'a', idCategoria: null } as FiltroDelCatalogo,
    })
    rerender({ buscar: 'ab', idCategoria: null })

    await act(async () => nueva.resolver(catalogoPublico({ articulos: [articuloPublico(2, { nombre: 'Nueva' })] })))
    await waitFor(() => expect(result.current.fase).toBe('listo'))
    await act(async () => vieja.resolver(catalogoPublico({ articulos: [articuloPublico(1, { nombre: 'Vieja' })] })))

    expect(result.current.articulos.map((a) => a.nombre)).toEqual(['Nueva'])
  })

  it('una falla tardía de un filtro viejo no tapa el resultado del nuevo', async () => {
    const vieja = diferida<CatalogoPublico>()
    obtenerCatalogoMock.mockImplementation(({ buscar }) =>
      buscar === 'a' ? vieja.promesa : Promise.resolve(catalogoPublico({ articulos: [articuloPublico(2)] })),
    )

    const { result, rerender } = renderHook((filtro: FiltroDelCatalogo) => useCatalogo(filtro), {
      initialProps: { buscar: 'a', idCategoria: null } as FiltroDelCatalogo,
    })
    rerender({ buscar: 'ab', idCategoria: null })
    await waitFor(() => expect(result.current.fase).toBe('listo'))

    await act(async () => vieja.rechazar(new Error('red')))

    expect(result.current.fase).toBe('listo')
    expect(result.current.articulos).toHaveLength(1)
  })

  it('mientras llega el resultado de un filtro nuevo conserva la lista anterior', async () => {
    const segunda = diferida<CatalogoPublico>()
    obtenerCatalogoMock
      .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(1)] }))
      .mockReturnValueOnce(segunda.promesa)

    const { result, rerender } = renderHook((filtro: FiltroDelCatalogo) => useCatalogo(filtro), {
      initialProps: SIN_FILTROS,
    })
    await waitFor(() => expect(result.current.fase).toBe('listo'))

    rerender({ buscar: 'x', idCategoria: null })

    await waitFor(() => expect(result.current.fase).toBe('cargando'))
    expect(result.current.articulos.map((a) => a.id)).toEqual([1])
  })

  describe('catálogo vacío', () => {
    function verHistorial(inicial: FiltroDelCatalogo) {
      const dibujos: { fase: string; total: number; respuestaSinFiltros: boolean; vacioAlEntrar: boolean }[] = []
      const vista = renderHook(
        (filtro: FiltroDelCatalogo) => {
          const catalogo = useCatalogo(filtro)
          dibujos.push({
            fase: catalogo.fase,
            total: catalogo.total,
            respuestaSinFiltros: catalogo.respuestaSinFiltros,
            vacioAlEntrar: catalogo.vacioAlEntrar,
          })

          return catalogo
        },
        { initialProps: inicial },
      )

      return { ...vista, dibujos }
    }

    it('entre limpiar el filtro y que llegue la respuesta nueva, la respuesta vacía de antes no figura como pedida sin filtros', async () => {
      const sinFiltros = diferida<CatalogoPublico>()
      obtenerCatalogoMock.mockImplementation(({ buscar }) =>
        buscar === 'zzz' ? Promise.resolve(catalogoPublico({ articulos: [], total: 0 })) : sinFiltros.promesa,
      )
      const { result, rerender, dibujos } = verHistorial({ buscar: 'zzz', idCategoria: null })
      await waitFor(() => expect(result.current.fase).toBe('listo'))
      expect(result.current.respuestaSinFiltros).toBe(false)
      expect(result.current.vacioAlEntrar).toBe(false)
      dibujos.length = 0

      rerender(SIN_FILTROS)

      expect(dibujos.length).toBeGreaterThan(0)
      expect(dibujos.filter((d) => d.respuestaSinFiltros || d.vacioAlEntrar)).toEqual([])
      await act(async () => sinFiltros.resolver(catalogoPublico({ articulos: [articuloPublico(1)] })))
      expect(result.current.respuestaSinFiltros).toBe(true)
      expect(result.current.vacioAlEntrar).toBe(false)
    })

    it('la primera respuesta buena, sin filtros y sin artículos, es "vacío al entrar"', async () => {
      obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [], total: 0 }))

      const { result } = verHistorial(SIN_FILTROS)
      await waitFor(() => expect(result.current.fase).toBe('listo'))

      expect(result.current.vacioAlEntrar).toBe(true)
      expect(result.current.respuestaSinFiltros).toBe(true)
    })

    it('una respuesta sin artículos pedida con una búsqueda o con una categoría no es "vacío al entrar"', async () => {
      obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [], total: 0 }))

      const conFiltro = verHistorial({ buscar: 'x', idCategoria: null })
      await waitFor(() => expect(conFiltro.result.current.fase).toBe('listo'))
      expect(conFiltro.result.current.vacioAlEntrar).toBe(false)

      const conCategoria = verHistorial({ buscar: '', idCategoria: 5 })
      await waitFor(() => expect(conCategoria.result.current.fase).toBe('listo'))
      expect(conCategoria.result.current.vacioAlEntrar).toBe(false)
      expect(conCategoria.result.current.respuestaSinFiltros).toBe(false)
    })

    it('si ya hubo una primera respuesta con artículos, una vacía sin filtros después no lo enciende', async () => {
      obtenerCatalogoMock
        .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(1)] }))
        .mockResolvedValueOnce(catalogoPublico({ articulos: [], total: 0 }))
        .mockResolvedValueOnce(catalogoPublico({ articulos: [], total: 0 }))
      const { result, rerender } = verHistorial(SIN_FILTROS)
      await waitFor(() => expect(result.current.fase).toBe('listo'))
      rerender({ buscar: 'x', idCategoria: null })
      await waitFor(() => expect(result.current.total).toBe(0))

      rerender(SIN_FILTROS)
      await waitFor(() => expect(obtenerCatalogoMock).toHaveBeenCalledTimes(3))
      await waitFor(() => expect(result.current.respuestaSinFiltros).toBe(true))

      expect(result.current.total).toBe(0)
      expect(result.current.vacioAlEntrar).toBe(false)
    })
  })

  it('"Ver más" pide la página siguiente y la agrega sin repetir artículos', async () => {
    obtenerCatalogoMock
      .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(1), articuloPublico(2)], total: 50 }))
      .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(2), articuloPublico(3)], total: 50, pagina: 2 }))

    const { result } = renderHook(() => useCatalogo(SIN_FILTROS))
    await waitFor(() => expect(result.current.hayMas).toBe(true))

    await act(async () => result.current.cargarMas())

    expect(obtenerCatalogoMock).toHaveBeenLastCalledWith({ buscar: '', idCategoria: null, pagina: 2, tamano: TAMANO_DE_PAGINA })
    expect(result.current.articulos.map((a) => a.id)).toEqual([1, 2, 3])
    expect(result.current.pagina).toBe(2)
    expect(result.current.cargandoMas).toBe(false)
  })

  it('hay más mientras lo cargado no alcance el total, y no cuando la última página lo cubre', async () => {
    obtenerCatalogoMock
      .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(1)], total: TAMANO_DE_PAGINA + 1 }))
      .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(2)], total: TAMANO_DE_PAGINA + 1, pagina: 2 }))

    const { result } = renderHook(() => useCatalogo(SIN_FILTROS))
    await waitFor(() => expect(result.current.hayMas).toBe(true))

    await act(async () => result.current.cargarMas())

    expect(result.current.hayMas).toBe(false)
  })

  it('una página que llega después de cambiar el filtro no se agrega a la lista del filtro nuevo', async () => {
    const masVieja = diferida<CatalogoPublico>()
    obtenerCatalogoMock
      .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(1)], total: 50 }))
      .mockReturnValueOnce(masVieja.promesa)
      .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(9)], total: 1 }))

    const { result, rerender } = renderHook((filtro: FiltroDelCatalogo) => useCatalogo(filtro), {
      initialProps: SIN_FILTROS,
    })
    await waitFor(() => expect(result.current.hayMas).toBe(true))
    act(() => void result.current.cargarMas())
    rerender({ buscar: 'x', idCategoria: null })
    await waitFor(() => expect(result.current.articulos.map((a) => a.id)).toEqual([9]))

    await act(async () => masVieja.resolver(catalogoPublico({ articulos: [articuloPublico(2)], total: 50, pagina: 2 })))

    expect(result.current.articulos.map((a) => a.id)).toEqual([9])
    expect(result.current.cargandoMas).toBe(false)
  })

  it('si falla "Ver más" conserva la lista, avisa y se puede reintentar', async () => {
    obtenerCatalogoMock
      .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(1)], total: 50 }))
      .mockRejectedValueOnce(new Error('red'))
      .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(2)], total: 50, pagina: 2 }))

    const { result } = renderHook(() => useCatalogo(SIN_FILTROS))
    await waitFor(() => expect(result.current.hayMas).toBe(true))

    await act(async () => result.current.cargarMas())
    expect(result.current.errorDeMas).toBe(true)
    expect(result.current.fase).toBe('listo')
    expect(result.current.articulos.map((a) => a.id)).toEqual([1])

    await act(async () => result.current.cargarMas())
    expect(result.current.errorDeMas).toBe(false)
    expect(result.current.articulos.map((a) => a.id)).toEqual([1, 2])
  })

  it('si falla la primera lectura queda en error y reintentar la repite', async () => {
    obtenerCatalogoMock
      .mockRejectedValueOnce(new Error('red'))
      .mockResolvedValueOnce(catalogoPublico({ articulos: [articuloPublico(1)] }))

    const { result } = renderHook(() => useCatalogo(SIN_FILTROS))
    await waitFor(() => expect(result.current.fase).toBe('error'))
    expect(result.current.cargadoAlgunaVez).toBe(false)

    await act(async () => result.current.reintentar())

    expect(result.current.fase).toBe('listo')
    expect(result.current.articulos).toHaveLength(1)
  })
})
