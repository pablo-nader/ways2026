import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEMORA_DE_FILTRO_DE_PADRON_MS, usePadronEnServidor } from './usePadronEnServidor'

type Filtros = { texto: string; opcion: string; desde: number | null }
type Columna = 'a' | 'b'

const VACIOS: Filtros = { texto: '', opcion: '', desde: null }

function esColumna(columna: string): columna is Columna {
  return columna === 'a' || columna === 'b'
}

function montar(ocupado: () => boolean = () => false) {
  return renderHook(() => usePadronEnServidor<Filtros, Columna>(VACIOS, { esColumnaOrdenable: esColumna, ocupado }))
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('usePadronEnServidor', () => {
  it('arranca con la consulta de siempre: sin filtros ni orden, página 1 de 25', () => {
    const { result } = montar()

    expect(result.current.consulta).toEqual({ filtros: VACIOS, orden: null, pagina: 1, tamanio: 25 })
    expect(result.current.hayFiltros).toBe(false)
  })

  it('un texto se muestra en el borrador al instante y se aplica recién tras la demora, una sola vez por ráfaga', () => {
    const { result } = montar()

    act(() => result.current.cambiarFiltros({ texto: 'a' }))
    act(() => vi.advanceTimersByTime(DEMORA_DE_FILTRO_DE_PADRON_MS - 1))
    act(() => result.current.cambiarFiltros({ texto: 'ab' }))
    act(() => vi.advanceTimersByTime(DEMORA_DE_FILTRO_DE_PADRON_MS - 1))

    expect(result.current.borrador.texto).toBe('ab')
    expect(result.current.consulta.filtros.texto).toBe('')

    act(() => vi.advanceTimersByTime(1))

    expect(result.current.consulta.filtros.texto).toBe('ab')
    expect(result.current.consultaRef.current).toBe(result.current.consulta)
  })

  it('un selector se aplica en el acto y arrastra el texto que estaba esperando', () => {
    const { result } = montar()
    act(() => result.current.cambiarFiltros({ texto: 'ab' }))

    act(() => result.current.cambiarFiltros({ opcion: 'true' }, true))

    expect(result.current.consulta.filtros).toEqual({ texto: 'ab', opcion: 'true', desde: null })
    act(() => vi.advanceTimersByTime(DEMORA_DE_FILTRO_DE_PADRON_MS * 2))
    expect(result.current.consulta.filtros.texto).toBe('ab')
  })

  it('si los filtros efectivos no cambian no hay consulta nueva ni vuelve a la página 1', () => {
    const { result } = montar()
    act(() => result.current.cambiarFiltros({ texto: 'ab' }, true))
    act(() => result.current.irAPagina(3))
    const antes = result.current.consulta

    act(() => result.current.cambiarFiltros({ texto: 'ab ' }, true))
    act(() => result.current.cambiarFiltros({ texto: 'ab' }, true))

    expect(result.current.consulta).toBe(antes)
    expect(result.current.consulta.pagina).toBe(3)
  })

  it('cambiar un filtro, el orden o el tamaño vuelve a la página 1', () => {
    const { result } = montar()
    act(() => result.current.irAPagina(4))
    act(() => result.current.cambiarFiltros({ opcion: 'x' }, true))
    expect(result.current.consulta.pagina).toBe(1)

    act(() => result.current.irAPagina(4))
    act(() => result.current.alternarOrden('a'))
    expect(result.current.consulta.pagina).toBe(1)

    act(() => result.current.irAPagina(4))
    act(() => result.current.cambiarTamanio(50))
    expect(result.current.consulta).toMatchObject({ pagina: 1, tamanio: 50 })

    act(() => result.current.irAPagina(4))
    act(() => result.current.cambiarFiltros({ texto: 'z' }))
    act(() => vi.advanceTimersByTime(DEMORA_DE_FILTRO_DE_PADRON_MS))
    expect(result.current.consulta.pagina).toBe(1)
  })

  it('el orden recorre ascendente, descendente y vuelve al de siempre; otra columna empieza ascendente', () => {
    const { result } = montar()

    act(() => result.current.alternarOrden('a'))
    expect(result.current.consulta.orden).toEqual({ columna: 'a', direccion: 'ascendente' })
    act(() => result.current.alternarOrden('a'))
    expect(result.current.consulta.orden).toEqual({ columna: 'a', direccion: 'descendente' })
    act(() => result.current.alternarOrden('b'))
    expect(result.current.consulta.orden).toEqual({ columna: 'b', direccion: 'ascendente' })
    act(() => result.current.alternarOrden('b'))
    act(() => result.current.alternarOrden('b'))
    expect(result.current.consulta.orden).toBeNull()
  })

  it('una columna que el servidor no ordena no cambia nada', () => {
    const { result } = montar()
    const antes = result.current.consulta

    act(() => result.current.alternarOrden('acciones'))

    expect(result.current.consulta).toBe(antes)
  })

  it('Anterior y Siguiente mueven la página y nunca bajan de la primera', () => {
    const { result } = montar()

    act(() => result.current.cambiarPagina(1))
    act(() => result.current.cambiarPagina(1))
    expect(result.current.consulta.pagina).toBe(3)
    act(() => result.current.cambiarPagina(-5))
    expect(result.current.consulta.pagina).toBe(1)
  })

  it('limpiar vacía los filtros y el borrador, y conserva el orden y el tamaño', () => {
    const { result } = montar()
    act(() => result.current.alternarOrden('a'))
    act(() => result.current.cambiarTamanio(100))
    act(() => result.current.cambiarFiltros({ texto: 'ab', desde: 3 }, true))
    expect(result.current.hayFiltros).toBe(true)

    act(() => result.current.limpiar())

    expect(result.current.borrador).toEqual(VACIOS)
    expect(result.current.consulta).toEqual({
      filtros: VACIOS,
      orden: { columna: 'a', direccion: 'ascendente' },
      pagina: 1,
      tamanio: 100,
    })
    expect(result.current.hayFiltros).toBe(false)
  })

  it('limpiar cancela el texto que esperaba su demora', () => {
    const { result } = montar()
    act(() => result.current.cambiarFiltros({ texto: 'ab' }))

    act(() => result.current.limpiar())
    act(() => vi.advanceTimersByTime(DEMORA_DE_FILTRO_DE_PADRON_MS * 2))

    expect(result.current.consulta.filtros).toEqual(VACIOS)
  })

  it('con una escritura en vuelo el texto no se aplica: reintenta cada demora hasta que termina', () => {
    let ocupado = true
    const { result } = montar(() => ocupado)
    act(() => result.current.cambiarFiltros({ texto: 'ab' }))

    act(() => vi.advanceTimersByTime(DEMORA_DE_FILTRO_DE_PADRON_MS * 3))
    expect(result.current.consulta.filtros.texto).toBe('')

    ocupado = false
    act(() => vi.advanceTimersByTime(DEMORA_DE_FILTRO_DE_PADRON_MS))
    expect(result.current.consulta.filtros.texto).toBe('ab')
  })

  it('desmontar cancela el texto que esperaba su demora', () => {
    const { result, unmount } = montar()
    act(() => result.current.cambiarFiltros({ texto: 'ab' }))

    unmount()

    expect(vi.getTimerCount()).toBe(0)
  })
})
