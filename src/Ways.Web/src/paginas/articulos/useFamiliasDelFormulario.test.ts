import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import type { FamiliaDetalle, FamiliaListado } from '../../api/tipos'

const listarMock = vi.fn()
const obtenerMock = vi.fn()

vi.mock('../../api/familias', () => ({
  clienteDeFamilias: {
    listar: (...args: unknown[]) => listarMock(...args),
    obtener: (...args: unknown[]) => obtenerMock(...args),
  },
}))

import { useFamiliasDelFormulario } from './useFamiliasDelFormulario'

function listado(id: number, nombre = `Familia ${id}`): FamiliaListado {
  return { id, nombre, activo: true, cantidadArticulos: 2 }
}

function detalle(id: number, nombre = `Familia ${id}`): FamiliaDetalle {
  return { id, nombre, activo: true, articulos: [], valores: null, precios: [] }
}

/** Una promesa que el test resuelve o rechaza cuando quiere. */
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
  listarMock.mockReset()
  obtenerMock.mockReset()
  listarMock.mockResolvedValue([])
})

describe('useFamiliasDelFormulario — familias que se ofrecen', () => {
  it('al montar pide las familias y las expone', async () => {
    listarMock.mockResolvedValue([listado(1), listado(2)])

    const { result } = renderHook(() => useFamiliasDelFormulario())

    await waitFor(() => expect(result.current.opciones).toEqual([listado(1), listado(2)]))
    expect(result.current.errorOpciones).toBe('')
  })

  /** Cláusula bajo prueba: el aviso del fallo de carga, que dice lo que la pantalla de verdad impone (sin
   * familias no hay selector) en vez de dejar el listado vacío en silencio (react-async-state regla 7). */
  it('si falla, deja el listado vacío y avisa que no se puede elegir una familia', async () => {
    listarMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Se cayó.'))

    const { result } = renderHook(() => useFamiliasDelFormulario())

    await waitFor(() =>
      expect(result.current.errorOpciones).toBe(
        'No se pudieron cargar las familias. No se puede elegir una familia al crear un artículo.',
      ),
    )
    expect(result.current.opciones).toEqual([])
  })

  /** Cláusula bajo prueba: un fallo de `recargarOpciones` conserva las familias de la última lectura que salió bien y el
   * aviso dice que pueden estar desactualizadas (no que no se pueda elegir). Evidencia de mutación (mutation-proof-tests):
   * agregar `setOpciones([])` al `catch` hace fallar este test; revertido, vuelve a verde. */
  it('si una recarga falla, conserva las familias que ya tenía y avisa que pueden estar desactualizadas', async () => {
    listarMock.mockResolvedValueOnce([listado(1), listado(2)]).mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    const { result } = renderHook(() => useFamiliasDelFormulario())
    await waitFor(() => expect(result.current.opciones).toEqual([listado(1), listado(2)]))

    await act(async () => {
      await result.current.recargarOpciones()
    })

    expect(result.current.opciones).toEqual([listado(1), listado(2)])
    expect(result.current.errorOpciones).toBe('No se pudieron actualizar las familias: las que se ofrecen pueden estar desactualizadas.')
  })

  /** Cláusula bajo prueba: `opcionesDeFamilia` en el aviso: lo que cuenta es lo que el selector ofrece, no lo que quedó en
   * el listado. Evidencia de mutación (mutation-proof-tests): contar `opciones.length` en vez de las ofrecidas hace
   * fallar este test; revertido, vuelve a verde. */
  it('si una recarga falla y ninguna de las que tenía se puede elegir, el aviso es el de que no se puede elegir una familia', async () => {
    listarMock
      .mockResolvedValueOnce([{ ...listado(1), activo: false }, { ...listado(2), cantidadArticulos: 0 }])
      .mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    const { result } = renderHook(() => useFamiliasDelFormulario())
    await waitFor(() => expect(result.current.opciones).toHaveLength(2))

    await act(async () => {
      await result.current.recargarOpciones()
    })

    expect(result.current.errorOpciones).toBe(
      'No se pudieron cargar las familias. No se puede elegir una familia al crear un artículo.',
    )
  })

  it('una recarga que sale bien después de un fallo reemplaza las familias y limpia el aviso', async () => {
    listarMock
      .mockResolvedValueOnce([listado(1)])
      .mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó.'))
      .mockResolvedValueOnce([listado(3)])
    const { result } = renderHook(() => useFamiliasDelFormulario())
    await waitFor(() => expect(result.current.opciones).toEqual([listado(1)]))
    await act(async () => {
      await result.current.recargarOpciones()
    })
    expect(result.current.errorOpciones).not.toBe('')

    await act(async () => {
      await result.current.recargarOpciones()
    })

    expect(result.current.opciones).toEqual([listado(3)])
    expect(result.current.errorOpciones).toBe('')
  })

  /** Cláusula bajo prueba: la generación de `recargarOpciones` (la última INICIADA gana, no la última resuelta).
   * Evidencia de mutación (mutation-proof-tests): sacar el `if (generacionOpcionesRef.current !== generacion)
   * return` de la rama de éxito hace fallar este test; revertido, vuelve a verde. */
  it('una respuesta tardía de una lectura anterior no pisa la de la lectura más nueva', async () => {
    const primera = diferida<FamiliaListado[]>()
    const segunda = diferida<FamiliaListado[]>()
    listarMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)

    const { result } = renderHook(() => useFamiliasDelFormulario())
    act(() => {
      void result.current.recargarOpciones()
    })

    await act(async () => {
      segunda.resolver([listado(2, 'Nueva')])
    })
    await waitFor(() => expect(result.current.opciones).toEqual([listado(2, 'Nueva')]))

    await act(async () => {
      primera.resolver([listado(1, 'Vieja')])
    })

    expect(result.current.opciones).toEqual([listado(2, 'Nueva')])
  })

  it('un fallo de una lectura anterior no pisa con un aviso la lectura más nueva que salió bien', async () => {
    const primera = diferida<FamiliaListado[]>()
    const segunda = diferida<FamiliaListado[]>()
    listarMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)

    const { result } = renderHook(() => useFamiliasDelFormulario())
    act(() => {
      void result.current.recargarOpciones()
    })

    await act(async () => {
      segunda.resolver([listado(2)])
    })
    await act(async () => {
      primera.rechazar(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })

    expect(result.current.errorOpciones).toBe('')
    expect(result.current.opciones).toEqual([listado(2)])
  })
})

describe('useFamiliasDelFormulario — detalle de la familia del formulario', () => {
  it('cargarDetalle devuelve la familia, la expone y deja de estar cargando', async () => {
    obtenerMock.mockResolvedValue(detalle(7, 'Sabores'))
    const { result } = renderHook(() => useFamiliasDelFormulario())

    let devuelta: FamiliaDetalle | null = null
    await act(async () => {
      devuelta = await result.current.cargarDetalle(7)
    })

    expect(obtenerMock).toHaveBeenCalledWith(7)
    expect(devuelta).toEqual(detalle(7, 'Sabores'))
    expect(result.current.detalle).toEqual(detalle(7, 'Sabores'))
    expect(result.current.cargando).toBe(false)
    expect(result.current.error).toBe('')
  })

  it('mientras la lectura está en vuelo, cargando es true y no hay error', async () => {
    const lectura = diferida<FamiliaDetalle>()
    obtenerMock.mockReturnValue(lectura.promesa)
    const { result } = renderHook(() => useFamiliasDelFormulario())

    act(() => {
      void result.current.cargarDetalle(7)
    })

    expect(result.current.cargando).toBe(true)
    expect(result.current.error).toBe('')

    await act(async () => {
      lectura.resolver(detalle(7))
    })
    expect(result.current.cargando).toBe(false)
  })

  it('un fallo con mensaje del servidor lo expone, deja el detalle en null y devuelve null', async () => {
    obtenerMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.'))
    const { result } = renderHook(() => useFamiliasDelFormulario())

    let devuelta: FamiliaDetalle | null | undefined
    await act(async () => {
      devuelta = await result.current.cargarDetalle(7)
    })

    expect(devuelta).toBeNull()
    expect(result.current.detalle).toBeNull()
    expect(result.current.error).toBe('No existe la familia 7.')
    expect(result.current.cargando).toBe(false)
  })

  it('un fallo que no es del servidor (la red) usa el mensaje genérico', async () => {
    obtenerMock.mockRejectedValue(new TypeError('Failed to fetch'))
    const { result } = renderHook(() => useFamiliasDelFormulario())

    await act(async () => {
      await result.current.cargarDetalle(7)
    })

    expect(result.current.error).toBe('No se pudo cargar la familia.')
  })

  it('una carga nueva limpia el error de la anterior', async () => {
    obtenerMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó.')).mockResolvedValueOnce(detalle(7))
    const { result } = renderHook(() => useFamiliasDelFormulario())

    await act(async () => {
      await result.current.cargarDetalle(7)
    })
    expect(result.current.error).toBe('Se cayó.')

    await act(async () => {
      await result.current.cargarDetalle(7)
    })
    expect(result.current.error).toBe('')
    expect(result.current.detalle).toEqual(detalle(7))
  })

  /** Cláusula bajo prueba: la generación de `cargarDetalle`. Sin ella, la respuesta tardía de la familia que se
   * dejó de elegir pisaría a la elegida después. Evidencia de mutación (mutation-proof-tests): sacar el
   * `if (generacionDetalleRef.current !== generacion) return null` de la rama de éxito hace fallar este test y el
   * de `descartarDetalle`; revertido, vuelven a verde. */
  it('la respuesta de una carga superada por otra más nueva no se aplica y devuelve null', async () => {
    const primera = diferida<FamiliaDetalle>()
    const segunda = diferida<FamiliaDetalle>()
    obtenerMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)
    const { result } = renderHook(() => useFamiliasDelFormulario())

    let devueltaPrimera: FamiliaDetalle | null | undefined
    let devueltaSegunda: FamiliaDetalle | null | undefined
    act(() => {
      void result.current.cargarDetalle(1).then((d) => (devueltaPrimera = d))
      void result.current.cargarDetalle(2).then((d) => (devueltaSegunda = d))
    })

    await act(async () => {
      segunda.resolver(detalle(2, 'Segunda'))
    })
    await act(async () => {
      primera.resolver(detalle(1, 'Primera'))
    })

    expect(devueltaPrimera).toBeNull()
    expect(devueltaSegunda).toEqual(detalle(2, 'Segunda'))
    expect(result.current.detalle).toEqual(detalle(2, 'Segunda'))
  })

  /** Cláusula bajo prueba: el `finally` de `cargarDetalle` solo baja `cargando` si la lectura sigue vigente
   * (react-async-state regla 4): el de una lectura superada no puede declarar terminada la que sigue en vuelo.
   * Evidencia de mutación (mutation-proof-tests): reemplazar la condición por un `setCargando(false)` incondicional
   * hace fallar este test; revertido, vuelve a verde. */
  it('la lectura superada que termina primero no apaga el estado cargando de la que sigue en vuelo', async () => {
    const primera = diferida<FamiliaDetalle>()
    const segunda = diferida<FamiliaDetalle>()
    obtenerMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)
    const { result } = renderHook(() => useFamiliasDelFormulario())

    act(() => {
      void result.current.cargarDetalle(1)
      void result.current.cargarDetalle(2)
    })

    await act(async () => {
      primera.resolver(detalle(1))
    })
    expect(result.current.cargando).toBe(true)

    await act(async () => {
      segunda.resolver(detalle(2))
    })
    expect(result.current.cargando).toBe(false)
  })

  /** Evidencia de mutación (mutation-proof-tests): sacar el gate del `catch` de `cargarDetalle` hace fallar este
   * test (el fallo viejo dejaba `error` puesto sobre la lectura vigente); revertido, vuelve a verde. */
  it('el fallo de una carga superada no deja un error visible para la que sigue vigente', async () => {
    const primera = diferida<FamiliaDetalle>()
    const segunda = diferida<FamiliaDetalle>()
    obtenerMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)
    const { result } = renderHook(() => useFamiliasDelFormulario())

    act(() => {
      void result.current.cargarDetalle(1)
      void result.current.cargarDetalle(2)
    })

    await act(async () => {
      primera.rechazar(new ErrorApi(500, 'error_interno', 'Se cayó la primera.'))
    })

    expect(result.current.error).toBe('')
  })

  /** Cláusula bajo prueba: `descartarDetalle` invalida la carga en vuelo (el formulario se cerró o cambió de
   * artículo: la familia que llega tarde ya no le corresponde a nadie). Evidencia de mutación
   * (mutation-proof-tests): sacar el `generacionDetalleRef.current += 1` de `descartarDetalle` hace fallar este
   * test; revertido, vuelve a verde. */
  it('descartarDetalle suelta el detalle y la respuesta que llega después no se aplica', async () => {
    const lectura = diferida<FamiliaDetalle>()
    obtenerMock.mockReturnValue(lectura.promesa)
    const { result } = renderHook(() => useFamiliasDelFormulario())

    let devuelta: FamiliaDetalle | null | undefined
    act(() => {
      void result.current.cargarDetalle(7).then((d) => (devuelta = d))
    })
    act(() => {
      result.current.descartarDetalle()
    })
    expect(result.current.cargando).toBe(false)

    await act(async () => {
      lectura.resolver(detalle(7))
    })

    expect(devuelta).toBeNull()
    expect(result.current.detalle).toBeNull()
    expect(result.current.cargando).toBe(false)
  })

  it('descartarDetalle también limpia el detalle ya cargado y su error', async () => {
    obtenerMock.mockResolvedValueOnce(detalle(7)).mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    const { result } = renderHook(() => useFamiliasDelFormulario())

    await act(async () => {
      await result.current.cargarDetalle(7)
    })
    expect(result.current.detalle).not.toBeNull()

    act(() => {
      result.current.descartarDetalle()
    })
    expect(result.current.detalle).toBeNull()

    await act(async () => {
      await result.current.cargarDetalle(7)
    })
    expect(result.current.error).toBe('Se cayó.')

    act(() => {
      result.current.descartarDetalle()
    })
    expect(result.current.error).toBe('')
  })
})
