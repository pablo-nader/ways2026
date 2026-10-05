import { act, renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import type { PrevisualizacionDeAgrupacion } from '../../api/tipos'

const previsualizarMock = vi.fn()

vi.mock('../../api/familias', () => ({
  clienteDeFamilias: { previsualizar: (...args: unknown[]) => previsualizarMock(...args) },
}))

import { usePrevisualizacion } from './usePrevisualizacion'

function previsualizacionDe(idArticuloReferencia: number): PrevisualizacionDeAgrupacion {
  return { idArticuloReferencia, idFamilia: null, articulos: [], problemas: [] }
}

function diferida<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })
  return { promesa, resolver, rechazar }
}

beforeEach(() => {
  previsualizarMock.mockReset()
})

describe('usePrevisualizacion', () => {
  it('arranca sin previsualización, sin error y sin pedir nada', () => {
    const { result } = renderHook(() => usePrevisualizacion())

    expect(result.current.previsualizacion).toBeNull()
    expect(result.current.previsualizando).toBe(false)
    expect(result.current.error).toBe('')
    expect(previsualizarMock).not.toHaveBeenCalled()
  })

  it('previsualizar manda la solicitud tal cual y expone la respuesta', async () => {
    previsualizarMock.mockResolvedValue(previsualizacionDe(31))
    const { result } = renderHook(() => usePrevisualizacion())

    await act(async () => {
      await result.current.previsualizar({ idArticuloReferencia: 31, idsArticulos: [32, 33] })
    })

    expect(previsualizarMock).toHaveBeenCalledExactlyOnceWith({ idArticuloReferencia: 31, idsArticulos: [32, 33] })
    expect(result.current.previsualizacion).toEqual(previsualizacionDe(31))
    expect(result.current.previsualizando).toBe(false)
  })

  it('mientras está en vuelo, previsualizando es true y la previsualización anterior ya no se muestra', async () => {
    previsualizarMock.mockResolvedValueOnce(previsualizacionDe(31))
    const lectura = diferida<PrevisualizacionDeAgrupacion>()
    previsualizarMock.mockReturnValueOnce(lectura.promesa)
    const { result } = renderHook(() => usePrevisualizacion())
    await act(async () => {
      await result.current.previsualizar({ idArticuloReferencia: 31, idsArticulos: [] })
    })

    act(() => {
      void result.current.previsualizar({ idArticuloReferencia: 31, idsArticulos: [32] })
    })

    expect(result.current.previsualizando).toBe(true)
    expect(result.current.previsualizacion).toBeNull()

    await act(async () => {
      lectura.resolver(previsualizacionDe(31))
    })
    expect(result.current.previsualizando).toBe(false)
  })

  it('un rechazo del servidor deja su mensaje con la ayuda de su código y ninguna previsualización', async () => {
    previsualizarMock.mockRejectedValue(new ErrorApi(400, 'demasiados_articulos', 'Son muchos.'))
    const { result } = renderHook(() => usePrevisualizacion())

    await act(async () => {
      await result.current.previsualizar({ idArticuloReferencia: 31, idsArticulos: [] })
    })

    expect(result.current.error).toBe('Son muchos. Elegí menos artículos y agrupá en más de un paso.')
    expect(result.current.previsualizacion).toBeNull()
    expect(result.current.previsualizando).toBe(false)
  })

  it('un fallo que no es del servidor (la red) usa el texto de la acción', async () => {
    previsualizarMock.mockRejectedValue(new TypeError('Failed to fetch'))
    const { result } = renderHook(() => usePrevisualizacion())

    await act(async () => {
      await result.current.previsualizar({ idArticuloReferencia: 31, idsArticulos: [] })
    })

    expect(result.current.error).toBe('No se pudo previsualizar la agrupación.')
  })

  it('una previsualización nueva limpia el error de la anterior', async () => {
    previsualizarMock.mockRejectedValueOnce(new ErrorApi(500, 'error', 'Se cayó.')).mockResolvedValueOnce(previsualizacionDe(31))
    const { result } = renderHook(() => usePrevisualizacion())
    await act(async () => {
      await result.current.previsualizar({ idArticuloReferencia: 31, idsArticulos: [] })
    })
    expect(result.current.error).toBe('Se cayó.')

    await act(async () => {
      await result.current.previsualizar({ idArticuloReferencia: 31, idsArticulos: [] })
    })

    expect(result.current.error).toBe('')
  })

  /** Cláusula bajo prueba: la generación de `previsualizar` (react-async-state regla 2): la respuesta de una solicitud
   * superada por otra más nueva no se aplica, aunque llegue después. */
  it('la respuesta tardía de una solicitud superada no pisa a la más nueva', async () => {
    const primera = diferida<PrevisualizacionDeAgrupacion>()
    const segunda = diferida<PrevisualizacionDeAgrupacion>()
    previsualizarMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)
    const { result } = renderHook(() => usePrevisualizacion())

    act(() => {
      void result.current.previsualizar({ idArticuloReferencia: 1, idsArticulos: [] })
      void result.current.previsualizar({ idArticuloReferencia: 2, idsArticulos: [] })
    })
    await act(async () => {
      segunda.resolver(previsualizacionDe(2))
    })
    await act(async () => {
      primera.resolver(previsualizacionDe(1))
    })

    expect(result.current.previsualizacion).toEqual(previsualizacionDe(2))
  })

  /** Cláusula bajo prueba: el `finally` gateado (regla 4): la solicitud superada que termina primero no apaga el
   * estado de la que sigue en vuelo. */
  it('la solicitud superada que termina primero no apaga "previsualizando" de la que sigue en vuelo', async () => {
    const primera = diferida<PrevisualizacionDeAgrupacion>()
    const segunda = diferida<PrevisualizacionDeAgrupacion>()
    previsualizarMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)
    const { result } = renderHook(() => usePrevisualizacion())
    act(() => {
      void result.current.previsualizar({ idArticuloReferencia: 1, idsArticulos: [] })
      void result.current.previsualizar({ idArticuloReferencia: 2, idsArticulos: [] })
    })

    await act(async () => {
      primera.resolver(previsualizacionDe(1))
    })

    expect(result.current.previsualizando).toBe(true)
  })

  it('el fallo de una solicitud superada no deja un error visible para la que sigue vigente', async () => {
    const primera = diferida<PrevisualizacionDeAgrupacion>()
    const segunda = diferida<PrevisualizacionDeAgrupacion>()
    previsualizarMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)
    const { result } = renderHook(() => usePrevisualizacion())
    act(() => {
      void result.current.previsualizar({ idArticuloReferencia: 1, idsArticulos: [] })
      void result.current.previsualizar({ idArticuloReferencia: 2, idsArticulos: [] })
    })

    await act(async () => {
      primera.rechazar(new ErrorApi(500, 'error', 'Se cayó la primera.'))
    })

    expect(result.current.error).toBe('')
  })

  it('invalidar descarta la previsualización', async () => {
    previsualizarMock.mockResolvedValue(previsualizacionDe(31))
    const { result } = renderHook(() => usePrevisualizacion())
    await act(async () => {
      await result.current.previsualizar({ idArticuloReferencia: 31, idsArticulos: [] })
    })
    expect(result.current.previsualizacion).toEqual(previsualizacionDe(31))

    act(() => result.current.invalidar())

    expect(result.current.previsualizacion).toBeNull()
  })

  it('invalidar descarta también el error de una previsualización fallida', async () => {
    previsualizarMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    const { result } = renderHook(() => usePrevisualizacion())
    await act(async () => {
      await result.current.previsualizar({ idArticuloReferencia: 31, idsArticulos: [] })
    })
    expect(result.current.error).toBe('Se cayó.')

    act(() => result.current.invalidar())

    expect(result.current.error).toBe('')
  })

  /** Cláusula bajo prueba: `invalidar` supera la solicitud en vuelo: la selección cambió, y la foto que llega ya no
   * es de lo que hay en pantalla. */
  it('invalidar con una solicitud en vuelo: la respuesta que llega después no se aplica y deja de estar previsualizando', async () => {
    const lectura = diferida<PrevisualizacionDeAgrupacion>()
    previsualizarMock.mockReturnValue(lectura.promesa)
    const { result } = renderHook(() => usePrevisualizacion())
    act(() => {
      void result.current.previsualizar({ idArticuloReferencia: 31, idsArticulos: [] })
    })

    act(() => result.current.invalidar())
    expect(result.current.previsualizando).toBe(false)

    await act(async () => {
      lectura.resolver(previsualizacionDe(31))
    })

    expect(result.current.previsualizacion).toBeNull()
  })
})
