import { useCallback, useRef, useState } from 'react'

/**
 * Una sola escritura a la vez. La guarda de reentrancia es un `ref` síncrono (dos clics en el mismo tick pasan ambos si
 * la guarda lee estado); `ocupado` existe aparte, solo para deshabilitar los controles durante toda la ventana. Solo esta
 * escritura toca ambos, así que el `finally` los libera sin condicionarlos a ningún token.
 */
export function useEscrituraUnica() {
  const enCursoRef = useRef(false)
  const [ocupado, setOcupado] = useState(false)

  const correr = useCallback(async (escritura: () => Promise<void>) => {
    if (enCursoRef.current) return

    enCursoRef.current = true
    setOcupado(true)
    try {
      await escritura()
    } finally {
      enCursoRef.current = false
      setOcupado(false)
    }
  }, [])

  return { ocupado, correr }
}
