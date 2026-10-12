import { useCallback, useEffect, useRef, useState } from 'react'

import { clienteDePublico, type ComboPublico } from '../api/publico'

/**
 * Lee los combos de la tienda una vez, al montar. Devuelve `null` hasta que llega una lectura buena
 * y también si falla (o la vitrina no está publicada): la sección se oculta y el carrito no sabe
 * nada de los combos, así que no marca ninguno como no disponible por un error de lectura. Una
 * lista vacía sí es una respuesta: no hay combos.
 *
 * Contrato de la generación (`generacion`): cada lectura la incrementa y el desmontaje también, y
 * toda respuesta que llegue con otro número se descarta. Así una respuesta tardía (la de la primera
 * pasada del modo estricto, o una de un componente ya desmontado) nunca pisa a la más nueva.
 */
export function useCombos(): readonly ComboPublico[] | null {
  const [combos, setCombos] = useState<readonly ComboPublico[] | null>(null)
  const generacion = useRef(0)

  const invalidar = useCallback(() => {
    generacion.current++
  }, [])

  useEffect(() => {
    const mia = ++generacion.current
    void (async () => {
      try {
        const lectura = await clienteDePublico.obtenerCombos()
        if (generacion.current !== mia) return
        setCombos(lectura)
      } catch {
        // Sin combos no se dibuja la sección; no hay nada que mostrar ni que reintentar.
      }
    })()

    return invalidar
  }, [invalidar])

  return combos
}
