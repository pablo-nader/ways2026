import { useCallback, useEffect, useRef, useState } from 'react'

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

/**
 * Lee la vitrina publicada del sitio y deja el documento con su tema, acento, título y descripción.
 * Cada lectura lleva un número de generación: `recargar` o el desmontaje invalidan la que sigue en
 * vuelo, y una respuesta tardía no pisa a la más nueva.
 */
export function useVitrinaPublica() {
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
      const sinPublicar = error instanceof ErrorApi && error.estado === 404
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
  useEffect(() => {
    if (vitrina) aplicarTemaDeVitrina(vitrina)
    aplicarCabeceraDelDocumento(vitrina)
  }, [vitrina])

  return { resultado, recargar: cargar }
}
