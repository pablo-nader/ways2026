import { useEffect, useRef, useState, type CSSProperties } from 'react'

const ANCHO_MAXIMO = 280
/** Distancia entre el ícono y el texto. El CSS la usa para el puente que mantiene el puntero sobre el texto al cruzarla. */
const SEPARACION = 8

type Posicion = { lado: 'arriba' | 'abajo'; top: number; left: number }

/**
 * Ícono "i" que muestra un texto de ayuda al pasar el mouse o al enfocarlo con el teclado. El texto vive
 * siempre en el DOM (`role="tooltip"` con `id`), de modo que el control que ayuda puede apuntarlo con
 * `aria-describedby`; solo se oculta visualmente. Se posiciona con `fixed` para no quedar recortado por
 * el scroll de un modal: va arriba del ícono, abajo si arriba no entra, y se recalcula con el scroll y el
 * redimensionado mientras se ve.
 *
 * El mouse y el foco se siguen por separado: se ve mientras el mouse esté sobre el ícono o el texto, o el foco en
 * el ícono. Escape lo oculta hasta la próxima vez que el mouse entra o el foco llega; mientras se ve, esa tecla no
 * llega a los `keydown` de `document` (como el de `Modal`), así que no cierra el modal que lo contiene.
 */
export function IconoDeAyuda({ id, texto, etiqueta = 'Ayuda' }: { id: string; texto: string; etiqueta?: string }) {
  const [conMouse, setConMouse] = useState(false)
  const [conFoco, setConFoco] = useState(false)
  const [descartado, setDescartado] = useState(false)
  const [posicion, setPosicion] = useState<Posicion | null>(null)
  const botonRef = useRef<HTMLButtonElement>(null)
  const textoRef = useRef<HTMLSpanElement>(null)
  const visible = (conMouse || conFoco) && !descartado

  function colocar() {
    const boton = botonRef.current
    const cuerpo = textoRef.current
    if (boton === null || cuerpo === null) return

    const caja = boton.getBoundingClientRect()
    const alto = cuerpo.getBoundingClientRect().height
    const mitad = ANCHO_MAXIMO / 2 + 8
    const left = Math.min(Math.max(caja.left + caja.width / 2, mitad), Math.max(window.innerWidth - mitad, mitad))
    const nueva: Posicion =
      caja.top - SEPARACION >= alto
        ? { lado: 'arriba', top: caja.top - SEPARACION, left }
        : { lado: 'abajo', top: caja.bottom + SEPARACION, left }

    setPosicion(nueva)
  }

  useEffect(() => {
    if (!visible) return

    function alTeclear(evento: KeyboardEvent) {
      if (evento.key !== 'Escape') return
      evento.stopPropagation()
      setDescartado(true)
    }

    // Captura en `window`: llega antes que el `keydown` que `Modal` escucha en `document`.
    window.addEventListener('keydown', alTeclear, true)
    // El scroll no burbujea: solo la captura ve el de un contenedor interno, como el cuerpo de un modal.
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
      className="icono-de-ayuda"
      onMouseEnter={() => {
        setConMouse(true)
        setDescartado(false)
        colocar()
      }}
      onMouseLeave={() => setConMouse(false)}
    >
      <button
        ref={botonRef}
        type="button"
        className="icono-de-ayuda__boton"
        aria-label={etiqueta}
        aria-describedby={id}
        onFocus={() => {
          setConFoco(true)
          setDescartado(false)
          colocar()
        }}
        onBlur={() => setConFoco(false)}
      >
        <svg
          width="16"
          height="16"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden="true"
          focusable="false"
        >
          <circle cx="12" cy="12" r="9" />
          <path d="M12 11v5" />
          <path d="M12 8h.01" />
        </svg>
      </button>
      <span
        ref={textoRef}
        id={id}
        role="tooltip"
        className="icono-de-ayuda__texto"
        data-visible={visible}
        data-lado={posicion?.lado}
        style={
          {
            top: posicion?.top,
            left: posicion?.left,
            maxWidth: ANCHO_MAXIMO,
            '--icono-de-ayuda-separacion': `${SEPARACION}px`,
          } as CSSProperties
        }
      >
        {texto}
      </span>
    </span>
  )
}
