import { act, renderHook, waitFor } from '@testing-library/react'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ArticuloListado, UnidadVenta } from '../api/tipos'
import { useUnidadesDeVentaDeLineas } from './useUnidadesDeVentaDeLineas'

const obtenerMock = vi.fn()

vi.mock('../api/articulos', () => ({
  clienteDeArticulos: { obtener: (...args: unknown[]) => obtenerMock(...(args as [number])) },
}))

type Linea = { clave: number; idArticulo: number | ''; cantidad: string; unidadVenta?: UnidadVenta }

function articulo(id: number, unidadVenta: UnidadVenta): ArticuloListado {
  return { id, unidadVenta } as ArticuloListado
}

function montar(inicial: Linea[], habilitado = true) {
  return renderHook(() => {
    const [lineas, setLineas] = useState<Linea[]>(inicial)
    useUnidadesDeVentaDeLineas(lineas, setLineas, habilitado)
    return { lineas, setLineas }
  })
}

beforeEach(() => {
  obtenerMock.mockReset()
})

describe('useUnidadesDeVentaDeLineas', () => {
  it('completa la unidad de las líneas que no la traen, con una sola consulta por artículo', async () => {
    obtenerMock.mockImplementation((id: number) => Promise.resolve(articulo(id, id === 10 ? 'Unidad' : 'Peso')))

    const { result } = montar([
      { clave: 1, idArticulo: 10, cantidad: '2' },
      { clave: 2, idArticulo: 10, cantidad: '3' },
      { clave: 3, idArticulo: 20, cantidad: '1.5' },
    ])

    await waitFor(() => expect(result.current.lineas.map((l) => l.unidadVenta)).toEqual(['Unidad', 'Unidad', 'Peso']))
    expect(obtenerMock).toHaveBeenCalledTimes(2)
    expect(obtenerMock).toHaveBeenCalledWith(10)
    expect(obtenerMock).toHaveBeenCalledWith(20)
  })

  it('si la pantalla reemplaza las líneas después de una consulta exitosa, la unidad vuelve sin otra consulta', async () => {
    obtenerMock.mockImplementation((id: number) => Promise.resolve(articulo(id, 'Unidad')))
    const { result } = montar([{ clave: 1, idArticulo: 10, cantidad: '2' }])
    await waitFor(() => expect(result.current.lineas[0].unidadVenta).toBe('Unidad'))

    act(() => result.current.setLineas([{ clave: 7, idArticulo: 10, cantidad: '2' }]))

    await waitFor(() => expect(result.current.lineas[0].unidadVenta).toBe('Unidad'))
    expect(obtenerMock).toHaveBeenCalledTimes(1)
  })

  it('un artículo cuya consulta falló no se vuelve a pedir cuando se reemplazan las líneas', async () => {
    obtenerMock.mockRejectedValue(new Error('sin red'))
    const { result } = montar([{ clave: 1, idArticulo: 10, cantidad: '2' }])
    await waitFor(() => expect(obtenerMock).toHaveBeenCalledTimes(1))

    act(() => result.current.setLineas([{ clave: 2, idArticulo: 10, cantidad: '2' }]))
    await act(async () => {})

    expect(obtenerMock).toHaveBeenCalledTimes(1)
    expect(result.current.lineas[0].unidadVenta).toBeUndefined()
  })

  it('deshabilitado (documento de solo lectura) no hace ninguna consulta ni completa nada', async () => {
    const { result } = montar([{ clave: 1, idArticulo: 10, cantidad: '2' }], false)

    await act(async () => {})
    expect(obtenerMock).not.toHaveBeenCalled()
    expect(result.current.lineas[0].unidadVenta).toBeUndefined()
  })

  it('no consulta nada por una línea que ya trae su unidad ni por una fila sin artículo', async () => {
    montar([
      { clave: 1, idArticulo: 10, cantidad: '2', unidadVenta: 'Unidad' },
      { clave: 2, idArticulo: '', cantidad: '' },
    ])

    await act(async () => {})
    expect(obtenerMock).not.toHaveBeenCalled()
  })

  it('si la consulta falla la línea queda sin unidad (permisivo) y no se reintenta', async () => {
    obtenerMock.mockRejectedValue(new Error('sin red'))

    const { result } = montar([{ clave: 1, idArticulo: 10, cantidad: '2.5' }])

    await waitFor(() => expect(obtenerMock).toHaveBeenCalledTimes(1))
    await act(async () => {})
    expect(result.current.lineas[0].unidadVenta).toBeUndefined()

    act(() => result.current.setLineas((prev) => prev.map((l) => ({ ...l, cantidad: '3' }))))
    await act(async () => {})
    expect(obtenerMock).toHaveBeenCalledTimes(1)
  })

  it('una respuesta sin unidad de venta tampoco inventa una', async () => {
    obtenerMock.mockResolvedValue(undefined)

    const { result } = montar([{ clave: 1, idArticulo: 10, cantidad: '1' }])

    await waitFor(() => expect(obtenerMock).toHaveBeenCalledTimes(1))
    await act(async () => {})
    expect(result.current.lineas[0].unidadVenta).toBeUndefined()
  })

  it('una respuesta tardía no pisa la unidad de una línea que el operador ya eligió mientras tanto', async () => {
    let resolver: (a: ArticuloListado) => void = () => {}
    obtenerMock.mockReturnValue(new Promise<ArticuloListado>((resolve) => (resolver = resolve)))

    const { result } = montar([{ clave: 1, idArticulo: 10, cantidad: '1' }])
    await waitFor(() => expect(obtenerMock).toHaveBeenCalledTimes(1))

    act(() => result.current.setLineas((prev) => prev.map((l) => ({ ...l, unidadVenta: 'Peso' as const }))))
    await act(async () => resolver(articulo(10, 'Unidad')))

    expect(result.current.lineas[0].unidadVenta).toBe('Peso')
  })

  it('una línea agregada después, con un artículo elegido sin unidad conocida, dispara su propia consulta', async () => {
    obtenerMock.mockImplementation((id: number) => Promise.resolve(articulo(id, 'Unidad')))
    const { result } = montar([])

    act(() => result.current.setLineas([{ clave: 1, idArticulo: 30, cantidad: '1' }]))

    await waitFor(() => expect(result.current.lineas[0].unidadVenta).toBe('Unidad'))
  })
})
