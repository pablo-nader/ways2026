import { useCallback, useEffect, useRef, useState } from 'react'

import { clienteDePublico, type ComboPublico } from '../api/publico'

/**
 * Lee los combos de la tienda cada vez que el catálogo hace una lectura (`lecturas`: primera carga,
 * otro filtro, "Ver más", "Reintentar"), así un combo que se dio de baja o cambió de precio se ve en
 * la próxima lectura sin recargar la página. Con `lecturas` en 0 todavía no hubo ninguna y no pide
 * nada. Devuelve `null` hasta que llega una lectura buena; una lectura que falla (o la vitrina no
 * publicada) no cambia lo que había: sin lectura buena la sección se oculta y el carrito no sabe
 * nada de los combos, así que no marca ninguno como no disponible por un error. Una lista vacía sí
 * es una respuesta: no hay combos.
 *
 * Contrato de la generación (`generacion`): cada lectura la incrementa y el desmontaje también, y
 * toda respuesta que llegue con otro número se descarta. Así una respuesta tardía (la de la primera
 * pasada del modo estricto, la de una lectura anterior o la de un componente ya desmontado) nunca
 * pisa a la más nueva.
 */
export function useCombos(lecturas: number): readonly ComboPublico[] | null {
  const [combos, setCombos] = useState<readonly ComboPublico[] | null>(null)
  const generacion = useRef(0)

  const invalidar = useCallback(() => {
    generacion.current++
  }, [])

  useEffect(() => {
    if (lecturas === 0) return

    const mia = ++generacion.current
    void (async () => {
      try {
        const lectura = await clienteDePublico.obtenerCombos()
        if (generacion.current !== mia) return
        setCombos(lectura)
      } catch {
        // Sin una lectura buena se conserva la última (o ninguna): no hay nada que mostrar ni que reintentar.
      }
    })()

    return invalidar
  }, [lecturas, invalidar])

  return combos
}
