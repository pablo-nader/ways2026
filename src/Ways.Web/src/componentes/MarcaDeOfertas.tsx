import { useEffect, useId, useRef, useState, type CSSProperties } from 'react'
import { textoDeOfertaVigente, type OfertaVigente } from '../api/ofertasVigentes'

const ANCHO_MAXIMO = 320
const SEPARACION = 6

type Posicion = { lado: 'arriba' | 'abajo'; top: number; left: number }

/**
 * Pastilla "Oferta" / "Ofertas (n)" con el detalle de las ofertas vigentes de un artículo en un tooltip al pasar el
 * mouse o enfocarla con el teclado. Misma mecánica que `IconoDeAyuda` (posición `fixed`, arriba o abajo según
 * entre, Escape en captura para no cerrar el modal que la contiene). Es un botón solo para poder recibir foco:
 * el clic no hace nada y no llega a la fila.
 */
export function MarcaDeOfertas({ ofertas }: { ofertas: OfertaVigente[] }) {
  const id = useId()
  const [conMouse, setConMouse] = useState(false)
  const [conFoco, setConFoco] = useState(false)
  const [descartado, setDescartado] = useState(false)
  const [posicion, setPosicion] = useState<Posicion | null>(null)
  const botonRef = useRef<HTMLButtonElement>(null)
  const textoRef = useRef<HTMLDivElement>(null)
  const visible = (conMouse || conFoco) && !descartado

  function colocar() {
    const boton = botonRef.current
    const cuerpo = textoRef.current
    if (boton === null || cuerpo === null) return

    const caja = boton.getBoundingClientRect()
    const alto = cuerpo.getBoundingClientRect().height
    const mitad = ANCHO_MAXIMO / 2 + 8
    const left = Math.min(Math.max(caja.left + caja.width / 2, mitad), Math.max(window.innerWidth - mitad, mitad))
    setPosicion(
      caja.top - SEPARACION >= alto
        ? { lado: 'arriba', top: caja.top - SEPARACION, left }
        : { lado: 'abajo', top: caja.bottom + SEPARACION, left },
    )
  }

  useEffect(() => {
    if (!visible) return

    function alTeclear(evento: KeyboardEvent) {
      if (evento.key !== 'Escape') return
      setDescartado(true)
      // Solo con el foco en la marca el Escape es para el tooltip; si apenas está el mouse encima, también cierra el modal.
      if (conFoco) evento.stopPropagation()
    }

    window.addEventListener('keydown', alTeclear, true)
    window.addEventListener('scroll', colocar, true)
    window.addEventListener('resize', colocar)
    return () => {
      window.removeEventListener('keydown', alTeclear, true)
      window.removeEventListener('scroll', colocar, true)
      window.removeEventListener('resize', colocar)
    }
  }, [visible, conFoco])

  const etiqueta = ofertas.length === 1 ? 'Oferta' : `Ofertas (${ofertas.length})`

  return (
    <span
      className="marca-de-ofertas"
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
        className="badge rounded-pill text-bg-success marca-de-ofertas__boton"
        aria-describedby={id}
        onFocus={() => {
          setConFoco(true)
          setDescartado(false)
          colocar()
        }}
        onBlur={() => setConFoco(false)}
        onClick={(e) => e.stopPropagation()}
      >
        {etiqueta}
      </button>
      <div
        ref={textoRef}
        id={id}
        role="tooltip"
        className="marca-de-ofertas__texto"
        data-visible={visible}
        data-lado={posicion?.lado}
        style={
          {
            top: posicion?.top,
            left: posicion?.left,
            maxWidth: ANCHO_MAXIMO,
            '--marca-de-ofertas-separacion': `${SEPARACION}px`,
          } as CSSProperties
        }
      >
        {ofertas.map((o) => (
          <div key={o.idOferta}>{textoDeOfertaVigente(o)}</div>
        ))}
      </div>
    </span>
  )
}
