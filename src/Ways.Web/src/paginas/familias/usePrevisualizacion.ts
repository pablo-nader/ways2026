import { useCallback, useRef, useState } from 'react'
import { clienteDeFamilias } from '../../api/familias'
import type { PrevisualizacionDeAgrupacion, SolicitudDePrevisualizacion } from '../../api/tipos'
import { mensajeDeError } from './mensajes'

/**
 * La previsualización de agrupar artículos (`POST /api/familias/previsualizacion`): una foto de lo que cambiaría,
 * sin escribir nada. Una previsualización nueva —o `invalidar`, cuando lo elegido cambió— supera a la que estaba en
 * vuelo (react-async-state regla 2): la respuesta de una solicitud que ya no es la de lo elegido no se aplica, y la
 * vieja que se tenía tampoco se conserva, porque ya no habla de lo que hay en pantalla.
 */
export function usePrevisualizacion() {
  const [previsualizacion, setPrevisualizacion] = useState<PrevisualizacionDeAgrupacion | null>(null)
  const [previsualizando, setPrevisualizando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  const previsualizar = useCallback(async (solicitud: SolicitudDePrevisualizacion) => {
    const generacion = (generacionRef.current += 1)
    setPrevisualizando(true)
    setPrevisualizacion(null)
    setError('')
    try {
      const resultado = await clienteDeFamilias.previsualizar(solicitud)
      if (generacionRef.current !== generacion) return
      setPrevisualizacion(resultado)
    } catch (e) {
      if (generacionRef.current !== generacion) return
      setError(mensajeDeError(e, 'previsualizar la agrupación'))
    } finally {
      if (generacionRef.current === generacion) setPrevisualizando(false)
    }
  }, [])

  const invalidar = useCallback(() => {
    generacionRef.current += 1
    setPrevisualizacion(null)
    setPrevisualizando(false)
    setError('')
  }, [])

  return { previsualizacion, previsualizando, error, previsualizar, invalidar }
}
