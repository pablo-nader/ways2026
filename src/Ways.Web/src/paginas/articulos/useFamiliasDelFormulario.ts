import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../../api/cliente'
import { clienteDeFamilias } from '../../api/familias'
import type { FamiliaDetalle, FamiliaListado } from '../../api/tipos'

/**
 * Lo que el formulario de artículo necesita saber de familias (doc 10 §3): las familias que admiten
 * un artículo nuevo y el detalle de LA familia del formulario —la elegida en un alta o la de un
 * miembro que se edita—. Cada lectura lleva su propia generación (`react-async-state` regla 2): la
 * respuesta de una carga superada por otra más nueva, o por `descartarDetalle`, no se aplica.
 */
export function useFamiliasDelFormulario() {
  const [opciones, setOpciones] = useState<FamiliaListado[]>([])
  const [errorOpciones, setErrorOpciones] = useState('')
  const [detalle, setDetalle] = useState<FamiliaDetalle | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const generacionOpcionesRef = useRef(0)
  const generacionDetalleRef = useRef(0)

  /** La última lectura iniciada gana, no la última resuelta. Un fallo deja el listado como estaba y
   * avisa: sin familias para elegir, el alta no puede ofrecer el selector. */
  const recargarOpciones = useCallback(async () => {
    const generacion = (generacionOpcionesRef.current += 1)
    try {
      const familias = await clienteDeFamilias.listar()
      if (generacionOpcionesRef.current !== generacion) return
      setOpciones(familias)
      setErrorOpciones('')
    } catch {
      if (generacionOpcionesRef.current !== generacion) return
      setOpciones([])
      setErrorOpciones('No se pudieron cargar las familias. No se puede elegir una familia al crear un artículo.')
    }
  }, [])

  useEffect(() => {
    void recargarOpciones()
  }, [recargarOpciones])

  /** Devuelve la familia cargada, o `null` si la carga falló (el motivo queda en `error`) o la superó otra:
   * quien llama no decide nada con un `null` salvo seguir sin familia. */
  const cargarDetalle = useCallback(async (idFamilia: number): Promise<FamiliaDetalle | null> => {
    const generacion = (generacionDetalleRef.current += 1)
    setCargando(true)
    setError('')
    try {
      const familia = await clienteDeFamilias.obtener(idFamilia)
      if (generacionDetalleRef.current !== generacion) return null
      setDetalle(familia)
      return familia
    } catch (e) {
      if (generacionDetalleRef.current !== generacion) return null
      setDetalle(null)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar la familia.')
      return null
    } finally {
      if (generacionDetalleRef.current === generacion) setCargando(false)
    }
  }, [])

  /** Suelta la familia del formulario e invalida cualquier carga en vuelo: la pantalla ya no la usa. */
  const descartarDetalle = useCallback(() => {
    generacionDetalleRef.current += 1
    setDetalle(null)
    setCargando(false)
    setError('')
  }, [])

  return { opciones, errorOpciones, detalle, cargando, error, recargarOpciones, cargarDetalle, descartarDetalle }
}
