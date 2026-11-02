import { useCallback, useEffect, useRef, useState } from 'react'

export type EstadoDeLectura<T> = {
  datos: T | null
  cargando: boolean
  error: string
  /** Vuelve a leer conservando lo que hay en pantalla hasta que llegue la respuesta. */
  recargar: () => void
}

/**
 * Lectura con generación: cada corrida (cambio de `leer`, recarga o desmontaje) invalida a la anterior, de modo que una respuesta
 * tardía de otra empresa o de otra página nunca se pinta ni baja `cargando` de la lectura vigente. `leer` debe venir memoizado por
 * sus parámetros; `null` deja la lectura en blanco (todavía no hay con qué consultar).
 */
export function useLectura<T>(leer: (() => Promise<T>) | null, mensajeDeError: (error: unknown) => string): EstadoDeLectura<T> {
  const [estado, setEstado] = useState<{ datos: T | null; cargando: boolean; error: string }>({
    datos: null,
    cargando: leer !== null,
    error: '',
  })
  const generacion = useRef(0)

  const ejecutar = useCallback(
    (limpiar: boolean) => {
      const token = ++generacion.current
      if (leer === null) {
        setEstado({ datos: null, cargando: false, error: '' })
        return
      }

      setEstado((previo) => ({ datos: limpiar ? null : previo.datos, cargando: true, error: '' }))
      leer()
        .then((datos) => {
          if (generacion.current === token) setEstado({ datos, cargando: false, error: '' })
        })
        .catch((e: unknown) => {
          if (generacion.current === token) setEstado({ datos: null, cargando: false, error: mensajeDeError(e) })
        })
    },
    [leer, mensajeDeError],
  )

  useEffect(() => {
    ejecutar(true)
    return () => {
      generacion.current += 1
    }
  }, [ejecutar])

  const recargar = useCallback(() => ejecutar(false), [ejecutar])

  return { ...estado, recargar }
}
