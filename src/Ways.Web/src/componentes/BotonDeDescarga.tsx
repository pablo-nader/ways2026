import { useRef, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'

type PropsBotonDeDescarga = {
  ruta: string
  etiqueta?: string
  onError: (mensaje: string) => void
  /** Se invoca al iniciar una descarga válida (tras la guarda) — limpia el error previo del caller. */
  onInicio?: () => void
  className?: string
  /** stage-13-stock-inteligente (Slice 3): deshabilita el botón desde afuera cuando OTRA acción
   * de la misma pantalla está en vuelo (`react-async-state` regla 5/9 — ventana completa
   * deshabilitada mientras `Existencias.tsx` guarda una fila). No reemplaza el guard interno de
   * re-entrancy del propio botón, que sigue cubriendo su propio doble click. */
  disabled?: boolean
}

/**
 * Botón de descarga de un `/export` (stage-11 slice 4): re-entrancy guard vía `useRef` + `disabled`
 * cubren toda la ventana de la descarga (`react-async-state` regla 5/9) — el `ref` bloquea un
 * doble click en el MISMO tick, antes de que React re-renderice el atributo `disabled`; un `useState`
 * solo no alcanza porque dos clicks sincrónicos leen el mismo valor obsoleto. Los errores nunca
 * navegan la SPA: se funnelean por `onError` al estado de la pantalla que monta el botón (proposal
 * decisión 8 — "a download that silently does nothing is this pattern's worst failure mode").
 */
export function BotonDeDescarga({ ruta, etiqueta = 'Descargar', onError, onInicio, className, disabled }: PropsBotonDeDescarga) {
  const [descargando, setDescargando] = useState(false)
  const enVueloRef = useRef(false)

  const manejarClick = async () => {
    if (enVueloRef.current) return
    enVueloRef.current = true
    setDescargando(true)
    onInicio?.()

    try {
      await api.descargar(ruta)
    } catch (e) {
      onError(e instanceof ErrorApi ? e.message : 'No se pudo descargar el archivo.')
    } finally {
      enVueloRef.current = false
      setDescargando(false)
    }
  }

  return (
    <button
      type="button"
      className={className ?? 'btn btn-sm btn-outline-secondary rounded-0'}
      disabled={descargando || disabled}
      onClick={manejarClick}
    >
      {descargando ? 'Descargando…' : etiqueta}
    </button>
  )
}
