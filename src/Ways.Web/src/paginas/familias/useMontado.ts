import { useEffect, useRef } from 'react'

/**
 * Si la pantalla sigue montada. La respuesta de una escritura que llega cuando el usuario ya se fue (el menú, el
 * Atrás del navegador) no tiene que navegar ni mandar al usuario a una pantalla que no pidió: quien escribe lo
 * consulta después de su `await` (react-async-state regla 2). El efecto vuelve a poner `true` al montar porque bajo
 * `StrictMode` el cleanup corre una vez antes de que la pantalla quede montada de verdad.
 */
export function useMontado() {
  const montado = useRef(true)

  useEffect(() => {
    montado.current = true

    return () => {
      montado.current = false
    }
  }, [])

  return montado
}
