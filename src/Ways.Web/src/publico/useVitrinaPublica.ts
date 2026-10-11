import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'

import { clienteDePublico } from '../api/publico'
import type { VitrinaPublica } from '../api/tipos'
import { ErrorApi } from '../api/cliente'
import { aplicarCabeceraDelDocumento, aplicarTemaDeVitrina } from './tema'

export type ResultadoDeVitrina =
  | { estado: 'cargando' }
  | { estado: 'publicada'; vitrina: VitrinaPublica }
  | { estado: 'sin_publicar' }
  | { estado: 'error' }

const CARGANDO: ResultadoDeVitrina = { estado: 'cargando' }

/** Código con el que la API distingue una vitrina sin publicar de cualquier otro 404. */
const CODIGO_VITRINA_NO_PUBLICADA = 'vitrina_no_publicada'

/**
 * Lee la vitrina publicada del sitio y deja el documento con su tema, acento, título y descripción.
 * Cada lectura lleva un número de generación: `recargar` o el desmontaje invalidan la que sigue en
 * vuelo, y una respuesta tardía no pisa a la más nueva.
 *
 * Solo un 404 con el código `vitrina_no_publicada` es "sin publicar"; cualquier otro 404 (una ruta
 * mal enrutada, un proxy) es un error que se puede reintentar. `prefijoDeTitulo` antepone la sección
 * al título (`Tienda online · Sur`).
 *
 * El tema se aplica antes de pintar (`useLayoutEffect`), pero no puede adelantarse a los datos: hasta
 * que llega la vitrina el esqueleto y las pantallas de estado se ven con el tema oscuro por defecto,
 * así que un comercio de tema claro ve un primer cuadro oscuro.
 */
export function useVitrinaPublica(prefijoDeTitulo?: string) {
  const [resultado, setResultado] = useState<ResultadoDeVitrina>(CARGANDO)
  const generacion = useRef(0)

  const cargar = useCallback(async () => {
    const mia = ++generacion.current
    setResultado(CARGANDO)
    try {
      const vitrina = await clienteDePublico.obtenerVitrina()
      if (generacion.current !== mia) return
      setResultado({ estado: 'publicada', vitrina })
    } catch (error) {
      if (generacion.current !== mia) return
      const sinPublicar =
        error instanceof ErrorApi && error.estado === 404 && error.codigo === CODIGO_VITRINA_NO_PUBLICADA
      setResultado({ estado: sinPublicar ? 'sin_publicar' : 'error' })
    }
  }, [])

  const invalidar = useCallback(() => {
    generacion.current++
  }, [])

  useEffect(() => {
    void cargar()

    return invalidar
  }, [cargar, invalidar])

  const vitrina = resultado.estado === 'publicada' ? resultado.vitrina : null
  useLayoutEffect(() => {
    if (!vitrina) return
    aplicarTemaDeVitrina(vitrina)
    aplicarCabeceraDelDocumento(vitrina, prefijoDeTitulo)
  }, [vitrina, prefijoDeTitulo])

  return { resultado, recargar: cargar }
}
