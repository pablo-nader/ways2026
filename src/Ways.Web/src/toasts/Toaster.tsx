import { useEffect, useState } from 'react'

import { DURACION_MS, toast, useToasts, type Toast } from './toasts'

function ItemDeToast({ item }: { item: Toast }) {
  const [pausado, setPausado] = useState(false)
  const [enfocado, setEnfocado] = useState(false)
  const detenido = pausado || enfocado
  const duracion = DURACION_MS[item.tipo]
  const { id, version } = item

  useEffect(() => {
    if (detenido) return
    const temporizador = setTimeout(() => toast.cerrar(id), duracion)
    return () => clearTimeout(temporizador)
  }, [id, version, detenido, duracion])

  const clase = item.tipo === 'exito' ? 'success' : item.tipo === 'error' ? 'danger' : 'warning'

  return (
    <div
      role={item.tipo === 'error' ? 'alert' : 'status'}
      className={`ws-toast ws-toast-${clase}`}
      onMouseEnter={() => setPausado(true)}
      onMouseLeave={() => setPausado(false)}
      onFocus={() => setEnfocado(true)}
      onBlur={() => setEnfocado(false)}
    >
      <span className="ws-toast__texto">{item.texto}</span>
      <button type="button" className="btn-close ws-toast__cerrar" aria-label="Cerrar aviso" onClick={() => toast.cerrar(id)} />
    </div>
  )
}

export function Toaster() {
  const items = useToasts()
  if (items.length === 0) return null

  return (
    <div className="ws-toaster d-print-none">
      {items.map((item) => (
        <ItemDeToast key={item.id} item={item} />
      ))}
    </div>
  )
}
