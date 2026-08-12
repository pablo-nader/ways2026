import { useRef, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'

type PropsBotonDeDescarga = {
  ruta: string
  etiqueta?: string
  onError: (mensaje: string) => void
  className?: string
}

/**
 * Botón de descarga de un `/export` (stage-11 slice 4): re-entrancy guard vía `useRef` + `disabled`
 * cubren toda la ventana de la descarga (`react-async-state` regla 5/9) — el `ref` bloquea un
 * doble click en el MISMO tick, antes de que React re-renderice el atributo `disabled`; un `useState`
 * solo no alcanza porque dos clicks sincrónicos leen el mismo valor obsoleto. Los errores nunca
 * navegan la SPA: se funnelean por `onError` al estado de la pantalla que monta el botón (proposal
 * decisión 8 — "a download that silently does nothing is this pattern's worst failure mode").
 */
export function BotonDeDescarga({ ruta, etiqueta = 'Descargar', onError, className }: PropsBotonDeDescarga) {
  const [descargando, setDescargando] = useState(false)
  const enVueloRef = useRef(false)

  const manejarClick = async () => {
    if (enVueloRef.current) return
    enVueloRef.current = true
    setDescargando(true)

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
      disabled={descargando}
      onClick={manejarClick}
    >
      {descargando ? 'Descargando…' : etiqueta}
    </button>
  )
}
