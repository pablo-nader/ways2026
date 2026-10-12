import { useEffect, useRef } from 'react'

type PropsErrorDeFormulario = {
  mensaje: string
  /** Cambia en cada fallo. Hace falta solo cuando el mensaje puede repetirse sin haber pasado por
   * vacío entre un fallo y el siguiente (p. ej. una validación local), porque el efecto depende
   * del mensaje y de este valor. */
  disparo?: number
}

/** Error de un formulario dentro de una modal: se anuncia como alerta y se trae a la vista cuando
 * aparece, porque en una modal desplazable el pie con Guardar queda fijo y el error puede nacer
 * fuera del área visible del cuerpo. No renderiza nada con el mensaje vacío. */
export function ErrorDeFormulario({ mensaje, disparo }: PropsErrorDeFormulario) {
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!mensaje) return
    ref.current?.scrollIntoView?.({ block: 'nearest' })
  }, [mensaje, disparo])

  if (!mensaje) return null

  return (
    <div ref={ref} role="alert" className="alert alert-danger py-2 px-3 small">
      {mensaje}
    </div>
  )
}
