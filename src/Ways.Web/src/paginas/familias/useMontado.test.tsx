import { StrictMode } from 'react'
import { renderHook } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { useMontado } from './useMontado'

describe('useMontado', () => {
  it('está montada mientras la pantalla lo está, y deja de estarlo al desmontarse', () => {
    const { result, unmount } = renderHook(() => useMontado())
    expect(result.current.current).toBe(true)

    unmount()

    expect(result.current.current).toBe(false)
  })

  /** Cláusula bajo prueba: `montado.current = true` del cuerpo del efecto. Bajo StrictMode React corre el cleanup una
   * vez antes de dejar la pantalla montada de verdad: sin esa línea la referencia quedaría en `false` para siempre.
   * Evidencia de mutación (mutation-proof-tests): sacarla hace fallar este test; revertido, vuelve a verde. */
  it('bajo StrictMode queda montada aunque el cleanup haya corrido una vez', () => {
    const { result, unmount } = renderHook(() => useMontado(), { wrapper: StrictMode })

    expect(result.current.current).toBe(true)

    unmount()
    expect(result.current.current).toBe(false)
  })
})
