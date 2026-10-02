import { useEffect, useRef, useState } from 'react'
import type { ActualizacionDisponible } from '../api/entornoTauri'
import {
  escucharActualizacionDescargada,
  instalarActualizacion,
  leerActualizacionDisponible,
} from '../api/entornoTauri'

type Props = {
  /** Por qué no se puede instalar todavía (venta en curso, impresión pendiente), o `null`. La
   * instalación cierra y reabre la app. */
  motivoDeBloqueo: string | null
  /** Avisa cuando la instalación arranca (y si falla, cuando termina) para que el shell deje el
   * POS inerte mientras el proceso se cierra. */
  alCambiarInstalando?: (instalando: boolean) => void
}

/**
 * Banner del POS de escritorio cuando el shell ya descargó una versión nueva. Fuera de Tauri no
 * renderiza nada: ni la consulta ni el evento existen en la app web.
 */
export function AvisoDeActualizacion({ motivoDeBloqueo, alCambiarInstalando }: Props) {
  const [actualizacion, setActualizacion] = useState<ActualizacionDisponible | null>(null)
  const [instalando, setInstalando] = useState(false)
  const [error, setError] = useState('')
  const instalandoRef = useRef(false)
  const montadoRef = useRef(true)

  useEffect(() => {
    montadoRef.current = true
    // El evento siempre trae lo más nuevo: una consulta inicial que resuelve después no lo pisa.
    let llegoEvento = false
    const desuscribir = escucharActualizacionDescargada((nueva) => {
      llegoEvento = true
      setActualizacion(nueva)
    })
    void leerActualizacionDisponible().then((inicial) => {
      if (montadoRef.current && !llegoEvento && inicial) setActualizacion(inicial)
    })
    return () => {
      montadoRef.current = false
      desuscribir()
    }
  }, [])

  async function instalar() {
    if (instalandoRef.current || motivoDeBloqueo !== null) return
    instalandoRef.current = true
    setInstalando(true)
    setError('')
    alCambiarInstalando?.(true)
    try {
      await instalarActualizacion()
    } catch (e) {
      instalandoRef.current = false
      alCambiarInstalando?.(false)
      if (!montadoRef.current) return
      setInstalando(false)
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  if (!actualizacion) return null

  if (instalando) {
    return (
      <div
        role="status"
        className="position-fixed top-0 start-0 w-100 h-100 d-flex align-items-center justify-content-center bg-dark bg-opacity-75 text-light fs-4 d-print-none"
        style={{ zIndex: 2000 }}
      >
        Instalando la actualización {actualizacion.version}… la aplicación se va a reiniciar.
      </div>
    )
  }

  return (
    <div
      role="status"
      className="alert alert-info rounded-0 py-1 px-3 mb-0 d-flex justify-content-between align-items-center gap-2 d-print-none"
    >
      <span>
        Actualización {actualizacion.version} disponible.
        {motivoDeBloqueo && ` ${motivoDeBloqueo}`}
        {error && ` ${error}`}
      </span>
      <button
        type="button"
        className="btn btn-sm btn-primary rounded-0"
        disabled={motivoDeBloqueo !== null}
        onClick={() => void instalar()}
      >
        Instalar y reiniciar
      </button>
    </div>
  )
}
