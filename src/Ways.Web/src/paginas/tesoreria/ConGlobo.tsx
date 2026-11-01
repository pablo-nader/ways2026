import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'

const SEPARACION = 6

type Posicion = { lado: 'arriba' | 'abajo'; top: number; left: number }

type Props = {
  id: string
  /** Contenido del globo; solo existe en el DOM mientras se ve. */
  globo: ReactNode
  /** Recibe el `aria-describedby` que el elemento enfocable debe llevar mientras el globo se ve. */
  children: (accesibilidad: { 'aria-describedby'?: string }) => ReactNode
}

/**
 * Globo de detalle al pasar el mouse o enfocar con el teclado. Se ve mientras el mouse esté sobre el disparador o el globo, o el
 * foco dentro del disparador; Escape lo oculta hasta la próxima entrada (WCAG 1.4.13). Se posiciona `fixed` para no quedar recortado
 * por el contenedor con scroll de la tabla: abajo del disparador, o arriba si abajo no entra.
 */
export function ConGlobo({ id, globo, children }: Props) {
  const [conMouse, setConMouse] = useState(false)
  const [conFoco, setConFoco] = useState(false)
  const [descartado, setDescartado] = useState(false)
  const [posicion, setPosicion] = useState<Posicion | null>(null)
  const envolturaRef = useRef<HTMLSpanElement>(null)
  const globoRef = useRef<HTMLSpanElement>(null)
  const visible = (conMouse || conFoco) && !descartado

  function colocar() {
    const envoltura = envolturaRef.current
    const cuerpo = globoRef.current
    if (envoltura === null || cuerpo === null) return

    const caja = envoltura.getBoundingClientRect()
    const { height: alto, width: ancho } = cuerpo.getBoundingClientRect()
    const mitad = ancho / 2 + 8
    const left = Math.min(Math.max(caja.left + caja.width / 2, mitad), Math.max(window.innerWidth - mitad, mitad))
    const cabeAbajo = caja.bottom + SEPARACION + alto <= window.innerHeight
    setPosicion(
      cabeAbajo || caja.top - SEPARACION < alto
        ? { lado: 'abajo', top: caja.bottom + SEPARACION, left }
        : { lado: 'arriba', top: caja.top - SEPARACION, left },
    )
  }

  useLayoutEffect(() => {
    if (visible) colocar()
    else setPosicion(null)
  }, [visible])

  useEffect(() => {
    if (!visible) return

    function alTeclear(evento: KeyboardEvent) {
      if (evento.key !== 'Escape') return
      evento.stopPropagation()
      setDescartado(true)
    }

    window.addEventListener('keydown', alTeclear, true)
    window.addEventListener('scroll', colocar, true)
    window.addEventListener('resize', colocar)
    return () => {
      window.removeEventListener('keydown', alTeclear, true)
      window.removeEventListener('scroll', colocar, true)
      window.removeEventListener('resize', colocar)
    }
  }, [visible])

  return (
    <span
      ref={envolturaRef}
      className="globo-libro"
      onMouseEnter={() => {
        setConMouse(true)
        setDescartado(false)
      }}
      onMouseLeave={() => setConMouse(false)}
      onFocus={() => {
        setConFoco(true)
        setDescartado(false)
      }}
      onBlur={() => setConFoco(false)}
    >
      {children({ 'aria-describedby': visible ? id : undefined })}
      {visible && (
        <span
          ref={globoRef}
          id={id}
          role="tooltip"
          className="globo-libro__texto"
          data-lado={posicion?.lado}
          style={
            {
              top: posicion?.top,
              left: posicion?.left,
              visibility: posicion === null ? 'hidden' : 'visible',
              '--globo-libro-separacion': `${SEPARACION}px`,
            } as CSSProperties
          }
        >
          {globo}
        </span>
      )}
    </span>
  )
}
