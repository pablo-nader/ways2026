import { useEffect, useRef, useState } from 'react'
import type { ActualizacionDisponible } from '../api/entornoTauri'
import {
  escucharActualizacionDescargada,
  instalarActualizacion,
  leerActualizacionDisponible,
} from '../api/entornoTauri'

type Props = {
  /** Con una venta en curso no se puede instalar: la instalación cierra y reabre la app. */
  ventaEnCurso: boolean
}

/**
 * Banner del POS de escritorio cuando el shell ya descargó una versión nueva. Fuera de Tauri no
 * renderiza nada: ni la consulta ni el evento existen en la app web.
 */
export function AvisoDeActualizacion({ ventaEnCurso }: Props) {
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
    if (instalandoRef.current || ventaEnCurso) return
    instalandoRef.current = true
    setInstalando(true)
    setError('')
    try {
      await instalarActualizacion()
    } catch (e) {
      instalandoRef.current = false
      if (!montadoRef.current) return
      setInstalando(false)
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  if (!actualizacion) return null

  return (
    <div
      role="status"
      className="alert alert-info rounded-0 py-1 px-3 mb-0 d-flex justify-content-between align-items-center gap-2 d-print-none"
    >
      <span>
        Actualización {actualizacion.version} disponible.
        {ventaEnCurso && !instalando && ' Terminá la venta en curso para instalarla.'}
        {error && ` ${error}`}
      </span>
      <button
        type="button"
        className="btn btn-sm btn-primary rounded-0"
        disabled={ventaEnCurso || instalando}
        onClick={() => void instalar()}
      >
        {instalando ? 'Instalando…' : 'Instalar y reiniciar'}
      </button>
    </div>
  )
}
